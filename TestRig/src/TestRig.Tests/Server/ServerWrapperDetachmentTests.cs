using System.Diagnostics;
using System.IO.Pipes;
using TestRig.Core.Infrastructure;
using TestRig.Core.Server;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Server;

/// <summary>
/// The host wrapper must not inherit the launcher's handles.
/// </summary>
/// <remarks>
/// <para>
/// Measured 2026-08-14: <c>start --target server</c> printed "Server PID 21348" and exited,
/// and the shell capturing its output blocked for 907 seconds, returning the instant the
/// SERVER stopped. The wrapper outlives the launcher by design, so a wrapper holding the
/// launcher's stdout pipe hangs the playtest engine and every scripted caller.
/// </para>
/// <para>
/// The cause is not a flag anybody forgot. <c>Process.Start</c> with
/// <c>UseShellExecute = false</c> ALWAYS passes <c>bInheritHandles: true</c> and always sets
/// STARTF_USESTDHANDLES to the caller's own handles; there is no managed way to turn either
/// off. The client half already went through CreateProcessW for the desktop, and the server
/// wrapper now does too.
/// </para>
/// <para>
/// The measurement here is an INHERITABLE pipe handle rather than stdout, because inheritance
/// is the property under test and a pipe handle is inherited if and only if the launch asked
/// for handle inheritance. The control launch proves the probe is real.
/// </para>
/// </remarks>
public sealed class ServerWrapperDetachmentTests : IDisposable
{
    private readonly TempDirectory _temp = new("wrapper");
    private readonly SystemProcessTable _processes = new();

    private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary>
    /// A child that lives past the assertion window and no longer.
    /// </summary>
    /// <remarks>
    /// Bounded deliberately. A pending read on an anonymous pipe holds a thread-pool thread
    /// until a write end closes, and a child that outlives its test starves the rest of the
    /// suite: the measured symptom was an unrelated CLI test whose 30-second stand-in process
    /// expired before its own assertion ran.
    /// </remarks>
    private const string LongLivedArguments = "/c\0ping -n 12 127.0.0.1";

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task StartWrapper_LeavesTheCallersHandlesBehind()
    {
        using var probe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var (pid, started) = new SystemServerProcessLauncher()
            .StartWrapper(CmdExe, LongLivedArguments, _temp.Path);

        try
        {
            Assert.True(pid > 0);
            Assert.NotNull(_processes.TryGetMatching(pid, "cmd"));
            Assert.NotNull(started);

            // The launcher drops its own copy of the write end. If the child did not inherit
            // one, no write end exists anywhere and the read is at end of stream at once.
            probe.DisposeLocalCopyOfClientHandle();

            var read = await probe.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, read);
        }
        finally
        {
            KillTree(pid);
        }
    }

    [Fact]
    public async Task TheControl_ProcessStartDoesHandTheChildTheCallersHandles()
    {
        // Without this the test above could pass for the wrong reason (a child that exited
        // immediately, a pipe that was never inheritable). This is the behaviour that was
        // measured hanging a caller for 907 seconds.
        using var probe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);

        var start = new ProcessStartInfo(CmdExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _temp.Path,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 30 127.0.0.1");

        using var child = Process.Start(start)!;
        probe.DisposeLocalCopyOfClientHandle();

        var read = probe.ReadAsync(new byte[1]).AsTask();
        try
        {
            var finished = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2)));
            Assert.NotSame(read, finished);
        }
        finally
        {
            // The whole TREE, not just the child: cmd.exe spawns ping.exe, which inherited the
            // same handle, so killing only the parent leaves a write end open and the read
            // pending. Awaited here so nothing is in flight when the pipe is disposed.
            KillTree(child.Id);
            await read.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Ends a launched child and everything it spawned.</summary>
    private static void KillTree(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(20_000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            // It exited on its own, which is the outcome this wanted anyway.
        }
    }
}
