using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;
using System;
using System.Reflection;

namespace MaintenanceBureauPlus
{
    // Server -> client broadcast of a bureau officer's reply. Receivers show
    // the text as a centered on-screen popup (AlertMessage), in addition to
    // the regular chat line the server also broadcasts via ChatMessage.
    //
    // Chat is a persistent record; the popup is for "you won't miss this".
    public class BureauReplyMessage : INetworkMessage
    {
        public string OfficerName;
        public string ReplyText;
        public float DisplayDuration;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteString(OfficerName ?? string.Empty);
            writer.WriteString(ReplyText ?? string.Empty);
            writer.WriteSingle(DisplayDuration);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            OfficerName = reader.ReadString();
            ReplyText = reader.ReadString();
            DisplayDuration = reader.ReadSingle();
        }

        public void Process(long hostId)
        {
            BureauPopup.Show(OfficerName, ReplyText, DisplayDuration);
        }
    }

    // Reflective wrapper around AlertMessage.Show(string, float). Stationeers
    // ships AlertMessage as a global UI class; we use reflection so a
    // namespace rename in a future game version does not break the build.
    // The MethodInfo is cached after the first lookup.
    internal static class BureauPopup
    {
        private static MethodInfo _showMethod;
        private static bool _resolveAttempted;

        public static void Show(string officer, string text, float duration)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var method = ResolveShow();
                if (method == null)
                {
                    MaintenanceBureauPlusPlugin.Log.LogWarning(
                        "[DIAG] BureauPopup: AlertMessage.Show not found; popup skipped.");
                    return;
                }
                var full = string.IsNullOrEmpty(officer) ? text : officer + ": " + text;
                method.Invoke(null, new object[] { full, duration });
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "[DIAG] BureauPopup shown: chars=" + full.Length +
                    " duration=" + duration + "s");
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError(
                    "[DIAG] BureauPopup.Show failed: " + ex.Message);
            }
        }

        private static MethodInfo ResolveShow()
        {
            if (_showMethod != null) return _showMethod;
            if (_resolveAttempted) return null;
            _resolveAttempted = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try { type = asm.GetType("AlertMessage"); } catch { }
                if (type == null)
                {
                    try
                    {
                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "AlertMessage") { type = t; break; }
                        }
                    }
                    catch { /* some assemblies throw on GetTypes; ignore */ }
                }
                if (type == null) continue;

                var method = type.GetMethod(
                    "Show",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(float) },
                    null);
                if (method != null)
                {
                    _showMethod = method;
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Resolved AlertMessage.Show at " + type.FullName);
                    return method;
                }
            }
            return null;
        }
    }
}
