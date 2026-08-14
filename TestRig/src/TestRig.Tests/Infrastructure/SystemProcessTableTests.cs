using System.Diagnostics;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// SystemProcessTable against real processes.
/// </summary>
/// <remarks>
/// Every child process here is started with UseShellExecute false and CreateNoWindow
/// true, so the suite itself cannot take the developer's foreground.
/// </remarks>
public sealed class SystemProcessTableTests
{
    private readonly SystemProcessTable _table = new();

    [Fact]
    public void TryGet_DescribesTheCurrentProcess()
    {
        using var self = Process.GetCurrentProcess();

        var info = _table.TryGet(Environment.ProcessId);

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessId, info.Value.Pid);
        Assert.Equal(self.ProcessName, info.Value.ImageName);
    }

    [Fact]
    public void TryGet_CarriesAStartTimeThatIsActuallyRead()
    {
        // This field is the whole answer to pid reuse: the caller records it when it
        // writes the pid file and compares it on the way back. A record carrying a
        // default timestamp would look valid and prove nothing.
        using var self = Process.GetCurrentProcess();
        var expected = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);

        var info = _table.TryGet(Environment.ProcessId);

        Assert.NotNull(info);
        Assert.NotEqual(default, info.Value.StartTimeUtc);
        Assert.Equal(expected, info.Value.StartTimeUtc);
        Assert.True(info.Value.StartTimeUtc <= DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryGet_RejectsPidsThatCannotBeARigProcess(int pid)
    {
        // 0 is the System Idle Process and is also what an empty or corrupt pid file
        // parses to.
        Assert.Null(_table.TryGet(pid));
    }

    [Fact]
    public async Task TryGet_RejectsAStalePid()
    {
        var pid = await RunAndWaitForExitAsync();

        Assert.Null(_table.TryGet(pid));
        Assert.Null(_table.TryGetMatching(pid, "cmd"));
    }

    [Fact]
    public void TryGetMatching_RejectsALivePidBelongingToAnotherImage()
    {
        // The dedicated server half used a bare Get-Process -Id with no image check, so
        // a recycled pid made status report a dead server as up.
        Assert.Null(_table.TryGetMatching(Environment.ProcessId, "rocketstation"));
    }

    [Fact]
    public void TryGetMatching_AcceptsTheRightImage()
    {
        using var self = Process.GetCurrentProcess();

        var info = _table.TryGetMatching(Environment.ProcessId, self.ProcessName);

        Assert.NotNull(info);
        Assert.Equal(Environment.ProcessId, info.Value.Pid);
    }

    [Fact]
    public void TryGetMatching_IsCaseInsensitiveAndToleratesAnExeSuffix()
    {
        using var self = Process.GetCurrentProcess();

        Assert.NotNull(_table.TryGetMatching(Environment.ProcessId, self.ProcessName.ToUpperInvariant()));

        // Passing "rocketstation.exe" to GetProcessesByName returns nothing at all,
        // which is a confidently wrong "not running" rather than a failure.
        Assert.NotNull(_table.TryGetMatching(Environment.ProcessId, self.ProcessName + ".exe"));
    }

    [Fact]
    public void FindByImage_FindsThisProcessAndNothingForAnUnknownImage()
    {
        using var self = Process.GetCurrentProcess();

        var found = _table.FindByImage(self.ProcessName);

        Assert.Contains(found, p => p.Pid == Environment.ProcessId);
        Assert.All(found, p => Assert.NotEqual(default, p.StartTimeUtc));

        Assert.Empty(_table.FindByImage("no-such-image-a1b2c3"));
    }

    [Fact]
    public async Task StopAsync_ReportsSuccessForAPidThatIsAlreadyGone()
    {
        var pid = await RunAndWaitForExitAsync();

        Assert.True(await _table.StopAsync(pid, TimeSpan.FromSeconds(5)));
        Assert.True(await _table.StopAsync(0, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopAsync_KillsALiveProcessAndWaitsForIt()
    {
        using var child = StartLongRunningChild();
        var pid = child.Id;

        Assert.NotNull(_table.TryGet(pid));

        var stopped = await _table.StopAsync(pid, TimeSpan.FromSeconds(30));

        Assert.True(stopped);
        Assert.True(child.HasExited);
        Assert.Null(_table.TryGet(pid));
    }

    /// <summary>Starts a child, waits for it to exit, and returns its now-dead pid.</summary>
    private static async Task<int> RunAndWaitForExitAsync()
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        var pid = process.Id;
        await process.WaitForExitAsync();

        // Windows does not hand a pid straight back out, so this stays dead for the few
        // milliseconds the assertions need.
        return pid;
    }

    private static Process StartLongRunningChild() =>
        Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 120 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
}
