---
title: DynamicThing
type: GameClasses
created_in: 0.2.6420.27780
verified_in: 0.2.6428.27798
verified_at: 2026-08-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.DynamicThing
  - TestRig/ClientRig/data/hostie/logs/unity-20260812-131417.log (MissingMethodException, two independent stacks)
  - steamapps\workshop\content\544550\3666779631\ScriptedScreens.dll (1.0.0.0, 2026-08-13)
  - steamapps\workshop\content\544550\3659911735\StationeersLua.dll (1.0.0.0, 2026-08-13)
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
<!-- verified: 0.2.6428.27798 @ 2026-08-13 -->

**Verified 2026-08-12 in 0.2.6420.27780, re-confirmed 2026-08-13 in 0.2.6428.27798**
(the same metadata string scan, with `DynamicThing` still passing as the control).
The removal has held across two game builds, so treat it as permanent rather than as
an accident of one release. The method
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

### Known callers: two, for one day
<!-- verified: 0.2.6428.27798 @ 2026-08-13 -->

Scanning every Workshop assembly under `steamapps\workshop\content\544550` for the
string on 2026-08-12 found exactly two, both by the same author, and both failed on
that build:

- `ScriptedScreens.dll` (Workshop 3666779631), version 0.9.5.0
- `StationeersLua.dll` (Workshop 3659911735), version 0.9.5.0

Disabling only the first was not enough: the second then threw the same exception
from `Prefab.LoadAll` instead of from its own entrypoint, so both had to be disabled
for the client to boot.

**Both shipped 1.0.0.0 on 2026-08-13 and neither calls the method any more.** The
same metadata string scan against the updated assemblies returns `SetSlotTypes`
absent from both, with `DynamicThing` still present in each as the control. The
window in which this stopped a client from booting was about one day wide, and the
"disable these two" instruction that briefly lived in this repository's rig notes is
retired. As of 2026-08-13 the scan finds **no** caller in any installed Workshop
assembly.

| | 0.9.5.0 (2026-08-12) | 1.0.0.0 (2026-08-13) |
|---|---|---|
| `ScriptedScreens.dll` calls `SetSlotTypes` | yes | no |
| `StationeersLua.dll` calls `SetSlotTypes` | yes | no |
| Client reaches a usable menu with both enabled | no | yes |

No replacement method was identified, and none was needed by either caller.
`SetSlotType` (singular) is also absent from 0.2.6420.27780 and from
0.2.6428.27798, so this was never a rename to the obvious neighbouring name.

## Verification History

- **0.2.6420.27780, 2026-08-12**: `SetSlotTypes` confirmed absent by metadata string
  scan (with `DynamicThing` as a passing control) and by two distinct runtime
  `MissingMethodException` stacks. Callers enumerated across all installed Workshop
  assemblies. The `gameInitialized`-never-true consequence observed directly on two
  separate boots of a rig instance.
- **0.2.6428.27798, 2026-08-13**: re-verified. The method is still absent from
  `Assembly-CSharp.dll` on the newer build, so the removal held across the update.
  Additive finding, contradicting nothing above: both known callers shipped 1.0.0.0
  on 2026-08-13 (file timestamps 15:50, `<Version>1.0.0.0</Version>` in each
  `About.xml`) and the string scan now returns zero hits in either assembly. The
  caller list for this build is therefore empty, and the "both must be disabled"
  consequence no longer applies to any installed mod. The removal itself, its two
  runtime stacks and the `Prefab.LoadAll` blast radius are unchanged and still
  correct for any assembly compiled against the older API.
