using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace TestRig
{
    /// <summary>
    ///     Asks every sibling control plane in the rig who it thinks it is, so this instance can
    ///     notice that another one is claiming the same ClientId.
    ///
    ///     Why this is worth a whole file. Two clients presenting the same ClientId is not a
    ///     cosmetic problem: the server keys a player's body on it, <c>Brain.PlayerBrains</c> is a
    ///     <c>Dictionary&lt;ulong, Brain&gt;</c> whose <c>RegisterBrain</c> overwrites silently, and
    ///     the second joiner therefore resolves onto the first joiner's character. Nothing on either
    ///     side warns. A test that believes it has two players and actually has one produces results
    ///     that look plausible and are meaningless, which is the same class of failure as an input
    ///     endpoint reporting success for input nobody consumed.
    ///
    ///     The check is cheap and entirely local: a GET to <c>127.0.0.1:&lt;peer&gt;/instance</c> for
    ///     each port in the manifest's peer list. A peer that is not running simply does not answer,
    ///     which is not a conflict.
    ///
    ///     It runs off the main thread, on a short timeout, and never blocks boot. The result is
    ///     advisory everywhere except <c>/connect</c>, which refuses a join into a known conflict
    ///     unless the caller explicitly overrides it. That is the right place to enforce it: the
    ///     damage happens at the join, not before.
    /// </summary>
    internal static class PeerProbe
    {
        internal sealed class Peer
        {
            public int Port;
            public string Name;
            public string ClientId;
            public bool Reachable;
            public string Error;
            public bool Conflicts;
        }

        private static readonly object _gate = new object();
        private static readonly List<Peer> _peers = new List<Peer>();
        private static DateTime _lastScan = DateTime.MinValue;
        private static bool _scanning;

        internal static bool ConflictDetected;
        internal static string ConflictSummary;

        /// <summary>
        ///     Kicks off a scan on a background thread if the cached result is older than
        ///     <paramref name="maxAgeMs"/>. Returns immediately.
        /// </summary>
        internal static void ScanAsync(int maxAgeMs = 15000)
        {
            lock (_gate)
            {
                if (_scanning) return;
                if ((DateTime.UtcNow - _lastScan).TotalMilliseconds < maxAgeMs) return;
                _scanning = true;
            }

            var t = new Thread(() => { try { Scan(); } catch { } })
            {
                IsBackground = true,
                Name = "TestRig-PeerProbe",
            };
            t.Start();
        }

        /// <summary>Scans synchronously. Safe to call from any thread except the Unity main thread.</summary>
        internal static void Scan()
        {
            var found = new List<Peer>();
            ulong ownId = Identity.EffectiveClientId;
            int ownPort = Plugin.EffectivePort;

            try
            {
                foreach (int port in InstanceManifest.PeerPorts)
                {
                    if (port == ownPort) continue;
                    var peer = new Peer { Port = port };
                    try
                    {
                        string body = Get("http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/instance", 1500);
                        peer.Reachable = body != null;
                        if (body != null)
                        {
                            var root = Json.Parse(body) as System.Collections.IDictionary;
                            var instance = root != null && Json.Has(root, "instance")
                                ? root["instance"] as System.Collections.IDictionary
                                : null;
                            if (instance != null)
                            {
                                peer.Name = Json.GetStr(instance, "name");
                                peer.ClientId = Json.GetStr(instance, "clientId");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        peer.Error = ex.Message;
                    }

                    if (peer.Reachable && ownId != 0 && !string.IsNullOrEmpty(peer.ClientId))
                    {
                        ulong theirs;
                        if (ulong.TryParse(peer.ClientId, NumberStyles.Integer, CultureInfo.InvariantCulture, out theirs))
                            peer.Conflicts = theirs == ownId;
                    }

                    found.Add(peer);
                }

                var conflicting = new List<string>();
                foreach (var p in found)
                    if (p.Conflicts)
                        conflicting.Add((string.IsNullOrEmpty(p.Name) ? "port " + p.Port : p.Name) +
                                        " on " + p.Port.ToString(CultureInfo.InvariantCulture));

                lock (_gate)
                {
                    _peers.Clear();
                    _peers.AddRange(found);
                    _lastScan = DateTime.UtcNow;
                    ConflictDetected = conflicting.Count > 0;
                    ConflictSummary = ConflictDetected
                        ? "ClientId " + ownId.ToString(CultureInfo.InvariantCulture) +
                          " is also claimed by " + string.Join(", ", conflicting.ToArray()) +
                          ". The server keys a player's body on this id, so both instances would " +
                          "resolve onto one Brain and the second joiner would take over the first " +
                          "joiner's character. Give each instance a distinct clientId."
                        : null;
                }

                if (ConflictDetected) Plugin.Log.LogError("identity conflict: " + ConflictSummary);
            }
            finally
            {
                lock (_gate) { _scanning = false; _lastScan = DateTime.UtcNow; }
            }
        }

        /// <summary>
        ///     A minimal HTTP GET. Deliberately not <c>WebClient</c> or <c>HttpClient</c>: this runs
        ///     inside a Unity Mono process against a peer that is very often mid-boot, and the only
        ///     behaviour that matters is a hard, short timeout with no retries and no proxy lookup.
        ///     Returns null when the peer did not answer.
        /// </summary>
        private static string Get(string url, int timeoutMs)
        {
            HttpWebRequest request;
            try { request = (HttpWebRequest)WebRequest.Create(url); }
            catch { return null; }

            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.Proxy = null;
            request.KeepAlive = false;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null) return null;
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        return reader.ReadToEnd();
                }
            }
            catch
            {
                // Refused, timed out, or answered garbage. All of those mean "no peer here",
                // which is the normal case for a rig that is not fully up yet.
                return null;
            }
        }

        internal static string DescribeJson()
        {
            var o = new Json.Obj();
            List<Peer> snapshot;
            DateTime scanned;
            lock (_gate)
            {
                snapshot = new List<Peer>(_peers);
                scanned = _lastScan;
            }

            o.Bit("conflictDetected", ConflictDetected);
            o.Str("conflict", ConflictSummary);
            o.Str("lastScanUtc", scanned == DateTime.MinValue ? null : scanned.ToString("o", CultureInfo.InvariantCulture));

            var rows = new List<string>();
            foreach (var p in snapshot)
                rows.Add(new Json.Obj()
                    .Int("port", p.Port)
                    .Bit("reachable", p.Reachable)
                    .Str("name", p.Name)
                    .Str("clientId", p.ClientId)
                    .Bit("conflicts", p.Conflicts)
                    .Str("error", p.Error)
                    .ToString());
            o.Raw("peers", "[" + string.Join(",", rows.ToArray()) + "]");
            o.Int("peerCount", rows.Count);
            return o.ToString();
        }
    }
}
