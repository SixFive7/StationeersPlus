using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /inventory, /inventory/move, /inventory/give, /inventory/arm.
//
// Slot specs accepted anywhere a slot is named: activeHand/active/hand,
// inactiveHand/inactive/offhand, leftHand/left, rightHand/right,
// either/eitherHand/freeHand, a slot StringKey, a bare index, or #N. Normalisation strips
// underscores, dashes and spaces and lower-cases, so "left_hand" and "Left Hand" both work.

/// <summary>
///     <c>/inventory</c>. List a character's slots. With no parameters this is the local
///     player.
/// </summary>
public sealed record InventoryRequest
{
    /// <summary>Resolve by the Human's ReferenceId.</summary>
    [JsonPropertyName("humanId")]
    public long? HumanId { get; init; }

    /// <summary>Resolve by display name, or by ClientId when the value is numeric.</summary>
    [JsonPropertyName("player")]
    public string? Player { get; init; }

    /// <summary>Alias of <see cref="Player"/>.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }
}

/// <summary>A character's slots. 409 with the resolution error when the character was not found.</summary>
public sealed record InventoryResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("player")]
    public string? Player { get; init; }

    [JsonPropertyName("humanId")]
    public long HumanId { get; init; }

    /// <summary>Numeric here, unlike the string form on <c>/instance</c> and the roster rows.</summary>
    [JsonPropertyName("clientId")]
    public long ClientId { get; init; }

    [JsonPropertyName("isLocalPlayer")]
    public bool IsLocalPlayer { get; init; }

    /// <summary>
    ///     <c>GameManager.RunSimulation</c>. False means these values are a replica and a
    ///     mutation here will be corrected by the server.
    /// </summary>
    [JsonPropertyName("hasSimulationAuthority")]
    public bool HasSimulationAuthority { get; init; }

    [JsonPropertyName("slots")]
    public InventorySlotRow[]? Slots { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
///     <c>/inventory/move</c>. Move an existing Thing into a slot. Pass either
///     <see cref="Thing"/> or <see cref="From"/>; 400 with neither.
/// </summary>
public sealed record InventoryMoveRequest
{
    /// <summary>A ReferenceId.</summary>
    [JsonPropertyName("thing")]
    public long? Thing { get; init; }

    /// <summary>Alias of <see cref="Thing"/>.</summary>
    [JsonPropertyName("thingId")]
    public long? ThingId { get; init; }

    /// <summary>A slot spec on the local player, used to name the Thing to move.</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>A slot spec. Defaults to <c>activeHand</c>.</summary>
    [JsonPropertyName("to")]
    public string? To { get; init; }

    /// <summary>A container other than the local player, by ReferenceId.</summary>
    [JsonPropertyName("intoThing")]
    public long? IntoThing { get; init; }

    /// <summary>Without this, an occupied destination is a 409.</summary>
    [JsonPropertyName("replace")]
    public bool? Replace { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>
///     Where the Thing ended up. Every resolution failure, an occupied destination without
///     <c>replace</c>, and the wait timeout are all 409.
/// </summary>
public sealed record InventoryMoveResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("thingId")]
    public long ThingId { get; init; }

    [JsonPropertyName("thingPrefab")]
    public string? ThingPrefab { get; init; }

    [JsonPropertyName("to")]
    public string? To { get; init; }

    /// <summary>Which mechanism actually performed the move.</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>The slot was read back and holds the Thing. This is the assertion, not <see cref="Ok"/> alone.</summary>
    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; init; }

    [JsonPropertyName("destination")]
    public SlotOccupant? Destination { get; init; }

    /// <summary>Present and true on the short-circuit: the Thing was already where it was asked to go.</summary>
    [JsonPropertyName("alreadyThere")]
    public bool? AlreadyThere { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
///     <c>/inventory/give</c>. Create a Thing directly into a slot. Host or single player
///     only: it needs <c>GameManager.RunSimulation</c> and answers 409 without it.
/// </summary>
public sealed record InventoryGiveRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    [JsonPropertyName("player")]
    public string? Player { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("humanId")]
    public long? HumanId { get; init; }

    /// <summary>A slot spec. Defaults to <c>either</c>.</summary>
    [JsonPropertyName("slot")]
    public string? Slot { get; init; }

    /// <summary>Zero skips the quantity set entirely.</summary>
    [JsonPropertyName("quantity")]
    public int? Quantity { get; init; }

    /// <summary>Drops the current occupant. Never destroys it.</summary>
    [JsonPropertyName("replace")]
    public bool? Replace { get; init; }
}

/// <summary>200 when the slot reads back holding the created Thing, 409 otherwise.</summary>
public sealed record InventoryGiveResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    [JsonPropertyName("referenceId")]
    public long ReferenceId { get; init; }

    [JsonPropertyName("player")]
    public string? Player { get; init; }

    [JsonPropertyName("humanId")]
    public long HumanId { get; init; }

    [JsonPropertyName("clientId")]
    public long ClientId { get; init; }

    [JsonPropertyName("isLocalPlayer")]
    public bool IsLocalPlayer { get; init; }

    [JsonPropertyName("slot")]
    public string? Slot { get; init; }

    [JsonPropertyName("slotIndex")]
    public int SlotIndex { get; init; }

    [JsonPropertyName("destination")]
    public SlotOccupant? Destination { get; init; }

    /// <summary>
    ///     A <b>string</b>, not a number, and a note rather than a value: the plugin puts
    ///     the outcome of the quantity set here, for example
    ///     <c>"10 (clamped from 50, max 10)"</c>. Absent when no quantity was asked for.
    /// </summary>
    [JsonPropertyName("quantity")]
    public string? Quantity { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

/// <summary>
///     <c>/inventory/arm</c>. Spawn a Thing and put it in a hand. Works on any role,
///     including a joiner, which is what makes it different from
///     <c>/inventory/give</c>.
/// </summary>
public sealed record InventoryArmRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    /// <summary>A slot spec. Defaults to <c>activeHand</c>.</summary>
    [JsonPropertyName("hand")]
    public string? Hand { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }

    /// <summary>Metres to search for the newly spawned Thing. Defaults to 8.</summary>
    [JsonPropertyName("searchRadius")]
    public double? SearchRadius { get; init; }

    [JsonPropertyName("replace")]
    public bool? Replace { get; init; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; init; }
}

/// <summary>
///     Three waited steps behind one response: spawn, diff-find the new Thing, move it to
///     the slot. A stage-1 failure is a 409 carrying <c>stage:"spawn"</c> and
///     <see cref="Preexisting"/>.
/// </summary>
public sealed record InventoryArmResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    [JsonPropertyName("hand")]
    public string? Hand { get; init; }

    [JsonPropertyName("confirmed")]
    public bool? Confirmed { get; init; }

    /// <summary>How many candidate Things the diff-find considered.</summary>
    [JsonPropertyName("candidatesSeen")]
    public int? CandidatesSeen { get; init; }

    [JsonPropertyName("route")]
    public string? Route { get; init; }

    [JsonPropertyName("activeHand")]
    public SlotOccupant? ActiveHand { get; init; }

    [JsonPropertyName("destination")]
    public SlotOccupant? Destination { get; init; }

    /// <summary>Present only on a stage-1 failure. The value is <c>spawn</c>.</summary>
    [JsonPropertyName("stage")]
    public string? Stage { get; init; }

    /// <summary>Present only on a stage-1 failure: how many matching Things already existed.</summary>
    [JsonPropertyName("preexisting")]
    public int? Preexisting { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}
