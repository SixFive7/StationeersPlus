---
title: Thing
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-22
sources:
  - Plans/RepairPrototype/plan.md:373-383
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing
related:
  - ./Structure.md
  - ./Entity.md
  - ./ColorSwatch.md
tags: [prefab, slots]
---

# Thing

Vanilla game class at `Assets.Scripts.Objects.Thing`. The base class of every in-world game object. All prefab types derive from `Thing` either as `DynamicThing` (non-fixed-position) or `Structure` (player-built, fixed).

## Object hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0223.

```
Assets.Scripts.Objects.Thing              (base of ALL game objects)
  |-- DynamicThing                        (non-fixed-position)
  |     |-- Item                          (inventory-storable)
  |     |-- Entity                        (living: Human, etc.)
  |-- Structure                           (player-built, fixed)
        |-- LargeStructure                (2m grid: frames, walls)
        |-- SmallGrid                     (0.5m grid: pipes, cables, devices)
              |-- Device                  (powered machines)
```

## CustomColor field and IsPaintable gate

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

`Thing` carries two color-related fields declared together at decompile line ~360 of `Assets.Scripts.Objects.Thing`:

```csharp
[Header("Thing Colors")]
[Tooltip("If set, will allow any parts of the thing with this material to be spraypainted")]
public Material PaintableMaterial;

[ReadOnly]
public ColorSwatch CustomColor;
```

Key facts:

- `CustomColor` is a `public ColorSwatch` field (not a property), reference type, nullable. Marked `[ReadOnly]` for inspector display only; runtime code freely reassigns it via `Thing.SetCustomColor(int)` (decompile line ~5265), which calls `CustomColor = GameManager.GetColorSwatch(index)`.
- `CustomColor` CAN be null at runtime. `SetCustomColor(int)` early-returns if `CustomColor == null` after the assignment (meaning `GameManager.GetColorSwatch` returned null). Existing mod code checks `if (__instance.CustomColor == null) return true;` before reading `CustomColor.Index` (see `GlowPaintPatches.cs:123`).
- When `CustomColor` is non-null, `CustomColor.Index` returns the 0-based index into `GameManager.Instance.CustomColors`. The `Index` property is a lazy `GameManager.GetColorIndex(this)` lookup per `../GameClasses/ColorSwatch.md`.
- `Thing.IsPaintable` (decompile line ~1772) is:

  ```csharp
  public bool IsPaintable
  {
      get
      {
          if (!(PaintableMaterial != null))
          {
              return HasPaintableMaskMaterial;
          }
          return true;
      }
  }
  ```

  A Thing is paintable when either `PaintableMaterial` is assigned (the normal case: pipes, cables, walls, large structures) or `HasPaintableMaskMaterial` is true (a virtual override used by mask-painted prefabs). Non-paintable Things (e.g. most small items, entities) have `IsPaintable == false` and no meaningful `CustomColor` value; callers that eyedrop must check `IsPaintable` before reading `CustomColor.Index` to avoid sampling a stale or default swatch on a prefab that does not participate in the color system.

- On save, `ThingSaveData.CustomColorIndex` persists the color, but `-1` is used as the sentinel for "unpainted / using prefab default": `savedData.CustomColorIndex = ((CustomColor.Normal == PaintableMaterial) ? (-1) : GameManager.GetColorIndex(CustomColor));` (decompile line ~4692). At load, `saveData.CustomColorIndex >= 0` gates the `SetCustomColor` call (line ~4667). The `-1` sentinel is save-format only: at runtime an unpainted paintable Thing still has a valid `CustomColor` assigned (equal to the swatch whose `Normal` material matches `PaintableMaterial`), not null.

- Two overloads of `SetCustomColor` exist on `Thing`:
  - `SetCustomColor(ColorSwatch colorSwatch)` forwards to `SetCustomColor(colorSwatch.Index)`.
  - `SetCustomColor(int index, bool emissive = false)` is the authoritative entry point; silently no-ops on invalid indices via `GameManager.IsValidColor(index)`.

- Network replication: `SetCustomColor(int)` sets `NetworkUpdateFlags |= 32` when `GameManager.RunSimulation` (server-authoritative), triggering a `ThingColorMessage` broadcast. Remote clients receive the color update and apply it locally through `Thing.SetCustomColor(index)`, so `CustomColor.Index` is readable and correct on every peer that has the Thing loaded.

Reading color client-side for an eyedropper:

```csharp
Thing target = CursorManager.CursorThing;
if (target == null) return;
if (!target.IsPaintable) return;
if (target.CustomColor == null) return;
int index = target.CustomColor.Index;
if (index < 0) return; // defensive: swatch not yet registered in GameManager.CustomColors
```

The client-local `CustomColor` reflects the last value applied by either a local paint action or an incoming `ThingColorMessage`; no server round-trip is needed to read it.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0223. No conflicts.
- 2026-04-22: added "CustomColor field and IsPaintable gate" section. Additive only; no existing content changed. Sources: decompile of `Assets.Scripts.Objects.Thing` fields at line ~360 (`PaintableMaterial`, `CustomColor`), `IsPaintable` at line ~1772, `SetCustomColor(int, bool)` at line ~5265, save round-trip at line ~4667 / ~4692, all in game version 0.2.6228.27061.

## Open questions

None at creation.
