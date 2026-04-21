using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MaintenanceBureauPlus
{
    public class PersonaRegistry
    {
        private readonly List<OfficerPersona> _personas = new List<OfficerPersona>();

        public int Count { get { return _personas.Count; } }
        public IReadOnlyList<OfficerPersona> All { get { return _personas; } }

        public void LoadFromEmbeddedResource()
        {
            var asm = Assembly.GetExecutingAssembly();
            var resourceName = "MaintenanceBureauPlus.Resources.Personas.md";
            using (var stream = asm.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("Embedded resource not found: " + resourceName +
                        ". Check that Personas.md is marked as EmbeddedResource in the csproj.");
                    return;
                }
                using (var reader = new StreamReader(stream))
                {
                    Parse(reader.ReadToEnd());
                }
            }
            MaintenanceBureauPlusPlugin.Log.LogInfo("PersonaRegistry loaded " + _personas.Count + " personas.");
        }

        private static readonly Regex HeadingRegex = new Regex(
            @"^##\s+(\d+)\.\s+Officer\s+(.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex BulletRegex = new Regex(
            @"^-\s+\*\*(Summary|Department|Tic|Voice|Backstory):\*\*\s+(.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private void Parse(string markdown)
        {
            _personas.Clear();
            var matches = HeadingRegex.Matches(markdown);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int idx;
                if (!int.TryParse(m.Groups[1].Value, out idx)) continue;
                var name = m.Groups[2].Value.Trim();

                int sectionStart = m.Index;
                int sectionEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : markdown.Length;
                var section = markdown.Substring(sectionStart, sectionEnd - sectionStart);

                var persona = new OfficerPersona { Index = idx, Name = name };
                foreach (Match b in BulletRegex.Matches(section))
                {
                    var field = b.Groups[1].Value;
                    var value = b.Groups[2].Value.Trim();
                    switch (field)
                    {
                        case "Summary": persona.Summary = value; break;
                        case "Department": persona.Department = value; break;
                        case "Tic": persona.Tic = value; break;
                        case "Voice": persona.Voice = value; break;
                        case "Backstory": persona.Backstory = value; break;
                    }
                }
                _personas.Add(persona);
            }
        }
    }
}
