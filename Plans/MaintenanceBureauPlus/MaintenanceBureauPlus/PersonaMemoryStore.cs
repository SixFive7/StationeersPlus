using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace MaintenanceBureauPlus
{
    public class PersonaMemoryStore
    {
        private readonly string _path;
        private readonly int _cap;
        private List<string> _summaries = new List<string>();

        public PersonaMemoryStore(string path, int cap)
        {
            _path = path;
            _cap = cap;
        }

        public IReadOnlyList<string> Summaries { get { return _summaries; } }

        public void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    _summaries = new List<string>();
                    return;
                }
                var json = File.ReadAllText(_path);
                _summaries = JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>();
                MaintenanceBureauPlusPlugin.Log.LogInfo("PersonaMemoryStore loaded " + _summaries.Count + " entries from " + _path);
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("PersonaMemoryStore load failed: " + ex.Message);
                _summaries = new List<string>();
            }
        }

        public void Append(string summary)
        {
            if (string.IsNullOrWhiteSpace(summary)) return;
            _summaries.Add(summary.Trim());
            while (_summaries.Count > _cap)
                _summaries.RemoveAt(0);
            Save();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonConvert.SerializeObject(_summaries, Formatting.Indented));
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("PersonaMemoryStore save failed: " + ex.Message);
            }
        }

        public string RenderForPrompt(int maxEntries)
        {
            if (_summaries.Count == 0) return "(no prior officers recorded)";
            var sb = new StringBuilder();
            int start = Math.Max(0, _summaries.Count - maxEntries);
            for (int i = start; i < _summaries.Count; i++)
            {
                sb.Append("- ");
                sb.AppendLine(_summaries[i]);
            }
            return sb.ToString().TrimEnd();
        }
    }
}
