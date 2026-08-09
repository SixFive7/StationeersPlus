using System;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    /// Forces a driven instance into a small window, and keeps it there.
    ///
    /// Why this cannot be done from the command line. Unity's own
    /// <c>-screen-fullscreen 0 -screen-width W -screen-height H</c> are honoured by the
    /// native player when it creates the window, and are then thrown away by the game
    /// a moment later, twice:
    ///
    ///   Settings.LoadSettings()      Screen.SetResolution(W, H, CurrentData.FullScreen, RefreshRate)
    ///   Settings.ApplyVideoSettings()  the same call again, from the tail of GameManager.Start()
    ///
    /// Both read <c>Settings.CurrentData</c>, which is deserialized from the instance's own
    /// <c>setting.xml</c> where <c>&lt;FullScreen&gt;</c> defaults to <c>true</c>. Neither call is
    /// guarded by IsBatchMode. So the launch flags lose to the game's own saved
    /// preference every time, and the instance comes up fullscreen on the developer's
    /// desktop.
    ///
    /// The fix is therefore to correct the source the game reads rather than to fight
    /// the symptom: rewrite <c>CurrentData.FullScreen / ScreenWidth / ScreenHeight</c> in
    /// memory before the game applies them, and re-assert afterwards. The game's own
    /// <c>SetResolution</c> call then asks for windowed mode, which means no extra
    /// resolution write of ours and no behaviour Unity would not already have produced
    /// for a player who simply prefers a window.
    ///
    /// Deliberately NOT done here: writing to <c>HKCU\Software\Rocketwerkz\rocketstation</c>.
    /// That key is per-Windows-user and shared with the developer's own client, so
    /// reaching into it would change their preference, not ours. The values there are
    /// Unity's, written natively; this file never touches them.
    ///
    /// Everything is config-gated and off by default, so a normal client that happens to
    /// have this plugin installed is completely unaffected.
    /// </summary>
    internal static class WindowMode
    {
        internal static bool ForceWindowed;
        internal static int Width = 800;
        internal static int Height = 600;

        internal static long Asserts;
        internal static long DataRewrites;
        internal static string LastAssertReason;
        internal static string LastError;

        private static Type _settingsType;
        private static bool _settingsTypeResolved;
        private static int _lastTickFrame = -1;

        /// <summary>
        /// The game's settings facade.
        ///
        /// Resolved by scanning rather than by name, because <c>Settings</c> is a
        /// common enough class name that several loaded assemblies carry one, and it
        /// sits in the GLOBAL namespace here, so <c>AccessTools.TypeByName("Settings")</c>
        /// can hand back somebody else's. The identifying marks are a static
        /// <c>CurrentData</c> field and a static <c>LoadSettings</c> method; nothing else
        /// in the process has both.
        /// </summary>
        internal static Type SettingsType
        {
            get
            {
                if (_settingsTypeResolved) return _settingsType;
                _settingsTypeResolved = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        Type[] types;
                        try { types = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                        catch { continue; }

                        foreach (var t in types)
                        {
                            if (t == null || t.Name != "Settings") continue;
                            if (AccessTools.Field(t, "CurrentData") == null) continue;
                            if (AccessTools.Method(t, "LoadSettings") == null) continue;
                            _settingsType = t;
                            return _settingsType;
                        }
                    }
                    LastError = "no type named Settings with a static CurrentData and LoadSettings";
                }
                catch (Exception ex) { LastError = "type resolve: " + ex.Message; }
                return _settingsType;
            }
        }

        /// <summary>
        /// Rewrites the three fields the game is about to read. Safe to call at any
        /// time and any number of times; it is a plain field assignment.
        /// </summary>
        internal static void RewriteCurrentData()
        {
            if (!ForceWindowed) return;
            try
            {
                Type t = SettingsType;
                if (t == null) { LastError = "Settings type not found"; return; }

                FieldInfo fCurrent = AccessTools.Field(t, "CurrentData");
                object data = fCurrent == null ? null : fCurrent.GetValue(null);
                if (data == null) { LastError = "Settings.CurrentData is null"; return; }

                Type dt = data.GetType();

                FieldInfo fFull = AccessTools.Field(dt, "FullScreen");
                if (fFull != null && fFull.FieldType == typeof(bool)) fFull.SetValue(data, false);

                // ScreenWidth / ScreenHeight are declared as string, not int, and
                // ApplyVideoSettings parses them with a bare int.Parse. A non-numeric
                // value there throws inside GameManager.Start(), so write digits only.
                SetDimension(dt, data, "ScreenWidth", Width);
                SetDimension(dt, data, "ScreenHeight", Height);

                DataRewrites++;
            }
            catch (Exception ex) { LastError = "rewrite: " + ex.Message; }
        }

        private static void SetDimension(Type dt, object data, string field, int value)
        {
            FieldInfo f = AccessTools.Field(dt, field);
            if (f == null) return;
            if (f.FieldType == typeof(string)) f.SetValue(data, value.ToString(CultureInfo.InvariantCulture));
            else if (f.FieldType == typeof(int)) f.SetValue(data, value);
        }

        /// <summary>
        /// Rewrites the settings and, if the window still does not match, asks Unity
        /// directly. The direct call is the fallback path: normally the game's own
        /// SetResolution has already done it off the corrected CurrentData.
        /// </summary>
        internal static void Assert(string why)
        {
            if (!ForceWindowed) return;
            try
            {
                RewriteCurrentData();

                if (Screen.fullScreen || Screen.width != Width || Screen.height != Height)
                {
                    Screen.SetResolution(Width, Height, false);
                    Asserts++;
                    LastAssertReason = why;
                }
            }
            catch (Exception ex) { LastError = "assert: " + ex.Message; }
        }

        /// <summary>
        /// Cheap per-frame guard, driven from the existing frame pump. The game only
        /// applies its settings twice during boot, but a mod, an options panel or an
        /// alt-enter can move the window later, and a driven instance going fullscreen
        /// mid-session over the developer's desktop is exactly the failure this exists
        /// to prevent. One Screen read per second is not worth optimising away.
        /// </summary>
        internal static void Tick()
        {
            if (!ForceWindowed) return;
            int frame = Time.frameCount;
            if (frame - _lastTickFrame < 60) return;
            _lastTickFrame = frame;
            Assert("frame pump");
        }

        internal static string DescribeJson()
        {
            var o = new Json.Obj();
            o.Bit("forceWindowed", ForceWindowed);
            o.Int("configuredWidth", Width);
            o.Int("configuredHeight", Height);
            try
            {
                o.Int("screenWidth", Screen.width);
                o.Int("screenHeight", Screen.height);
                o.Bit("screenFullScreen", Screen.fullScreen);
                o.Str("screenFullScreenMode", Screen.fullScreenMode.ToString());
            }
            catch { }
            o.Int("setResolutionCalls", Asserts);
            o.Int("settingsRewrites", DataRewrites);
            o.Str("lastAssertReason", LastAssertReason);
            o.Str("lastError", LastError);
            o.Bit("settingsTypeFound", SettingsType != null);
            return o.ToString();
        }
    }

    /// <summary>
    /// First of the game's two resolution applications, from
    /// <c>WorldManager.ManagerAwake</c>. A postfix is enough here: the settings object
    /// has just been deserialized, so correcting it and re-asserting immediately means
    /// at most a few frames of the wrong window size during boot.
    /// </summary>
    [HarmonyPatch]
    internal static class SettingsLoadWindowPatch
    {
        private static MethodBase Resolve()
        {
            Type t = WindowMode.SettingsType;
            return t == null ? null : AccessTools.Method(t, "LoadSettings");
        }

        internal static bool Prepare() => Resolve() != null;
        internal static MethodBase TargetMethod() => Resolve();

        internal static void Postfix()
        {
            try { WindowMode.Assert("Settings.LoadSettings"); } catch { }
        }
    }

    /// <summary>
    /// Second application, the last statement of <c>GameManager.Start()</c>. The prefix
    /// is the important half: correcting <c>CurrentData</c> before the original body reads
    /// it means the game's own <c>Screen.SetResolution</c> asks for a window, so no
    /// separate call of ours is needed and there is no fullscreen frame in between.
    /// </summary>
    [HarmonyPatch]
    internal static class SettingsApplyVideoWindowPatch
    {
        private static MethodBase Resolve()
        {
            Type t = WindowMode.SettingsType;
            return t == null ? null : AccessTools.Method(t, "ApplyVideoSettings");
        }

        internal static bool Prepare() => Resolve() != null;
        internal static MethodBase TargetMethod() => Resolve();

        internal static void Prefix()
        {
            try { WindowMode.RewriteCurrentData(); } catch { }
        }

        internal static void Postfix()
        {
            try { WindowMode.Assert("Settings.ApplyVideoSettings"); } catch { }
        }
    }
}
