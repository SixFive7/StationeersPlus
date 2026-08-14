using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     Whether this process holds the foreground, and on which Win32 desktop. Read-only:
///     nothing in the plugin can focus, raise or activate a window, which is the whole
///     reason a driven instance does not steal the developer's screen.
/// </summary>
/// <remarks>
///     The desktop comparison comes first because <c>GetForegroundWindow</c> returns NULL
///     on a non-input desktop, and every conclusion drawn from that NULL is wrong.
/// </remarks>
public sealed record ForegroundBlock
{
    /// <summary><c>foreground</c>, <c>background</c>, <c>otherDesktop</c>, <c>noForeground</c> or <c>unknown</c>.</summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    [JsonPropertyName("holdsForeground")]
    public bool HoldsForeground { get; init; }

    [JsonPropertyName("ownPid")]
    public int OwnPid { get; init; }

    /// <summary>Null, not zero, for <c>otherDesktop</c> and <c>unknown</c>.</summary>
    [JsonPropertyName("foregroundPid")]
    public int? ForegroundPid { get; init; }

    [JsonPropertyName("ownDesktop")]
    public string? OwnDesktop { get; init; }

    [JsonPropertyName("inputDesktop")]
    public string? InputDesktop { get; init; }

    [JsonPropertyName("onInputDesktop")]
    public bool OnInputDesktop { get; init; }

    /// <summary><c>Application.isFocused</c>. Documented unreliable; the verdict above is the answer.</summary>
    [JsonPropertyName("unityIsFocused")]
    public bool UnityIsFocused { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary>The console tee's own health: a per-source ring that evicts boot output when it fills.</summary>
public sealed record ConsoleTeeBlock
{
    [JsonPropertyName("maxLinesPerSource")]
    public int MaxLinesPerSource { get; init; }

    [JsonPropertyName("maxCharsPerLine")]
    public int MaxCharsPerLine { get; init; }

    [JsonPropertyName("maxCharsPerSource")]
    public long MaxCharsPerSource { get; init; }

    [JsonPropertyName("consoleLines")]
    public int ConsoleLines { get; init; }

    [JsonPropertyName("consoleChars")]
    public long ConsoleChars { get; init; }

    [JsonPropertyName("bepInExLines")]
    public int BepInExLines { get; init; }

    [JsonPropertyName("bepInExChars")]
    public long BepInExChars { get; init; }
}

/// <summary>The plugin's own health, carried on <c>/status</c>.</summary>
public sealed record DriverBlock
{
    [JsonPropertyName("pumpFrames")]
    public long PumpFrames { get; init; }

    [JsonPropertyName("pumpItems")]
    public long PumpItems { get; init; }

    /// <summary><c>Update</c>, <c>Frame</c>, <c>Fallback</c> or <c>none</c>.</summary>
    [JsonPropertyName("lastPump")]
    public string? LastPump { get; init; }

    [JsonPropertyName("fallbackPumpUsed")]
    public bool FallbackPumpUsed { get; init; }

    [JsonPropertyName("pumpObjectCreations")]
    public long PumpObjectCreations { get; init; }

    [JsonPropertyName("pluginDestroyCount")]
    public long PluginDestroyCount { get; init; }

    [JsonPropertyName("serverRunning")]
    public bool ServerRunning { get; init; }

    [JsonPropertyName("serverRequests")]
    public long ServerRequests { get; init; }

    [JsonPropertyName("serverLastAcceptError")]
    public string? ServerLastAcceptError { get; init; }

    [JsonPropertyName("consoleTapPatched")]
    public bool ConsoleTapPatched { get; init; }

    [JsonPropertyName("bepInExTapAttached")]
    public bool BepInExTapAttached { get; init; }

    [JsonPropertyName("inputEnabled")]
    public bool InputEnabled { get; init; }

    /// <summary>A comma-joined string, not an array.</summary>
    [JsonPropertyName("heldKeys")]
    public string? HeldKeys { get; init; }

    [JsonPropertyName("keyOverrides")]
    public long KeyOverrides { get; init; }

    [JsonPropertyName("scrollOverrides")]
    public long ScrollOverrides { get; init; }

    [JsonPropertyName("consoleNextSeq")]
    public long ConsoleNextSeq { get; init; }

    [JsonPropertyName("consoleDropped")]
    public long ConsoleDropped { get; init; }

    [JsonPropertyName("consoleTruncated")]
    public long ConsoleTruncated { get; init; }

    [JsonPropertyName("consoleTee")]
    public ConsoleTeeBlock? ConsoleTee { get; init; }
}

/// <summary>One recorded join step. Captured while the attempt is live, before cleanup wipes the evidence.</summary>
public sealed record JoinTraceEvent
{
    [JsonPropertyName("ms")]
    public long Ms { get; init; }

    [JsonPropertyName("kind")]
    public string? Kind { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

/// <summary>
///     What the join recorder saw. <see cref="Patched"/> false means the recorder never
///     installed and every empty trace below it is meaningless.
/// </summary>
public sealed record JoinTraceBlock
{
    [JsonPropertyName("armed")]
    public bool Armed { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("elapsedMs")]
    public long ElapsedMs { get; init; }

    [JsonPropertyName("events")]
    public int Events { get; init; }

    [JsonPropertyName("droppedEvents")]
    public int DroppedEvents { get; init; }

    [JsonPropertyName("patched")]
    public bool Patched { get; init; }

    [JsonPropertyName("hooks")]
    public string[]? Hooks { get; init; }

    [JsonPropertyName("trace")]
    public JoinTraceEvent[]? Trace { get; init; }
}

/// <summary>The RakNet peer as <c>/diag/join</c> and a failed <c>/connect</c> report it.</summary>
public sealed record PeerProbeBlock
{
    [JsonPropertyName("managerPresent")]
    public bool ManagerPresent { get; init; }

    [JsonPropertyName("handleNull")]
    public bool HandleNull { get; init; }

    [JsonPropertyName("connections")]
    public int Connections { get; init; }

    [JsonPropertyName("slots")]
    public string[]? Slots { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("active")]
    public bool? Active { get; init; }
}

/// <summary>
///     A confirmation dialog, as <c>/modal</c> reports it and as <c>/connect</c> and
///     <c>/host</c> splice it into a failure under the key <c>dialog</c>.
/// </summary>
/// <remarks>
///     <see cref="Visible"/> is <c>panelActive AND the data stack is non-empty</c>.
///     <c>IsVisible</c> alone is true for a short window during boot with nothing behind
///     it, and reporting that as a dialog makes a connect poll bail out for no reason.
/// </remarks>
public sealed record ModalBlock
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("panelActive")]
    public bool PanelActive { get; init; }

    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("button1")]
    public string? Button1 { get; init; }

    [JsonPropertyName("button2")]
    public string? Button2 { get; init; }

    [JsonPropertyName("button3")]
    public string? Button3 { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>Enter/exit counts for one patched method in the input call chain.</summary>
public sealed record ChainLinkCounts
{
    [JsonPropertyName("enter")]
    public long Enter { get; init; }

    [JsonPropertyName("exit")]
    public long Exit { get; init; }

    /// <summary><c>enter - exit</c>. A persistent non-zero means a link is wedged.</summary>
    [JsonPropertyName("unbalanced")]
    public long Unbalanced { get; init; }

    [JsonPropertyName("lastEnterFrame")]
    public long LastEnterFrame { get; init; }
}

/// <summary>
///     The input call chain, one entry per patched method. The keys carry dots because
///     they are <c>Type.Method</c> as the probe names them.
/// </summary>
public sealed record ChainBlock
{
    [JsonPropertyName("GameManager.Update")]
    public ChainLinkCounts? GameManagerUpdate { get; init; }

    [JsonPropertyName("KeyManager.ManagerUpdate")]
    public ChainLinkCounts? KeyManagerUpdate { get; init; }

    [JsonPropertyName("KeyMap.PollInputs")]
    public ChainLinkCounts? KeyMapPollInputs { get; init; }

    [JsonPropertyName("InventoryManager.ManagerUpdate")]
    public ChainLinkCounts? InventoryManagerUpdate { get; init; }

    [JsonPropertyName("InventoryManager.CheckDisplaySlotInput")]
    public ChainLinkCounts? InventoryManagerCheckDisplaySlotInput { get; init; }

    [JsonPropertyName("InventoryManager.NormalMode")]
    public ChainLinkCounts? InventoryManagerNormalMode { get; init; }

    [JsonPropertyName("installed")]
    public string[]? Installed { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary>Which Unity input methods this plugin actually has patched.</summary>
public sealed record InputPatchesBlock
{
    [JsonPropertyName("patchUnityInput")]
    public bool PatchUnityInput { get; init; }

    [JsonPropertyName("inputInjectionEnabled")]
    public bool InputInjectionEnabled { get; init; }

    [JsonPropertyName("getKey")]
    public bool GetKey { get; init; }

    [JsonPropertyName("getKeyDown")]
    public bool GetKeyDown { get; init; }

    [JsonPropertyName("mouseScrollDelta")]
    public bool MouseScrollDelta { get; init; }
}

/// <summary>
///     The gameplay input gate as <c>/diag/input</c> reports it: the full form, with the
///     counters. The per-request forms on <c>/input/key</c> and <c>/input/scroll</c> are
///     narrower and are separate types.
/// </summary>
public sealed record InputGateBlock
{
    [JsonPropertyName("forceGameplayInput")]
    public bool ForceGameplayInput { get; init; }

    [JsonPropertyName("everywhere")]
    public bool Everywhere { get; init; }

    [JsonPropertyName("inWorld")]
    public bool InWorld { get; init; }

    [JsonPropertyName("gateOpen")]
    public bool GateOpen { get; init; }

    [JsonPropertyName("shutReason")]
    public string? ShutReason { get; init; }

    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; }

    [JsonPropertyName("consoleOpen")]
    public bool ConsoleOpen { get; init; }

    [JsonPropertyName("modalUp")]
    public bool ModalUp { get; init; }

    [JsonPropertyName("keyInputState")]
    public string? KeyInputState { get; init; }

    [JsonPropertyName("cursorLockState")]
    public string? CursorLockState { get; init; }

    [JsonPropertyName("gateAsserts")]
    public long GateAsserts { get; init; }

    [JsonPropertyName("cursorForcedHiddenCount")]
    public long CursorForcedHiddenCount { get; init; }

    [JsonPropertyName("skippedNotInWorld")]
    public long SkippedNotInWorld { get; init; }

    [JsonPropertyName("skippedModalUp")]
    public long SkippedModalUp { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary>
///     Window state. Managed only: the plugin rewrites the game's own settings and calls
///     <c>Screen.SetResolution</c>, which resizes but never activates.
/// </summary>
public sealed record WindowBlock
{
    [JsonPropertyName("forceWindowed")]
    public bool ForceWindowed { get; init; }

    [JsonPropertyName("configuredWidth")]
    public int ConfiguredWidth { get; init; }

    [JsonPropertyName("configuredHeight")]
    public int ConfiguredHeight { get; init; }

    [JsonPropertyName("screenWidth")]
    public int? ScreenWidth { get; init; }

    [JsonPropertyName("screenHeight")]
    public int? ScreenHeight { get; init; }

    [JsonPropertyName("screenFullScreen")]
    public bool? ScreenFullScreen { get; init; }

    [JsonPropertyName("screenFullScreenMode")]
    public string? ScreenFullScreenMode { get; init; }

    [JsonPropertyName("setResolutionCalls")]
    public long SetResolutionCalls { get; init; }

    [JsonPropertyName("settingsRewrites")]
    public long SettingsRewrites { get; init; }

    [JsonPropertyName("lastAssertReason")]
    public string? LastAssertReason { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }

    /// <summary>
    ///     False means the Settings type was not found by scan. The scan exists because
    ///     more than one loaded assembly carries a type called <c>Settings</c> and
    ///     resolving by bare name picked the wrong one.
    /// </summary>
    [JsonPropertyName("settingsTypeFound")]
    public bool SettingsTypeFound { get; init; }
}
