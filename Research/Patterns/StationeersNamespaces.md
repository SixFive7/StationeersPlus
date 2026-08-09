---
title: Stationeers namespace pitfalls
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-08-09
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:613-619 (F0049, primary)
  - Mods/SprayPaintPlus/SprayPaintPlus/ConsumableSyncPatch.cs:6 (F0388)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: type declarations at 39197, 48571, 95793, 212198, 212509, 213543, 264502, 264704, 264713, 264734, 265200, 265338, 267663, 272554
related:
  - ../GameClasses/Client.md
  - ../GameSystems/SettingsPersistence.md
  - ../GameSystems/SaveConsoleProtocol.md
tags: [network, save-load]
---

# Stationeers namespace pitfalls

Reference table of game types whose namespace is easy to guess wrong. All verified against game version 0.2.6228.27061. Compile errors of the form `using <namespace>;` followed by "type not found" usually trace back to one of these.

## Reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0049 (Mods/PowerTransmitterPlus/RESEARCH.md:613-619, primary):

| Type | Namespace | Common mistake |
|---|---|---|
| `EnumCollection<,>` | `Assets.Scripts` | Not `Assets.Scripts.Util` |
| `ProgrammableChip` | `Assets.Scripts.Objects.Electrical` | Not `Motherboards` |
| `ProgrammableChip.Constant` | nested in `ProgrammableChip` | Must qualify as `ProgrammableChip.Constant` |
| `LogicType` | `Assets.Scripts.Objects.Motherboards` | (Most Logic types ARE in Motherboards, just not the chip) |
| `GameState` (enum) | `Assets.Scripts.GridSystem` | Not `Assets.Scripts` (where `GameManager` lives; decompile 290777 under the 290052 namespace) |
| `PowerTransmitterVisualiser` | global namespace | NOT `Assets.Scripts.Objects.Electrical` despite the dish being there |

F0388 (Mods/SprayPaintPlus/SprayPaintPlus/ConsumableSyncPatch.cs:6) adds:

- `Consumable` (used for spray cans, fuel canisters, welding torches) resides in `Assets.Scripts.Objects.Items`.

## Save, settings and networking types
<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

The save/settings cluster splits across two namespaces and the global namespace, which is the trap: three types a caller usually touches together are not reachable from one `using`.

| Type | Namespace | Decompile line | Common mistake |
|---|---|---|---|
| `Settings` (and nested `Settings.SettingData`) | `Assets.Scripts.Serialization` | 265338 | Not `Assets.Scripts`. |
| `SaveHelper` | `Assets.Scripts.Serialization` | 264734 | |
| `SaveResult`, `SaveMethod` | `Assets.Scripts.Serialization` | 264713, 264704 | |
| `SaveLoadConstants` | `Assets.Scripts.Serialization` | 265200 | |
| `LoadHelper` | `Assets.Scripts.Serialization` | 264502 | |
| `XmlSaveLoad` | `Assets.Scripts.Serialization` | 267663 | |
| `StationSaveUtils` | **global namespace** | 48571 | NOT `Assets.Scripts.Serialization`, despite sitting between `SaveHelper` and `Settings` in every call chain. |
| `NetworkBase` | **global namespace** | 39197 | NOT `Assets.Scripts.Networking`, even though `NetworkManager` is. |
| `Client`, `FragmentHandler`, `NetworkServer` | `Assets.Scripts` | 212198, 212509, 213543 | Not `Assets.Scripts.Networking`. |
| `NetworkChannel` | `Assets.Scripts.Networking` | 272554 | |
| Every `*Command` class (`SaveCommand`, `SettingsCommand`, `CommandBase`, `CommandScope`, ...) | `Util.Commands` | 95793 (namespace) | Not under `Assets.Scripts` at all. |

C# gotcha that this table exists to pre-empt: `using Assets.Scripts;` does NOT make types in the nested `Assets.Scripts.Serialization` namespace addressable by their short name. C# namespace lookup does not descend into child namespaces, so `Settings` and `SaveHelper` each need their own `using Assets.Scripts.Serialization;` (or full qualification) even in a file that already has `using Assets.Scripts;` for `Client` and `NetworkServer`.

The `Client` / `NetworkServer` / `NetworkBase` / `NetworkChannel` rows restate what the [Client](../GameClasses/Client.md) page established on 2026-07-27; they are repeated here so a namespace question resolves in one lookup.

## When to consult this
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- After a compile error immediately following a freshly added `using Assets.Scripts.<guess>;`.
- Before writing a Harmony patch against a game type whose namespace you inferred from file-system layout or a file name.
- When decompiled code elides the namespace: ILSpy and dnSpy both show `LogicType` without its enclosing namespace in some views.

Add entries here as new namespace surprises are discovered. The table is small by design; add only types that have actually tripped someone.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0049: primary table covering the five highest-surprise entries encountered in PowerTransmitterPlus.
- F0388: `Consumable` namespace note from SprayPaintPlus code comment.

## Verification history
- 2026-08-09: added the "Save, settings and networking types" section, stamped 0.2.6403.27689. Every declaration line in it was read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` in that pass. Additive: the original F0049 / F0388 table and its 0.2.6228.27061 stamp are untouched and were not re-verified. Two entries are the point of the section: `StationSaveUtils` (48571) is in the global namespace rather than `Assets.Scripts.Serialization` alongside `SaveHelper` and `Settings`, and `NetworkBase` (39197) is in the global namespace rather than `Assets.Scripts.Networking` alongside `NetworkManager`. Frontmatter `verified_in` / `verified_at` bumped to reflect the new section, per the "most recent section verification" semantics in `Research/CLAUDE.md`.
- 2026-07-14: added the GameState enum row (Assets.Scripts.GridSystem, decompile 290777; hit while a new file using GameManager.GameState failed to resolve GameState with only the Assets.Scripts using). Additive.
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0049 primary, F0388 additional.

## Open questions

None at creation.
