using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /reflect, /reflect/members, /reflect/instance, /thing, /thing/members.
//
// Every one of these carries a ValueBlock somewhere. Read its remarks before trusting a
// rendered value: valueType is the field that says whether the rendering answers the
// question at all.

/// <summary><c>/reflect</c>. Read a static field or property by name.</summary>
public sealed record ReflectRequest
{
    /// <summary>The full type name.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>A static field or property. A BepInEx <c>ConfigEntry&lt;T&gt;</c> is unwrapped to its <c>.Value</c>.</summary>
    [JsonPropertyName("member")]
    public string? Member { get; init; }

    /// <summary>Expands a dictionary into <c>entries</c> or a collection into <c>items</c>.</summary>
    [JsonPropertyName("expand")]
    public bool? Expand { get; init; }

    /// <summary>Clamped to 1 through 500. Defaults to 25.</summary>
    [JsonPropertyName("expandLimit")]
    public int? ExpandLimit { get; init; }

    /// <summary>
    ///     Probes a dictionary for one key without dumping it. Matched on the invariant
    ///     string form, case-insensitively, so a dictionary keyed by long, int or ulong all
    ///     answer to the decimal text.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>
///     The value block plus what was asked for. <c>ok:false</c> arrives at HTTP
///     <b>200</b>, not 409.
/// </summary>
public sealed record ReflectResponse : ValueBlock, IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("member")]
    public string? Member { get; init; }

    /// <summary><c>field</c> or <c>property</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }
}

/// <summary><c>/reflect/members</c>. Every static member of a type.</summary>
public sealed record ReflectMembersRequest
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }
}

/// <summary><c>ok:false</c> arrives at HTTP 200.</summary>
public sealed record ReflectMembersResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    /// <summary>Which assembly the BepInEx <c>ConfigEntryBase</c> comparand came from, for diagnosing a duplicate load.</summary>
    [JsonPropertyName("configEntryBaseAsm")]
    public string? ConfigEntryBaseAsm { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("members")]
    public MemberRow[]? Members { get; init; }
}

/// <summary>
///     <c>/thing</c>. Read named members off up to 50 live Things at once.
/// </summary>
/// <remarks>
///     Member paths support dots (<c>ParentSlot.Parent.ReferenceId</c>) and <c>[n]</c>
///     list indexing on any segment.
/// </remarks>
public sealed record ThingRequest
{
    /// <summary>Comma-separated ReferenceIds. Required; 400 on none and 400 above 50.</summary>
    [JsonPropertyName("refId")]
    public string? RefId { get; init; }

    /// <summary>Alias of <see cref="RefId"/>.</summary>
    [JsonPropertyName("refIds")]
    public string? RefIds { get; init; }

    /// <summary>Alias of <see cref="RefId"/>.</summary>
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Alias of <see cref="RefId"/>.</summary>
    [JsonPropertyName("ids")]
    public string? Ids { get; init; }

    /// <summary>Comma-separated member paths.</summary>
    [JsonPropertyName("fields")]
    public string? Fields { get; init; }

    /// <summary>Alias of <see cref="Fields"/>.</summary>
    [JsonPropertyName("field")]
    public string? Field { get; init; }

    /// <summary>Pins the declaring type of the FIRST path segment. 400 when the type is not loaded.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    ///     Defaults to <b>true</b>. False makes every <c>matchesPrefab</c> null, which is
    ///     not the same as false and must not be read as "differs".
    /// </summary>
    [JsonPropertyName("comparePrefab")]
    public bool? ComparePrefab { get; init; }

    [JsonPropertyName("expand")]
    public bool? Expand { get; init; }

    [JsonPropertyName("expandLimit")]
    public int? ExpandLimit { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>
///     One row per requested id. Status is 200 only when every row and every field
///     resolved; anything else is <b>409</b>, which a transport treats as a throw.
/// </summary>
/// <remarks>
///     The PowerShell fake ignored the requested id list entirely and always returned two
///     hardcoded Things with <c>requested:2, found:2</c> (divergence D-37), so
///     <see cref="Missing"/>, <see cref="Requested"/> and <see cref="Found"/> were never
///     meaningfully exercised. It also omitted the row-level <c>customColorIndex</c>
///     (D-32) and the whole <c>location</c> block (D-33).
/// </remarks>
public sealed record ThingResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("requested")]
    public int Requested { get; init; }

    [JsonPropertyName("found")]
    public int Found { get; init; }

    /// <summary>Ids that resolved to nothing, as <b>strings</b>.</summary>
    [JsonPropertyName("missing")]
    public string[]? Missing { get; init; }

    [JsonPropertyName("things")]
    public ThingRow[]? Things { get; init; }
}

/// <summary><c>/reflect/instance</c>. One member on one Thing.</summary>
public sealed record ReflectInstanceRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("refId")]
    public long? RefId { get; init; }

    /// <summary>Alias of <see cref="RefId"/>.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("member")]
    public string? Member { get; init; }

    /// <summary>Alias of <see cref="Member"/>.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("expand")]
    public bool? Expand { get; init; }

    [JsonPropertyName("expandLimit")]
    public int? ExpandLimit { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }
}

/// <summary>The value block, plus the Thing it came off. 409 when the member did not resolve.</summary>
public sealed record ReflectInstanceResponse : ValueBlock, IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    /// <summary>A <b>string</b>, echoing what was asked for.</summary>
    [JsonPropertyName("refId")]
    public string? RefId { get; init; }

    [JsonPropertyName("member")]
    public string? Member { get; init; }

    [JsonPropertyName("pinnedType")]
    public string? PinnedType { get; init; }

    [JsonPropertyName("thing")]
    public ThingIdentity? Thing { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("resolvedName")]
    public string? ResolvedName { get; init; }

    [JsonPropertyName("declaredBy")]
    public string? DeclaredBy { get; init; }

    [JsonPropertyName("declaredType")]
    public string? DeclaredType { get; init; }

    /// <summary>Present and true when a BepInEx <c>ConfigEntry&lt;T&gt;</c> was unwrapped to its value.</summary>
    [JsonPropertyName("unwrappedConfigEntry")]
    public bool? UnwrappedConfigEntry { get; init; }
}

/// <summary>
///     <c>/thing/members</c>. Every instance member of a Thing or of a type.
/// </summary>
/// <remarks>
///     <see cref="Values"/> defaults to true and <b>invokes every property getter</b>.
///     A getter is arbitrary game code: it can allocate, lazily construct, or throw. A
///     throw is caught and reported per member; side effects are not preventable, so a
///     caller that only wants the shape of a type passes false.
/// </remarks>
public sealed record ThingMembersRequest
{
    [JsonPropertyName("refId")]
    public long? RefId { get; init; }

    /// <summary>Alias of <see cref="RefId"/>.</summary>
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    /// <summary>Used when there is no <see cref="RefId"/>, and forces <see cref="Values"/> off. 400 with neither.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary>Clamped to 1 through 2000. Defaults to 400.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>Defaults to true. Invokes every property getter.</summary>
    [JsonPropertyName("values")]
    public bool? Values { get; init; }
}

/// <summary>409 when a <see cref="ThingMembersRequest.RefId"/> was given and the Thing is absent.</summary>
public sealed record ThingMembersResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The inheritance chain as text, so a member's origin is obvious without a second call.</summary>
    [JsonPropertyName("typeChain")]
    public string? TypeChain { get; init; }

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    /// <summary>False when <c>values</c> was off or was forced off by a type-only request.</summary>
    [JsonPropertyName("valuesRead")]
    public bool ValuesRead { get; init; }

    [JsonPropertyName("thing")]
    public ThingIdentity? Thing { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("members")]
    public MemberRow[]? Members { get; init; }
}
