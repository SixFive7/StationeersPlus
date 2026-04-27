using System.Collections.Generic;
using System.Text;

namespace MaintenanceBureauPlus
{
    public class ConversationState
    {
        public OfficerPersona Officer { get; set; }
        public int TurnCount { get; set; }
        public int MinTurns { get; set; }
        public int MaxTurns { get; set; }
        public bool IsActive { get; set; }
        public bool IsAwaitingPersona { get; set; }

        // True from the moment we kick off the classifier (or the cycle's
        // opening reply) until the officer's reply has broadcast. Bridges
        // the gap between Engine.IsBusy decrementing after the classifier
        // call finishes and the reply call's Increment running on the next
        // main-thread drain. ChatPatch's busy guard checks both flags so a
        // player message arriving in that microsecond window is still routed
        // to a busy response instead of slipping through.
        public bool PendingTurn { get; set; }

        // Full per-cycle transcript. No size cap: the cycle itself is bounded
        // by MaxTurns (default 15) plus the final approval / refusal reply, so
        // the list cannot grow unbounded. Cleared on Reset() when the cycle
        // ends and a new persona takes over.
        public List<TranscriptEntry> Transcript { get; } = new List<TranscriptEntry>();

        public void Reset()
        {
            Officer = null;
            TurnCount = 0;
            IsActive = false;
            IsAwaitingPersona = false;
            PendingTurn = false;
            Transcript.Clear();
        }

        public string RenderTranscript()
        {
            if (Transcript.Count == 0) return "(no prior messages this cycle)";
            var sb = new StringBuilder();
            foreach (var entry in Transcript)
            {
                sb.Append(entry.Speaker);
                sb.Append(": ");
                sb.AppendLine(entry.Text);
            }
            return sb.ToString().TrimEnd();
        }

        public void AppendPlayer(string speaker, string text)
        {
            Transcript.Add(new TranscriptEntry { Speaker = "Player (" + speaker + ")", Text = text });
        }

        public void AppendOfficer(string text)
        {
            var name = Officer != null ? Officer.Name : "Officer";
            Transcript.Add(new TranscriptEntry { Speaker = "Officer " + name, Text = text });
        }

        public struct TranscriptEntry
        {
            public string Speaker;
            public string Text;
        }
    }
}
