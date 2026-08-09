---
title: CursorManager and the cursor-target tuple
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-29
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.GameManager.decompiled.cs
  - TestRig/ClientRig/dev-plugins/ClientDriver/ClientDriver/Routes/Router.cs
related:
  - ../Workflows/DrivingTheGameClientProgrammatically.md
  - ../Patterns/ThingEnumerationOffMainThread.md
tags: [ui, unity, harmony, dead-end]
---

What the client's cursor state actually consists of, why a mod that sets only part of it can put
`GameManager.Update` into a permanent exception loop, and what a mod has to write to set or clear a
cursor target safely.

## The cursor is a tuple, not a field
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

`CursorManager : ManagerBase` holds the cursor target across four members, and `SetCursorTarget`
writes them together on every frame:

```csharp
[ReadOnly] public Thing FoundThing;
[ReadOnly] public Collider CursorTargetCollider;
public CursorTerrain FoundTerrain { get; set; }
public static RaycastHit CursorHit { get; set; }

public static Thing CursorThing => Instance.FoundThing;
public static CursorTerrain CursorTerrain => Instance.FoundTerrain;
```

`[ReadOnly]` is a Stationeers inspector attribute, not a C# access modifier: both fields are freely
writable from code.

`SetCursorTarget()` has exactly three outcomes, and each writes the whole tuple:

- **Suppressed** (console open, Stationpedia open and locked, `BlockCursorRaycast`, or the player
  seated without mouse control): `CursorTargetCollider = null; FoundThing = null;
  FoundTerrain = CursorTerrain.Invalid;`.
- **Raycast miss**: the same three assignments.
- **Raycast hit**: `CursorTargetCollider = _raycastHit.collider;` plus either the terrain branch
  (`FoundThing = null`, `FoundTerrain` set) or the Thing branch (`FoundTerrain = Invalid`,
  `FoundThing = _raycastTransform.GetComponentInParent<Thing>()`).

`CursorHit` is assigned from the private static `_raycastHit` before the branch, so it is written
even on a miss. It is a `RaycastHit` struct; `.collider` is a read-only Unity property backed by an
instance-id field and cannot be assigned. That matters less than it looks: **nothing in the assembly
reads `CursorHit.collider`**. Only `.point` is read, at seven sites. The field consumers actually
dereference is `CursorManager.Instance.CursorTargetCollider`.

There is one vanilla path that leaves a stale tuple: in third person, when a second raycast from the
camera hits a different transform, `SetCursorTarget` returns early and last frame's values stand.

`CursorManager.ClearAll()` exists and is an empty stub. `LateUpdate()` clears only two per-frame
position caches. There is no reset a mod can call; the tuple is purely rebuilt by `SetCursorTarget`.

## `Thing.GetSlot(Collider)` has no null guard, and its dictionary is never null
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

```csharp
private readonly Dictionary<Collider, Slot> _slotLookup = new Dictionary<Collider, Slot>();

public Slot GetSlot(Collider selectedCollider)
{
    _slotLookup.TryGetValue(selectedCollider, out var value);
    return value;
}
```

`_slotLookup` is `readonly` and eagerly constructed, so `GetSlot(null)` reaches
`Dictionary.TryGetValue(null)` and throws `ArgumentNullException` on **every** Thing.

Its sibling looks similar and behaves differently:

```csharp
public Interactable GetInteractable(Collider selectedCollider)
{
    if (_interactableColliderLookup == null) { return null; }
    _interactableColliderLookup.TryGetValue(selectedCollider, out var value);
    return value;
}
```

That guard is on the dictionary, not the key. It survives a null key only because
`_interactableColliderLookup` is lazily allocated and stays null on a Thing with no interactables;
on a Thing that has them, `GetInteractable(null)` throws too. So "the interactable variant is safe"
is a coincidence of allocation, not a guarantee, and does not generalise.

`_slotLookup` is keyed on `Slot.Collider` (`public BoxCollider Collider;`) and only populated for
slots that have one and are interactable:

```csharp
_slotLookup.Clear();
...
    if ((bool)slot.Collider && slot.IsInteractable && !_slotLookup.ContainsKey(slot.Collider))
    {
        _slotLookup.Add(slot.Collider, slot);
    }
```

## Why a throw from `UpdateEachFrame` starves the cursor forever
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

`GameManager.Update` runs the per-frame Thing pass and the manager loop in one method with no
try/catch between them:

```csharp
public void Update()
{
    if (!IsInitialized) { return; }
    if (!WorldManager.IsGamePaused)
    {
        ...
        OcclusionManager.UpdatingThings.ForEach(UpdateEachFrameAction);   // ~line 1498
        ...
    }
    foreach (ManagerBase manager2 in Managers)                            // ~line 1540
    {
        manager2.ManagerUpdate();
    }
    BatchRenderer.RenderAll();
    WindTurbineGenerator.UpdateWind();
}
```

and the delegate itself is unguarded:

```csharp
private static readonly Action<Thing> UpdateEachFrameAction = delegate(Thing thing)
{
    thing?.UpdateEachFrame();
};
```

`CursorManager.ManagerUpdate` is the only caller of `SetCursorTarget()`, and it sits in that second
loop. So any exception raised from a Thing's `UpdateEachFrame` aborts the frame **before** the
cursor can be rebuilt. If the exception's cause is the cursor state itself, nothing ever repairs it
and the loop is self-sustaining.

Two consequences beyond the cursor, both from the same unguarded loop:

- `NetworkManager.ManagerUpdate` is in it and is the client's only network receive pump, so a wedged
  client processes zero packets. This is the same mechanism behind the multiplayer join stall
  documented in `../Workflows/DrivingTheGameClientProgrammatically.md`.
- `KeyManager.ManagerUpdate` is in it, so no input reaches the game either.

## The concrete failure: `PlantAnalyserCartridge`
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

```csharp
private Plant GetScannedPlant()
{
    if (!RootParent || !RootParent.HasAuthority) { return null; }
    if (!InPlayerHand()) { return null; }
    Collider cursorTargetCollider = CursorManager.Instance.CursorTargetCollider;
    Thing cursorThing = CursorManager.CursorThing;
    if (!(cursorThing is HydroponicTray hydroponicTray))
    {
        if (cursorThing is HydroponicsTrayDevice hydroponicsTrayDevice)
        {
            return hydroponicsTrayDevice.Plant;
        }
        Slot slot = cursorThing?.GetSlot(cursorTargetCollider);
        ...
```

The `?.` guards `cursorThing`, not the collider. Reached from
`Cartridge.UpdateEachFrame` -> `OnScreenUpdate` -> `GenerateInfoStrings`, gated on a held, powered,
switched-on tablet.

Observed cost of the loop, on a driven client with the tablet in hand: 100 exceptions per 6 seconds,
indefinitely, with the frame loop dead. Only leaving the world recovered it.

## Unguarded readers of the cursor state
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

Anything that sets a cursor target has to satisfy all of these, not just the cartridge:

| Reader | What breaks with a null or wrong collider |
|---|---|
| `CursorManager.GetCurrentVoxelWorld` | `((BoxCollider)Instance.CursorTargetCollider).center`, guarded only by `CursorTerrain.IsValid`. Null gives a `NullReferenceException`, a non-box collider an `InvalidCastException`. |
| `InventoryManager` attack path | `CanAttackWith(collider)` dereferences `selectedCollider.isTrigger`. |
| `PlantAnalyserCartridge.GetScannedPlant` | `Thing.GetSlot(null)`, see above. |

Readers of `CursorHit.point` are all safe: `RaycastHit.point` is a value type, so a stale pin only
produces a stale point.

Keeping `FoundTerrain = CursorTerrain.Invalid` is what keeps the voxel cast unreachable, so it
belongs in any forced-cursor write even though the terrain itself is not the subject.

## Setting a cursor target from a mod, safely
<!-- verified: 0.2.6403.27689 @ 2026-07-29 -->

`SetCursorTarget` overwrites the tuple every frame from `ManagerUpdate`, so a pin has to be a
Harmony postfix on it. The postfix must write all three:

```csharp
instance.FoundThing = thing;
instance.CursorTargetCollider = collider;   // never leave this at whatever the raycast produced
instance.FoundTerrain = CursorTerrain.Invalid;
```

Choosing the collider, most faithful first. Only the first makes `GetSlot(collider)` return an
actual Slot rather than merely not throw:

1. A `Slot.Collider` from `thing.Slots` where `slot.IsInteractable` (these are the `_slotLookup`
   keys).
2. `thing._selfColliders`, then `_staticColliders`, then `_dynamicColliders`. Despite the underscore
   names these are public `List<Collider>` fields, populated by `Thing.CacheColliders()`.
3. `thing.GetComponentInChildren<Collider>()`.

There is no `Thing.GetCollider()`, no `Thing.Collider`, and no `Thing.InteractableColliders`.

If none of those yields a collider, do not pin: the resulting state is the wedge.

**Clearing has to write the fields, not just drop the pin.** Removing a mod's stored target only
stops the postfix re-applying it; it cannot help when the reason the cursor is stale is that
`SetCursorTarget` is no longer reachable. Assign `FoundThing = null`,
`CursorTargetCollider = null`, `FoundTerrain = CursorTerrain.Invalid` directly. For that write to
land on an already-wedged client it must be driven from a pump that is not downstream of
`GameManager.Update`; a plugin's own `MonoBehaviour.Update` or an `ImGuiManager.LateUpdate` postfix
both qualify.

Also unpin when the target is destroyed. A pinned Thing that gets deconstructed or consumed leaves a
dead reference in a field the game will happily dereference.

## Verification history

- 2026-07-29: page created from a decompile sweep of `CursorManager`, `Thing.GetSlot`,
  `PlantAnalyserCartridge` and `GameManager.Update` on 0.2.6403.27689, prompted by ClientDriver's
  `/cursor/force` wedging a live client. Corrects the earlier working theory recorded in
  `.work/2026-07-27-spraypaintplus-settings-split/TEST-RESULTS.md` (run 2, T1a), which named
  `CursorManager.CursorHit.collider` as the field being dereferenced; the actual field is
  `CursorManager.Instance.CursorTargetCollider`, and `CursorHit.collider` has no readers anywhere in
  the assembly. The fix built on this page's findings was exercised on a live client the same day:
  a target pinned with its collider, with the player then aimed at empty sky so the vanilla raycast
  missed, held for 10 seconds at roughly 57 fps with zero exceptions, network traffic still flowing,
  and `clear` restoring `CursorThing` to null.

## Open questions

- The refusal path for a Thing with no reachable collider is implemented but has never fired: every
  candidate tried in a live world, including a cartridge sitting inside a tablet, had a reachable
  collider. Whether any Thing in a normal world lacks one is unknown.
- Whether `Physics.Raycast` against a real collider could be used to synthesise a fully faithful
  `CursorHit` (including `.point` and `.normal`) for a pinned target was not explored, because no
  consumer needs it today.
