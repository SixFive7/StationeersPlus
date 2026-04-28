---
title: Thing
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-28
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

## Initial CustomColor by spawn path

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

Every spawn route eventually calls `Thing.Create<T>(prefab, worldPos, worldRot, refId)` (decompile line ~2320) which calls `UnityEngine.Object.Instantiate(prefab, ...)`. Instantiation runs `Thing.Awake()` (line 3619). Lines 3745-3748 of `Awake` are the single authoritative initializer of `CustomColor` during Thing instantiation:

```csharp
if ((bool)PaintableMaterial)
{
    CustomColor = GameManager.GetColorSwatch(PaintableMaterial);
}
```

`GameManager.GetColorSwatch(Material)` (decompile line 539-554) linearly scans `CustomColors` and returns the swatch whose `Normal` material reference-equals `PaintableMaterial`, or `null` on miss. Consequences:

- A paintable Thing's `CustomColor` is non-null after Awake iff `PaintableMaterial` is a Material that appears as `.Normal` on some entry of `GameManager.CustomColors`. For the 12 vanilla swatches, this holds for any vanilla paintable prefab (pipes, cables, frames, walls, devices): their `PaintableMaterial` is one of the 12 registered `.Normal` materials.
- Whatever `CustomColor` was inspector-assigned on the prefab asset is IGNORED: Awake unconditionally overwrites it when `PaintableMaterial` is non-null. So the inspector-level `CustomColor` on a prefab has no runtime effect.
- Non-paintable Things (`PaintableMaterial == null` and `HasPaintableMaskMaterial == false`) leave `CustomColor` at whatever the prefab asset had, which is typically null. `IsPaintable` is false for these and the eyedropper should refuse them.

### Kit / Structure color asymmetry

Structures that are player-built (e.g. `Ladder`, `Pipe`, `Cable`, `Wall`) are never spawned as raw Structure prefabs via the console, creative menu, or fabricator flows. Those flows only accept `DynamicThing` prefabs. The in-world Structure is born out of a **kit DynamicThing** (a `Constructor` or `MultiConstructor` item, prefab name typically `KitLadder`, `KitPipe`, etc.) via the authoring/construction pipeline.

Two PaintableMaterial slots are therefore in play for any built Structure:

1. The **kit's** `PaintableMaterial` (set on the `Constructor` / `MultiConstructor` prefab asset). This determines the color the kit item appears in while held / stacked.
2. The **target Structure's** `PaintableMaterial` (set on the Structure prefab asset). This determines the built-structure's Awake default.

These two Materials are independent and commonly differ. For the Ladder specifically the user-observed "yellow kit vs orange structure" asymmetry is evidence that `KitLadder.PaintableMaterial` and `Ladder.PaintableMaterial` are mapped to different `CustomColors` entries in the prefab asset YAML. The DLL does not store the Material references (they are Unity asset GUIDs on the prefab), but the code downstream proves the kit's color wins.

### Per-spawn-path behavior

- **Console `spawn <prefabName> [amount]`** (`Util.Commands.ThingCommand.Execute` case `"spawn"`, decompile line 139-170): calls `OnServer.SpawnDynamicThingMaxStack(humanRefId, prefabName)`. `OnServer.SpawnDynamicThingMaxStack` (line 675-735) does `Prefab.Find<DynamicThing>(prefabName)` and `Create<DynamicThing>` — silently fails if `prefabName` resolves to a non-DynamicThing prefab (e.g. typing `/spawn Ladder` finds nothing because `Ladder` is a `Structure`). When it succeeds it spawns the DynamicThing (e.g. `KitLadder`) with `CustomColor == GameManager.GetColorSwatch(KitLadder.PaintableMaterial)` and never layers a `SetCustomColor`. Result: kit-color, NOT structure-color. The user sees the kit in-hand with its printer default.
- **Creative menu (inventory `+` button)** (`Assets.Scripts.UI.ImGuiUi.ImguiCreativeSpawnMenu` line 196 → `InventoryManager.SpawnDynamicThing(ICreativeSpawnable)` line 937-947): the method checks `prefab is DynamicThing` before forwarding to `SpawnDynamicThingMaxStack`. Only `DynamicThing`s are registered into the menu in the first place (`Prefab.RegisterExisting` and `WorldManager.RegisterThing` both gate on `is DynamicThing` before calling `ImguiCreativeSpawnMenu.AddDynamicItem`, decompile lines 116842 and 268759). Same outcome as console: kit is spawned with its own Awake default.
- **Creative menu (authoring placement, Authoring Tool wand)** (`Assets.Scripts.Inventory.InventoryManager.UsePrimary` line 2334/2338 → `OnServer.UseItemPrimaryAuthoring` / `UseItemPrimary` line 938-956): when `ActiveHand.Slot.Occupant is AuthoringTool`, the server substitutes `Prefab.Find<Constructor>(spawnPrefab.SpawnId)` for the in-hand tool and calls `OnUsePrimary(..., authoringMode: true)`. This reaches `Constructor.Construct` (decompile line 23-34) which creates a `CreateStructureInstance(BuildStructure, ..., steamId)` with default `CustomColor == -1`, then IF `PaintableMaterial != null && CustomColor.Normal != null` (on the Constructor kit prefab; the kit has just been instantiated with Awake defaults and its `CustomColor` is the kit's own swatch), overwrites `createStructureInstance.CustomColor = CustomColor.Index` (the kit's color index). `SpawnConstruct` calls `Thing.Create<Structure>(BuildStructure, ...)` (Awake sets the Structure's CustomColor to the Structure's Awake default, e.g. orange-ladder), then `Structure.SetStructureData(..., instance.CustomColor)` (decompile line 2239-2248), which applies `SetCustomColor(kitColorIndex)` only if `kitColorIndex >= 0 && PaintableMaterial != null && kitColorIndex != CustomColor.Index`. **Net result: the Structure's runtime CustomColor is the KIT's default color, not the Structure's default color, whenever the kit has a `PaintableMaterial`.** This is why a Ladder placed via the creative menu (authoring click) comes out yellow, not orange.
- **Normal player build (kit in hand)** (`Item.OnUsePrimary` on a `Constructor` / `MultiConstructor` during `authoringMode == false`): same path as creative authoring; the player's in-hand kit runs `Constructor.Construct` → `SpawnConstruct`. The built Structure inherits the kit's `CustomColor`, which equals the kit prefab's Awake default if the kit was printed-and-untouched, or whatever paint has been applied to the kit in inventory since.
- **Fabricator output** (`Assets.Scripts.Objects.Electrical.SimpleFabricatorBase.SpawnCreatedItems`, line 894-908): calls `Thing.Create<DynamicThing>(_currentResult, ...)` with no follow-up `SetCustomColor`. The fabricated DynamicThing carries its own Awake default. For a fabricator that prints a kit, this is the kit prefab's default.
- **MultiConstructor** (`Assets.Scripts.Objects.MultiConstructor.Construct` line 47-61): same shape as `Constructor`. Uses `Constructables[optionIndex]` as the `Prefab`, reads `PaintableMaterial != null && CustomColor.Normal != null` on the MultiConstructor item itself, sets `createStructureInstance.CustomColor = CustomColor.Index`, then routes through `Constructor.SpawnConstruct`. Kit color wins for the same reason.
- **DynamicThingConstructor** (`Assets.Scripts.Objects.Items.DynamicThingConstructor.OnUseItem`, decompile around line 323731 of the game DLL): calls `OnServer.CreateOld(ConstructedPrefab, ...)` then if `PaintableMaterial != null && CustomColor.Normal != null` on the DynamicThingConstructor item, calls `OnServer.SetCustomColor(thing, CustomColor.Index)`. The built DynamicThing ends up wearing the tool's current color.
- **Reverse lookup: kit-for-structure.** `Prefab.OnLoad` (decompile line 244-247) registers every `IConstructionKit` into `Assets.Scripts.Objects.Items.ElectronicReader._constructKitLookup` via `ElectronicReader.AddToLookup(IConstructionKit creator)` (line 529-539). Each kit's `GetConstructedPrefabs()` (implemented by `Constructor` with `{ BuildStructure }` and `MultiConstructor` with all `Constructables`) enumerates the Structures it builds. The registration calls `AddToLookup(IConstructionKit, Thing)` (line 599-614) which inserts `(created.PrefabHash -> List<IConstructionKit>)`. Read-back at runtime: `ElectronicReader.GetAllConstructors(Thing)` (line 642-646) returns every kit that builds a given Structure, or `null` if none. This is the single canonical reverse-lookup from a placed Structure back to the kit(s) that build it.
- **Vanilla prefab default** (the prefab asset itself, before instantiation): `PaintableMaterial` is a serialized `Material` slot filled in the Unity inspector. `CustomColor` is a serialized `ColorSwatch` slot also fillable in the inspector, but it is OVERWRITTEN during `Awake` on every instantiation whenever `PaintableMaterial` is set. The `CustomColor` slot on the prefab asset is therefore a no-op for runtime behavior. There is no separate `DefaultColor` / `DefaultCustomColor` / `PrefabColor` / `InitialColorSwatch` field on `Thing`, `Structure`, `Item`, `Tool`, `Consumable`, `Pipe`, or `LargeStructure`; grep over the full Thing decompile confirms no such field exists.

For any paintable Thing from any spawn path, the moment `Awake` returns the Thing has `CustomColor == GameManager.GetColorSwatch(PaintableMaterial)` with `CustomColor.Normal == PaintableMaterial` and `CustomColor.Index == GameManager.GetColorIndex(PaintableMaterial)`. Constructor / MultiConstructor / DynamicThingConstructor paths then layer a `SetCustomColor(kitColorIndex)` on top before the Thing is handed off. There is no observable window in which `CustomColor` is null for a paintable Thing post-Awake.

**The observed yellow-Ladder-vs-orange-Ladder rule.** A Ladder placed by any of the three construct-via-kit paths (player build, creative Authoring Tool click, MultiConstructor) ends with `CustomColor == kit.PaintableMaterial-derived-swatch`. A Ladder `Structure` in isolation (never reachable from any public spawn path, but recoverable as `Prefab.Find<Ladder>("Ladder")`) would have `CustomColor == Ladder.PaintableMaterial-derived-swatch`. Those two PaintableMaterials are independent prefab-asset values; when they differ, so do the resulting in-world colors. The user's yellow-vs-orange observation is this asymmetry surfacing.

## Printer-default color lookup

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

"The color an object would have if freshly built via the normal flow" has two different valid definitions:

- **"As it rolls out of the printer" (kit-color).** The DynamicThing (kit or item) that the fabricator emits into the export slot. For Items and non-built DynamicThings this equals the Thing's own `PaintableMaterial`-derived swatch. For kits, this is the kit's own Awake default, which is NOT the same as the Structure the kit builds.
- **"As freshly placed in the world by the normal construct flow" (built-structure color, which is the kit's color applied to the placed Structure).** This is what a player sees standing next to a just-built Ladder. Per the Kit / Structure color asymmetry section above, this equals the kit's `PaintableMaterial`-derived swatch, NOT the Structure's own `PaintableMaterial`-derived swatch.

These two definitions collapse to the same answer for Things that are not kit-built (pipes placed as items, tools, resources) but diverge for kit-built Structures.

### Lookup for a DynamicThing / Item (no kit involved)

```csharp
int PrinterDefaultColorIndex(Thing target)
{
    if (!target.IsPaintable) return -1;
    Material def = target.PaintableMaterial;
    if (def == null) return -1;        // HasPaintableMaskMaterial but no base material
    return GameManager.GetColorIndex(def);  // == -1 if not in CustomColors
}
```

Why this works for the non-kit case:

- `Thing.PaintableMaterial` is a serialized field on the prefab asset and is never reassigned by any vanilla code path. Grep across the Thing decompile shows no writes to `PaintableMaterial` anywhere; only reads. Painting a Thing reassigns `CustomColor`, not `PaintableMaterial`.
- On the live instance, `target.PaintableMaterial` is the same `Material` reference as on the source prefab (Unity prefab instantiation shares the serialized Material reference).
- `GameManager.GetColorIndex(Material)` (line 467) finds the matching swatch in `CustomColors` and returns its index, or `-1` on miss.
- For a Thing that has `HasPaintableMaskMaterial == true` but `PaintableMaterial == null`, there is no registered "printer default" in the swatch list; the helper should return `-1` and the feature falls back to a no-op.

### Lookup for a placed Structure (kit was involved)

`target.PaintableMaterial` on a placed Structure is **not** the "as-built" color. The as-built color is the kit's PaintableMaterial. Recover it via the kit reverse lookup:

```csharp
int AsBuiltColorIndex(Thing target)
{
    if (!target.IsPaintable) return -1;

    // Kit-built Structures: consult the reverse lookup first.
    List<IConstructionKit> kits = ElectronicReader.GetAllConstructors(target);
    if (kits != null && kits.Count > 0)
    {
        // Prefer a Constructor-kit (1:1 match) over a MultiConstructor (1:N).
        IConstructionKit kit = kits[0];
        Thing kitThing = kit as Thing;
        if (kitThing != null && kitThing.PaintableMaterial != null)
            return GameManager.GetColorIndex(kitThing.PaintableMaterial);
    }

    // Fallback for non-kit-built Things.
    Material def = target.PaintableMaterial;
    if (def == null) return -1;
    return GameManager.GetColorIndex(def);
}
```

Caveats:

- `ElectronicReader.GetAllConstructors(Thing)` keys by `PrefabHash`. It works on either a prefab or a live instance — both share the hash.
- When a Structure has multiple constructor kits (modded parallel kits, or a `MultiConstructor` that builds the same Structure as one option among many), the list carries all of them. The kits' `PaintableMaterial`s may disagree. The helper above picks `[0]` which is registration order; a user-visible eyedropper probably wants the first `Constructor` over any `MultiConstructor`, since a MultiConstructor's color represents the selector-kit itself, not any one of its N outputs. A production implementation should iterate, prefer a `Constructor` match, and fall back to the first `MultiConstructor` if that is all that exists.
- For Structures that no kit builds (console-spawned-via-mod, hand-placed by a dev tool, or `SpawnData` content), `GetAllConstructors` returns `null` and the fallback to `target.PaintableMaterial` is the best approximation.

### Recommendation for the Ctrl+right-click eyedropper variant

The feature wants the "as it rolls out of the printer" color, which for a placed Ladder means the kit color (yellow), not the Structure color (orange). The old one-liner `SprayPaintHelpers.GetPaintColorIndex(target.PaintableMaterial)` returns the Structure color, which is wrong for any kit-built Structure. Replace with the `AsBuiltColorIndex` shape above.

Feasibility verdict: **100% faithful recovery is possible for any vanilla kit-built Structure** via `ElectronicReader.GetAllConstructors`, because the registration is prefab-deterministic at load time. The only lossy case is a Structure placed by a non-kit path (vanilla code has no such placements; modded code could), where the helper falls back to `target.PaintableMaterial`. In that fallback case the feature is "as faithful as possible given no kit metadata exists."

## PrefabName and PrefabHash visual-variant identity
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`Thing` carries two fields under `[Header("Thing")]` that identify the source prefab of every spawned instance:

```csharp
[Header("Thing")]
[ReadOnly]
public string PrefabName;

[ReadOnly]
public int PrefabHash;
```

`PrefabHash` is the canonical per-prefab integer identity. Every Unity prefab variant has its own value: `StructureWall`, `StructureWallFlat`, `StructureWallArched`, `StructureWallIron`, `StructureWallPadded`, etc. all map to distinct hashes even though they share the same C# class (`Wall`). Same for the `Frame` family (open frame, web frame, girder), the `Floor` family (visual floor variants), and every other class with multiple visual prefabs.

`PrefabName` is the matching string identifier (also the value passed to `Prefab.Find<T>(name)`). It is a one-to-one alias of `PrefabHash` via `Animator.StringToHash(name)` at registration time; reads of either are equivalent for identity comparison, but `PrefabHash` is cheaper (int compare vs string compare).

Implication for type-keyed flood-fill or selection code: filtering candidates by `s.GetType() == origin.GetType()` collapses every visual variant of a given C# class into one bucket. To distinguish visual variants (e.g. paint only Flat Walls when sprayed on a Flat Wall, leaving Arched Walls untouched), use `s.PrefabHash == origin.PrefabHash`. The reverse direction also holds: when code intends to treat all visual variants of a class as one group, the type filter is correct and the prefab-hash filter would be too narrow.

`PrefabHash` is set during prefab registration and never reassigned at runtime; it is identical on the live instance and on the source prefab asset (Unity prefab instantiation copies the serialized field). It is server-authoritative in the sense that every connected peer has the same value for the same Thing (the value is part of the prefab itself, not part of any networked state).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0223. No conflicts.
- 2026-04-22: added "CustomColor field and IsPaintable gate" section. Additive only; no existing content changed. Sources: decompile of `Assets.Scripts.Objects.Thing` fields at line ~360 (`PaintableMaterial`, `CustomColor`), `IsPaintable` at line ~1772, `SetCustomColor(int, bool)` at line ~5265, save round-trip at line ~4667 / ~4692, all in game version 0.2.6228.27061.
- 2026-04-22: added "Initial CustomColor by spawn path" and "Printer-default color lookup" sections. Additive; no existing content changed. Sources: `Thing.Awake` lines 3619-3748 (CustomColor initializer at 3745-3748), `Thing.Create<T>` line ~2320, `GameManager.GetColorSwatch(Material)` line 539-554, `GameManager.GetColorIndex(Material)` line 467, `Util.Commands.ThingCommand.Execute` `"spawn"` case, `Assets.Scripts.UI.ImGuiUi.ImguiCreativeSpawnMenu.SpawnDynamicThing` line 196, `Assets.Scripts.Inventory.InventoryManager.SpawnDynamicThing` line 937-952, `OnServer.SpawnDynamicThingMaxStack` line 675-735, `Assets.Scripts.Objects.Electrical.SimpleFabricatorBase.SpawnCreatedItems` line 894-908, `Assets.Scripts.Objects.Constructor.Construct` / `SpawnConstruct`, `Assets.Scripts.Objects.MultiConstructor.Construct`, `Assets.Scripts.Objects.Items.DynamicThingConstructor.OnUseItem`, `Assets.Scripts.Objects.Structure.SetStructureData` line 2239-2247. All in game version 0.2.6228.27061. No conflict with existing content.
- 2026-04-28: added "PrefabName and PrefabHash visual-variant identity" section after a SprayPaintPlus bug report ("wall painting spills across visual wall variants"). Additive; no existing content contradicted. Sources: `Assets.Scripts.Objects.Thing` fields at decompile line 297860-297865 (`[Header("Thing")] [ReadOnly] public string PrefabName; [ReadOnly] public int PrefabHash;`), in game version 0.2.6228.27061.
- 2026-04-22: refined "Initial CustomColor by spawn path" and rewrote "Printer-default color lookup" after user reported a yellow-kit-vs-orange-structure color asymmetry on a placed Ladder. Prior opening sentence "console/creative/fabricator/constructor all end at the same default" was misleading: it was true that Awake sets a default, but omitted that the Constructor path always re-overwrites that default with the KIT's color (not the target Structure's), and it failed to note that no vanilla path lets a raw Structure reach the world without going through a kit. Additions: (a) new subsection "Kit / Structure color asymmetry" explaining the two PaintableMaterial slots on a built structure, (b) new subsection "Per-spawn-path behavior" with the Authoring Tool / placement-click path (`OnServer.UseItemPrimaryAuthoring` line 948-956 substitutes `Prefab.Find<Constructor>(spawnPrefab.SpawnId)` for the held `AuthoringTool`), (c) documentation of the `_constructKitLookup` reverse-lookup registered in `Prefab.OnLoad` line 244-247 and read via `ElectronicReader.GetAllConstructors(Thing)` line 642-646, (d) rewrote `PrinterDefaultColorIndex` into a kit-aware `AsBuiltColorIndex` helper. No verified claim was removed — the `Constructor` bullet at original line 120 already carried the `instance.CustomColor` detail correctly; this pass promotes that detail to the top of the section and adds the reverse-lookup primitive. No fresh validator required: refinement/addition, not contradiction of previously-verified claims. Sources additionally consulted: `Constructor.Construct` line 23-34, `MultiConstructor.Construct` line 47-61, `CreateStructureInstance(Structure, Grid3, Quaternion, ulong, int = -1)` ctor line 35-43, `Structure.SetStructureData` line 2239-2248, `OnServer.UseItemPrimary` / `UseItemPrimaryAuthoring` line 938-956, `Assets.Scripts.UI.ImGuiUi.ImguiCreativeSpawnMenu` line 58-64 + 166-200, `InventoryManager.SpawnDynamicThing(ICreativeSpawnable)` line 937-947, `Prefab.OnLoad` kit-registration line 244-247, `ElectronicReader._constructKitLookup` line 89 and `GetAllConstructors` line 642-646, `ElectronicReader.AddToLookup(IConstructionKit)` line 529-539 and 599-614, `DynamicThingConstructor.OnUseItem` at game-DLL line ~323731. All in game version 0.2.6228.27061.

## Open questions

None at creation.
