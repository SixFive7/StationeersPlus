using System.Text.RegularExpressions;

namespace MaintenanceBureauPlus
{
    public enum ApprovalTag { None, Continue, Approved, Refused }

    public static class ApprovalTagParser
    {
        private static readonly Regex TagRegex = new Regex(
            @"\[(CONTINUE|APPROVED|REFUSED)\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public struct ParsedReply
        {
            public ApprovalTag Tag;
            public string StrippedText;
        }

        public static ParsedReply Parse(string rawReply)
        {
            if (string.IsNullOrEmpty(rawReply))
                return new ParsedReply { Tag = ApprovalTag.None, StrippedText = string.Empty };

            var match = TagRegex.Match(rawReply);
            if (!match.Success)
                return new ParsedReply { Tag = ApprovalTag.None, StrippedText = rawReply.Trim() };

            var token = match.Groups[1].Value.ToUpperInvariant();
            var tag = ApprovalTag.Continue;
            if (token == "APPROVED") tag = ApprovalTag.Approved;
            else if (token == "REFUSED") tag = ApprovalTag.Refused;

            var stripped = TagRegex.Replace(rawReply, string.Empty).Trim();
            return new ParsedReply { Tag = tag, StrippedText = stripped };
        }
    }
}
