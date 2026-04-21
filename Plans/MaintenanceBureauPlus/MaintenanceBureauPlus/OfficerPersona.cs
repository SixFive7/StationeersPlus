namespace MaintenanceBureauPlus
{
    // Public fields (not auto-properties) because UnityEngine.JsonUtility can only
    // serialize public fields, and the LLM returns a flat JSON document that
    // we deserialize straight into this class.
    [System.Serializable]
    public class OfficerPersona
    {
        public int Index;
        public string Name;
        public string Department;
        public string Tic;
        public string Voice;
        public string Backstory;
        public string Summary;

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
