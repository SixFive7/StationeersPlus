using TestRig.Core.Abstractions;
using TestRig.Playtest.Seams;

namespace TestRig.Playtest.Runner;

/// <summary>
///     Everything the engine reaches the outside world through.
/// </summary>
/// <remarks>
///     <para>
///     Every member is required. There is no "unwired seam" state and therefore no runtime
///     error explaining that the composition root forgot one: the PowerShell library carried
///     three such errors, each a paragraph long, because a script-scoped variable can be
///     absent and a constructor parameter cannot.
///     </para>
///     <para>
///     The clock and the sleeper are separate on purpose. The offline suite advances time
///     rather than spending it, so a 300 second barrier and a 10 second retry gap are
///     exercised exactly as they run for real and cost nothing.
///     </para>
/// </remarks>
public sealed class PlaytestDependencies
{
    public required IRigTransport Transport { get; init; }

    public required IRigLauncher Launcher { get; init; }

    public required IRigRegistry Registry { get; init; }

    public required IFileSystem Files { get; init; }

    public required ILogFiles LogFiles { get; init; }

    public required IClock Clock { get; init; }

    public required ISleeper Sleeper { get; init; }

    /// <summary>The rig root: the folder holding ClientRig/ and DedicatedServer/.</summary>
    public required string RigHome { get; init; }

    /// <summary>
    ///     The developer's own save folder, which is tier 1 and off limits unconditionally.
    /// </summary>
    /// <remarks>
    ///     Listed, never read. If this path is wrong the run learns nothing, which is why a
    ///     root that was absent at both ends is its own verdict rather than "identical".
    /// </remarks>
    public required string Tier1SaveRoot { get; init; }

    /// <summary>Where the engine's progress lines go. Defaults to nowhere.</summary>
    public Action<string>? Log { get; init; }
}
