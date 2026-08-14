// =============================================================================
// THE FOCUS CONSTRAINT. Read this before touching anything in this file.
//
// The following are never imported, anywhere in TestRig/src/, and there is a test
// that fails the build if any of them appear in code:
//
//     SwitchDesktop, SetForegroundWindow, ShowWindow, SetWindowPos,
//     AttachThreadInput, BringWindowToTop, SetActiveWindow, SetThreadDesktop
//
// Why: instances run on a Win32 desktop that is created and never switched to. A
// window on another desktop cannot appear on the developer's screen and cannot reach
// their foreground or their input queue at all. Measured, sampling the foreground
// every 3 seconds for two minutes: launching with SW_SHOWNOACTIVATE alone stole focus
// on 40 samples out of 40, and the foreground never came back; launching onto a
// separate desktop stole it on 0 out of 55, through a full boot and an entire
// acceptance test with two instances running.
//
// SW_SHOWNOACTIVATE alone fails because wShowWindow only governs the first
// ShowWindow(SW_SHOWDEFAULT), and Unity calls ShowWindow itself once its window
// exists. The desktop is the mechanism; the show flag is belt and braces.
//
// A desktop is destroyed when its last HANDLE closes and no window exists on it. Both
// halves of that matter, because a launching game has neither for the first seconds of
// its life: no window yet, and no handle unless it is given one. So the child is handed
// an inheritable handle of its own. Do not "tidy" that back into a plain
// CreateDesktopW + bInheritHandles: false; that is the bug this file was fixed for, and
// it presents as the game dying 0.02 s in with 0xC0000142 and writing nothing anywhere.
//
// The constraint is not a preference. An agent taking the foreground interrupted the
// developer mid-work, and it has stood ever since. This tool runs unattended while
// they are asleep.
// =============================================================================

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// Launches a process onto a named Win32 desktop, without ever switching to it.
/// </summary>
/// <remarks>
/// Namespace note: this file sits under Infrastructure/Win32/ but stays in the
/// Infrastructure namespace, so a consumer needs one using for the whole seam layer.
///
/// Why this is a P/Invoke and must stay one: ProcessStartInfo cannot express either
/// lpDesktop or wShowWindow when UseShellExecute is false. There is no managed route to
/// either field. Anyone tempted to simplify this back to Process.Start would be removing
/// the mechanism described at the top of this file, and the failure would not show up in
/// a test: it shows up as the developer's editor losing focus.
/// </remarks>
public static partial class DesktopProcessLauncher
{
    /// <summary>The desktop the rig runs instances on.</summary>
    public const string DefaultDesktopName = "StationeersRig";

    /// <summary>
    /// SW_SHOWNOACTIVATE. Belt and braces alongside the desktop, not the mechanism.
    /// </summary>
    public const short ShowNoActivate = 4;

    /// <summary>STARTF_USESHOWWINDOW. Makes CreateProcessW read wShowWindow at all.</summary>
    private const int StartfUseShowWindow = 0x00000001;

    /// <summary>
    /// CREATE_NO_WINDOW. For a CONSOLE child that must not attach to the caller's console.
    /// </summary>
    /// <remarks>
    /// Only the dedicated server's host wrapper needs this, because the wrapper is this same
    /// console binary re-invoked. A game client is a GUI process and gets no console either
    /// way, so passing it there would change nothing.
    /// </remarks>
    public const uint CreateNoWindow = 0x08000000;

    /// <summary>GENERIC_ALL, the access CreateDesktopW is asked for.</summary>
    private const uint GenericAll = 0x10000000;

    /// <summary>HANDLE_FLAG_INHERIT.</summary>
    private const uint HandleFlagInherit = 0x00000001;

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    /// <summary>
    /// Creates the desktop if it does not exist, opens it if it does.
    /// </summary>
    /// <remarks>
    /// A pre-flight, and nothing more: it proves the desktop can be created before any
    /// instance is launched, so a failure is reported once rather than N times.
    ///
    /// This handle IS leaked, deliberately, and that is all it is good for: it holds the
    /// desktop up for as long as THIS process runs, which covers the launch loop. It does
    /// not outlive the launcher, so it is not what keeps an instance alive. See
    /// <see cref="Start"/> for the handle that does.
    /// </remarks>
    public static void EnsureDesktop(string desktopName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);
        CreateDesktopOrThrow(desktopName, inheritable: false);
    }

    /// <summary>
    /// CreateDesktopW, with the error message the failure actually needs.
    /// </summary>
    /// <param name="inheritable">
    /// Whether the returned handle can be inherited by a child launched with
    /// <c>bInheritHandles = TRUE</c>. See <see cref="Start"/> for why that matters.
    /// </param>
    private static IntPtr CreateDesktopOrThrow(string bareName, bool inheritable)
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = Unsafe.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = inheritable ? 1 : 0,
        };

        var handle = CreateDesktopW(bareName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, ref attributes);
        var error = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(error,
                $"CreateDesktopW could not create or open the desktop '{bareName}': {error} " +
                $"({new Win32Exception(error).Message}). Without it an instance would launch onto the " +
                "developer's own desktop and take their foreground.");
        }

        return handle;
    }

    /// <summary>
    /// Qualifies a bare desktop name for STARTUPINFO.lpDesktop.
    /// </summary>
    /// <remarks>
    /// CreateDesktopW takes the bare name; lpDesktop wants the window station too. Two
    /// different forms of the same string, one line apart in the caller, is exactly the
    /// kind of thing that ends up wrong and then silently launches onto the default
    /// desktop, so it is spelled once here.
    /// </remarks>
    public static string QualifyDesktop(string desktopName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);
        return desktopName.Contains('\\') ? desktopName : @"WinSta0\" + desktopName;
    }

    /// <summary>The desktop name without its window station, which is what CreateDesktopW takes.</summary>
    private static string BareDesktopName(string qualified)
    {
        var separator = qualified.LastIndexOf('\\');
        return separator >= 0 ? qualified[(separator + 1)..] : qualified;
    }

    /// <summary>
    /// Starts a process on the given desktop and returns its pid.
    /// </summary>
    /// <param name="exePath">Full path to the executable. Becomes lpApplicationName.</param>
    /// <param name="commandLine">
    /// The complete command line INCLUDING argv[0]. Build it with
    /// <see cref="WindowsCommandLine.Build"/>; an unquoted join is what broke every lock
    /// the playtest harness ever took.
    /// </param>
    /// <param name="workingDirectory">
    /// Becomes lpCurrentDirectory. The rig passes data/&lt;instance&gt;, not the game
    /// tree, because imgui.ini and output_log.txt are resolved against it.
    /// </param>
    /// <param name="showWindow">wShowWindow. <see cref="ShowNoActivate"/> in practice.</param>
    /// <param name="desktop">
    /// Fully qualified, as WinSta0\Name, and created if it does not exist yet. Null or
    /// empty launches on the caller's own desktop, which is a debugging-only path: an
    /// instance there WILL take the foreground.
    /// </param>
    /// <param name="creationFlags">
    /// dwCreationFlags. <see cref="CreateNoWindow"/> for a console child; 0 otherwise.
    /// </param>
    public static uint Start(
        string exePath,
        string commandLine,
        string? workingDirectory,
        short showWindow,
        string? desktop,
        uint creationFlags = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(commandLine);

        var commandLineBuffer = IntPtr.Zero;
        var desktopBuffer = IntPtr.Zero;
        var desktopHandle = IntPtr.Zero;

        try
        {
            // CreateProcessW writes into lpCommandLine, so it cannot be a marshalled
            // string: it null-terminates the program token in place. A managed string is
            // interned and shared, so letting the OS write into one would corrupt every
            // other reference to it. The +64 headroom matches the original.
            var chars = new char[commandLine.Length + 64];
            commandLine.CopyTo(0, chars, 0, commandLine.Length);
            commandLineBuffer = Marshal.AllocHGlobal(chars.Length * sizeof(char));
            Marshal.Copy(chars, 0, commandLineBuffer, chars.Length);

            var startup = default(STARTUPINFOW);
            startup.cb = Unsafe.SizeOf<STARTUPINFOW>();

            // dwFlags carries STARTF_USESHOWWINDOW only. Notably NOT STARTF_USESTDHANDLES:
            // stdio is not redirected, so a client instance has no stdin anyone can reach,
            // which is why `send` is refused against the client half and answered over
            // HTTP instead.
            //
            // That omission is also half of what makes a launch here DETACH. Process.Start
            // cannot express it: with UseShellExecute = false the BCL always sets
            // STARTF_USESTDHANDLES to the caller's own handles and always passes
            // bInheritHandles: true, so a child started that way holds the caller's stdout
            // pipe open for its whole life. Measured 2026-08-14: `start --target server`
            // printed its result and exited, and the shell capturing it blocked for 907
            // seconds, returning the instant the SERVER stopped. Anything that starts a
            // detached process comes through here.
            //
            // The other half is SuppressStdHandleInheritance below. bInheritHandles is TRUE
            // for a desktop launch now, so detachment can no longer rest on that flag and
            // rests on there being nothing inheritable to take instead.
            startup.dwFlags = StartfUseShowWindow;
            startup.wShowWindow = showWindow;

            if (!string.IsNullOrEmpty(desktop))
            {
                // Measured on this machine, 2026-08-14: CreateProcessW does NOT fail when
                // lpDesktop names a desktop that does not exist. It returns success, the
                // desktop is not created, and the process lands on the CALLER's desktop
                // with nothing reported anywhere. That is the exact catastrophe this
                // mechanism exists to prevent, reached by a typo.
                //
                // So the desktop is created here, immediately before the launch, rather
                // than trusting the caller to have called EnsureDesktop with a name that
                // matches. CreateDesktopW opens an existing desktop, so doing it twice
                // costs one call.
                //
                // INHERITABLE, and this is the whole reason a client instance boots at all.
                // A desktop is destroyed when its last HANDLE closes and no window exists on
                // it yet, and for the first seconds of a launch the child has neither. The
                // launcher used to leak this handle and exit, which closed it, which
                // destroyed the desktop out from under a process that was still loading its
                // DLLs. Measured 2026-08-14 on this machine, holding hProcess to read the
                // code: the child died 0.02 s in with 0xC0000142, STATUS_DLL_INIT_FAILED, no
                // Unity log file created at all, nothing in the event log. Holding the handle
                // for 2 s instead was enough to survive; so was launching while another
                // instance already had a window there. Handing the child a handle of its own
                // removes the race rather than widening it: the desktop now genuinely dies
                // with its last instance, which is what this file always claimed.
                desktopHandle = CreateDesktopOrThrow(BareDesktopName(desktop), inheritable: true);

                desktopBuffer = Marshal.StringToHGlobalUni(desktop);
                startup.lpDesktop = desktopBuffer;
            }

            // Only a desktop launch needs inheritance, so only a desktop launch pays for it.
            var inheritHandles = desktopHandle != IntPtr.Zero;

            using var suppressed = inheritHandles
                ? SuppressStdHandleInheritance()
                : default;

            var created = CreateProcessW(
                exePath,
                commandLineBuffer,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: inheritHandles,
                dwCreationFlags: creationFlags,
                // Null means inherit the launcher's environment block, which is how
                // STATIONEERS_CLIENTRIG_MANIFEST reaches the instance.
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                ref startup,
                out var info);

            var error = Marshal.GetLastWin32Error();

            if (!created)
            {
                throw new Win32Exception(error,
                    $"""
                     CreateProcessW failed with {error} ({new Win32Exception(error).Message}).
                         exe     : {exePath}
                         workdir : {workingDirectory ?? "(inherited)"}
                         desktop : {desktop ?? "(the caller's own)"}
                     """);
            }

            // The launcher tracks the process by pid file, not by handle, so both handles
            // are closed immediately. Holding hProcess would keep a zombie entry alive
            // after the game exits and make every liveness check answer wrong.
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);

            return info.dwProcessId;
        }
        finally
        {
            if (commandLineBuffer != IntPtr.Zero) Marshal.FreeHGlobal(commandLineBuffer);
            if (desktopBuffer != IntPtr.Zero) Marshal.FreeHGlobal(desktopBuffer);

            // Ours is closed on the way out, every time, success or failure. The child's
            // inherited copy is what holds the desktop up now, so there is nothing to leak
            // and no teardown step to forget. A failed launch leaves no desktop behind
            // either, which is the correct outcome: nothing is running on it.
            if (desktopHandle != IntPtr.Zero) CloseDesktop(desktopHandle);
        }
    }

    /// <summary>
    /// Serialises the std-handle mutation below, which is process-global.
    /// </summary>
    private static readonly Lock StdHandleGate = new();

    /// <summary>
    /// Clears HANDLE_FLAG_INHERIT on stdin, stdout and stderr for the duration of a launch,
    /// and restores exactly what was there before.
    /// </summary>
    /// <remarks>
    /// <para>
    /// bInheritHandles is all-or-nothing: it hands the child EVERY inheritable handle in
    /// this process, not the one we care about. The one we care about is the desktop. The
    /// ones that would come along for the ride are the caller's console or pipe handles,
    /// and a GUI child holding the shell's stdout pipe is precisely the 907-second block
    /// measured above, arrived at from the other direction. Measured 2026-08-14 without
    /// this suppression: the game inherited stdout and dumped Unity's entire boot
    /// configuration into the launcher's own output.
    /// </para>
    /// <para>
    /// PROC_THREAD_ATTRIBUTE_HANDLE_LIST is the usual way to inherit exactly one handle and
    /// is NOT usable here: it filters the kernel handle table, and a desktop is a USER
    /// object, which lives in a different table and can only be inherited through
    /// SECURITY_ATTRIBUTES.bInheritHandle. Narrowing what is inheritable is therefore the
    /// only route, so it is done to the three handles that are inheritable by default.
    /// </para>
    /// </remarks>
    private static StdHandleInheritanceScope SuppressStdHandleInheritance() => new(StdHandleGate);

    /// <summary>Restores the std handles' inherit flags, and releases the gate.</summary>
    internal readonly struct StdHandleInheritanceScope : IDisposable
    {
        private static readonly int[] StdHandleIds = [StdInputHandle, StdOutputHandle, StdErrorHandle];

        private readonly Lock? _gate;
        private readonly IntPtr[]? _restore;

        internal StdHandleInheritanceScope(Lock gate)
        {
            _gate = gate;
            gate.Enter();

            var restore = new IntPtr[StdHandleIds.Length];

            for (var i = 0; i < StdHandleIds.Length; i++)
            {
                var handle = GetStdHandle(StdHandleIds[i]);

                // 0 is "this process has no such handle"; -1 is INVALID_HANDLE_VALUE. Neither
                // is a handle, and SetHandleInformation on either fails rather than helping.
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) continue;
                if (!GetHandleInformation(handle, out var flags)) continue;
                if ((flags & HandleFlagInherit) == 0) continue;

                if (SetHandleInformation(handle, HandleFlagInherit, 0)) restore[i] = handle;
            }

            _restore = restore;
        }

        public void Dispose()
        {
            if (_restore is not null)
            {
                foreach (var handle in _restore)
                {
                    if (handle != IntPtr.Zero) SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit);
                }
            }

            _gate?.Exit();
        }
    }

    // ---- the P/Invoke surface. Seven imports, and nothing else. ----------
    //
    // DllImport rather than the source-generated LibraryImport: LibraryImport emits
    // unsafe code and so needs AllowUnsafeBlocks in the project file. Every struct here
    // is blittable and the only marshalling is two UTF-16 strings, which NativeAOT
    // generates a static stub for under either attribute, so the AOT story is unchanged.
    // The type stays partial so the swap is a three-attribute change.
    //
    // CloseDesktop is here now and was once ruled out. The reasoning that ruled it out
    // ("the desktop dies with its last process, so there is nothing to close") was the bug:
    // it died with the LAUNCHER. Closing our own copy is correct precisely because the
    // child now holds one. SwitchDesktop is a different call and is still never imported.

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateDesktopW(
        string lpszDesktop,
        IntPtr lpszDevice,
        IntPtr pDevmode,
        int dwFlags,
        uint dwDesiredAccess,
        ref SECURITY_ATTRIBUTES lpsa);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        IntPtr lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// STARTUPINFOW, with the three string fields as raw pointers.
    /// </summary>
    /// <remarks>
    /// lpReserved, lpDesktop and lpTitle are pointers rather than strings so the struct
    /// stays blittable, which is what LibraryImport needs to generate a marshaller with
    /// no reflection in it. The one that carries meaning, lpDesktop, is allocated and
    /// freed by the caller.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    /// <summary>
    /// SECURITY_ATTRIBUTES, carried for one field: bInheritHandle.
    /// </summary>
    /// <remarks>
    /// lpSecurityDescriptor stays null, so the desktop keeps the default DACL it had when
    /// this was passed as a null pointer. Only inheritability changes.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }
}
