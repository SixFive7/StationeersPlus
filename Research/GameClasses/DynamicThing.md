---
title: DynamicThing
type: GameClasses
created_in: 0.2.6420.27780
verified_in: 0.2.6420.27780
verified_at: 2026-08-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.DynamicThing
  - TestRig/ClientRig/data/hostie/logs/unity-20260812-131417.log (MissingMethodException, two independent stacks)
related:
  - ./Thing.md
  - ../Patterns/HarmonyInheritedMethods.md
tags: [slots, modding, breaking-change]
---

# DynamicThing

`Assets.Scripts.Objects.DynamicThing` is the `Thing` subclass for objects that can
be carried and can live in a slot. Only one property of it is recorded here so far,
and it is a breaking change rather than a description of the class.

## `SetSlotTypes(Slot.Class)` was REMOVED in 0.2.6420.27780

**Verified 2026-08-12 in 0.2.6420.27780.** The method
`void Assets.Scripts.Objects.DynamicThing.SetSlotTypes(Assets.Scripts.Objects.Slot.Class)`
existed in 0.2.6403.27689 and does not exist in 0.2.6420.27780.

Evidence, two independent kinds:

**1. Metadata.** The string `SetSlotTypes` does not occur anywhere in the new
`Assembly-CSharp.dll`, while `DynamicThing` does (control). .NET stores method names
in the `#Strings` heap, so a method whose name is absent from the assembly's bytes
does not exist in it. The same scan finds `SetSlotTypes` present in
`ScriptedScreens.dll`, which was compiled against the older build.

**2. Runtime.** A client with mods that call it throws
`MissingMethodException: Method not found: void Assets.Scripts.Objects.DynamicThing.SetSlotTypes(Assets.Scripts.Objects.Slot/Class)`
from two different call sites, in this order:

```
# a mod's own entrypoint
StationeersLaunchPad.Entrypoints.StationeersModsEntrypoint.Initialize (LoadedMod mod)
StationeersLaunchPad.Loading.LoadedMod.LoadEntrypoints ()
StationeersLaunchPad.Loading.LoadStrategyLinearSerial.LoadEntryPoints ()

# and the GAME'S OWN prefab load, once the first caller is disabled
Assets.Scripts.Objects.Prefab.LoadAll ()
WorldManager.LoadGameDataAsync ()
WorldManager.Initialize ()
Assets.Scripts.GameManager.Start ()
```

### Why this matters beyond the mod that calls it

The second stack is the important one. When the throwing call happens inside
`Prefab.LoadAll()`, it propagates through `WorldManager.Initialize()` and out of
`GameManager.Start()`, which never completes. The client then sits at
`phase == "menu"` with **`gameInitialized == false`** for ever, rendering normally at
full frame rate, with every mod apparently loaded. **One third-party mod calling a
removed method is enough to stop the client reaching a usable main menu**, and the
symptom does not name the mod: the BepInEx `LogOutput.log` stops early and records
nothing about it. Only the Unity player log carries the exception.

That failure is easy to misread as StationeersLaunchPad's documented Workshop park,
which is a different condition with a different signature:

| | plugin count | gameInitialized | cause |
|---|---|---|---|
| Workshop park | `<= 2` | false | a failed Steam Workshop query |
| this | full count (36-37 observed) | false | a removed method thrown out of `Prefab.LoadAll` |

### Known callers, as of 2026-08-12

Scanning every Workshop assembly under `steamapps\workshop\content\544550` for the
string finds exactly two, both by the same author, and both fail on this build:

- `ScriptedScreens.dll` (Workshop 3666779631), version 0.9.5.0
- `StationeersLua.dll` (Workshop 3659911735), version 0.9.5.0

Disabling only the first is not enough: the second then throws the same exception
from `Prefab.LoadAll` instead of from its own entrypoint. Both must be disabled for
the client to boot on this build, which is what the earlier "disable these two"
workaround in this repository's rig notes amounts to.

No replacement method was identified. `SetSlotType` (singular) is also absent, so
this is not a rename to the obvious neighbouring name.

## Verification History

- **0.2.6420.27780, 2026-08-12**: `SetSlotTypes` confirmed absent by metadata string
  scan (with `DynamicThing` as a passing control) and by two distinct runtime
  `MissingMethodException` stacks. Callers enumerated across all installed Workshop
  assemblies. The `gameInitialized`-never-true consequence observed directly on two
  separate boots of a rig instance.
