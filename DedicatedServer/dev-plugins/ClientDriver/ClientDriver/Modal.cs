using System;
using System.Collections;
using System.Reflection;
using Assets.Scripts.Util;
using HarmonyLib;
using UI;
using UnityEngine.Events;

namespace ClientDriver
{
    /// <summary>
    /// Reads and dismisses the game's confirmation dialogs.
    ///
    /// This is the one place unattended operation genuinely breaks without help. A
    /// failed Direct Connect pops <c>ConfirmationPanel</c> after a 10 second timer
    /// (<c>NetworkClient.ConnectionTimerOnElapsed</c>), and until something clicks OK
    /// the client sits behind a modal with the cursor unlocked and nothing else
    /// responding. Same for a version mismatch, a full server, or a kick.
    ///
    /// The panel keeps a private <c>Stack&lt;Data&gt;</c> whose entries carry the
    /// button texts and their <c>UnityAction</c> callbacks. Clicking through the
    /// driver reproduces exactly what <c>UiButton.SetOnClickCallbacks</c> wires up:
    /// <c>CloseCurrentPanel</c> first, then the button's own action.
    /// </summary>
    internal static class Modal
    {
        private static object PeekData(out ConfirmationPanel panel)
        {
            panel = Singleton<ConfirmationPanel>.Instance;
            if (panel == null) return null;
            var stackField = AccessTools.Field(typeof(ConfirmationPanel), "_dataStack");
            if (stackField == null) return null;
            var stack = stackField.GetValue(panel) as IEnumerable;
            if (stack == null) return null;
            foreach (var item in stack) return item;   // Stack enumerates top first
            return null;
        }

        private static string FieldStr(object data, string name)
        {
            if (data == null) return null;
            var f = data.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null) return null;
            var v = f.GetValue(data);
            return v == null ? null : v.ToString();
        }

        private static UnityAction FieldAction(object data, string name)
        {
            if (data == null) return null;
            var f = data.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(data) as UnityAction;
        }

        internal static string Describe()
        {
            var o = new Json.Obj().Bit("ok", true);
            ConfirmationPanel panel;
            object data;
            try { data = PeekData(out panel); }
            catch (Exception ex) { return o.Bit("visible", false).Str("error", ex.Message).ToString(); }

            // IsVisible is just gameObject.activeInHierarchy, which is true for a
            // short window during boot before the panel is first deactivated, with
            // an empty data stack behind it. Reporting that as a visible dialog is a
            // false positive that makes a connect poll bail out for no reason, so a
            // dialog only counts as showing when it has data.
            bool panelActive = false;
            try { panelActive = panel != null && panel.IsVisible; } catch { }
            o.Bit("panelActive", panelActive);
            bool visible = panelActive && data != null;
            o.Bit("visible", visible);
            if (!visible) return o.ToString();

            o.Str("title", FieldStr(data, "TitleText"));
            o.Str("message", FieldStr(data, "MessageText"));
            o.Str("button1", FieldStr(data, "Button1Text"));
            o.Str("button2", FieldStr(data, "Button2Text"));
            o.Str("button3", FieldStr(data, "Button3Text"));
            return o.ToString();
        }

        internal static string Click(int button)
        {
            ConfirmationPanel panel;
            object data;
            try { data = PeekData(out panel); }
            catch (Exception ex) { return new Json.Obj().Bit("ok", false).Str("error", ex.Message).ToString(); }

            bool visible = false;
            try { visible = panel != null && panel.IsVisible; } catch { }
            if (!visible || data == null)
                return new Json.Obj().Bit("ok", false).Str("error", "no confirmation panel is showing").ToString();

            string label = FieldStr(data, "Button" + button + "Text");
            var action = FieldAction(data, "Button" + button + "OnClick");

            // Order matters and mirrors SetOnClickCallbacks: close first, then run
            // the callback. The connect-timeout panel's callback is
            // NetworkClient.Cancel, which itself calls GameManager.LeaveGame.
            try { panel.CloseCurrentPanel(); }
            catch (Exception ex) { return new Json.Obj().Bit("ok", false).Str("error", "close failed: " + ex.Message).ToString(); }

            try { action?.Invoke(); }
            catch (Exception ex)
            {
                return new Json.Obj().Bit("ok", false).Str("clickedLabel", label)
                    .Str("error", "callback threw: " + ex.Message).ToString();
            }

            return new Json.Obj().Bit("ok", true).Int("button", button)
                .Str("clickedLabel", label).Bit("hadCallback", action != null).ToString();
        }
    }
}
