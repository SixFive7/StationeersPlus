using System.ComponentModel;
using System.Runtime.InteropServices;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// DesktopProcessLauncher against the real Win32 entry points.
/// </summary>
/// <remarks>
/// Every process these tests launch goes onto a desktop of its own with a name unique to
/// the run, so the suite cannot disturb a live rig's StationeersRig desktop and cannot
/// put a window anywhere the developer can see. The one test that launches nothing at all
/// is the one that proves lpDesktop is genuinely reaching the OS.
/// </remarks>
public sealed class DesktopProcessLauncherTests : IDisposable
{
    private readonly TempDirectory _temp = new("desktop");
    private readonly SystemProcessTable _processes = new();
    private readonly string _desktop = "TestRigTest" + Guid.NewGuid().ToString("N")[..8];

    private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("StationeersRig", @"WinSta0\StationeersRig")]
    [InlineData(@"WinSta0\StationeersRig", @"WinSta0\StationeersRig")]
    public void QualifyDesktop_AddsTheWindowStationExactlyOnce(string input, string expected)
    {
        Assert.Equal(expected, DesktopProcessLauncher.QualifyDesktop(input));
    }

    [Fact]
    public void EnsureDesktop_CreatesTheDesktopAndOpensItAgainWithoutComplaint()
    {
        // "Creates it if absent, opens it if present" is the contract, and start is
        // called once per invocation across many start/stop cycles in one session.
        DesktopProcessLauncher.EnsureDesktop(_desktop);
        DesktopProcessLauncher.EnsureDesktop(_desktop);
    }

    [Fact]
    public async Task Start_CreatesTheDesktopRatherThanSilentlyFallingBackToTheCallers()
    {
        // Measured 2026-08-14: CreateProcessW does NOT fail when lpDesktop names a
        // desktop that does not exist. It succeeds, creates nothing, and puts the process
        // on the CALLER's desktop with nothing reported. So Start creates the desktop
        // itself immediately before the launch, and this is the regression test for that:
        // without it, the name below would still not exist afterwards.
        var name = "TestRigNeverCreated" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(DesktopExists(name), "the probe name must be unused before the launch");

        var commandLine = WindowsCommandLine.Build(CmdExe, "/c", "ping", "-n", "30", "127.0.0.1");

        var pid = DesktopProcessLauncher.Start(
            CmdExe, commandLine, _temp.Path,
            DesktopProcessLauncher.ShowNoActivate,
            DesktopProcessLauncher.QualifyDesktop(name));

        try
        {
            Assert.True(DesktopExists(name), "the launch must have put the process on its own desktop");
        }
        finally
        {
            await _processes.StopAsync((int)pid, TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task Start_LaunchesOntoTheDesktopAndReturnsARealPid()
    {
        DesktopProcessLauncher.EnsureDesktop(_desktop);

        var commandLine = WindowsCommandLine.Build(CmdExe, "/c", "ping", "-n", "30", "127.0.0.1");

        var pid = DesktopProcessLauncher.Start(
            CmdExe,
            commandLine,
            _temp.Path,
            DesktopProcessLauncher.ShowNoActivate,
            DesktopProcessLauncher.QualifyDesktop(_desktop));

        try
        {
            Assert.True(pid > 0);

            var info = _processes.TryGetMatching((int)pid, "cmd");
            Assert.NotNull(info);
            Assert.Equal((int)pid, info.Value.Pid);
        }
        finally
        {
            // Always tear down. A leaked child holds a desktop open.
            await _processes.StopAsync((int)pid, TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public async Task Start_LeavesTheDesktopHeldByTheCHILDAndNotByTheLauncher()
    {
        // The bug this pins, measured on this machine 2026-08-14. A desktop dies when its
        // last HANDLE closes and no window exists on it, and a launching game has neither
        // for its first seconds. Start used to leak its handle and the launcher used to
        // exit, which closed it, which destroyed the desktop under a process still loading
        // DLLs: the game died 0.02 s in with 0xC0000142 (STATUS_DLL_INIT_FAILED), having
        // written no Unity log, no BepInEx log and no crash dump. No client instance had
        // ever booted through this rig.
        //
        // CREATE_NO_WINDOW is what makes this an assertion about the HANDLE. With a console
        // window on the desktop, the window alone would hold it up and the test would pass
        // against either implementation.
        var name = "TestRigLifetime" + Guid.NewGuid().ToString("N")[..8];
        var commandLine = WindowsCommandLine.Build(CmdExe, "/c", "ping", "-n", "30", "127.0.0.1");

        var pid = DesktopProcessLauncher.Start(
            CmdExe, commandLine, _temp.Path,
            DesktopProcessLauncher.ShowNoActivate,
            DesktopProcessLauncher.QualifyDesktop(name),
            DesktopProcessLauncher.CreateNoWindow);

        try
        {
            // Start closed its own handle before returning, and the child has no window, so
            // the only thing this can be is the handle the child inherited.
            Assert.True(
                DesktopExists(name),
                "the child must hold a desktop handle of its own once Start has returned");
        }
        finally
        {
            await _processes.StopAsync((int)pid, TimeSpan.FromSeconds(20));
        }

        // And the other half: it dies WITH the child. Under the old implementation the
        // launcher's leaked handle kept the desktop alive here for the life of the process,
        // so this is the assertion that tells the two apart.
        Assert.False(
            await DesktopGoneWithin(name, TimeSpan.FromSeconds(10)),
            $"desktop '{name}' outlived the only process on it, so a handle to it has leaked");
    }

    [Fact]
    public async Task Start_DoesNotHandTheChildTheLaunchersOwnStdHandles()
    {
        // bInheritHandles is all-or-nothing, so buying the desktop handle above risks
        // handing over the caller's console or pipe as well. That is not hypothetical: it
        // is the 907-second block from the other direction, a GUI child holding the
        // shell's stdout open long after the launcher printed its result and exited.
        //
        // Asserted by observation rather than by reading the flag: the child writes to its
        // own stdout, and if it had inherited ours the bytes would land in the file this
        // test's own stdout is redirected to. It cannot, because a redirect that reaches
        // the child is exactly what must not happen, so the assertion is that the child
        // starts and stays detached while the launcher's handles keep their original
        // inheritability.
        var name = "TestRigStdIo" + Guid.NewGuid().ToString("N")[..8];
        var before = StdHandleInheritFlags();

        var commandLine = WindowsCommandLine.Build(CmdExe, "/c", "ping", "-n", "30", "127.0.0.1");

        var pid = DesktopProcessLauncher.Start(
            CmdExe, commandLine, _temp.Path,
            DesktopProcessLauncher.ShowNoActivate,
            DesktopProcessLauncher.QualifyDesktop(name),
            DesktopProcessLauncher.CreateNoWindow);

        try
        {
            // Restored exactly, or the NEXT launch through here inherits nothing it should
            // and every later one silently changes behaviour.
            Assert.Equal(before, StdHandleInheritFlags());
        }
        finally
        {
            await _processes.StopAsync((int)pid, TimeSpan.FromSeconds(20));
        }
    }

    [Fact]
    public void Start_NamesTheExecutableWhenItIsMissing()
    {
        var missing = _temp.File("rocketstation.exe");
        var commandLine = WindowsCommandLine.Build(missing);

        var ex = Assert.Throws<Win32Exception>(() =>
            DesktopProcessLauncher.Start(missing, commandLine, _temp.Path, DesktopProcessLauncher.ShowNoActivate, null));

        Assert.Equal(2, ex.NativeErrorCode);
        Assert.Contains(missing, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_RejectsAnEmptyExecutablePath()
    {
        Assert.Throws<ArgumentException>(() =>
            DesktopProcessLauncher.Start("", "x", _temp.Path, DesktopProcessLauncher.ShowNoActivate, null));
    }

    [Fact]
    public void ShowNoActivate_IsFour()
    {
        // SW_SHOWNOACTIVATE. Belt and braces beside the desktop, and wrong by one is the
        // kind of constant that goes unnoticed because the desktop covers for it.
        Assert.Equal(4, DesktopProcessLauncher.ShowNoActivate);
    }

    /// <summary>
    /// Whether a desktop of that name exists on the current window station.
    /// </summary>
    /// <remarks>
    /// OpenDesktopW and CloseDesktop are read-only enquiries. Neither switches to a
    /// desktop, moves a thread onto one, or touches a window, so neither is on the
    /// forbidden list the guard suite enforces.
    /// </remarks>
    private static bool DesktopExists(string name)
    {
        const uint DesktopReadObjects = 0x0001;

        var handle = OpenDesktopW(name, 0, false, DesktopReadObjects);
        if (handle == IntPtr.Zero) return false;

        CloseDesktop(handle);
        return true;
    }

    /// <summary>
    /// Waits for a desktop to disappear, so the assertion is not racing process teardown.
    /// </summary>
    /// <returns>True while it is still there, which is the failing answer.</returns>
    private static async Task<bool> DesktopGoneWithin(string name, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline)
        {
            if (!DesktopExists(name)) return false;
            await Task.Delay(100);
        }

        return DesktopExists(name);
    }

    /// <summary>The inherit flag on each std handle, as a stable string to compare.</summary>
    private static string StdHandleInheritFlags()
    {
        var parts = new List<string>(3);

        foreach (var id in new[] { -10, -11, -12 })
        {
            var handle = GetStdHandle(id);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) parts.Add($"{id}:none");
            else if (!GetHandleInformation(handle, out var flags)) parts.Add($"{id}:unreadable");
            else parts.Add($"{id}:{flags & 1}");
        }

        return string.Join(",", parts);
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr OpenDesktopW(
        string lpszDesktop,
        int dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit,
        uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr hDesktop);
}
