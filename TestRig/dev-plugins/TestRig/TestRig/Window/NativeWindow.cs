using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TestRig
{
    /// <summary>
    ///     Read-only window and desktop queries against user32.
    ///
    ///     The standing rule for this plugin is that it must never focus, raise or activate the game
    ///     window (see README.md). That rule is about CHANGING window state. Reading which window
    ///     holds the foreground, and which desktop is receiving input, changes nothing, and it is
    ///     the only way to answer the question a driven client needs answered before an input test:
    ///     "will input land here, and if not, why not".
    ///
    ///     Everything in this file is an observation. Nothing here calls SetForegroundWindow,
    ///     ShowWindow, BringWindowToTop, SetActiveWindow, SetWindowPos, AttachThreadInput,
    ///     SetThreadDesktop or SwitchDesktop, and nothing here may grow to. SwitchDesktop is the
    ///     dangerous neighbour of the calls that ARE here: it is the one that would put a driven
    ///     instance in front of the developer. It is deliberately not imported.
    ///
    ///     Two reasons this file exists.
    ///
    ///     1. <c>UnityEngine.Application.isFocused</c> is not a usable answer. Two background
    ///        instances both reported it true at the same moment on 2026-07-29, which cannot be the
    ///        case, so anything gating on it is misled about whether a test is worth running.
    ///
    ///     2. <c>GetForegroundWindow</c> alone is not enough either. It returns NULL when the
    ///        calling process sits on a desktop that is not receiving input, which is the normal
    ///        state for every instance in this rig. The previous report showed
    ///        <c>foregroundPid: 0</c> and could not tell "I am a background window on the
    ///        developer's desktop" from "I am on a desktop of my own". Those are different
    ///        situations that deserve different responses: the first says another window is in
    ///        front, the second says the isolation is working exactly as intended. Comparing this
    ///        process's own desktop name against the input desktop's name separates them, and that
    ///        comparison is the whole reason for the extra three imports.
    /// </summary>
    internal static class NativeWindow
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        ///     Handle to the desktop this thread runs on. Per the Win32 contract the returned handle
        ///     must NOT be closed, which is why no CloseDesktop follows it.
        /// </summary>
        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        /// <summary>
        ///     Opens the desktop currently receiving user input. Read-only: DESKTOP_READOBJECTS is
        ///     the only access right requested, and the handle is closed immediately. Opening a
        ///     desktop neither switches to it nor makes it visible.
        /// </summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformationW(
            IntPtr hObj, int index, StringBuilder info, int length, out int lengthNeeded);

        private const int UOI_NAME = 2;
        private const uint DESKTOP_READOBJECTS = 0x0001;

        private static int _ownPid = -1;
        internal static string LastError;

        internal static int ProcessId
        {
            get
            {
                if (_ownPid < 0)
                {
                    try { _ownPid = Process.GetCurrentProcess().Id; }
                    catch { _ownPid = 0; }
                }
                return _ownPid;
            }
        }

        // ---- desktop identity ---------------------------------------------------

        /// <summary>Name of the desktop this process runs on, or null when it cannot be read.</summary>
        internal static string OwnDesktopName()
        {
            try
            {
                IntPtr hdesk = GetThreadDesktop(GetCurrentThreadId());
                return hdesk == IntPtr.Zero ? null : NameOf(hdesk);
            }
            catch (Exception ex) { LastError = "own desktop: " + ex.Message; return null; }
        }

        /// <summary>Name of the desktop receiving input, or null when it cannot be read.</summary>
        internal static string InputDesktopName()
        {
            IntPtr hdesk = IntPtr.Zero;
            try
            {
                hdesk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
                return hdesk == IntPtr.Zero ? null : NameOf(hdesk);
            }
            catch (Exception ex) { LastError = "input desktop: " + ex.Message; return null; }
            finally
            {
                if (hdesk != IntPtr.Zero) { try { CloseDesktop(hdesk); } catch { } }
            }
        }

        private static string NameOf(IntPtr hObject)
        {
            var sb = new StringBuilder(256);
            int needed;
            // The length is in BYTES, and the buffer is UTF-16, so it is twice the char capacity.
            if (!GetUserObjectInformationW(hObject, UOI_NAME, sb, sb.Capacity * 2, out needed)) return null;
            return sb.ToString();
        }

        // ---- the report ---------------------------------------------------------

        /// <summary>
        ///     One of five verdicts, each of which a caller should act on differently:
        ///
        ///     <list type="bullet">
        ///       <item>"foreground": this process owns the foreground window. Real OS input would
        ///             land here, and a focus steal FROM here would be visible to the developer.</item>
        ///       <item>"background": another process on the same desktop holds the foreground.</item>
        ///       <item>"otherDesktop": this process is on a desktop that is not receiving input.
        ///             There is no foreground to hold and none to steal. This is the intended state
        ///             for a rig instance, and it is not a warning.</item>
        ///       <item>"noForeground": same desktop, nothing holds the foreground right now.</item>
        ///       <item>"unknown": the query failed; see lastError.</item>
        ///     </list>
        /// </summary>
        internal static string Verdict(out int foregroundPid, out string ownDesktop, out string inputDesktop)
        {
            foregroundPid = 0;
            ownDesktop = OwnDesktopName();
            inputDesktop = InputDesktopName();

            // The desktop question comes first. On a non-active desktop GetForegroundWindow
            // returns NULL, and every conclusion drawn from that NULL is wrong.
            if (!string.IsNullOrEmpty(ownDesktop) && !string.IsNullOrEmpty(inputDesktop) &&
                !string.Equals(ownDesktop, inputDesktop, StringComparison.OrdinalIgnoreCase))
                return "otherDesktop";

            try
            {
                IntPtr hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero)
                {
                    // For a process demonstrably on the input desktop this is genuinely rare. If
                    // the desktop names could not be read, the likelier explanation is that we are
                    // on another desktop and simply could not prove it, so do not claim otherwise.
                    return (ownDesktop == null || inputDesktop == null) ? "unknown" : "noForeground";
                }

                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                foregroundPid = (int)pid;
                return foregroundPid != 0 && foregroundPid == ProcessId ? "foreground" : "background";
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return "unknown";
            }
        }

        /// <summary>
        ///     The full foreground picture. <c>holdsForeground</c> is the field to gate on;
        ///     <c>verdict</c> is what makes a "no" interpretable.
        /// </summary>
        internal static string DescribeJson()
        {
            int foregroundPid;
            string ownDesktop, inputDesktop;
            string verdict = Verdict(out foregroundPid, out ownDesktop, out inputDesktop);

            var o = new Json.Obj();
            o.Str("verdict", verdict);
            o.Bit("holdsForeground", verdict == "foreground");
            o.Int("ownPid", ProcessId);
            // Reported as null rather than 0 when it is not knowable. Zero previously read as a
            // real answer ("nothing is focused anywhere") when it actually meant "wrong desktop,
            // that is not the question to ask here".
            if (verdict == "otherDesktop" || verdict == "unknown") o.Raw("foregroundPid", "null");
            else o.Int("foregroundPid", foregroundPid);
            o.Str("ownDesktop", ownDesktop);
            o.Str("inputDesktop", inputDesktop);
            o.Bit("onInputDesktop", verdict != "otherDesktop" && verdict != "unknown");
            try { o.Bit("unityIsFocused", UnityEngine.Application.isFocused); } catch { }
            o.Str("note", verdict == "otherDesktop"
                ? "This instance runs on its own Win32 desktop, so it holds no foreground and cannot " +
                  "take the developer's. Synthetic input is unaffected: it never depended on focus, " +
                  "only on the cursor gate."
                : null);
            o.Str("lastError", LastError);
            return o.ToString();
        }

        /// <summary>Boolean-only form, for callers that just want "is it us".</summary>
        internal static bool TryIsForeground(out bool isForeground, out int foregroundPid)
        {
            string ownDesktop, inputDesktop;
            string verdict = Verdict(out foregroundPid, out ownDesktop, out inputDesktop);
            isForeground = verdict == "foreground";
            return verdict != "unknown";
        }
    }
}
