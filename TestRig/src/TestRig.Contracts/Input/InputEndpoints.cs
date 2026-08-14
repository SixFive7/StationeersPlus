using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /input/key, /input/scroll, /input/mouse, /input/mouseposition, /input/releaseall,
// /input/clear, /input/keymap, /input/enable.
//
// The contract these routes exist to make assertable: consumed = delivered AND the
// gameplay gate was open. "settled" only means the requested frames elapsed, and a caller
// that reads it as success is measuring wall clock, not input.

/// <summary>The gate as an <c>/input/key</c> response reports it. Narrower than <see cref="InputGateBlock"/>.</summary>
public sealed record KeyGateBlock
{
    [JsonPropertyName("open")]
    public bool Open { get; init; }

    [JsonPropertyName("shutReason")]
    public string? ShutReason { get; init; }

    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; }

    [JsonPropertyName("consoleOpen")]
    public bool ConsoleOpen { get; init; }

    [JsonPropertyName("keyInputState")]
    public string? KeyInputState { get; init; }

    /// <summary>How many times <c>KeyMap.PollInputs</c> ran while the key was held down.</summary>
    [JsonPropertyName("keyMapPollRan")]
    public int KeyMapPollRan { get; init; }

    [JsonPropertyName("inventoryManagerUpdateRan")]
    public int InventoryManagerUpdateRan { get; init; }

    [JsonPropertyName("normalModeRan")]
    public int NormalModeRan { get; init; }
}

/// <summary>The gate as an <c>/input/scroll</c> response reports it.</summary>
public sealed record ScrollGateBlock
{
    [JsonPropertyName("open")]
    public bool Open { get; init; }

    [JsonPropertyName("shutReason")]
    public string? ShutReason { get; init; }

    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; }

    [JsonPropertyName("consoleOpen")]
    public bool ConsoleOpen { get; init; }

    /// <summary>The consumption signal for a scroll: non-zero is what makes <c>consumed</c> true.</summary>
    [JsonPropertyName("checkDisplaySlotInputRan")]
    public int CheckDisplaySlotInputRan { get; init; }

    [JsonPropertyName("normalModeRan")]
    public int NormalModeRan { get; init; }
}

/// <summary>How many times the game read the key while it was held.</summary>
public sealed record ObservedKeyReads
{
    [JsonPropertyName("getKey")]
    public int GetKey { get; init; }

    [JsonPropertyName("getKeyDown")]
    public int GetKeyDown { get; init; }

    [JsonPropertyName("getKeyUp")]
    public int GetKeyUp { get; init; }
}

/// <summary><c>/input/key</c>. Press a key the game will actually read.</summary>
public sealed record InputKeyRequest
{
    /// <summary>
    ///     A <c>KeyCode</c> name or a <c>KeyMap</c> action name. Required; 400
    ///     <c>missing 'key'</c> otherwise, and 400 naming <c>/input/keymap</c> for an
    ///     unknown one.
    /// </summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary><c>tap</c>, <c>down</c> or <c>up</c>. Defaults to <c>tap</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    /// <summary>Frames to hold. Defaults to 3.</summary>
    [JsonPropertyName("frames")]
    public int? Frames { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    /// <summary>
    ///     Defaults to <b>true</b>, which turns an unconsumed key into a 409 instead of a
    ///     silent success. Passing false is how a caller asks for a key press it does not
    ///     expect the game to read.
    /// </summary>
    [JsonPropertyName("requireConsumed")]
    public bool? RequireConsumed { get; init; }
}

/// <summary>
///     Whether the key landed. Not sealed: <see cref="InputMouseResponse"/> derives from
///     it because <c>/input/mouse</c> delegates to the same handler, and deriving is what
///     stops the two shapes drifting apart.
/// </summary>
/// <remarks>
///     <see cref="Consumed"/> is <c>delivered AND gate.open</c>, and 200 means
///     <c>consumed || !requireConsumed</c>. <see cref="Settled"/> only means the frames
///     elapsed, which is why it carries its own <see cref="SettledMeans"/> string.
/// </remarks>
public record InputKeyResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    /// <summary><c>KeyCode</c> or <c>KeyMap.&lt;Field&gt;</c>.</summary>
    [JsonPropertyName("resolvedVia")]
    public string? ResolvedVia { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    /// <summary>The one to assert on. Equals <c>delivered AND gate.open</c>.</summary>
    [JsonPropertyName("consumed")]
    public bool Consumed { get; init; }

    /// <summary>The override was installed and the game read it. Not the same as consumed.</summary>
    [JsonPropertyName("delivered")]
    public bool Delivered { get; init; }

    [JsonPropertyName("observed")]
    public ObservedKeyReads? Observed { get; init; }

    [JsonPropertyName("gate")]
    public KeyGateBlock? Gate { get; init; }

    /// <summary>Only means the requested frames elapsed. See <see cref="SettledMeans"/>.</summary>
    [JsonPropertyName("settled")]
    public bool Settled { get; init; }

    [JsonPropertyName("settledMeans")]
    public string? SettledMeans { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/input/scroll</c>. One frame is one notch.</summary>
public sealed record InputScrollRequest
{
    [JsonPropertyName("notches")]
    public double? Notches { get; init; }

    /// <summary>Defaults to <b>1</b>, not 3: one frame is one notch.</summary>
    [JsonPropertyName("frames")]
    public int? Frames { get; init; }

    /// <summary>Minimum 1.</summary>
    [JsonPropertyName("repeat")]
    public int? Repeat { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    /// <summary>Frames between repeats. Minimum 1, defaults to 3.</summary>
    [JsonPropertyName("gapFrames")]
    public int? GapFrames { get; init; }

    [JsonPropertyName("requireConsumed")]
    public bool? RequireConsumed { get; init; }
}

/// <summary>
///     Whether the scroll landed. Here <c>consumed = delivered AND
///     gate.checkDisplaySlotInputRan &gt; 0</c>, a different rule from
///     <see cref="InputKeyResponse"/>. A settle failure is <b>504</b>, not 409.
/// </summary>
public sealed record InputScrollResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("notches")]
    public double Notches { get; init; }

    [JsonPropertyName("frames")]
    public int Frames { get; init; }

    [JsonPropertyName("repeat")]
    public int Repeat { get; init; }

    [JsonPropertyName("consumed")]
    public bool Consumed { get; init; }

    [JsonPropertyName("delivered")]
    public bool Delivered { get; init; }

    [JsonPropertyName("scrollReads")]
    public int ScrollReads { get; init; }

    [JsonPropertyName("gate")]
    public ScrollGateBlock? Gate { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/input/mouse</c>. A mouse button, as <c>KeyCode.Mouse0 + button</c>.</summary>
public sealed record InputMouseRequest
{
    /// <summary>0 is the left button. Maps to <c>KeyCode.Mouse0 + button</c>.</summary>
    [JsonPropertyName("button")]
    public int? Button { get; init; }

    /// <summary><c>tap</c>, <c>down</c> or <c>up</c>.</summary>
    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("frames")]
    public int? Frames { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("requireConsumed")]
    public bool? RequireConsumed { get; init; }
}

/// <summary>
///     Identical to <see cref="InputKeyResponse"/> by construction: the router delegates
///     <c>/input/mouse</c> straight into the <c>/input/key</c> handler, so the shapes
///     cannot be allowed to differ.
/// </summary>
public sealed record InputMouseResponse : InputKeyResponse;

/// <summary><c>/input/mouseposition</c>. Override or release the reported cursor position.</summary>
public sealed record InputMousePositionRequest
{
    /// <summary>Drops the override and hands the position back to the real mouse.</summary>
    [JsonPropertyName("clear")]
    public bool? Clear { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    /// <summary>Minimum 1, defaults to 2.</summary>
    [JsonPropertyName("frames")]
    public int? Frames { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }
}

/// <summary>Always 200; there is no <c>requireConsumed</c> equivalent here.</summary>
public sealed record InputMousePositionResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("cleared")]
    public bool Cleared { get; init; }

    [JsonPropertyName("x")]
    public double X { get; init; }

    [JsonPropertyName("y")]
    public double Y { get; init; }

    [JsonPropertyName("settled")]
    public bool Settled { get; init; }

    [JsonPropertyName("delivered")]
    public bool Delivered { get; init; }

    [JsonPropertyName("reads")]
    public int Reads { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/input/releaseall</c>. No parameters. Lifts every held key.</summary>
public sealed record InputReleaseAllRequest;

/// <summary><c>{"ok":true}</c> and nothing else.</summary>
public sealed record InputReleaseAllResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary><c>/input/clear</c>. No parameters. Drops every override, held or not.</summary>
public sealed record InputClearRequest;

/// <summary><c>{"ok":true}</c> and nothing else.</summary>
public sealed record InputClearResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary><c>/input/keymap</c>. No parameters, never touches the main thread.</summary>
public sealed record InputKeyMapRequest;

/// <summary>Each entry is <c>"&lt;Field&gt;=&lt;KeyCode&gt;"</c>, sorted. These are the action names <c>/input/key</c> accepts.</summary>
public sealed record InputKeyMapResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("actions")]
    public string[]? Actions { get; init; }
}

/// <summary><c>/input/enable</c>. Turns injection on or off wholesale.</summary>
public sealed record InputEnableRequest
{
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }
}

/// <summary>The resulting state. Never touches the main thread.</summary>
public sealed record InputEnableResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}
