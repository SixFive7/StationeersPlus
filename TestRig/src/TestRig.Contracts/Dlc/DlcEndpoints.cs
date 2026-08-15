using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /dlc, /dlc/remove, /dlc/restore.
//
// THIS IS THE ENDPOINT THE WHOLE CONTRACTS ASSEMBLY EXISTS FOR. The PowerShell playtest
// fake answered /dlc with { ok, owned } while the real checks read state.removedOwned and
// state.shared. Nothing compared the two shapes, 399 assertions stayed green, and every
// real check was broken. Six separate divergences (D-01 through D-06) hid in that one
// two-field object:
//
//   D-01  the fake had no `state` object at all
//   D-02  the fake put `owned` at the TOP LEVEL; the real one is at state.owned
//   D-03  the fake's `owned` was an ARRAY; the real one is a comma-joined STRING
//   D-04  the real one carries an integer *Mask twin beside every name string
//   D-05  the fake had no instance, epoch, known, direction or sequence
//   D-06  the fake had no state.gameInitialized or state.ownedFieldReachable
//
// Every one of them is now a property that either exists on the type or does not compile.

/// <summary>
///     One DLCType the running game version knows about.
/// </summary>
public sealed record DlcKnownRow
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>The bit value, so a caller can build a mask without guessing.</summary>
    [JsonPropertyName("value")]
    public int Value { get; init; }
}

/// <summary>
///     The entitlement state. Every mask has a name twin and vice versa: the integer is
///     the one to compute with, the string is the one to read.
/// </summary>
/// <remarks>
///     The eight <c>baseline*</c>, <c>removed*</c>, <c>baselineSession</c> and
///     <c>removeCalls</c> members are present ONLY when <see cref="Overridden"/> is true,
///     which means only after this process's first removal.
/// </remarks>
public sealed record DlcState
{
    /// <summary>The bitmask this process's own DLCManager holds.</summary>
    [JsonPropertyName("ownedMask")]
    public int OwnedMask { get; init; }

    /// <summary>
    ///     A <b>comma-joined string</b> of DLCType names, or the literal <c>None</c>.
    ///     Never an array. The fake used an array (divergence D-03), and the mismatch was
    ///     invisible because PowerShell renders an array space-joined and a substring match
    ///     happened to work either way.
    /// </summary>
    [JsonPropertyName("owned")]
    public string? Owned { get; init; }

    /// <summary>The session-wide union held by SharedDLCManager.</summary>
    [JsonPropertyName("sharedMask")]
    public int SharedMask { get; init; }

    /// <summary>Comma-joined names, or <c>None</c>.</summary>
    [JsonPropertyName("shared")]
    public string? Shared { get; init; }

    /// <summary>True once this process has removed anything. Gates the eight members below.</summary>
    [JsonPropertyName("overridden")]
    public bool Overridden { get; init; }

    [JsonPropertyName("baselineOwnedMask")]
    public int? BaselineOwnedMask { get; init; }

    [JsonPropertyName("baselineOwned")]
    public string? BaselineOwned { get; init; }

    [JsonPropertyName("baselineSharedMask")]
    public int? BaselineSharedMask { get; init; }

    [JsonPropertyName("baselineShared")]
    public string? BaselineShared { get; init; }

    [JsonPropertyName("removedOwnedMask")]
    public int? RemovedOwnedMask { get; init; }

    /// <summary>The field the real playtest checks read, and the one the fake could not produce at all.</summary>
    [JsonPropertyName("removedOwned")]
    public string? RemovedOwned { get; init; }

    [JsonPropertyName("removedSharedMask")]
    public int? RemovedSharedMask { get; init; }

    [JsonPropertyName("removedShared")]
    public string? RemovedShared { get; init; }

    /// <summary>
    ///     The <c>epoch.session</c> the baseline was captured in. A later session
    ///     invalidates it.
    /// </summary>
    /// <remarks>
    ///     A <c>long</c>, matching <see cref="EpochBlock.Session"/>, which is the value the
    ///     plugin copies in here. It was <c>int?</c>, so one field said the session counter
    ///     was 32 bits wide and another said 64. The counter never gets near either bound,
    ///     but a wire type narrower than its source is the defect class that made
    ///     <c>connectionId</c> take a whole endpoint down, and agreeing costs nothing.
    /// </remarks>
    [JsonPropertyName("baselineSession")]
    public long? BaselineSession { get; init; }

    [JsonPropertyName("removeCalls")]
    public int? RemoveCalls { get; init; }

    /// <summary>False means <c>DLCManager._ownedDLC</c> could not be resolved, so nothing here is writable.</summary>
    [JsonPropertyName("ownedFieldReachable")]
    public bool OwnedFieldReachable { get; init; }

    /// <summary>
    ///     False means <c>DLCManager.Initialize()</c> has not run, so <see cref="Owned"/>
    ///     is zero for that reason rather than because anything was removed.
    ///     <c>/dlc/remove</c> refuses at 409 in that state.
    /// </summary>
    [JsonPropertyName("gameInitialized")]
    public bool GameInitialized { get; init; }
}

/// <summary><c>/dlc</c>. No parameters.</summary>
public sealed record DlcRequest;

/// <summary>The entitlement state, the names this game version knows, and the ordering rules.</summary>
public sealed record DlcResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    /// <summary>Everything a check reads. Not at the top level: <c>state.owned</c>, not <c>owned</c>.</summary>
    [JsonPropertyName("state")]
    public DlcState? State { get; init; }

    [JsonPropertyName("known")]
    public DlcKnownRow[]? Known { get; init; }

    /// <summary>The standing "REMOVAL ONLY" statement. This API cannot grant entitlement by any route.</summary>
    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    /// <summary>The eight ordering rules that decide whether a removal actually holds.</summary>
    [JsonPropertyName("sequence")]
    public string[]? Sequence { get; init; }
}

/// <summary>Before and after for one scope, with a mask twin on every name.</summary>
public sealed record DlcScopeDelta
{
    [JsonPropertyName("beforeMask")]
    public int BeforeMask { get; init; }

    [JsonPropertyName("before")]
    public string? Before { get; init; }

    [JsonPropertyName("afterMask")]
    public int AfterMask { get; init; }

    [JsonPropertyName("after")]
    public string? After { get; init; }

    /// <summary>What this call actually took away.</summary>
    [JsonPropertyName("clearedMask")]
    public int ClearedMask { get; init; }

    [JsonPropertyName("cleared")]
    public string? Cleared { get; init; }

    /// <summary>What was asked for but was already gone. Not a failure.</summary>
    [JsonPropertyName("alreadyAbsentMask")]
    public int AlreadyAbsentMask { get; init; }

    [JsonPropertyName("alreadyAbsent")]
    public string? AlreadyAbsent { get; init; }
}

/// <summary>
///     <c>/dlc/remove</c>. Removal only, by construction: the single write expression in
///     the implementation is <c>current &amp; ~bits</c>, and every write is read back and
///     rolled back if a bit appeared.
/// </summary>
/// <remarks>
///     Nine grant-shaped field names are refused with HTTP 400 whether or not they carry a
///     value: <c>add</c>, <c>grant</c>, <c>give</c>, <c>set</c>, <c>own</c>, <c>owned</c>,
///     <c>enable</c>, <c>unlock</c>, <c>value</c>. This request type deliberately declares
///     none of them, so the refusal cannot be triggered by serializing this record.
/// </remarks>
public sealed record DlcRemoveRequest
{
    /// <summary>
    ///     Required. A DLCType name, several separated by comma, pipe or plus,
    ///     <c>all</c>, a decimal mask, or a <c>0x</c> hex mask.
    /// </summary>
    [JsonPropertyName("dlc")]
    public string? Dlc { get; init; }

    /// <summary>Alias of <see cref="Dlc"/>.</summary>
    [JsonPropertyName("remove")]
    public string? Remove { get; init; }

    /// <summary>
    ///     <c>both</c> (the default), <c>owned</c>/<c>local</c>/<c>own</c>, or
    ///     <c>shared</c>/<c>pool</c>/<c>session</c>. Anything else is a 400.
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }
}

/// <summary>What was taken away, from which scope, and what the state is now. 409 when <see cref="Ok"/> is false.</summary>
public sealed record DlcRemoveResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    /// <summary>
    ///     Present on the pre-initialisation refusal only, at the TOP level, alongside the
    ///     copy inside <see cref="State"/>. False there means the removal would have been a
    ///     no-op that reported success and was then overwritten from Steam.
    /// </summary>
    [JsonPropertyName("gameInitialized")]
    public bool? GameInitialized { get; init; }

    [JsonPropertyName("requestedMask")]
    public int? RequestedMask { get; init; }

    [JsonPropertyName("requested")]
    public string? Requested { get; init; }

    /// <summary>The resolved scope: <c>both</c>, <c>owned</c> or <c>shared</c>.</summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; init; }

    /// <summary>Absent when the scope excluded it.</summary>
    [JsonPropertyName("owned")]
    public DlcScopeDelta? Owned { get; init; }

    /// <summary>Absent when the scope excluded it.</summary>
    [JsonPropertyName("shared")]
    public DlcScopeDelta? Shared { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("state")]
    public DlcState? State { get; init; }

    [JsonPropertyName("sequence")]
    public string[]? Sequence { get; init; }

    /// <summary>
    ///     Present when the scope was <c>shared</c> alone, which leaves the local
    ///     entitlement intact so the next world entry re-seeds what was just removed.
    /// </summary>
    [JsonPropertyName("scopeWarning")]
    public string? ScopeWarning { get; init; }
}

/// <summary>
///     <c>/dlc/restore</c>. Takes no arguments: it puts back the baseline captured from
///     this process's own live state before its first removal, so there is no value a
///     caller could name that it would write. The same nine grant-shaped field names are
///     refused with 400, and this record declares none of them.
/// </summary>
public sealed record DlcRestoreRequest;

/// <summary>
///     The restore result. <c>{ok:true, restored:false}</c> is the ordinary answer when
///     nothing was ever removed. 409 when a write failed.
/// </summary>
public sealed record DlcRestoreResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>False with <see cref="Ok"/> true means there was no baseline to restore.</summary>
    [JsonPropertyName("restored")]
    public bool Restored { get; init; }

    [JsonPropertyName("ownedBeforeMask")]
    public int? OwnedBeforeMask { get; init; }

    [JsonPropertyName("ownedBefore")]
    public string? OwnedBefore { get; init; }

    [JsonPropertyName("ownedAfterMask")]
    public int? OwnedAfterMask { get; init; }

    [JsonPropertyName("ownedAfter")]
    public string? OwnedAfter { get; init; }

    [JsonPropertyName("sharedBeforeMask")]
    public int? SharedBeforeMask { get; init; }

    [JsonPropertyName("sharedBefore")]
    public string? SharedBefore { get; init; }

    [JsonPropertyName("sharedAfterMask")]
    public int? SharedAfterMask { get; init; }

    [JsonPropertyName("sharedAfter")]
    public string? SharedAfter { get; init; }

    /// <summary>A <c>long</c>, matching <see cref="EpochBlock.Session"/> and <see cref="DlcState.BaselineSession"/>.</summary>
    [JsonPropertyName("baselineSession")]
    public long? BaselineSession { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("state")]
    public DlcState? State { get; init; }
}
