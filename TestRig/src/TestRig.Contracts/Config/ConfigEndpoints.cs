using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /config, /config/set, /config/reload.
//
// ALL THREE RETURN {ok:false, error} AT HTTP 200 ON A LOOKUP FAILURE. That is the exact
// asymmetry RigResult exists for: a refusal elsewhere in the API is a 409 and arrives as a
// transport failure, while a config lookup failure arrives as a perfectly ordinary 200
// response with ok:false in it. The PowerShell fake had no `ok` field on /config at all
// (divergence D-24), so the two paths through the harness were never compared.

/// <summary>One BepInEx <c>ConfigEntry</c>, rendered.</summary>
public sealed record ConfigEntryRow
{
    /// <summary>The <c>Config.Bind</c> section, which is also the settings-panel group header.</summary>
    [JsonPropertyName("section")]
    public string? Section { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>The setting type's short name: <c>Boolean</c>, <c>String</c>, <c>Int32</c> and so on.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    ///     <c>boxed.ToString()</c>, so <c>"True"</c>/<c>"False"</c> for a bool and the
    ///     member NAME for an enum. The fake rendered every value as the literal
    ///     <c>'x'</c> (divergence D-27), which is why the bool-as-text comparison path was
    ///     never exercised through a config read.
    /// </summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>The bound default, same rendering. <c>default</c> is a C# keyword, hence the property name.</summary>
    [JsonPropertyName("default")]
    public string? Default { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary><c>/config</c>. Dump one plugin's settings.</summary>
public sealed record ConfigRequest
{
    /// <summary>The plugin GUID. Required in practice: without it the response is <c>ok:false, error:"missing 'guid'"</c> at 200.</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    /// <summary>Case-insensitive substring matched against <c>"&lt;Section&gt; / &lt;Key&gt;"</c>.</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; init; }
}

/// <summary>
///     The settings as they are live in the process. <see cref="ConfigPath"/> pins which
///     <c>.cfg</c> on disk they came from, which the fake omitted (divergence D-25).
/// </summary>
public sealed record ConfigResponse : IWireResult
{
    /// <summary>False at HTTP <b>200</b> on a lookup failure. Never infer success from the status.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>For example <c>no plugin with GUID '&lt;g&gt;' found in any loaded assembly</c>.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Echoed back. The fake ignored the parameter and always answered with its own (divergence D-28).</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("configPath")]
    public string? ConfigPath { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("entries")]
    public ConfigEntryRow[]? Entries { get; init; }
}

/// <summary><c>/config/set</c>. Change one setting.</summary>
public sealed record ConfigSetRequest
{
    /// <summary>Required.</summary>
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    /// <summary>Null matches any section, which is what makes a key alone usually enough.</summary>
    [JsonPropertyName("section")]
    public string? Section { get; init; }

    /// <summary>Required.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary>Required. Parsed against the entry's declared type.</summary>
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    /// <summary>Defaults to <b>true</b>, which writes the <c>.cfg</c> file to disk.</summary>
    [JsonPropertyName("save")]
    public bool? Save { get; init; }
}

/// <summary>What the setting was and what it now is. <c>ok:false</c> arrives at HTTP 200.</summary>
public sealed record ConfigSetResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("section")]
    public string? Section { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("oldValue")]
    public string? OldValue { get; init; }

    [JsonPropertyName("newValue")]
    public string? NewValue { get; init; }

    [JsonPropertyName("savedToDisk")]
    public bool SavedToDisk { get; init; }
}

/// <summary><c>/config/reload</c>. Re-read a plugin's <c>.cfg</c> from disk.</summary>
public sealed record ConfigReloadRequest
{
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }
}

/// <summary><c>ok:false</c> arrives at HTTP 200.</summary>
public sealed record ConfigReloadResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("guid")]
    public string? Guid { get; init; }
}
