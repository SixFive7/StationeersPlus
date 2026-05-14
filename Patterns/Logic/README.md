# Patterns/Logic

Shared conventions, documentation, and code for the Stationeers `LogicType` enum across every SixFive7 mod.

The single source of truth for which `ushort` value is assigned to which custom `LogicType` lives in [`LogicTypeNumbers.cs`](LogicTypeNumbers.cs) in this folder. Each mod's `.csproj` links that file via:

```xml
<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />
```

so every mod's `LogicTypeRegistry.cs` references `StationeersPlus.Shared.LogicTypeNumbers.<Name>` instead of redeclaring a literal integer.

## Why this exists

`LogicType` is a vanilla `ushort` enum (0-65535). The vanilla game uses 0-349 densely. Modders extend the enum at runtime by patching `ProgrammableChip.AllConstants`, `Logicable.LogicTypes`, `EnumCollections.LogicTypes`, and `ScreenDropdownBase.LogicTypes`. The integer assigned to each new name is permanent: savegames serialise per-Thing logic overrides keyed by it, IC10 scripts compile against it, multiplayer sync depends on host and client agreeing on it. Renumbering breaks every save and every script that uses the name.

Without a single catalogue, two mods can collide on the same integer, with no compiler error and no runtime warning. The conflict only surfaces when both mods are loaded and a chip writes a value: the wrong device's slot updates, or a `CanLogicRead` patch silently shadows another. Centralising the assignment list here prevents that for mods in this monorepo and documents what to avoid for everything else.

## Assignment table

Append at the next free slot. Increments of 1. Compact packing - a mod may have gaps where other mods own intermediate values.

| Value | Mod | Name | Read / Write | Notes |
|---|---|---|---|---|
| 6571 | PowerTransmitterPlus | MicrowaveSourceDraw | R | Watts pulled from source cable network. |
| 6572 | PowerTransmitterPlus | MicrowaveDestinationDraw | R | Watts delivered to receiver's downstream network. |
| 6573 | PowerTransmitterPlus | MicrowaveTransmissionLoss | R | Source draw minus destination draw. |
| 6574 | PowerTransmitterPlus | MicrowaveEfficiency | R | Delivered / source, 0..1. |
| 6575 | PowerTransmitterPlus | MicrowaveAutoAimTarget | R / W | Target Thing.ReferenceId. Set 0 to disable. |
| 6576 | PowerTransmitterPlus | MicrowaveLinkedPartner | R | Linked partner's ReferenceId, or 0 when unlinked. |
| 6577 | PowerGridPlus | LogicPassthroughMode | R / W | 0 = vanilla logic-opaque transformer, 1 = logic-transparent. |

**Next free slot: 6578.**

## Rules for adding a new entry

1. Pick the next free integer in the table above (currently `6578`). Do not skip; do not pick from a "preferred band" - the catalogue is one flat list.
2. Add a `public const ushort` to [`LogicTypeNumbers.cs`](LogicTypeNumbers.cs) in the correct mod section. Append a new section header if it is the first entry for a mod.
3. Update the table above with the value, mod, name, read/write, and a one-line description.
4. Update the "Next free slot" line above.
5. In the consuming mod, the per-mod `LogicTypeRegistry.cs` declares its own `LogicType` and `CustomLogicType` records but reads the integer from `LogicTypeNumbers`:

   ```csharp
   using StationeersPlus.Shared;

   internal const ushort LogicPassthroughModeValue = LogicTypeNumbers.LogicPassthroughMode;
   internal static readonly LogicType LogicPassthroughMode = (LogicType)LogicPassthroughModeValue;
   ```

6. The mod's `.csproj` already has the `<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />` entry once the mod has joined this catalogue. A new mod adds it the first time it registers a `LogicType`.

7. Commit the `Patterns/Logic/` change and the mod change together so the catalogue and the implementation always match. The catalogue commit happens first; the mod's reference commit follows.

## Never bump an existing value

`LogicType` integers are baked into saves, IC10 source code (`s d0 LogicPassthroughMode 1`), and multiplayer state sync. A renumber from 6577 to 6578 silently breaks every existing save that wrote a value with the old number, every chip script that names the constant, and every joining client whose mod still uses the old integer. The vanilla game does not validate `LogicType` values against a registry; a stale integer becomes a phantom slot that no `CanLogicRead` claim covers, which usually surfaces as `0` reads everywhere.

Add new entries at the end. Never reorder, renumber, or reuse a retired entry's value. If a value is truly orphaned (a mod removed it), leave the row in the table marked `RETIRED` so the integer is never re-used.

## Known third-party reservations

These bands are not under SixFive7 control. Do not pick anything from these ranges.

| Band | Source | Source of truth |
|---|---|---|
| 0-349 | Vanilla Stationeers | The `LogicType` enum in `Assembly-CSharp.dll`. |
| 1000-1830 | Stationeers Logic Extended (ThunderDuck) | Documented in PowerTransmitterPlus's `LogicTypeRegistry.cs` source comment. |

Every published mod is free to assign its own values; the registry above is what we know about, not the full picture. Conflicts with other Workshop mods are a real risk and the only mitigation is to keep this list current. See the repo-root `TODO.md` for the open task "scan popular Workshop mods for LogicType numbering reservations" which expands this table.

## Discovery for agents

The root `CLAUDE.md` "Workflow: shared patterns under `Patterns/`" section points here. Agents touching any `LogicType`-related code, any `LogicTypeRegistry.cs`, or any new logic extension MUST read this file before assigning a number.
