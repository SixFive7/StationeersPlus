using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Seams;
using TestRig.Playtest.Values;

namespace TestRig.Playtest.Model;

/// <summary>The thirteen named readers a check may conclude from.</summary>
/// <remarks>
///     A reader is how a value gets from the authority to an assertion. There is deliberately
///     no way to assert on an action's own answer: an endpoint's 200 is a statement about the
///     request, not about the world. A <c>/connect</c> answered ok on 2026-08-09 while nothing
///     had joined, and an <c>/inventory/arm</c> reported confirmed while the host-side check
///     was inconclusive.
/// </remarks>
public enum Reader
{
    /// <summary>The one computed answer to what this process is: role, hosting, hostPort, phase, save hygiene.</summary>
    Status,

    /// <summary>Status narrowed to connectedClients: the SERVER-side roster. The host is in its own roster.</summary>
    Roster,

    /// <summary>Every ConfigEntry of a loaded plugin, as the running process sees it.</summary>
    Config,

    /// <summary>An INSTANCE field on one Thing, per machine.</summary>
    Thing,

    /// <summary>Any STATIC field or property by full type name. Instance fields belong to Thing.</summary>
    Reflect,

    /// <summary>Things around the player.</summary>
    Nearby,

    /// <summary>The sequence-numbered console tee. A BOUNDED RING: boot-time lines are routinely evicted.</summary>
    Console,

    /// <summary>The instance's BepInEx log FILE. No ring, emptied per session, so it is the authority for boot.</summary>
    BepInExLog,

    /// <summary>Every slot of a character.</summary>
    Inventory,

    /// <summary>Every plugin found by assembly scan.</summary>
    Plugins,

    /// <summary>Where this process writes, and whether that is isolated from the developer folder.</summary>
    SavePath,

    /// <summary>The player block. Only the player block; see the note on the catalogue.</summary>
    Player,

    /// <summary>What this process believes it is entitled to.</summary>
    Dlc,
}

/// <summary>The readiness stages a barrier can wait for.</summary>
public enum Stage
{
    /// <summary>The control plane answered at all.</summary>
    Ping,

    /// <summary>StationeersLaunchPad finished loading mods.</summary>
    ModsLoaded,

    /// <summary>The main menu.</summary>
    Menu,

    /// <summary>In a world.</summary>
    InWorld,
}

/// <summary>Where an evidence file goes inside a check's folder.</summary>
public enum EvidenceKind
{
    Root,
    Requests,
    Observations,
    Console,
    Launcher,
}

/// <summary>
///     One value, read from one instance, through one named reader.
/// </summary>
/// <remarks>
///     The only thing an assert verb accepts. It carries where the value came from, when, and
///     which request record produced it, so a failure can say what was compared with what.
/// </remarks>
/// <param name="Instance">The instance that was asked.</param>
/// <param name="Reader">The reader that asked it.</param>
/// <param name="Select">The dotted path into the narrowed response.</param>
/// <param name="Of">The narrowing key, when the reader takes one.</param>
/// <param name="ReaderArgs">
///     The Contracts request record the query string was built from. Carried so a re-read
///     reproduces the request EXACTLY. Forgetting this once already cost a campaign: without
///     the args a re-read went out as a bare <c>/thing</c>, the endpoint answered 400, and
///     every before-and-after check on a per-Thing field ended inconclusive with no
///     comparison made.
/// </param>
/// <param name="Value">The narrowed value, or null when the path did not resolve.</param>
/// <param name="Source">Where it came from, as one line.</param>
/// <param name="CapturedUtc">When.</param>
/// <param name="EvidenceRef">The request record in the bundle.</param>
public sealed record Observation(
    string Instance,
    Reader Reader,
    string Select,
    string Of,
    object? ReaderArgs,
    JsonNode? Value,
    string Source,
    string CapturedUtc,
    string EvidenceRef)
{
    /// <summary>The value as a check would interpolate it into a message.</summary>
    public string Text => ValueText.Render(Value);
}

/// <summary>
///     What an action did. No assert verb accepts one of these, by design.
/// </summary>
/// <param name="Instance">The instance that was driven.</param>
/// <param name="Path">The endpoint, query string included.</param>
/// <param name="Attempts">How many attempts it took. More than one marks the check degraded.</param>
/// <param name="Degraded">Whether this call needed retrying.</param>
/// <param name="HttpStatus">The status the answer arrived at.</param>
/// <param name="RawBody">The body, verbatim.</param>
/// <param name="ElapsedMs">Wall time of the final attempt.</param>
/// <param name="EvidenceRef">The request record in the bundle.</param>
public sealed record ActionResult(
    string Instance,
    string Path,
    int Attempts,
    bool Degraded,
    int HttpStatus,
    string RawBody,
    long ElapsedMs,
    string EvidenceRef)
{
    /// <summary>The parsed body, for a check that needs a value out of an action's answer.</summary>
    /// <remarks>
    ///     Reading a value here is how a check learns what it just spawned. It is NOT how a
    ///     check concludes anything: assert on the authority through a reader.
    /// </remarks>
    public T? As<T>() where T : class => RigWire.Deserialize<T>(RawBody);

    /// <summary>The body as a node, for the rare ad hoc read.</summary>
    public JsonNode? Body => PlaytestJson.TryParse(RawBody);
}

/// <summary>What a join produced.</summary>
/// <param name="Joiner">The instance that joined.</param>
/// <param name="Host">The instance it joined.</param>
/// <param name="Roster">The HOST's roster after the join, which is the authority for arrival.</param>
/// <param name="Attempts">How many connect attempts it took.</param>
/// <param name="SeqBeforeConnect">
///     The joiner's console sequence immediately before the FINAL connect.
///     <para>
///     This exists because retrying broke the check that retrying was meant to fix: anything
///     the mod prints once per JOIN appears once per attempt, so a check that baselined before
///     the helper ran counted three lines after three attempts and failed a correct mod.
///     </para>
/// </param>
public sealed record JoinResult(
    string Joiner,
    string Host,
    IReadOnlyList<ConnectedClient> Roster,
    int Attempts,
    long? SeqBeforeConnect);
