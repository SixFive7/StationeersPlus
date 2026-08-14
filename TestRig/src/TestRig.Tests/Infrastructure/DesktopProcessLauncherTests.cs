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
