---
title: IC10SourceLimits
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-03
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: InputSourceCode (MAX_LINES, MAX_FILE_SIZE, LINE_LENGTH_LIMIT, Paste, Copy, UpdateFileSize, HandleInput, Initialize, gutter label $"{i}.")
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: ProgrammableChip.SetSourceCode / ProgrammableChip._LineOfCode (label-detect _JumpTags.Add(key, lineNumber)) / _DEFINE_Operation / DoubleValueVariable
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: AsciiString (ctor, ParseLine)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Regexes (PreprocessHashes, PreprocessStrings, PreprocessBinary, PreprocessHex)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: ProgrammableChipSaveData (NextAddr, JumpTagsKeys/Values) ; ProgrammableChip save/load (Serialize/Deserialize _NextAddr and _JumpTags) ; AppendErrorsToActionInstance / ErrorLineNumberString / GameStrings.ProgrammableChipErrorCode
related:
  - ./IC10SyntaxHighlighting.md
  - ./IC10DeviceAddressing.md
  - ../GameClasses/ProgrammableChip.md
tags: [ic10, logic, ui]
---

# IC10SourceLimits

Source-size and symbol-count limits that govern whether IC10 source loads into a ProgrammableChip. There are TWO distinct ingestion paths with DIFFERENT enforcement:

1. The in-game code editor (`InputSourceCode`, the source-edit window opened on a computer/laptop/tablet, plus paste-from-clipboard and the script library) enforces hard caps: 128 lines, 4096 characters total, 90 characters per line.
2. The runtime `ProgrammableChip.SetSourceCode(string)` (used by deserialize-on-join, network sync, save load, and any modded direct call) enforces NO line-count, total-size, or per-line cap. It splits on `\n` and adds one `_LineOfCode` per element with no length validation.

The practical limit a human hits is the editor's, because the editor is the only way a player types or pastes a script. A 234-physical-line, ~6 KB file cannot be entered through the editor.

## Editor caps: InputSourceCode
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

The code-edit window is class `InputSourceCode` (a `UserInterfaceBase`). It declares three constants:

```csharp
public const int MAX_FILE_SIZE = 4096;
public const int MAX_LINES = 128;
public const int LINE_LENGTH_LIMIT = 90;
```

`Initialize()` instantiates exactly 128 fixed line slots, each a `TMP_InputField` with `characterLimit = 90`:

```csharp
LinesOfCode = new List<EditorLineOfCode>(128);
for (int i = 0; i < 128; i++)
{
    EditorLineOfCode editorLineOfCode = UnityEngine.Object.Instantiate(LineOfCodePrefab, LineParent);
    editorLineOfCode.Parent = this;
    editorLineOfCode.LineNumber.text = $"{i}.";
    editorLineOfCode.name = $"~LineOfCode_{i}";
    LinesOfCode.Add(editorLineOfCode);
    editorLineOfCode.GetComponent<TMP_InputField>().characterLimit = 90;
}
```

The 128 slots are permanent. There is no "add line beyond 128" path: pressing Enter (`HandleInput`, KeyCode.Return) recycles `LinesOfCode[127]` (the last slot) and re-sorts; it never grows the list. So the editor can hold at most 128 lines, period.

### 128-line cap: lines 129+ silently dropped on paste
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`Paste(string)` (clipboard paste, "Open" from the script library, and `ShowInputPanel`'s default-text fill all route through it) iterates over the 128 fixed slots, NOT over the pasted lines:

```csharp
public static void Paste(string value)
{
    Instance.AcceptedStrings = new List<string>();
    if (string.IsNullOrEmpty(value)) value = string.Empty;
    value = value.TrimEnd();
    string[] array = value.Split('\n');
    for (int i = 0; i < Instance.LinesOfCode.Count; i++)   // Count == 128
    {
        EditorLineOfCode editorLineOfCode = Instance.LinesOfCode[i];
        string text = ((i >= array.Length) ? string.Empty : array[i].TrimEnd());
        text = AsciiString.ParseLine(text, 90);
        editorLineOfCode.InputField.text = text;
        editorLineOfCode.ReformatText(text);
    }
    Instance.UpdateFileSize();
}
```

When `array.Length > 128`, indices 128..array.Length-1 are never read. Pasting a 234-line script silently keeps only physical lines 1..128 (`array[0]`..`array[127]`) and discards lines 129..234. No error, no warning: the extra lines are simply not consumed. `Copy()` (which feeds `OnSave` / `ButtonInputSubmit`) only ever serializes the 128 slots, so the dropped lines are gone.

### 4096-character cap: Submit button disabled over limit
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`UpdateFileSize()` recomputes `_fileSize` after every edit/paste and gates the Submit button on it:

```csharp
public void UpdateFileSize()
{
    _fileSize = 0;
    int num = 0;
    for (int num2 = LinesOfCode.Count - 1; num2 >= 0; num2--)
    {
        num = num2;
        if (LinesOfCode[num2].Text.Length > 0) break;   // num = index of last non-empty line
    }
    for (int i = 0; i < LinesOfCode.Count; i++)
    {
        EditorLineOfCode editorLineOfCode = LinesOfCode[i];
        _fileSize += editorLineOfCode.Text.Length;
        if (i < LinesOfCode.Count - 1 && i < num)
        {
            _fileSize++;   // +2 per line that is before the last non-empty line
            _fileSize++;   // (accounts for the "\r\n" the editor counts per joined line)
        }
    }
    SizeText.text = GameStrings.CodeEditorFileSize.AsString(StringManager.Get(_fileSize), StringManager.Get(4096));
    SizeText.color = ((_fileSize > 4096) ? UnityEngine.Color.red : UnityEngine.Color.white);
    SubmitButton.interactable = _fileSize <= 4096;
}
```

`_fileSize` counts the sum of all line text lengths plus 2 characters per line that precedes the last non-empty line. When `_fileSize > 4096`: the size readout turns red and `SubmitButton.interactable = false`, so the script cannot be saved to the chip from the editor. `Copy()` additionally hard-truncates to 4096 chars (`if (text.Length > 4096) text = text.Substring(0, 4096)`), but with Submit disabled the player cannot reach that path anyway.

### 90-character per-line cap
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

Each line's `TMP_InputField.characterLimit = 90` blocks typing past 90 chars. `Paste` and `Copy` both run `AsciiString.ParseLine(text, 90)`:

```csharp
public static string ParseLine(string text, int maxLength)
{
    text = text.Replace("\t", " ");
    text = Regex.Replace(text, "([^\\x00-\\x7F])", string.Empty);   // strip non-ASCII
    if (text.Length > maxLength) text = text.Substring(0, maxLength);
    return text;
}
```

Tabs become spaces, non-ASCII bytes are removed, and the line is truncated to 90. A line longer than 90 chars loses its tail (which corrupts the instruction). The apostrophe `'` (0x27) is ASCII and survives `ParseLine`.

## Runtime path has no caps: ProgrammableChip.SetSourceCode
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`ProgrammableChip.SetSourceCode(string)` is the runtime entry (called by `DeserializeOnJoin`, `BuildUpdate`/network sync, save load, and any direct/modded caller). It does NOT cap line count, total size, or per-line length:

```csharp
public void SetSourceCode(string sourceCode)
{
    _LinesOfCode.Clear(); _Aliases.Clear(); _Defines.Clear(); _JumpTags.Clear();
    ...
    if (string.IsNullOrEmpty(sourceCode)) sourceCode = string.Empty;
    SourceCode = new AsciiString(sourceCode);   // AsciiString ctor = Encoding.ASCII.GetBytes(text), no length cap
    if (CircuitHousing != null)
    {
        new _ALIAS_Operation(this, 0, "db", $"d{int.MaxValue}").Execute(0, updateLabels: false);
        new _ALIAS_Operation(this, 0, "sp", $"r{_StackPointerIndex}").Execute(0);
        new _ALIAS_Operation(this, 0, "ra", $"r{_ReturnAddressIndex}").Execute(0);
    }
    string[] array = sourceCode.Split('\n');
    for (int i = 0; i < array.Length; i++)   // iterates ALL lines, no 128 cap
    {
        try
        {
            if (array[i].IndexOf('#') == 0)
                _LinesOfCode.Add(new _LineOfCode(this, string.Empty, i));   // comment-only line -> empty NOOP, index preserved
            else
                _LinesOfCode.Add(new _LineOfCode(this, array[i], i));
        }
        catch (ProgrammableChipException ex) { ...; CompileErrorLineNumber = ex.LineNumber; CompileErrorType = ex.ExceptionType; break; }
        catch (System.Exception) { ...; CompileErrorLineNumber = (ushort)i; CompileErrorType = ...Unknown; break; }
    }
    _NextAddr = 0;
}
```

Storage for symbols is plain dictionaries with no count cap (`ProgrammableChip` fields):

```csharp
private readonly Dictionary<string, _AliasValue> _Aliases = new Dictionary<string, _AliasValue>();
private readonly Dictionary<string, int> _JumpTags = new Dictionary<string, int>();
private readonly Dictionary<string, double> _Defines = new Dictionary<string, double>();
private readonly List<_LineOfCode> _LinesOfCode = new List<_LineOfCode>();
```

So a >128-line, >4096-char script that arrives through the runtime path (e.g. written to the save XML by hand, or pushed by a mod) parses with no size objection; only per-line syntax errors raise `CompileError*`. This asymmetry matters: a script too big for the editor can still be injected and will run, but a player can never type or paste it through the in-game UI.

### Line numbering counts comment/blank lines (runtime)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

In `SetSourceCode`, every physical line (split on `\n`) becomes one `_LineOfCode` at index `i`, INCLUDING comment-only and blank lines. A line whose first char is `#` is added as `_LineOfCode(this, string.Empty, i)` (an empty line that compiles to NOOP) but it still occupies index `i`. Inside `_LineOfCode`, a blank/comment-stripped line with zero tokens becomes a `_NOOP_Operation`. Therefore runtime jump addresses are 0-based physical line indices and DO count comment and blank lines.

Label resolution does not depend on counting only executable lines: a label `foo:` records `chip._JumpTags[foo] = lineNumber` where `lineNumber` is the physical index, and `j foo` jumps to that index. Because both the label definition and every other line keep their physical index, jumps land correctly as long as the whole physical file is present and in order.

Caveat: this physical-index numbering is the RUNTIME chip's. The editor (`SortLines`, `Paste`) renumbers its own display slots `0..127` and is the path that drops lines 129+; a script that survives the editor has at most 128 lines and the editor's saved text is what the chip then parses.

### A label records its OWN physical line, and that line stays addressable
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

In `_LineOfCode`, a line consisting of a single token ending in `:` is detected and the label is stored at the label line's own physical index. From the `_LineOfCode` constructor (Assembly-CSharp.decompiled.cs:387927-387936):

```csharp
else if (array.Length == 1 && array[0].Length >= 2 && array[0][array[0].Length - 1] == ':')
{
    Operation = new _NOOP_Operation(chip, lineNumber);   // label line itself = NOOP
    string key = array[0].Substring(0, array[0].Length - 1);
    if (chip._JumpTags.ContainsKey(key))
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.JumpTagDuplicate, lineNumber);
    chip._JumpTags.Add(key, lineNumber);   // value == the label's OWN physical line index
}
```

So `loop:` sitting on physical line 29 records `_JumpTags["loop"] = 29`, NOT 30. The label line is not skipped or folded into the next instruction: it compiles to its own `_NOOP_Operation` occupying index 29, and `j loop` returns 29. Execution lands on the NOOP at 29 and falls through to 30 on the next loop iteration (`_NOOP_Operation.Execute` returns `index + 1`). A duplicate label name throws `JumpTagDuplicate` at compile time. This is the calibration anchor for reading a save: if a chip's saved `JumpTagsValues` entry for `loop` is 29, then physical line 29 of the source IS the `loop:` line, and address N maps to the (N+1)-th physical line counting all blank and comment lines from line 0.

### NextAddr and JumpTags persist verbatim across save/load
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`ProgrammableChipSaveData.NextAddr` is `public int NextAddr` (Assembly-CSharp.decompiled.cs:387509). On SAVE, the chip writes its live program counter straight out (`programmableChipSaveData.NextAddr = _NextAddr;`, line 393908). On LOAD, it reads it straight back (`_NextAddr = programmableChipSaveData.NextAddr;`, line 393840). No remap, no recompile-time adjustment: the saved `NextAddr` is exactly the address the chip will execute next when the world resumes.

Combined with the runtime-error catch block (see `./IC10ExecutionTick.md` and `./IC10Stack.md`: `catch (ProgrammableChipException ex) { CircuitHousing?.RaiseError(1); _ErrorLineNumber = ex.LineNumber; _NextAddr = nextAddr; }`), a chip that halted on a RUNTIME error has `_NextAddr` REWOUND to the faulting instruction's address before the save is written. Therefore, in a `world.xml`, a `ProgrammableChipSaveData` whose housing `Error == 1` has `NextAddr` pointing AT the faulting instruction (the one that threw), not the instruction after it and not a post-yield resume point. `_ErrorLineNumber` (the synced `_ErrorLineNumberSynced` ushort) holds the same physical line index, sourced from `ex.LineNumber`, which every operation sets to its own `_LineNumber` (its physical line index). For a clean (non-error) halt the chip parks at the post-`yield` address instead: `yield` returns `-index - 1`, the run loop negates it back to `index`, so `_NextAddr` is the yield line's own address and the chip re-runs the line after `yield` next tick. (`SetSourceCode` resets `_NextAddr = 0` on any fresh source load, so a re-flash clears a stale error address.)

The same save record round-trips the jump table verbatim: `JumpTagsKeys[m]` / `JumpTagsValues[m]` are loaded into `_JumpTags` (line 393869) and written from it on save. The saved `JumpTagsValues` therefore equal the physical line indices computed at the last `SetSourceCode`, which is why they are a reliable address calibration anchor for the saved `SourceCode` string.

### The in-game error-line display is 0-indexed and shows NextAddr verbatim
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

When a chip is in a runtime-error state, the housing's interaction tooltip surfaces the error via `ProgrammableChip.AppendErrorsToActionInstance` (Assembly-CSharp.decompiled.cs:394246-394256):

```csharp
else if (_ErrorType != ProgrammableChipException.ICExceptionType.None)
{
    actionInstance.AppendStateMessage(GameStrings.ProgrammableChipErrorCode, ErrorTypeString, ErrorLineNumberString);
}
```

The format string is `"Error <color=red>{ErrorType}</color> at line {LineNumber}"` (`GameStrings.ProgrammableChipErrorCode`, line 265385), and the line value is `ErrorLineNumberString => StringManager.Get(_ErrorLineNumberSynced)` (line 393380), the raw `_ErrorLineNumber` with NO `+1` applied. The in-game code editor (`InputSourceCode`) labels its gutter slots `$"{i}."` starting at `i == 0` (lines 223814 and 223829), so the editor display is 0-indexed too.

Consequence for interpreting a user report: "error at line 34" is the 0-based physical line index and equals the faulted `NextAddr` value directly (no conversion). With the loop=29 calibration above, "line 34" is the 35th physical line counting from line 0, which in the solar-tower program is `sb SolarPanelHeavy Horizontal r0`. There is no off-by-one between the displayed error line, `_ErrorLineNumber`, `NextAddr` on a runtime fault, and the 0-based physical line index: all four are the same number.

## Preprocessing: HASH / STR / binary / hex evaluated at import
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`HASH("...")`, `STR("...")`, binary `%...`, and hex `$...` are all expanded to integer literals at import time, in the runtime `_LineOfCode` constructor (not just in the editor highlighter). Order: inline comment stripped first, then STR, then HASH, then binary, then hex:

```csharp
public _LineOfCode(ProgrammableChip chip, string lineOfCode, int lineNumber)
{
    string masterString = ((lineOfCode.IndexOf('#') < 0) ? lineOfCode : lineOfCode.Substring(0, lineOfCode.IndexOf('#')));   // strip inline comment
    // STR("...") -> PackAscii6(name).ToString()
    Localization.RegexResult matchesForStringPreprocessing = Localization.GetMatchesForStringPreprocessing(ref masterString);
    for (...) masterString = masterString.Replace(full, PackAscii6(name, lineNumber).ToString(InvariantCulture));
    try {
        // HASH("...") -> Animator.StringToHash(name).ToString()
        Localization.RegexResult matchesForHashPreprocessing = Localization.GetMatchesForHashPreprocessing(ref masterString);
        for (...) masterString = masterString.Replace(full, Animator.StringToHash(name).ToString());
    } catch (System.Exception) { throw new ProgrammableChipException(...InvalidPreprocessHash, lineNumber); }
    // binary %01_01 -> Convert.ToInt64(name.Replace("_",""), 2)   ; throws InvalidProcessBinary on failure
    // hex $1A_FF -> Convert.ToInt64(name.Replace("_",""), 16)      ; throws InvalidPreprocessHex on failure
    ...
    string[] array = masterString.Split();   // tokenize after macro expansion
    ...
}
```

The macros are matched by these regexes (`Regexes` static ctor):

```csharp
PreprocessStrings = new Regex("STR\\(\"([^\"]+)\"\\)");
PreprocessHashes  = new Regex("HASH\\(\"([^\"]+)\"\\)");
PreprocessBinary  = new Regex("\\%([01_]+)");
PreprocessHex     = new Regex("\\$([0-9A-Fa-f_]+)+");
```

`GetMatchesForHashPreprocessing` first strips comments (`Regexes.CommentLite.Replace(masterString, "")`) then matches `HASH\("([^"]+)"\)`. The capture group `[^"]+` is "one or more non-double-quote characters", so an apostrophe inside the quotes is captured normally: `HASH("A'")` captures `A'` and resolves to `Animator.StringToHash("A'")`, a valid int. There is no special quote/apostrophe handling that breaks it. (`Animator.StringToHash` is Unity's built-in 32-bit string hash; in Stationeers it is what IC10 `HASH("...")` evaluates to, and what a device's `CustomName` is hashed with for `lbn`/`sbn` name-hash matching.)

A `define NAME HASH("...")` line therefore works because HASH is replaced by its integer string BEFORE the value is parsed; the define's value parser (below) never sees the literal `HASH(...)`.

## define value parsing: negatives, floats, HASH all accepted
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`define NAME VALUE` builds a `_DEFINE_Operation`, which parses VALUE via `DoubleValueVariable` and stores a `double`. A duplicate define name throws `ExtraDefine`:

```csharp
private class _DEFINE_Operation : _Operation
{
    public _DEFINE_Operation(ProgrammableChip chip, int lineNumber, string defineCode, string floatCode)
        : base(chip, lineNumber)
    {
        DoubleValueVariable doubleValueVariable = new DoubleValueVariable(chip, lineNumber, floatCode, InstructionInclude.MaskDefineValue);
        if (chip._Defines.ContainsKey(defineCode))
            throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.ExtraDefine, lineNumber);
        chip._Defines.Add(defineCode, doubleValueVariable.Get());
    }
    ...
}
```

`DoubleValueVariable` tries `InternalEnums`, then `AllConstants`, then falls back to a culture-invariant numeric parse:

```csharp
if (double.TryParse(code, NumberStyles.Number, NumberFormatInfo.InvariantInfo, out var result))
    _Value = result;
```

`NumberStyles.Number` permits a leading sign and a decimal point, so:

- `define GAS_SENSOR -1252983604` -> parses as the negative integer -1252983604 (stored as double).
- `define FORCEFIELD_POWER_THRESHOLD 0.25` -> parses as 0.25.
- `define A HASH("A")` -> HASH expands to an int string in `_LineOfCode` first, then `TryParse` reads that int.

All three forms are accepted. (Note `NumberFormatInfo.InvariantInfo` means the decimal separator is always `.`, never `,`, regardless of OS locale.)

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

- 2026-06-03: Added three additive subsections under "Line numbering" while interpreting a saved `ProgrammableChipSaveData` (housing `Error == 1`, `NextAddr == 34`, `JumpTagsValues` for `loop` == 29) from a v0.2.6228.27061 world.xml. (1) "A label records its OWN physical line": confirmed `_LineOfCode` ctor stores `chip._JumpTags.Add(key, lineNumber)` at the label line's own physical index (decompile line 387935) and compiles the label line to a `_NOOP_Operation`, so `loop:` on physical line 29 yields `_JumpTags["loop"] == 29`, not 30. (2) "NextAddr and JumpTags persist verbatim": confirmed `ProgrammableChipSaveData.NextAddr` is `public int NextAddr` (387509), saved from `_NextAddr` (393908) and loaded back into `_NextAddr` (393840) with no remap, and that JumpTags round-trip via lines 393869; combined with the runtime-error rewind (`_NextAddr = nextAddr` in the catch), a chip whose housing `Error == 1` has `NextAddr` pointing AT the faulting instruction, while a clean `yield` halt parks at the yield line's own address. (3) "Error-line display is 0-indexed": confirmed `AppendErrorsToActionInstance` (394246) feeds `ErrorLineNumberString` (393380, raw `_ErrorLineNumberSynced`, no `+1`) into `GameStrings.ProgrammableChipErrorCode` `"... at line {LineNumber}"` (265385), and the editor gutter labels are `$"{i}."` from `i == 0` (223814/223829), so the displayed error line equals `NextAddr` on a runtime fault with no conversion. All three are additive (no prior verified content contradicted); no fresh validator required.
- 2026-05-31: page created from a decompile review of v0.2.6228.27061. Confirmed editor caps (MAX_LINES=128, MAX_FILE_SIZE=4096, LINE_LENGTH_LIMIT=90) in `InputSourceCode`; confirmed the runtime `ProgrammableChip.SetSourceCode` path has no line/size/length cap (dictionaries + unbounded loop); confirmed `Paste` drops lines beyond slot 127; confirmed Submit gates on `_fileSize <= 4096`; confirmed HASH/STR/binary/hex expansion happens in the runtime `_LineOfCode` ctor with HASH = `Animator.StringToHash`; confirmed `HASH("A'")` (apostrophe) is captured by the `[^"]+` group; confirmed negative-int and float define values parse via `double.TryParse(NumberStyles.Number, InvariantInfo)`.

## Open questions

None at creation.
