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

    // Reflective popup. Tries ConfirmationPanel first (Stationeers' modal
    // dialog with a button that has to be clicked, same one shown for "save
    // failed to load" errors). Falls back to AlertMessage (auto-fading
    // overlay) if ConfirmationPanel can't be resolved. The chat broadcast
    // path always runs in addition; popups are an attention-grabbing
    // overlay, not a replacement for the chat record.
    internal static class BureauPopup
    {
        // Cached resolved entrypoints for ConfirmationPanel.Instance.ShowRaw.
        private static object _confirmationInstance;
        private static MethodInfo _confirmationShowRaw;
        private static Type[] _confirmationShowRawParamTypes;
        private static bool _confirmationResolveAttempted;

        // Cached AlertMessage.Show fallback.
        private static MethodInfo _alertShowMethod;
        private static bool _alertResolveAttempted;

        public static void Show(string officer, string text, float duration)
        {
            if (string.IsNullOrEmpty(text)) return;
            try
            {
                var title = string.IsNullOrEmpty(officer) ? "Bureau" : officer;
                if (TryShowConfirmation(title, text)) return;
                if (TryShowAlert(title + ": " + text, duration)) return;
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] BureauPopup: neither ConfirmationPanel nor AlertMessage available.");
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError(
                    "[DIAG] BureauPopup.Show failed: " + ex.Message);
            }
        }

        // ConfirmationPanel.Instance.ShowRaw(title, message, b1Text, b1Action, b2Text, b2Action, b3Text, b3Action)
        // is Stationeers' modal-dialog-with-button entrypoint (the same one
        // used for save-load errors). Single button labelled "Continue", no
        // callback. Multiple calls stack via ConfirmationPanel's internal queue,
        // so a player who steps away returns to a stack of bureau replies.
        private static bool TryShowConfirmation(string title, string message)
        {
            ResolveConfirmation();
            if (_confirmationInstance == null || _confirmationShowRaw == null) return false;

            try
            {
                var pTypes = _confirmationShowRawParamTypes;
                var args = new object[pTypes.Length];
                // Parameter layout per the popup-research decompile:
                //   0: title (string)
                //   1: message (string)
                //   2: b1Text (string)   - "Continue"
                //   3: b1OnClick (UnityAction or Action) - null = auto-dismiss only
                //   4..7: b2/b3 text + action - all null
                args[0] = title;
                args[1] = message;
                if (pTypes.Length > 2) args[2] = "Continue";
                for (int i = 3; i < pTypes.Length; i++) args[i] = null;

                _confirmationShowRaw.Invoke(_confirmationInstance, args);
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "[DIAG] BureauPopup: shown via ConfirmationPanel.ShowRaw " +
                    "(title=" + title + " chars=" + message.Length + ")");
                return true;
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError(
                    "[DIAG] BureauPopup.ConfirmationPanel.ShowRaw threw: " + ex.Message);
                return false;
            }
        }

        private static bool TryShowAlert(string text, float duration)
        {
            ResolveAlert();
            if (_alertShowMethod == null) return false;
            try
            {
                _alertShowMethod.Invoke(null, new object[] { text, duration });
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "[DIAG] BureauPopup: shown via AlertMessage (fallback) duration=" + duration + "s");
                return true;
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError(
                    "[DIAG] BureauPopup.AlertMessage.Show threw: " + ex.Message);
                return false;
            }
        }

        private static void ResolveConfirmation()
        {
            if (_confirmationResolveAttempted) return;
            _confirmationResolveAttempted = true;

            var type = FindTypeByName("ConfirmationPanel");
            if (type == null) return;

            // Singleton<T>.Instance is a static property exposed by the type.
            var instanceProp = type.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            object instance = null;
            try { instance = instanceProp != null ? instanceProp.GetValue(null) : null; } catch { }
            if (instance == null) return;

            // Pick the ShowRaw overload with the most parameters that starts
            // with (string title, string message, ...). Decompile reports the
            // canonical 8-arg overload; we tolerate variants.
            MethodInfo best = null;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "ShowRaw") continue;
                var ps = m.GetParameters();
                if (ps.Length < 2) continue;
                if (ps[0].ParameterType != typeof(string) || ps[1].ParameterType != typeof(string)) continue;
                if (best == null || ps.Length > best.GetParameters().Length) best = m;
            }
            if (best == null) return;

            _confirmationInstance = instance;
            _confirmationShowRaw = best;
            _confirmationShowRawParamTypes = new Type[best.GetParameters().Length];
            var bps = best.GetParameters();
            for (int i = 0; i < bps.Length; i++) _confirmationShowRawParamTypes[i] = bps[i].ParameterType;

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Resolved ConfirmationPanel.ShowRaw (" + bps.Length + " args) at " + type.FullName);
        }

        private static void ResolveAlert()
        {
            if (_alertResolveAttempted) return;
            _alertResolveAttempted = true;

            var type = FindTypeByName("AlertMessage");
            if (type == null) return;

            _alertShowMethod = type.GetMethod(
                "Show",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(float) },
                null);
            if (_alertShowMethod != null)
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "[DIAG] Resolved AlertMessage.Show at " + type.FullName);
        }

        private static Type FindTypeByName(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == simpleName) return t;
                    }
                }
                catch { /* GetTypes can throw on flaky assemblies */ }
            }
            return null;
        }
    }
}
