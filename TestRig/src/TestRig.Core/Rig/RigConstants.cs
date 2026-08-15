// =============================================================================
// WHAT THIS DIRECTORY IS
//
// TestRig.Core/Rig/ is the port of TestRig/lib/common.ps1, the library BOTH
// halves share. Nothing in it is client-specific or server-specific; the
// namespace is TestRig.Core.Rig and both halves consume every type in here.
//
// It sat under Client/ for one pass of the port, because that pass was scoped to
// Client/** and Server/** and a third top-level folder was outside it. That was
// never what the code meant, and the folder has been moved to match the namespace.
//
// The file it replaces existed because the two halves used to answer the same
// question differently: "is that pid alive", "where is the game install", "what
// game version is this", "how do I write modconfig.xml". Each had two or three
// implementations that had drifted. One answer each, here.
// =============================================================================

namespace TestRig.Core.Rig;

/// <summary>
/// Every constant the two halves must agree on, declared exactly once.
/// </summary>
/// <remarks>
/// The game ports are the sharpest case. The client half hardcoded the dedicated
/// server's 28015/28016 in its collision table while the server half declared them
/// independently as parameter defaults, so changing one did not change the other and
/// the collision check would have gone quietly wrong. Here the reserved-port table is
/// COMPUTED from the two server constants rather than typed again.
/// </remarks>
public static class RigConstants
{
    // ---- process images (COMMON-007, COMMON-008) --------------------------

    /// <summary>What the process table reports for the dedicated server, minus the extension.</summary>
    public const string ServerImageName = "rocketstation_DedicatedServer";

    /// <summary>What the process table reports for a game client, minus the extension.</summary>
    public const string ClientImageName = "rocketstation";

    /// <summary>
    /// The images the dedicated server's host wrapper may run as.
    /// </summary>
    /// <remarks>
    /// The PowerShell wrapper was a pwsh process and the liveness check accepted either
    /// <c>pwsh</c> or <c>powershell</c> (COMMON-033). The C# rig re-invokes its own
    /// binary, so <c>testrig</c> is the first entry; both shells stay because a rig
    /// mid-migration can still have a PowerShell wrapper alive, and a wrapper reported
    /// dead is a wrapper whose orphaned server nothing will clean up.
    /// </remarks>
    public static readonly IReadOnlyList<string> HostWrapperImageNames = ["testrig", "pwsh", "powershell"];

    // ---- ports (COMMON-009, COMMON-010) -----------------------------------

    /// <summary>The dedicated server's UDP GamePort. +1000 off the Stationeers client default so both coexist.</summary>
    public const int ServerGamePort = 28016;

    /// <summary>The dedicated server's UDP UpdatePort.</summary>
    public const int ServerUpdatePort = 28015;

    /// <summary>Control-plane TCP port base. An instance gets base + its index.</summary>
    public const int ControlPortBase = 27700;

    /// <summary>
    /// The dedicated server's control-plane TCP port.
    /// </summary>
    /// <remarks>
    /// The merged plugin loads into BOTH halves, so the server needed a port of its own or the
    /// two would bind the same one from the same default. It sits above the whole client
    /// instance band and below the game's own 28015/28016. It must equal the plugin's
    /// <c>Plugin.ServerDefaultPort</c>; the plugin is a separate build with no compile-time
    /// link to this file, which is why the number appears in a doc comment on both sides.
    /// </remarks>
    public const int ServerControlPort = 27750;

    /// <summary>RakNet UDP game port base. An instance gets base + its index.</summary>
    public const int GamePortBase = 27800;

    /// <summary>The Stationeers client's own default UpdatePort. Never usable by an instance.</summary>
    public const int StationeersDefaultUpdatePort = 27015;

    /// <summary>The Stationeers client's own default GamePort. Never usable by an instance.</summary>
    public const int StationeersDefaultGamePort = 27016;

    // ---- readiness (COMMON-011) -------------------------------------------

    /// <summary>
    /// The MINIMUM loaded-plugin count that counts as "the mod set is up".
    /// </summary>
    /// <remarks>
    /// <para>
    /// A count of 2 means StationeersLaunchPad loaded nothing, which is what a transient
    /// Steam Workshop failure looks like from outside.
    /// </para>
    /// <para>
    /// <b>The comparison is <c>&gt;=</c>, and it used to be <c>&gt;</c>.</b> That made the
    /// effective threshold 11 while the constant was named for 10 and every reader took it
    /// at its word, so an instance sitting at exactly 10 was reported as not ready for the
    /// whole barrier and then failed with a message naming a number it had reached. The
    /// name is the contract: a minimum is inclusive. Nothing else changes, because the two
    /// answers differ at exactly one count.
    /// </para>
    /// </remarks>
    public const int StageMinPlugins = 10;

    // ---- control-plane timeouts (COMMON-012, COMMON-013) -------------------

    /// <summary>Nothing gets less than this, whatever the request asked for.</summary>
    public const int ControlTimeoutFloorSeconds = 120;

    /// <summary>
    /// Added to the caller's own timeout, so the plugin gives up first and can explain.
    /// </summary>
    /// <remarks>
    /// The difference between "the plugin gave up and told us why" and "we gave up first
    /// and threw the answer away with the connection".
    /// </remarks>
    public const int ControlTimeoutMarginSeconds = 30;

    /// <summary>Absolute ceiling on a derived timeout.</summary>
    public const int ControlTimeoutCeilingSeconds = 3600;

    /// <summary>The floor for an endpoint on the long-path list.</summary>
    public const int ControlLongPathSeconds = 300;

    /// <summary>Endpoints that legitimately take minutes, each taking the long-path floor.</summary>
    public static readonly IReadOnlyList<string> ControlLongPaths =
    [
        "/host", "/connect", "/save", "/load", "/newworld", "/waitfor",
    ];

    // ---- waits (COMMON-014, COMMON-015) ------------------------------------

    /// <summary>
    /// How long a blocking wait waits by default, ON BOTH HALVES.
    /// </summary>
    /// <remarks>
    /// It was 30 on the server and 300 on the client for the same flag with the same
    /// meaning, so a 60 second save confirmed on one half and warned "may have completed
    /// silently or failed" on the other. 300 wins because of which way the two errors
    /// point: a genuinely slow save produces a FALSE WARNING under a 30 second budget,
    /// and the whole contract of the action is that it warns rather than claiming
    /// success. A false warning is indistinguishable from a real one.
    /// </remarks>
    public const int WaitDefaultSeconds = 300;

    /// <summary>
    /// How long a FORCE-KILLED game process gets to leave the process table, both halves.
    /// </summary>
    /// <remarks>
    /// Not the teardown grace and not a politeness window: by the time this applies the
    /// process has already been terminated. A game client killed mid-frame takes seconds to
    /// unwind, Windows is not obliged to have reaped it when the terminate call returns, and
    /// the rig's own process table still reports it.
    ///
    /// It is load bearing because of what happens next. The teardown deletes the pid file, so
    /// a process still unwinding becomes an UNTRACKED game process, and an untracked game
    /// process is one of the three conditions the state restore refuses on. That is how a
    /// release-time restore came to be skipped after a force-killed host: the guarantee held,
    /// because acquisition restores too, but the release half never fired.
    /// </remarks>
    public static readonly TimeSpan ProcessExitGrace = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Process-teardown grace, both halves. The ONLY thing a teardown timeout means.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="WaitDefaultSeconds"/> deliberately: the server's
    /// stop once fed the teardown grace into a save confirmation, so raising the kill
    /// timeout also silently raised how long a save was given to land.
    /// </remarks>
    public const int TeardownGraceSeconds = 30;

    // ---- reserved ports (COMMON-017) ---------------------------------------

    /// <summary>
    /// Game ports an instance must never take, with the reason for each.
    /// </summary>
    /// <remarks>
    /// A second RakNet socket on an already-bound port does not fail: both bindings
    /// coexist and traffic routes by destination address, so the joiner reaches SOMETHING
    /// and the test is confidently wrong with nothing logged anywhere. This refusal is the
    /// only warning there will ever be.
    ///
    /// The two server entries are computed from the constants above rather than typed
    /// again, which is the entire reason this file exists.
    /// </remarks>
    public static readonly IReadOnlyDictionary<int, string> ReservedGamePorts =
        new Dictionary<int, string>
        {
            [StationeersDefaultUpdatePort] = "the Stationeers client's own default UpdatePort",
            [StationeersDefaultGamePort] = "the Stationeers client's own default GamePort",
            [ServerUpdatePort] = "this rig's dedicated server UpdatePort",
            [ServerGamePort] = "this rig's dedicated server GamePort",
        };

    /// <summary>The lowest and highest port an instance may be given.</summary>
    public const int MinPort = 1024;

    /// <summary>The highest port an instance may be given.</summary>
    public const int MaxPort = 65535;

    /// <summary>The Win32 desktop instances are launched onto and never switched to.</summary>
    public const string DefaultDesktop = "StationeersRig";
}
