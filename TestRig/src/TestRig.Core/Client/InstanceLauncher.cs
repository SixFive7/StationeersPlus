using TestRig.Core.Abstractions;
using TestRig.Core.Infrastructure;

namespace TestRig.Core.Client;

/// <summary>Everything one instance launch needs.</summary>
/// <param name="Desktop">
/// The BARE desktop name, or null/empty to launch on the caller's own desktop, which is a
/// debugging-only path: an instance there WILL take the foreground.
/// </param>
/// <param name="ManifestPath">
/// Handed to the child through <c>STATIONEERS_CLIENTRIG_MANIFEST</c>. The plugin finds its
/// manifest through that variable first and the working directory second.
/// </param>
public sealed record InstanceLaunch(
    string ExePath,
    string CommandLine,
    string WorkingDirectory,
    string? Desktop,
    string ManifestPath);

/// <summary>The seam over process creation for a game client.</summary>
/// <remarks>
/// An interface so the suite can assert on the exact command line, the working directory
/// and the desktop without launching a 10 GB game. What it must NEVER become is a route to
/// <c>Process.Start</c>: <c>ProcessStartInfo</c> cannot express <c>lpDesktop</c> with
/// <c>UseShellExecute = false</c>, and the desktop is the entire mechanism that keeps an
/// instance off the developer's screen.
/// </remarks>
public interface IInstanceLauncher
{
    /// <summary>Starts the instance and returns its pid.</summary>
    uint Start(InstanceLaunch launch);

    /// <summary>
    /// Creates the isolated desktop if it does not exist, or opens it if it does.
    /// </summary>
    /// <remarks>
    /// Nothing ever switches to it. There is no <c>SwitchDesktop</c> import anywhere in
    /// this tree and there never will be.
    /// </remarks>
    void EnsureDesktop(string desktopName);
}

/// <summary>
/// The real launcher: <c>CreateProcessW</c> onto a desktop that is created and never
/// switched to.
/// </summary>
/// <remarks>
/// <para>
/// Measured, sampling the foreground every 3 seconds for two minutes: launching with
/// SW_SHOWNOACTIVATE alone stole focus on 40 samples out of 40, and the foreground never
/// came back; launching onto a separate desktop stole it on 0 out of 55, through a full
/// boot and an entire acceptance test with two instances running.
/// </para>
/// <para>
/// The environment variable is set on THIS process around the call and removed in a
/// finally, because <c>CreateProcessW</c> is given a null environment block and therefore
/// inherits ours (CLIENT-123). It is the one piece of global state this type touches, and
/// the finally is what stops a failed launch leaking it into the next one.
/// </para>
/// </remarks>
public sealed class DesktopInstanceLauncher : IInstanceLauncher
{
    /// <summary>The variable the plugin reads to find its manifest.</summary>
    public const string ManifestVariable = "STATIONEERS_CLIENTRIG_MANIFEST";

    public void EnsureDesktop(string desktopName) => DesktopProcessLauncher.EnsureDesktop(desktopName);

    public uint Start(InstanceLaunch launch)
    {
        Environment.SetEnvironmentVariable(ManifestVariable, launch.ManifestPath);
        try
        {
            return DesktopProcessLauncher.Start(
                launch.ExePath,
                launch.CommandLine,
                launch.WorkingDirectory,
                // Belt and braces alongside the desktop, never the mechanism: wShowWindow
                // governs only the first ShowWindow(SW_SHOWDEFAULT), and Unity calls
                // ShowWindow itself once its window exists.
                DesktopProcessLauncher.ShowNoActivate,
                string.IsNullOrEmpty(launch.Desktop)
                    ? null
                    : DesktopProcessLauncher.QualifyDesktop(launch.Desktop));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ManifestVariable, null);
        }
    }
}

/// <summary>The seam over stopping a process that will not go quietly.</summary>
/// <remarks>
/// <see cref="IProcessTable.StopAsync"/> already covers this, so this exists only to name
/// the intent at the call sites that force-kill: a force-kill of a game client is the one
/// action in the client half that can lose a world.
/// </remarks>
public static class ForceKill
{
    public static Task<bool> NowAsync(IProcessTable processes, int pid, CancellationToken ct = default) =>
        processes.StopAsync(pid, TimeSpan.Zero, ct);
}
