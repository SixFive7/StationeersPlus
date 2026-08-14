using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     Where a Thing is: loose in the world, or in a named slot on a named parent, with
///     the containment chain up to the root.
/// </summary>
/// <remarks>
///     <see cref="Authoritative"/> is the guard to read first. It is
///     <c>GameManager.RunSimulation</c>, and it separates "this is the world's state" from
///     "this is what this one process believes". The PowerShell fake emitted no location
///     block at all (divergence D-33), so the single check that gated everything else it
///     read on <c>location.authoritative</c> was never exercised.
/// </remarks>
public sealed record LocationBlock
{
    /// <summary><c>GameManager.RunSimulation</c>. False means this process is reading a replica.</summary>
    [JsonPropertyName("authoritative")]
    public bool Authoritative { get; init; }

    /// <summary>False for anything that is not a DynamicThing.</summary>
    [JsonPropertyName("canBeInSlot")]
    public bool CanBeInSlot { get; init; }

    [JsonPropertyName("inSlot")]
    public bool InSlot { get; init; }

    /// <summary>Emitted only for a DynamicThing that is not in a slot.</summary>
    [JsonPropertyName("onGround")]
    public bool? OnGround { get; init; }

    /// <summary>An array <c>[x,y,z]</c>. Absent when the Thing is in a slot.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    /// <summary>Human summary, for example <c>in Bob's left hand</c>.</summary>
    [JsonPropertyName("whereIs")]
    public string? WhereIs { get; init; }

    [JsonPropertyName("slotIndex")]
    public int? SlotIndex { get; init; }

    [JsonPropertyName("slotKey")]
    public string? SlotKey { get; init; }

    [JsonPropertyName("slotType")]
    public string? SlotType { get; init; }

    [JsonPropertyName("isHandSlot")]
    public bool? IsHandSlot { get; init; }

    [JsonPropertyName("parentId")]
    public long? ParentId { get; init; }

    [JsonPropertyName("parentType")]
    public string? ParentType { get; init; }

    [JsonPropertyName("parentName")]
    public string? ParentName { get; init; }

    [JsonPropertyName("parentPrefab")]
    public string? ParentPrefab { get; init; }

    /// <summary>String, for the 2^53 reason. Present only when the parent is a Human.</summary>
    [JsonPropertyName("parentClientId")]
    public string? ParentClientId { get; init; }

    /// <summary><c>left</c>, <c>right</c>, or null when the slot is neither hand.</summary>
    [JsonPropertyName("handSide")]
    public string? HandSide { get; init; }

    [JsonPropertyName("parentIsLocalPlayer")]
    public bool? ParentIsLocalPlayer { get; init; }

    /// <summary>
    ///     Emitted only when the parent is the local player. Which hand is active is
    ///     InventoryManager state, client-local and never replicated.
    /// </summary>
    [JsonPropertyName("isActiveHand")]
    public bool? IsActiveHand { get; init; }

    /// <summary>Replaces <see cref="IsActiveHand"/> for a remote character, explaining why it cannot be answered here.</summary>
    [JsonPropertyName("activeHandNote")]
    public string? ActiveHandNote { get; init; }

    /// <summary>Containment chain outward, up to 8 deep. A can inside a gun inside a hand reads as such.</summary>
    [JsonPropertyName("chain")]
    public LocationChainLink[]? Chain { get; init; }

    /// <summary>Emitted only when the root differs from the Thing itself.</summary>
    [JsonPropertyName("rootId")]
    public long? RootId { get; init; }

    [JsonPropertyName("rootType")]
    public string? RootType { get; init; }

    [JsonPropertyName("rootName")]
    public string? RootName { get; init; }
}

/// <summary>One step outward in a containment chain.</summary>
public sealed record LocationChainLink
{
    [JsonPropertyName("referenceId")]
    public long ReferenceId { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("slotKey")]
    public string? SlotKey { get; init; }
}

/// <summary>
///     The identity block every Thing row leads with, so a value is attributable to an
///     object rather than to a bare reference id.
/// </summary>
public record ThingIdentity
{
    /// <summary>
    ///     Numeric here. Compare with <see cref="ValueBlock.ReferenceId"/>, which is a
    ///     string: the two really do differ and this assembly reproduces both.
    /// </summary>
    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("typeFullName")]
    public string? TypeFullName { get; init; }

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>An array <c>[x,y,z]</c>, never an object.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    [JsonPropertyName("paintable")]
    public bool? Paintable { get; init; }

    /// <summary>
    ///     The swatch index, or -1 when unpainted. This is the documented workaround for
    ///     the <c>Thing.CustomColor</c> rendering trap described on <see cref="ValueBlock"/>.
    ///     Note the name: <c>/nearby</c> uses the same one, and the PowerShell fake called
    ///     it <c>colorIndex</c> there (divergence D-46), which reads as an absent field.
    /// </summary>
    [JsonPropertyName("customColorIndex")]
    public int? CustomColorIndex { get; init; }
}

/// <summary>One row of a <c>/thing</c> response.</summary>
public sealed record ThingRow : ThingIdentity
{
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    /// <summary>The id as the caller sent it, echoed back as a <b>string</b>.</summary>
    [JsonPropertyName("requestedRefId")]
    public string? RequestedRefId { get; init; }

    [JsonPropertyName("found")]
    public bool Found { get; init; }

    /// <summary>Per-row failure. A missing Thing is a row with <c>found:false</c> and this set.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("location")]
    public LocationBlock? Location { get; init; }

    [JsonPropertyName("fields")]
    public ThingFieldRow[]? Fields { get; init; }

    /// <summary>Set when the prefab comparison degraded, for example when no prefab was resolvable.</summary>
    [JsonPropertyName("prefabNote")]
    public string? PrefabNote { get; init; }
}

/// <summary>
///     One requested member on one Thing: the value block plus how the member was
///     resolved and how it compares to the prefab's own value.
/// </summary>
public sealed record ThingFieldRow : ValueBlock
{
    /// <summary>The path as the caller wrote it.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>False when the member did not resolve. The whole response then answers at HTTP 409.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary><c>field</c> or <c>property</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    /// <summary>
    ///     The member's real name after path resolution. The PowerShell reader matched a
    ///     field row by <c>name</c> or <c>resolvedName</c>, and the fake emitted neither
    ///     (divergence D-35), so the second half of that match never fired once.
    /// </summary>
    [JsonPropertyName("resolvedName")]
    public string? ResolvedName { get; init; }

    [JsonPropertyName("declaredBy")]
    public string? DeclaredBy { get; init; }

    [JsonPropertyName("declaredType")]
    public string? DeclaredType { get; init; }

    /// <summary>The same member read off the prefab, present when <c>comparePrefab</c> is on.</summary>
    [JsonPropertyName("prefabValue")]
    public ValueBlock? PrefabValue { get; init; }

    /// <summary>
    ///     Null when the comparison could not run, which is what <c>comparePrefab=false</c>
    ///     produces. Do not read a null as "differs".
    /// </summary>
    [JsonPropertyName("matchesPrefab")]
    public bool? MatchesPrefab { get; init; }

    [JsonPropertyName("matchesPrefabNote")]
    public string? MatchesPrefabNote { get; init; }

    [JsonPropertyName("prefabError")]
    public string? PrefabError { get; init; }
}

/// <summary>One member row from <c>/thing/members</c> or <c>/reflect/members</c>.</summary>
public sealed record MemberRow : ValueBlock
{
    /// <summary><c>field</c> or <c>property</c>.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("declaredBy")]
    public string? DeclaredBy { get; init; }

    [JsonPropertyName("declaredType")]
    public string? DeclaredType { get; init; }

    /// <summary>Present on <c>/reflect/members</c> rows only.</summary>
    [JsonPropertyName("runtimeType")]
    public string? RuntimeType { get; init; }

    /// <summary>Present on <c>/reflect/members</c> rows only. A true value means the value was unwrapped from a BepInEx ConfigEntry.</summary>
    [JsonPropertyName("isConfigEntryBase")]
    public bool? IsConfigEntryBase { get; init; }

    /// <summary>Field rows only.</summary>
    [JsonPropertyName("public")]
    public bool? Public { get; init; }

    /// <summary>Property rows only.</summary>
    [JsonPropertyName("canWrite")]
    public bool? CanWrite { get; init; }

    /// <summary>Set instead of a value when the getter threw.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
///     A Thing seen under the cursor or in a proximity scan. Same identity fields, plus
///     distance and slot context for <c>/nearby</c>.
/// </summary>
public sealed record NearbyThingRow : ThingIdentity
{
    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("inSlot")]
    public bool? InSlot { get; init; }

    [JsonPropertyName("slotKey")]
    public string? SlotKey { get; init; }

    [JsonPropertyName("parentId")]
    public long? ParentId { get; init; }

    [JsonPropertyName("parentName")]
    public string? ParentName { get; init; }
}
