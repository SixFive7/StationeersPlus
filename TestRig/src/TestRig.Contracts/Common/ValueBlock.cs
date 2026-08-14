using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     The rendered value of one reflected member, produced by
///     <c>ThingReflect.Describe</c>. Shared by <c>/reflect</c>, <c>/reflect/instance</c>,
///     <c>/thing</c> field rows and <c>/thing/members</c> member rows.
/// </summary>
/// <remarks>
///     <para>
///     <b><see cref="ValueType"/> is the single most expensive field in the API to
///     ignore.</b> <c>Thing.CustomColor</c> renders with
///     <c>value = "Assets.Scripts.Objects.ColorSwatch"</c> and
///     <c>matchesPrefab = true</c> whether the object is painted or not, because the
///     renderer fell through to <c>ToString()</c>. A campaign spent a day chasing a mod
///     defect that did not exist. If <see cref="ValueType"/> says the rendering is a type
///     name rather than a value, the field cannot answer the question, and the row-level
///     <c>customColorIndex</c> is the documented workaround. The PowerShell fake emitted
///     no <c>valueType</c> at all (divergence D-31), so the trap was unrepresentable.
///     </para>
///     <para>
///     <see cref="Value"/> is the one to read; <see cref="ValueJson"/> is the one to
///     compare against. Integers and Thing reference ids are rendered as <b>strings</b> in
///     both, because a JSON number parsed through double loses precision above 2^53.
///     </para>
///     <para>
///     The block is recursive: <see cref="Entries"/>, <see cref="Items"/> and
///     <see cref="KeyValue"/> are value blocks in turn.
///     </para>
/// </remarks>
public record ValueBlock
{
    /// <summary>True for a C# null and for a destroyed UnityEngine.Object.</summary>
    [JsonPropertyName("isNull")]
    public bool IsNull { get; init; }

    /// <summary>
    ///     Present and true only for a destroyed UnityEngine.Object. That is not a C#
    ///     null, but every <c>== null</c> test in the game says it is.
    /// </summary>
    [JsonPropertyName("destroyed")]
    public bool? Destroyed { get; init; }

    [JsonPropertyName("destroyedNote")]
    public string? DestroyedNote { get; init; }

    /// <summary>
    ///     The human rendering. <c>"True"</c>/<c>"False"</c> for a bool, the member name
    ///     for an enum, a comma-joined triple for a Vector3, a comma-joined quad for a
    ///     Color, and the exact invariant digits for any integer.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>
    ///     The raw JSON form: a bool, a number, a string, or an array for a vector or a
    ///     colour. Absent entirely for a dictionary or a collection. Typed as a
    ///     <see cref="JsonElement"/> because the shape genuinely varies per value.
    /// </summary>
    [JsonPropertyName("valueJson")]
    public JsonElement? ValueJson { get; init; }

    /// <summary>
    ///     The runtime type's short name. Check this before believing
    ///     <see cref="Value"/>. See the remarks on this type.
    /// </summary>
    [JsonPropertyName("valueType")]
    public string? ValueType { get; init; }

    /// <summary>Present for an enum. <see cref="ValueJson"/> carries the numeric form.</summary>
    [JsonPropertyName("enumName")]
    public string? EnumName { get; init; }

    /// <summary>Present for a dictionary or a collection.</summary>
    [JsonPropertyName("count")]
    public int? Count { get; init; }

    /// <summary>Dictionary rows, present only with <c>expand=true</c>. Each row is a value block carrying <see cref="Key"/>.</summary>
    [JsonPropertyName("entries")]
    public ValueBlock[]? Entries { get; init; }

    /// <summary>Collection items, present only with <c>expand=true</c>.</summary>
    [JsonPropertyName("items")]
    public ValueBlock[]? Items { get; init; }

    /// <summary>
    ///     The dictionary key, in two cases: the key probe echoing back what was asked,
    ///     and an expanded dictionary row naming itself.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The answer to the key probe. Matching is case-insensitive on the invariant string form.</summary>
    [JsonPropertyName("containsKey")]
    public bool? ContainsKey { get; init; }

    /// <summary>The probed key's value, when the key was found.</summary>
    [JsonPropertyName("keyValue")]
    public ValueBlock? KeyValue { get; init; }

    [JsonPropertyName("keyLookupError")]
    public string? KeyLookupError { get; init; }

    /// <summary>
    ///     Present when the value is a Thing. A <b>string</b>, unlike the numeric
    ///     <c>referenceId</c> on a <c>/thing</c> or <c>/nearby</c> row.
    /// </summary>
    [JsonPropertyName("referenceId")]
    public string? ReferenceId { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    /// <summary>A ready-made follow-up request, for example <c>GET /thing?refId=442</c>.</summary>
    [JsonPropertyName("chainWith")]
    public string? ChainWith { get; init; }
}
