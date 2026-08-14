using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace TestRig
{
    internal sealed class HttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public string Query = "";
        public string Body = "";
        public readonly Dictionary<string, string> QueryParams =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];

        public static HttpResponse Json(string json, int status = 200) => new HttpResponse
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(json ?? "null"),
        };

        public static HttpResponse Text(string text, int status = 200) => new HttpResponse
        {
            Status = status,
            ContentType = "text/plain; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(text ?? ""),
        };

        public static HttpResponse Png(byte[] png) => new HttpResponse
        {
            Status = 200,
            ContentType = "image/png",
            Body = png ?? new byte[0],
        };

        public static HttpResponse Error(string message, int status = 500) =>
            Json(new Json.Obj().Bit("ok", false).Str("error", message).ToString(), status);
    }

    /// <summary>
    /// A deliberately small HTTP/1.1 server on a raw <see cref="TcpListener"/>.
    ///
    /// Why not <c>HttpListener</c>: on the Microsoft CLR it goes through http.sys and
    /// needs a URL ACL reservation (or elevation) for anything but the machine
    /// defaults, and under Unity's Mono the managed implementation has its own quirks
    /// with keep-alive and binary bodies. A socket plus 150 lines of parsing has no
    /// such dependencies and behaves identically on every machine.
    ///
    /// One request per connection, always answered with Connection: close. That is
    /// slower than keep-alive and completely irrelevant here: the driver issues a
    /// handful of requests per second at most.
    /// </summary>
    internal sealed class HttpServer
    {
        private readonly int _port;
        private readonly Func<HttpRequest, HttpResponse> _handler;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logError;

        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        internal int Port => _port;
        internal bool Running => _running && _listener != null;
        internal long Requests;
        internal string LastAcceptError;

        public HttpServer(int port, Func<HttpRequest, HttpResponse> handler, Action<string> logInfo, Action<string> logError)
        {
            _port = port;
            _handler = handler;
            _logInfo = logInfo;
            _logError = logError;
        }

        public bool Start()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, _port);
                _listener.Start();
                _running = true;
                _thread = new Thread(AcceptLoop)
                {
                    IsBackground = true,
                    Name = "TestRig-Http",
                };
                _thread.Start();
                _logInfo("HTTP control plane listening on http://127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + "/");
                return true;
            }
            catch (Exception ex)
            {
                _logError("Failed to bind 127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                return false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _listener = null;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (Exception ex)
                {
                    LastAcceptError = ex.Message;
                    if (!_running)
                    {
                        _logInfo("accept loop exiting: listener stopped");
                        return;
                    }
                    _logError("accept failed: " + ex.Message);
                    Thread.Sleep(200);
                    continue;
                }

                Requests++;

                // Serve inline. Requests are strictly sequential by design: a test
                // harness that fires two engine mutations concurrently gets
                // nondeterministic results, so serialising here is a feature.
                try { Serve(client); }
                catch (Exception ex) { _logError("Request failed: " + ex); }
                finally { try { client.Close(); } catch { } }
            }
        }

        private void Serve(TcpClient client)
        {
            client.NoDelay = true;
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 30000;

            using (var stream = client.GetStream())
            {
                var req = ReadRequest(stream);
                HttpResponse resp;
                if (req == null)
                {
                    resp = HttpResponse.Error("malformed request", 400);
                }
                else
                {
                    try { resp = _handler(req) ?? HttpResponse.Error("handler returned null"); }
                    catch (Exception ex) { resp = HttpResponse.Error(ex.ToString()); }
                }
                WriteResponse(stream, resp);
                try { stream.Flush(); } catch { }
            }
        }

        private static HttpRequest ReadRequest(NetworkStream stream)
        {
            var headerBytes = new List<byte>(1024);
            int consecutive = 0;
            var one = new byte[1];
            while (headerBytes.Count < 64 * 1024)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) return null;
                headerBytes.Add(one[0]);
                if (one[0] == '\n')
                {
                    consecutive++;
                    if (consecutive == 2) break;
                }
                else if (one[0] != '\r')
                {
                    consecutive = 0;
                }
            }

            string headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
            var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            var parts = lines[0].Split(' ');
            if (parts.Length < 2) return null;

            var req = new HttpRequest { Method = parts[0].ToUpperInvariant() };
            string rawTarget = parts[1];
            int q = rawTarget.IndexOf('?');
            if (q >= 0)
            {
                req.Path = UrlDecode(rawTarget.Substring(0, q));
                req.Query = rawTarget.Substring(q + 1);
                foreach (var pair in req.Query.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    if (eq < 0) req.QueryParams[UrlDecode(pair)] = "";
                    else req.QueryParams[UrlDecode(pair.Substring(0, eq))] = UrlDecode(pair.Substring(eq + 1));
                }
            }
            else
            {
                req.Path = UrlDecode(rawTarget);
            }

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            }

            if (contentLength > 0)
            {
                var body = new byte[contentLength];
                int read = 0;
                while (read < contentLength)
                {
                    int n = stream.Read(body, read, contentLength - read);
                    if (n <= 0) break;
                    read += n;
                }
                req.Body = Encoding.UTF8.GetString(body, 0, read);
            }

            return req;
        }

        private static void WriteResponse(NetworkStream stream, HttpResponse resp)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(resp.Status.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(StatusText(resp.Status)).Append("\r\n");
            sb.Append("Content-Type: ").Append(resp.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(resp.Body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Connection: close\r\n\r\n");
            var head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (resp.Body.Length > 0) stream.Write(resp.Body, 0, resp.Body.Length);
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Status";
            }
        }

        private static string UrlDecode(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            if (s.IndexOf('%') < 0 && s.IndexOf('+') < 0) return s;
            var bytes = new List<byte>(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+') { bytes.Add((byte)' '); }
                else if (c == '%' && i + 2 < s.Length)
                {
                    int hi = HexVal(s[i + 1]);
                    int lo = HexVal(s[i + 2]);
                    if (hi >= 0 && lo >= 0) { bytes.Add((byte)((hi << 4) | lo)); i += 2; }
                    else bytes.Add((byte)c);
                }
                else
                {
                    foreach (var b in Encoding.UTF8.GetBytes(c.ToString())) bytes.Add(b);
                }
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        private static int HexVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
