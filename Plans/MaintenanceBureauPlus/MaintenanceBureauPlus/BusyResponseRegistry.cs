using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MaintenanceBureauPlus
{
    // Loads Resources/BusyResponses.md (embedded into the assembly via
    // .csproj) and parses it into a pool of BusyResponse entries. Used by
    // ChatPatch.Postfix when the LLM is busy with a prior request: instead
    // of enqueuing the new player message (which would queue work behind
    // an in-flight inference and break linearity), we broadcast a random
    // canned reply from a non-officer entity and drop the player message.
    //
    // Parser format mirrors PersonaRegistry: each entry is an H2 heading
    // (## N. Sender) followed by body text until the next H2 or end of
    // file. The leading 'N.' number is stripped and ignored; the sender
    // is the rest of the heading text.
    public class BusyResponseRegistry
    {
        private readonly List<BusyResponse> _responses = new List<BusyResponse>();
        private readonly Random _rng = new Random();

        public int Count { get { return _responses.Count; } }

        public void LoadFromEmbeddedResource()
        {
            const string resourceName = "MaintenanceBureauPlus.Resources.BusyResponses.md";
            var asm = typeof(BusyResponseRegistry).Assembly;
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    MaintenanceBureauPlusPlugin.Log.LogWarning(
                        "[DIAG] BusyResponseRegistry: embedded resource not found: " + resourceName);
                    return;
                }
                using (var reader = new StreamReader(stream))
                {
                    Parse(reader.ReadToEnd());
                }
            }
            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] BusyResponseRegistry loaded " + _responses.Count + " entries.");
        }

        public BusyResponse PickRandom()
        {
            if (_responses.Count == 0) return null;
            return _responses[_rng.Next(_responses.Count)];
        }

        private void Parse(string content)
        {
            BusyResponse current = null;
            var body = new StringBuilder();
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.StartsWith("## ", StringComparison.Ordinal))
                    {
                        Flush(current, body);
                        body.Length = 0;
                        var heading = line.Substring(3).Trim();
                        // Strip leading "N." organisational number if present.
                        int dot = heading.IndexOf('.');
                        if (dot > 0 && int.TryParse(heading.Substring(0, dot), out _))
                            heading = heading.Substring(dot + 1).Trim();
                        current = new BusyResponse { Sender = heading };
                    }
                    else if (current != null)
                    {
                        if (body.Length > 0) body.Append('\n');
                        body.Append(line);
                    }
                }
                Flush(current, body);
            }
        }

        private void Flush(BusyResponse entry, StringBuilder body)
        {
            if (entry == null) return;
            var text = body.ToString().Trim();
            if (string.IsNullOrEmpty(text)) return;
            if (string.IsNullOrEmpty(entry.Sender)) return;
            entry.Text = text;
            _responses.Add(entry);
        }
    }
}
