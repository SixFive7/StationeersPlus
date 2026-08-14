using System.Text.Json.Serialization;
using TestRig.Contracts;

namespace TestRig.Core.Client;

/// <summary>
/// One row of <c>ClientRig/data/rig.json</c>: everything the rig remembers about an
/// instance between commands.
/// </summary>
/// <remarks>
/// Every field is nullable and read through an accessor, because entries written before a
/// field existed simply do not have it and every reader has to cope rather than assume
/// (CLIENT-020). <c>role</c> and <c>gamePort</c> are exactly that case: a rig provisioned
/// before hosting existed has neither.
///
/// A null value AND an empty string both count as absent (CLIENT-021). That is
/// load-bearing: a blank username has to fall back to the instance name rather than
/// launching an instance with no name at all.
/// </remarks>
public sealed record InstanceEntry
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = "";

    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("gamePort")]
    public int? GamePort { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("width")]
    public int? Width { get; init; }

    [JsonPropertyName("height")]
    public int? Height { get; init; }

    [JsonPropertyName("forceGameplayInput")]
    public bool? ForceGameplayInput { get; init; }

    /// <summary>
    /// The root this tree was actually built in.
    /// </summary>
    /// <remarks>
    /// What makes <c>--instances-root</c> stick past one command: start, stop, call, remove
    /// and the state reset all find the tree without the flag being re-typed, and an
    /// instance built on another volume stops being reported as unprovisioned.
    /// </remarks>
    [JsonPropertyName("instancesRoot")]
    public string? InstancesRoot { get; init; }

    [JsonPropertyName("provisionedUtc")]
    public string? ProvisionedUtc { get; init; }

    // ---- accessors, with the absent-means-default rule -------------------

    /// <summary>The provisioned role, or the given default when the field is absent or blank.</summary>
    public string RoleOr(string fallback = "client") => Blank(Role) ? fallback : Role!;

    /// <summary>The game port, or the given default when the field is absent or zero.</summary>
    public int GamePortOr(int fallback) => GamePort is null or 0 ? fallback : GamePort.Value;

    /// <summary>The ClientId, or the given default when the field is absent or blank.</summary>
    public string ClientIdOr(string fallback = "") => Blank(ClientId) ? fallback : ClientId!;

    /// <summary>The username, which falls back to the instance name.</summary>
    public string UsernameOr(string fallback) => Blank(Username) ? fallback : Username!;

    /// <summary>The recorded instances root, or the empty string.</summary>
    public string RecordedRoot => Blank(InstancesRoot) ? "" : InstancesRoot!;

    /// <summary>Whether this instance was provisioned as a host.</summary>
    public bool IsHost => string.Equals(RoleOr(), "host", StringComparison.OrdinalIgnoreCase);

    private static bool Blank(string? value) => string.IsNullOrEmpty(value);
}

/// <summary>The window block of an instance manifest.</summary>
public sealed record ManifestWindow
{
    [JsonPropertyName("forceWindowed")]
    public bool ForceWindowed { get; init; } = true;

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

/// <summary>The gameplay-input block of an instance manifest.</summary>
public sealed record ManifestGameplayInput
{
    [JsonPropertyName("force")]
    public bool Force { get; init; }

    [JsonPropertyName("everywhere")]
    public bool Everywhere { get; init; }
}

/// <summary>
/// <c>data/&lt;instance&gt;/instance.json</c>: what the plugin reads at load.
/// </summary>
/// <remarks>
/// <c>role</c> is advisory, because the plugin computes the LIVE role from the game's own
/// state and reports it on <c>/status</c>. <c>gamePort</c> is load-bearing, because
/// <c>POST /host</c> binds it. <c>peerPorts</c> carries the control port of every instance
/// in the rig, which is what lets an instance notice a sibling claiming the same ClientId
/// (CLIENT-099).
/// </remarks>
public sealed record InstanceManifest
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "client";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; init; }

    [JsonPropertyName("clientId")]
    public string ClientId { get; init; } = "";

    [JsonPropertyName("username")]
    public string Username { get; init; } = "";

    [JsonPropertyName("window")]
    public ManifestWindow Window { get; init; } = new();

    [JsonPropertyName("gameplayInput")]
    public ManifestGameplayInput GameplayInput { get; init; } = new();

    [JsonPropertyName("savePath")]
    public string SavePath { get; init; } = "";

    [JsonPropertyName("desktop")]
    public string Desktop { get; init; } = "";

    [JsonPropertyName("rigRoot")]
    public string RigRoot { get; init; } = "";

    [JsonPropertyName("peerPorts")]
    public int[] PeerPorts { get; init; } = [];
}

/// <summary>
/// <c>data/&lt;instance&gt;/provision.stamp</c>: when this tree was built and out of what.
/// </summary>
/// <remarks>
/// Nothing recorded any of this before, so "is this instance stale" had no answer short of
/// comparing file times by hand. <see cref="LauncherHostname"/> is read by nothing today
/// and is kept anyway (CLIENT-095): it is the only field that would identify a tree built
/// on a different machine, which matters the moment an instances root lands on shared
/// storage.
/// </remarks>
public sealed record ProvisionStamp
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = "";

    [JsonPropertyName("provisionedUtc")]
    public string ProvisionedUtc { get; init; } = "";

    [JsonPropertyName("role")]
    public string Role { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; init; }

    /// <summary>Where the tree went. The registry entry is the authority; this is for a human.</summary>
    [JsonPropertyName("tree")]
    public string Tree { get; init; } = "";

    [JsonPropertyName("sourceInstall")]
    public string SourceInstall { get; init; } = "";

    [JsonPropertyName("sourceVersion")]
    public string SourceVersion { get; init; } = "";

    [JsonPropertyName("pluginBuiltUtc")]
    public string PluginBuiltUtc { get; init; } = "";

    [JsonPropertyName("launcherHostname")]
    public string LauncherHostname { get; init; } = "";
}

/// <summary>One row of a <c>snapshot</c>: an instance's control-plane answer, or why there is none.</summary>
public sealed record SnapshotRow
{
    [JsonPropertyName("instanceName")]
    public string InstanceName { get; init; } = "";

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("status")]
    public StatusResponse? Status { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
/// The source-generated serializer for the client half's own on-disk files.
/// </summary>
/// <remarks>
/// Separate from <c>RigJsonContext</c>, which covers the wire contract. These types never
/// travel over HTTP; they are the rig's own state. Under AOT a type missing from a context
/// throws at runtime rather than falling back to reflection, so both lists are exhaustive
/// by necessity.
///
/// <c>WriteIndented</c> is on because a human reads <c>rig.json</c> and
/// <c>provision.stamp</c> while diagnosing a rig, and the PowerShell's
/// <c>ConvertTo-Json</c> produced indented output too.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(InstanceEntry))]
[JsonSerializable(typeof(InstanceEntry[]))]
[JsonSerializable(typeof(List<InstanceEntry>))]
[JsonSerializable(typeof(InstanceManifest))]
[JsonSerializable(typeof(ProvisionStamp))]
[JsonSerializable(typeof(SnapshotRow))]
[JsonSerializable(typeof(SnapshotRow[]))]
public sealed partial class ClientJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
