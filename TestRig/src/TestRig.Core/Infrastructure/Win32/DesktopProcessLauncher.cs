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

    /// <summary>GENERIC_ALL, the access CreateDesktopW is asked for.</summary>
    private const uint GenericAll = 0x10000000;

    /// <summary>
    /// Creates the desktop if it does not exist, opens it if it does.
    /// </summary>
    /// <remarks>
    /// The handle is deliberately not stored and deliberately not closed. A desktop
    /// object lives as long as a process is running on it and disappears by itself
    /// afterwards, so there is nothing to clean up and no teardown step to forget.
    ///
    /// Do not wrap this in a SafeHandle that closes on dispose. It would be a behaviour
    /// change for no gain: the instance holds its own reference either way, and the
    /// leaked handle is what makes the lifetime rule "the desktop dies with its last
    /// process" rather than "the desktop dies when the launcher exits".
    ///
    /// Nothing switches to this desktop. There is no CloseDesktop import and no
    /// SwitchDesktop import, and there never will be.
    /// </remarks>
    public static void EnsureDesktop(string desktopName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);

        var handle = CreateDesktopW(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        var error = Marshal.GetLastWin32Error();

        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(error,
                $"CreateDesktopW could not create or open the desktop '{desktopName}': {error} " +
                $"({new Win32Exception(error).Message}). Without it an instance would launch onto the " +
                "developer's own desktop and take their foreground.");
        }
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
    public static uint Start(
        string exePath,
        string commandLine,
        string? workingDirectory,
        short showWindow,
        string? desktop)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        ArgumentNullException.ThrowIfNull(commandLine);

        var commandLineBuffer = IntPtr.Zero;
        var desktopBuffer = IntPtr.Zero;

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
                EnsureDesktop(BareDesktopName(desktop));

                desktopBuffer = Marshal.StringToHGlobalUni(desktop);
                startup.lpDesktop = desktopBuffer;
            }

            var created = CreateProcessW(
                exePath,
                commandLineBuffer,
                IntPtr.Zero,
                IntPtr.Zero,
                bInheritHandles: false,
                dwCreationFlags: 0,
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
        }
    }

    // ---- the P/Invoke surface. Three imports, and nothing else. ----------
    //
    // DllImport rather than the source-generated LibraryImport: LibraryImport emits
    // unsafe code and so needs AllowUnsafeBlocks in the project file. Both structs here
    // are blittable and the only marshalling is two UTF-16 strings, which NativeAOT
    // generates a static stub for under either attribute, so the AOT story is unchanged.
    // The type stays partial so the swap is a three-attribute change.

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateDesktopW(
        string lpszDesktop,
        IntPtr lpszDevice,
        IntPtr pDevmode,
        int dwFlags,
        uint dwDesiredAccess,
        IntPtr lpsa);

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
}
