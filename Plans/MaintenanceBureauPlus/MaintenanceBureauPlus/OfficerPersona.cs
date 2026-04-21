namespace MaintenanceBureauPlus
{
    public class OfficerPersona
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string Department { get; set; }
        public string Tic { get; set; }
        public string Voice { get; set; }
        public string Backstory { get; set; }
        public string Summary { get; set; }

        public string ToPersonaBlock()
        {
            return
                "Name: " + (Name ?? string.Empty) + "\n" +
                "Department: " + (Department ?? string.Empty) + "\n" +
                "Tic: " + (Tic ?? string.Empty) + "\n" +
                "Voice: " + (Voice ?? string.Empty) + "\n" +
                "Backstory: " + (Backstory ?? string.Empty);
        }

        public string ToPoolSnippet()
        {
            var idx = Index > 0 ? Index.ToString() + ". " : string.Empty;
            return idx + (Name ?? "Officer") +
                   (string.IsNullOrEmpty(Department) ? string.Empty : " (" + Department + ")") +
                   (string.IsNullOrEmpty(Summary) ? string.Empty : " - " + Summary);
        }
    }
}
