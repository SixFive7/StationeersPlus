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
        public List<TranscriptEntry> TranscriptTail { get; } = new List<TranscriptEntry>();

        public void Reset()
        {
            Officer = null;
            TurnCount = 0;
            IsActive = false;
            IsAwaitingPersona = false;
            TranscriptTail.Clear();
        }

        public string RenderTranscript()
        {
            if (TranscriptTail.Count == 0) return "(no prior messages this cycle)";
            var sb = new StringBuilder();
            foreach (var entry in TranscriptTail)
            {
                sb.Append(entry.Speaker);
                sb.Append(": ");
                sb.AppendLine(entry.Text);
            }
            return sb.ToString().TrimEnd();
        }

        public void AppendPlayer(string speaker, string text)
        {
            TranscriptTail.Add(new TranscriptEntry { Speaker = "Player (" + speaker + ")", Text = text });
            TrimTail();
        }

        public void AppendOfficer(string text)
        {
            var name = Officer != null ? Officer.Name : "Officer";
            TranscriptTail.Add(new TranscriptEntry { Speaker = "Officer " + name, Text = text });
            TrimTail();
        }

        private void TrimTail()
        {
            int cap = MaintenanceBureauPlusPlugin.TranscriptTailTurns;
            while (TranscriptTail.Count > cap)
                TranscriptTail.RemoveAt(0);
        }

        public struct TranscriptEntry
        {
            public string Speaker;
            public string Text;
        }
    }
}
