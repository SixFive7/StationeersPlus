using System.Diagnostics;
using TestRig.Core.Abstractions;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>
/// Resolving a pid to the executable behind it, for orphan scoping.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="BusyProbe"/> takes this as an optional delegate and answers
/// <see cref="OrphanScope.Unknown"/> without it. That is not a cosmetic difference: an
/// untracked <c>rocketstation</c> process with no path cannot be told from the DEVELOPER'S
/// OWN RUNNING CLIENT, so it is reported as an orphan, and a reported orphan blocks every
/// state reset. Left unwired, the rig refuses to restore itself whenever the developer has
/// the game open.
/// </para>
/// <para>
/// It lives here rather than in <c>Infrastructure/</c> only because that folder was frozen
/// while this port was written. It is the one place in the client half that touches
/// <see cref="Process"/> directly, and it is a pure read.
/// </para>
/// <para>
/// Wire it at every construction site: <c>new BusyProbe(fs, processes, paths,
/// ProcessImagePaths.Resolve)</c>.
/// </para>
/// </remarks>
public static class ProcessImagePaths
{
    /// <summary>
    /// The full path of a live process's main module, or null.
    /// </summary>
    /// <remarks>
    /// Null on every failure, and there are several real ones: the process exited between
    /// the enumeration and this call, it is 32-bit and this is not (or the reverse), or it
    /// belongs to another user and the read is denied. Null is the honest answer and the
    /// probe treats it as "cannot be told" rather than as "not ours", which is the safe
    /// direction: an unidentifiable process is reported, never silently dropped.
    /// </remarks>
    public static string? Resolve(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or System.ComponentModel.Win32Exception
                                       or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>A probe with orphan scoping wired, which is the only correct way to build one.</summary>
    public static BusyProbe Probe(IFileSystem fs, IProcessTable processes, RigPaths paths) =>
        new(fs, processes, paths, Resolve);
}
