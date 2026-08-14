using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     What is in a slot. The wire has three states, and this record covers all of them:
///     the whole property is <c>null</c> when there is no such slot, <c>empty:true</c>
///     alone when the slot exists and holds nothing, and <c>empty:false</c> plus the
///     occupant's identity otherwise.
/// </summary>
public sealed record SlotOccupant
{
    [JsonPropertyName("empty")]
    public bool Empty { get; init; }

    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>The stack size, or 1 for anything that is not Stackable.</summary>
    [JsonPropertyName("quantity")]
    public int? Quantity { get; init; }

    /// <summary>Present and true only for a SprayCan.</summary>
    [JsonPropertyName("isSprayCan")]
    public bool? IsSprayCan { get; init; }

    /// <summary>The ColorSwatch index the can's paint material resolves to, or -1.</summary>
    [JsonPropertyName("paintColorIndex")]
    public int? PaintColorIndex { get; init; }

    [JsonPropertyName("paintMaterial")]
    public string? PaintMaterial { get; init; }

    [JsonPropertyName("paintColorName")]
    public string? PaintColorName { get; init; }

    /// <summary>Present and true only for a SprayGun.</summary>
    [JsonPropertyName("isSprayGun")]
    public bool? IsSprayGun { get; init; }

    /// <summary>The loaded can's colour index, or -1 when the gun is empty.</summary>
    [JsonPropertyName("loadedCanColorIndex")]
    public int? LoadedCanColorIndex { get; init; }
}

/// <summary>One row of the <c>/inventory</c> slot list.</summary>
public sealed record InventorySlotRow
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("isHandSlot")]
    public bool IsHandSlot { get; init; }

    /// <summary>
    ///     Emitted only for the local player. Which hand is active is client-local UI
    ///     state and is never replicated.
    /// </summary>
    [JsonPropertyName("isActiveHand")]
    public bool? IsActiveHand { get; init; }

    [JsonPropertyName("occupant")]
    public SlotOccupant? Occupant { get; init; }
}

/// <summary>
///     The Thing under the cursor, as <c>/player</c> reports it.
/// </summary>
public sealed record CursorTarget
{
    [JsonPropertyName("referenceId")]
    public long ReferenceId { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("paintable")]
    public bool Paintable { get; init; }

    /// <summary>Swatch index, or -1 when unpainted. Not <c>colorIndex</c>.</summary>
    [JsonPropertyName("customColorIndex")]
    public int CustomColorIndex { get; init; }

    /// <summary>An array <c>[x,y,z]</c>, never an object.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }
}

/// <summary>
///     The local character. Everything except <see cref="Present"/> is absent when there
///     is no local Human.
/// </summary>
/// <remarks>
///     The PowerShell fake had no <c>player</c> wrapper at all and put a bare
///     <c>position</c> object at the top level (divergences D-51, D-52, D-53). The real
///     <c>/player</c> response nests this block under <c>player</c>, and
///     <see cref="Position"/> is an <b>array</b>, so a selector written as
///     <c>position.x</c> reads null against the live endpoint while working perfectly
///     against the fake.
/// </remarks>
public sealed record PlayerBlock
{
    [JsonPropertyName("present")]
    public bool Present { get; init; }

    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>
    ///     An array <c>[x,y,z]</c>. <c>Json.Obj.Vec</c> emits three bare numbers in
    ///     brackets; there is no <c>{x,y,z}</c> object form anywhere in the API.
    /// </summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    /// <summary>An array <c>[x,y,z]</c> of Euler angles.</summary>
    [JsonPropertyName("rotationEuler")]
    public double[]? RotationEuler { get; init; }

    [JsonPropertyName("dead")]
    public bool? Dead { get; init; }

    [JsonPropertyName("lookPitch")]
    public double? LookPitch { get; init; }

    [JsonPropertyName("lookYaw")]
    public double? LookYaw { get; init; }

    [JsonPropertyName("cameraPosition")]
    public double[]? CameraPosition { get; init; }

    [JsonPropertyName("cameraOrigin")]
    public double[]? CameraOrigin { get; init; }

    [JsonPropertyName("thirdPerson")]
    public bool? ThirdPerson { get; init; }

    [JsonPropertyName("activeHand")]
    public SlotOccupant? ActiveHand { get; init; }

    [JsonPropertyName("inactiveHand")]
    public SlotOccupant? InactiveHand { get; init; }

    [JsonPropertyName("activeHandSlotId")]
    public int? ActiveHandSlotId { get; init; }

    [JsonPropertyName("cursorTarget")]
    public CursorTarget? CursorTarget { get; init; }

    /// <summary>Set instead of the hand slots when reading them threw.</summary>
    [JsonPropertyName("handsError")]
    public string? HandsError { get; init; }

    /// <summary>Set instead of <see cref="CursorTarget"/> when reading it threw.</summary>
    [JsonPropertyName("cursorError")]
    public string? CursorError { get; init; }
}

/// <summary>
///     One row of the server-side roster. Empty on anything that is not a server:
///     <c>NetworkBase.Clients</c> is a static list a joined client never fills, which is
///     what makes the roster the server's answer rather than a joiner's guess.
/// </summary>
public sealed record ConnectedClient
{
    /// <summary>
    ///     A <b>string</b>. A JSON number goes through double on the reading side and
    ///     silently loses precision above 2^53, and a truncated ClientId is exactly the
    ///     failure these ids exist to detect.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary>
    ///     The connection state, which distinguishes a half-joined client from a settled
    ///     one. The PowerShell fake's roster rows omitted it (divergence D-14), and that
    ///     is precisely the instant the joiner poll waits on.
    /// </summary>
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("isHost")]
    public bool IsHost { get; init; }

    [JsonPropertyName("connectionId")]
    public int? ConnectionId { get; init; }
}
