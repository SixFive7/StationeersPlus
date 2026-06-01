---
title: LogicType Registration
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-01
sources:
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicTypeRegistry.cs
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/Ic10ConstantsPatcher.cs
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/EnumNamePatches.cs
  - Mods/PowerGridPlus/PowerGridPlus/LogicTypeRegistry.cs
  - Mods/PowerGridPlus/PowerGridPlus/Patches/LogicableInitializePatch.cs
  - Mods/PowerGridPlus/PowerGridPlus/Ic10ConstantsPatcher.cs
  - Mods/PowerGridPlus/PowerGridPlus/Patches/EnumNamePatches.cs
  - Patterns/Logic/LogicTypeNumbers.cs
  - Patterns/Logic/README.md
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:239485-239504
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:238767-238770
related:
  - ../GameSystems/LogicType.md
  - ../GameSystems/ScreenDropdownBase.md
  - ../GameSystems/IC10SyntaxHighlighting.md
  - ./ModLogicTypeRegistration.md
  - ./LogicableInitializeAppend.md
  - ./CustomLogicValueInjection.md
tags: [logic, harmony, ic10]
---

# LogicType Registration

Custom LogicType values added by a mod at runtime need to land in five distinct game-side static collections plus three reflection-target name-lookup fallback patches. Missing any of the five extension sites produces silent breakage on one of the UI surfaces or in the IC10 compiler. Missing the name-lookup substitutes leaves vanilla `Enum.GetName` / `EnumCollection.GetName*` callers seeing empty strings for custom values.

This is the recipe page for how to register. For the question of which mods register which LogicType integers, see [./ModLogicTypeRegistration.md](./ModLogicTypeRegistration.md). For the canonical value-assignment catalogue (which integer belongs to which SixFive7 mod), see [`Patterns/Logic/README.md`](../../Patterns/Logic/README.md) at the repo root. For the underlying enum reference (vanilla values, gas-ratio members, the `EnumCollection<TEnum, TInt>` API), see [../GameSystems/LogicType.md](../GameSystems/LogicType.md).

## Five extension sites
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

The game stores LogicType identity and metadata across five independent collections, each snapshotted from `Enum.GetValues` / `Enum.GetNames(typeof(LogicType))` at static class load or, for `ScreenDropdownBase.LogicTypes`, at every `Awake`. All five must be extended for a custom LogicType to function with full UI coverage and IC10 compilation:

| # | Site | Drives | Patcher in this monorepo |
|---|---|---|---|
| 1 | `Logicable.LogicTypes` + `LogicTypeNames` (plus `LogicTypeNamesRedirects` binary-search index where present) | `NextLogicType` cycling on the tablet | `LogicableInitializePatch.Postfix` |
| 2 | `Assets.Scripts.EnumCollections.LogicTypes` (the `EnumCollection<LogicType, ushort>` wrapper: `Values`, `ValuesAsInts`, `Names`, `PaddedNames`, `<Length>k__BackingField`) | `ConfigCartridge` tablet UI; `Stationpedia.AddLogicTypeInfo` page builder | `LogicableInitializePatch.ExtendEnumCollection` |
| 3 | `Assets.Scripts.UI.Motherboard.ScreenDropdownBase.LogicTypes` + `LogicTypeNames` | `LogicMotherboard` condition / action dropdowns on Big Screens, Wall Screens, Consoles | `LogicableInitializePatch.ExtendScreenDropdownBase` |
| 4 | `Assets.Scripts.Objects.Electrical.ProgrammableChip.AllConstants` (`Constant[]`) | IC10 / MIPS compiler token resolution | `Ic10ConstantsPatcher.Apply` |
| 5 | `ProgrammableChip.InternalEnums` entries `ScriptEnum<LogicType>` and `BasicEnum<LogicType>` (private `_types` / `_names`) | In-game screen syntax highlighting: orange for bare names, teal for dotted `LogicType.Name` form | `Ic10ConstantsPatcher.ExtendSyntaxHighlighting` |

All five mutations are reflection writes against the static collection's backing arrays / fields; none of them is a Harmony patch on the host method. Harmony enters the picture only as a *trigger* for sites 1, 2, 3 (postfixing `Logicable.Initialize` is a convenient one-shot hook; the actual extension is a `field.SetValue(null, longerArray)` reflection call).

## Three name-lookup fallback patches
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Extending the five collections above makes the custom integer visible to the dropdowns, the compiler, and the cycler. Some vanilla UI paths additionally call `Enum.GetName(typeof(LogicType), value)` or `EnumCollection<LogicType, ushort>.GetName(value)` directly; without a fallback those calls return null / empty for custom values, so the corresponding name fields render blank. Three Harmony postfixes in `EnumNamePatches.cs` supply the fallback, lifted verbatim from `Mods/PowerTransmitterPlus/PowerTransmitterPlus/EnumNamePatches.cs`:

```csharp
[HarmonyPatch(typeof(Enum), nameof(Enum.GetName), new Type[] { typeof(Type), typeof(object) })]
public static class EnumGetNamePatch
{
    public static void Postfix(Type enumType, object value, ref string __result)
    {
        if (__result != null) return;
        if (enumType != typeof(LogicType)) return;
        if (value == null) return;
        try
        {
            var t = (LogicType)Convert.ToUInt16(value);
            if (LogicTypeRegistry.TryGetName(t, out var name))
                __result = name;
        }
        catch
        {
            // Non-LogicType-valued cast, ignore silently.
        }
    }
}

[HarmonyPatch(typeof(EnumCollection<LogicType, ushort>), "GetName")]
public static class EnumCollectionGetNamePatch
{
    public static void Postfix(LogicType value, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result)) return;
        if (LogicTypeRegistry.TryGetName(value, out var name))
            __result = name;
    }
}

[HarmonyPatch(typeof(EnumCollection<LogicType, ushort>), "GetNameFromValue")]
public static class EnumCollectionGetNameFromValuePatch
{
    public static void Postfix(ushort value, ref string __result)
    {
        if (!string.IsNullOrEmpty(__result)) return;
        if (LogicTypeRegistry.TryGetName((LogicType)value, out var name))
            __result = name;
    }
}
```

The pattern is uniform: only substitute when vanilla returned empty (preserves vanilla resolution for the 0-349 range), look up the custom name from the mod's own `LogicTypeRegistry`.

## Module split: timing and triggers
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

The five extension sites and the three fallback postfixes split across three files because they fire at different times:

| Module | Trigger | Sites covered | Idempotency |
|---|---|---|---|
| `LogicableInitializePatch` (Harmony postfix on `Logicable.Initialize`) | First `Logicable` instance initializes during save load | 1, 2, 3 | `_injected` flag; subsequent calls no-op |
| `Ic10ConstantsPatcher.Apply()` (plain call from plugin Awake; not a Harmony patch) | Plugin Awake at process start | 4, 5 | `_applied` flag |
| `EnumNamePatches` (three Harmony postfixes) | Whenever a vanilla `Enum.GetName` / `EnumCollection.GetName*` call returns null / empty | n/a (postfixes, not registrations) | Stateless |

Sites 1-3 use the `Logicable.Initialize` trigger because the runtime extension is most reliably performed when the game has already constructed its own arrays for those types. Sites 4-5 do not require this gating: `ProgrammableChip.AllConstants` and `ProgrammableChip.InternalEnums` are reliably populated by the time the plugin Awake runs.

## ScreenDropdownBase extension survives Awake
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Site 3 (`ScreenDropdownBase.LogicTypes`) deserves a separate note: unlike sites 1, 2, 4, and 5 (one-shot population at type-initializer time), `ScreenDropdownBase.LogicTypes` is populated by `ScreenDropdownBase.Awake()` on every dropdown instance, and `Awake` does NOT skip its population block after the first call (the `_isInitialized` flag is declared and tested but never assigned to `true` anywhere in the game). The extension still works because Awake's loop is bounded by `Enum.GetValues(typeof(LogicType)).Length` (the underlying enum's fixed member count, decided at game build time), not by the static field's current array length. Awake writes only indices `[0, vanillaCount)`; a reflection-installed longer array's appended tail at indices `>= vanillaCount` is never touched.

Consequence: patch ordering is irrelevant. The `LogicableInitializePatch` postfix can fire before or after the first `ScreenDropdownBase.Awake`; the appended custom LogicType entries survive either way. See [../GameSystems/ScreenDropdownBase.md](../GameSystems/ScreenDropdownBase.md) for the full Awake analysis, with the 2026-05-28 fresh-validator resolution that established this point.

## Why all five sites are needed
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Each site drives a different UI surface or compiler path. Skipping one produces a specific, observable breakage:

- **Skip site 1 (`Logicable.LogicTypes`)**: tablet `NextLogicType` cycling skips the custom value. Visible on a Slot in the inventory tablet.
- **Skip site 2 (`EnumCollections.LogicTypes`)**: the ConfigCartridge tablet UI omits the custom value from its dropdown. `Stationpedia.AddLogicTypeInfo` iterates `EnumCollections.LogicTypes.Values`, so the Stationpedia per-device page omits the custom logic row even when the device's own `CanLogicRead` / `CanLogicWrite` returns true for it.
- **Skip site 3 (`ScreenDropdownBase.LogicTypes`)**: the in-game `LogicMotherboard` condition / action dropdowns on Big Screens, Wall Screens, and Consoles do not list the custom value.
- **Skip site 4 (`ProgrammableChip.AllConstants`)**: IC10 / MIPS source code referencing the custom name fails to compile (unknown identifier). Players must use raw integer literals.
- **Skip site 5 (`ProgrammableChip.InternalEnums`)**: in-game screen syntax highlighting renders the custom name in the screen's default invalid-token red text color, even though the script compiles and runs. See [../GameSystems/IC10SyntaxHighlighting.md](../GameSystems/IC10SyntaxHighlighting.md) for the highlighting pipeline.

Beyond the five registry extensions, a mod that wants a vanilla device to actually read or write the custom LogicType still needs to add `CanLogicRead` / `CanLogicWrite` / `GetLogicValue` / `SetLogicValue` Harmony patches on that device class. That is orthogonal to registration; see [./CustomLogicValueInjection.md](./CustomLogicValueInjection.md) for the patch shape.

## Verbatim source comments
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

The implementation files in this monorepo carry inline comments that explain each block. Lossless copy from `Mods/PowerTransmitterPlus/PowerTransmitterPlus/`:

`LogicableInitializePatch.cs` (file header, lines 11-16):

```text
// Appends our custom LogicType values + names into the static arrays the
// configuration tablet UI uses to populate its dropdowns. Logicable holds
// parallel LogicType[] / string[] arrays plus a redirect index for binary
// search lookup; we extend all three.
//
// Pattern lifted from Stationeers Logic Extended (ThunderDuck).
```

`LogicableInitializePatch.Postfix` body, the three internal sub-extensions (lines 55-71):

```text
// Some game versions maintain a binary-search redirect array
// (LogicTypeNamesRedirects). Best-effort rebuild if present;
// otherwise the tablet will fall back to linear scans.
TryRebuildRedirects(newNames);

// ConfigCartridge (and other tablet UI paths) iterate
// EnumCollections.LogicTypes instead of Logicable.LogicTypes.
// That collection wraps Enum.GetValues, so our custom values
// are invisible to the tablet dropdown unless we also extend
// its Values / ValuesAsInts / Names / PaddedNames / Length.
ExtendEnumCollection(additions);

// The in-game screen preview (code rendered on the
// computer/laptop when NOT in the editor) validates tokens
// against ScreenDropdownBase.LogicTypes / LogicTypeNames.
// Without this, custom names draw red as "invalid".
ExtendScreenDropdownBase(additions);
```

`Ic10ConstantsPatcher.cs` (file header):

```text
// Teaches the IC10 / MIPS compiler to recognize our LogicType names.
// The compiler resolves tokens like "MicrowaveSourceDraw" to a numeric
// constant by scanning ProgrammableChip.AllConstants, a public static
// Constant[] array where each Constant has (Literal, Description, Value).
//
// Also extends the syntax-highlighting entries in
// ProgrammableChip.InternalEnums so that custom LogicType names render
// with the correct color on in-game screens instead of falling through
// to the default (red) text color.
//
// Pattern lifted from Stationeers Logic Extended (ThunderDuck): one-time
// reflection write that appends our entries to the existing array. No
// Harmony patch needed for this step; IC10 reads the array dynamically.
```

`Ic10ConstantsPatcher.ExtendSyntaxHighlighting` body comment (lines 69-75):

```text
// ProgrammableChip.InternalEnums holds IScriptEnum instances that
// Localization.ParseScript iterates to wrap known tokens in <color>
// tags. ScriptEnum<LogicType> (bare names like "MicrowaveSourceDraw")
// and BasicEnum<LogicType> (dotted names like "LogicType.Microwave...")
// both snapshot Enum.GetValues/GetNames at construction, so our
// runtime-added values are missing. Extend their private _types and
// _names arrays via reflection.
```

`EnumNamePatches.cs` (file header):

```text
// The configuration tablet and various UI paths look up the display name
// for a given LogicType value via Enum.GetName(...) and the game's own
// EnumCollection<LogicType, ushort>. Both return null for our 6571+ values
// because the underlying enum has no metadata for them. These postfixes
// substitute our names from the registry when the lookup would otherwise
// come up empty.
//
// Pattern lifted from Stationeers Logic Extended (ThunderDuck).
```

## Reference implementation in this monorepo
<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Two mods register custom LogicTypes today; both follow the identical four-file pattern. PowerGridPlus's files are structurally identical to PowerTransmitterPlus's, modulo per-mod registry contents and slightly less verbose logging.

| File | PowerTransmitterPlus | PowerGridPlus |
|---|---|---|
| Registry (the `(name, value, description)` list + lookup helpers) | [LogicTypeRegistry.cs](../../Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicTypeRegistry.cs) | [LogicTypeRegistry.cs](../../Mods/PowerGridPlus/PowerGridPlus/LogicTypeRegistry.cs) |
| Sites 1, 2, 3 patcher | [LogicableInitializePatch.cs](../../Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs) | [Patches/LogicableInitializePatch.cs](../../Mods/PowerGridPlus/PowerGridPlus/Patches/LogicableInitializePatch.cs) |
| Sites 4, 5 patcher | [Ic10ConstantsPatcher.cs](../../Mods/PowerTransmitterPlus/PowerTransmitterPlus/Ic10ConstantsPatcher.cs) | [Ic10ConstantsPatcher.cs](../../Mods/PowerGridPlus/PowerGridPlus/Ic10ConstantsPatcher.cs) |
| Name-lookup fallback postfixes | [EnumNamePatches.cs](../../Mods/PowerTransmitterPlus/PowerTransmitterPlus/EnumNamePatches.cs) | [Patches/EnumNamePatches.cs](../../Mods/PowerGridPlus/PowerGridPlus/Patches/EnumNamePatches.cs) |

The integer assignments themselves live in [`Patterns/Logic/LogicTypeNumbers.cs`](../../Patterns/Logic/LogicTypeNumbers.cs), shared into each mod via `<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />` in the `.csproj`. The single source of truth for the catalogue (current assignments, next free slot, third-party reservations) is [`Patterns/Logic/README.md`](../../Patterns/Logic/README.md).

A new SixFive7 mod adding a custom LogicType clones the four files above, replaces the registry entries, and links the shared `LogicTypeNumbers.cs` from its `.csproj`. The Logicable postfix, IC10 patcher entrypoint, and EnumNamePatches Harmony declarations are otherwise drop-in.

## Verification history

- 2026-06-01: page created. Content split out of [../GameSystems/LogicType.md](../GameSystems/LogicType.md) (which previously held the registration mechanism alongside the enum-value reference) so the mechanism page stands on its own and `LogicType.md` is the enum-value reference. The "Five extension sites" framing consolidates the prior page's inconsistent "four registries" vs "three arrays" sections; site 4 (`ProgrammableChip.AllConstants`) was previously mentioned only in the "Why custom LogicTypes don't appear without patches" section and not counted in the registry tally. Implementation verified by direct reads of `Mods/PowerTransmitterPlus/PowerTransmitterPlus/{LogicTypeRegistry,LogicableInitializePatch,Ic10ConstantsPatcher,EnumNamePatches}.cs` and the parallel `Mods/PowerGridPlus/` files; both mods are structurally identical in mechanism. The ScreenDropdownBase Awake-survival behavior reuses the 2026-05-28 fresh-validator resolution recorded on [../GameSystems/ScreenDropdownBase.md](../GameSystems/ScreenDropdownBase.md). No new contradictions of verified content.

## Open questions

None at creation.
