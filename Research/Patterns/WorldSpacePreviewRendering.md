---
title: WorldSpacePreviewRendering
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager (construction cursors)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.CursorManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Wireframe / WireframeGenerator
  - $(StationeersPath)\BepInEx\plugins\...\BlueprintMod.dll :: BlueprintGuidelines / BlueprintPreview / BlueprintWireBatcher / GuidelineAnimator
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs
  - .work/decomp/0.2.6228.27061/BlueprintMod.decompiled.cs
related:
  - ../GameClasses/CursorManager.md
  - ../GameClasses/ThingRenderer.md
  - ../GameSystems/RenderingPipelineAndGlow.md
  - ../GameSystems/Explosions.md
tags: [unity, ui, transforms]
---

# WorldSpacePreviewRendering

What rendering primitives a Stationeers mod can reuse to draw a world-space preview volume (a placement ghost, a selection box, a blast sphere, a "what gets affected" highlight) instead of inventing its own. The game has no general "draw a translucent volume here" or "outline this Thing" API; it has a small set of concrete patterns, catalogued below, plus a worked precedent in the BlueprintMod region-selection / paste-preview code.

## Build / placement ghost: pre-instantiated, material-swapped, toggled
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

The translucent placement preview a player sees while holding a structure kit is **not** drawn with `Graphics.DrawMesh`. `Assets.Scripts.Inventory.InventoryManager` owns it:

```csharp
public static Structure ConstructionCursor;
private static readonly Dictionary<string, Structure> _constructionCursors = new Dictionary<string, Structure>();
private static readonly Dictionary<string, GameObject> _dynamicThingCursors = new Dictionary<string, GameObject>();
public static GameObject PrecisionPlaceCursor;
```

At load (`SetupConstructionCursors` -> `HandleStructurePrefab`), the game instantiates one clone per structure prefab into a hidden parent GameObject `~ConstructionCursors`, strips colliders / lights / animators / UI, and swaps every renderer material for a clone of `CursorManager.Instance.CursorShader`:

```csharp
structure2.tag = "Cursor";
structure2.IgnoreSave = true;
structure2.IsCursor = true;
...
foreach (ThingRenderer renderer in structure2.Renderers) {
    Material[] materials = renderer.Materials;
    for (int num2 = 0; num2 < materials.Length; num2++) {
        Material material = new Material(CursorManager.Instance.CursorShader);
        material.color = UnityEngine.Color.white;
        material.SetFloat(Offset, -300f);          // Offset = Shader.PropertyToID("_Offset")
        material.mainTexture = null;
        materials[num2] = material;
    }
    renderer.Materials = materials;
    renderer.SetShadowCastMode(ShadowCastingMode.Off);
}
```

Showing the ghost = `gameObject.SetActive(true)` + move the transform (`InventoryManager.UpdatePlacement(Structure)`); hiding = `SetActive(false)` (`CancelPlacement()`). Validity coloring is just `material.color`. `CursorManager` also exposes `public Material CursorMaterial` and `public Shader CursorShader`; both are wired in the prefab, not named in code. The dynamic-thing cursor path (loose items) instead instantiates the prefab's own `Blueprint` GameObject and tints its shared material green at alpha 0.2.

Takeaway: there is no static "draw a ghost mesh at matrix M". The game's pattern is "pre-instantiate, toggle SetActive". A mod wanting a ghost-styled object does the same: `Object.Instantiate` something, `new Material(CursorManager.Instance.CursorShader)`, set `_Color` / `_Offset`. `CursorManager.Instance` can be null very early; guard.

## The hover / selection highlight: one stretched box, not a per-mesh outline
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Assets.Scripts.CursorManager` carries two prefab-driven visualizers:

```csharp
[Tooltip("A prefab that is stretched to size of bounds of target to indicate what is being selected")]
public GameBase CursorSelectionHighlighter;     // the box that wraps a hovered Thing
[Tooltip("A prefab that is used as the cursor")]
public GameObject CursorHighlighter;            // the crosshair dot
public Shader CursorShader;
public Material CursorMaterial;
public static Renderer CursorSelectionRenderer { get; set; }
public static Renderer CursorRenderer { get; set; }
```

`SetSelection` just scales / positions / rotates one shared transform to the target's bounds:

```csharp
public static void SetSelection(SelectionInstance selection, int selectionSoundHash) {
    if (selection == null) { CursorSelectionRenderer.gameObject.SetActive(false); ClearLastSelectionId(); return; }
    CursorSelectionTransform.localScale = selection.GetScale();
    CursorSelectionTransform.position   = selection.GetPosition();
    CursorSelectionTransform.rotation   = selection.GetRotation();
    Instance.CursorSelectionHighlighter.SetVisible(true);
    ...
}
```

`SelectionInstance` is a plain data class: `Bounds Bounds`, `Vector3 Position`, `Quaternion Rotation`, `bool IsWorldMode` / `TargetIsHuman`, plus `GetScale()` / `GetPosition()` / `GetRotation()`. Get one for any Thing via `Thing.GetSelection()`. `SetSelectionColor(Color)` does `CursorSelectionRenderer.material.color = color.SetAlpha(InventoryManager.Instance.CursorAlphaInteractable)`.

There is **no built-in "make this arbitrary Thing glow / outline" call**: no outline-material swap, no `MaterialPropertyBlock` rim-light, no `SetHighlight`. The `HighlightInWorld*` hash names in code are audio-clip names, not visual effects. To highlight N affected Things a mod must either (a) drop a stretched semi-transparent box per Thing at `thing.GetSelection().GetPosition()` with `.GetScale()` (reusing the box pattern), or (b) push a tint into each Thing's own renderers via a `MaterialPropertyBlock` and restore it when the Thing leaves the set (the `_EmissionColor` / `_Color` write pattern; see `../GameSystems/RenderingPipelineAndGlow.md` for the emissive variant, and the BlueprintMod section below for a worked `_Color` example).

## World-space lines and wireframes: GL.LINES via Hidden/Internal-Colored
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

The holographic "blueprint" outline shown when holding certain kits is drawn with GL immediate mode by `Assets.Scripts.Wireframe : MonoBehaviour`:

```csharp
private void CreateLineMaterial() {
    if (!LineMaterial) {
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        LineMaterial = new Material(shader);
        LineMaterial.hideFlags = HideFlags.HideAndDontSave;
        LineMaterial.SetInt("_SrcBlend", 5);   // SrcAlpha
        LineMaterial.SetInt("_DstBlend", 10);  // OneMinusSrcAlpha
        LineMaterial.SetInt("_Cull", 2);       // Back
        LineMaterial.SetInt("_ZWrite", 1);
    }
}
public void OnRenderObject() {
    if (Camera.current == CameraController.Instance.StormCardCamera) return;
    CreateLineMaterial();
    LineMaterial.SetPass(0);
    GL.Begin(1);   // GL.LINES
    GL.Color(BlueprintRenderer.material.color.SetAlpha(InventoryManager.Instance.CursorAlphaLine));
    foreach (Edge wireframeEdge in WireframeEdges) {
        ... wireframeEdge.CachedPoint1 = TransformPoint(wireframeEdge.Point1); ...
        GL.Vertex3(p1.x, p1.y, p1.z); GL.Vertex3(p2.x, p2.y, p2.z);
    }
    GL.End();
}
public static void DrawArrow(Vector3 pos, Vector3 direction, Color color, float arrowHeadLength = 0.25f, float arrowHeadAngle = 20f) { ... }
```

`Wireframe.WireframeEdges` is built by `WireframeGenerator`, which merges a prefab's mesh and extracts silhouette edges. The construction cursor attaches one when `structure.Blueprint` is set: `gameObject2 = Object.Instantiate(structure2.Blueprint); structure2.Wireframe = gameObject2.GetComponent<Wireframe>();`. This is the canonical "draw a world-space wireframe" recipe a mod can copy verbatim: a `MonoBehaviour` with `OnRenderObject()` doing `new Material(Shader.Find("Hidden/Internal-Colored"))` -> `SetPass(0)` -> `GL.Begin(GL.LINES)` -> vertex pairs -> `GL.End()`. (`LineRenderer` components also exist in the codebase, e.g. the graph cartridge `GraphDisplay`, for the "have a `LineRenderer`, call `SetPositions`, swap `.material`" recipe, but that is used for screen overlays, not world guidelines.)

`OnRenderObject()` on any active `MonoBehaviour` is the per-frame immediate-mode draw hook; it fires once per camera per frame, so guard against the storm-card camera as the game does: `if (Camera.current == CameraController.Instance.StormCardCamera) return;`.

## BlueprintMod precedent: cube markers, ghost previews, batched wire
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

The third-party BlueprintMod (region-select + copy/paste) is the closest existing analog to "show a preview volume and highlight what is inside it". It uses three techniques, all directly liftable:

**(a) Animated box markers (`BlueprintGuidelines.CreateBoxMarkers`)** builds a GameObject named `"GuidelineBox"` full of Unity primitive cubes (`GameObject.CreatePrimitive(PrimitiveType.Cube)`) for corner brackets, corner dots, and flowing edge dots, with `Shader.Find("Unlit/Color")` materials (fallback `"Sprites/Default"`), colliders destroyed, animated by a `GuidelineAnimator : MonoBehaviour` in its own `Update()`:

```csharp
Shader val = Shader.Find("Unlit/Color") ?? Shader.Find("Sprites/Default");
GameObject box = new GameObject("GuidelineBox");
GuidelineAnimator anim = box.AddComponent<GuidelineAnimator>();
Material cornerMat = new Material(val) { color = color };
Material edgeMat   = new Material(val) { color = edgeColor };
...
GameObject edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
edge.transform.parent = box.transform;
edge.transform.position = mid + dir * (len * 0.5f);
edge.transform.rotation = Quaternion.LookRotation(dir);
edge.transform.localScale = new Vector3(thin, thin, len);
Object.Destroy(edge.GetComponent<Collider>());
edge.GetComponent<Renderer>().sharedMaterial = cornerMat;
```

The 8 AABB corners come from the standard bit pattern: `array[i] = min + new Vector3((i & 1) != 0 ? size.x : 0f, (i & 2) != 0 ? size.y : 0f, (i & 4) != 0 ? size.z : 0f);`.

**(b) Ghost mesh previews with per-instance tint (`BlueprintPreview`)** instantiates each structure's `Thing.Blueprint` GameObject, positions it, strips colliders and disables MonoBehaviours, keeps renderers enabled, and tints colliding ones red via a shared `MaterialPropertyBlock`:

```csharp
private static readonly MaterialPropertyBlock _mpb = new MaterialPropertyBlock();
private static readonly int _colorPropID = Shader.PropertyToID("_Color");
private static readonly Color CollisionColor = new Color(1f, 0.2f, 0.2f, 1f);
...
GameObject ghost = Object.Instantiate(prefab.Blueprint);   // prefab = Prefab.Find(prefabName)
ghost.SetActive(true);
// strip colliders + disable MonoBehaviours; renderers stay on
...
// per frame:
_mpb.SetColor(_colorPropID, colliding ? CollisionColor : entry.OriginalColor);
entry.Rend.SetPropertyBlock(_mpb);
// collision test:
bool colliding = collisionPreviewEnabled && GridController.World != null
                 && GridController.World.GetStructure(cell) != null;
```

Note BlueprintMod only `SetPropertyBlock`s its own spawned ghost clones, never live world Things. Tinting live Things means owning the bookkeeping to restore them (track the previous-frame affected set, restore anything that dropped out; a missed restore leaves permanently-tinted structures).

**(c) Batched GL.LINES wireframe (`BlueprintWireBatcher`)** collects every preview's `Wireframe.WireframeEdges`, transforms to world space, and draws them all in one `OnRenderObject` pass with the same `Hidden/Internal-Colored` material (`_SrcBlend=5, _DstBlend=10, _Cull=2, _ZWrite=1`).

## Enumerating "what will be affected" (matches the real blast set)
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

To preview exactly the Things a blast will hit, mirror `Explosion.DamageThingsInSphere`: `Physics.OverlapSphereNonAlloc(center, radius, buffer)` -> `Thing.Find(collider)` for each -> filter out `Structure { IsBroken: true }`, and (when the explosion passes `mineTerrain: true`) `Ore` and `IExplosive`. Dedupe by `Thing` (one Thing can have several colliders). See `../GameSystems/Explosions.md` for the exact vanilla loop and the `Thing.Find(Collider)` lookup. A "forcibly delete everything in radius" mod that does not use the vanilla blast at all may instead want `OcclusionManager.AllThings.ToList()` filtered by `Vector3.Distance(t.Position, center) <= radius`, which also catches collider-less Things (see `../GameClasses/Thing.md` for the `OcclusionManager.AllThings` pattern).

## Per-frame hook for a preview
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

A StationeersLaunchPad mod's main class can itself be a `MonoBehaviour` (BlueprintMod's `OnLoaded(List<GameObject>, ConfigFile)` class has `Update()` and a static `Instance`), so it gets a free per-frame tick with no Harmony patch. For immediate-mode drawing use `OnRenderObject()` on any active `MonoBehaviour`. For "what is the player holding / aiming at": `CursorManager.CursorThing` (the Thing under the crosshair; see `../GameClasses/CursorManager.md`), `InventoryManager.ActiveHand.Slot.Occupant` (held item), `Human.LocalHuman` / `InventoryManager.WorldPosition`, `GridController.World.GetStructure(Vector3)` (structure on a grid cell). If a patch is preferred over a self-owned MonoBehaviour, a postfix on `CursorManager.SetCursorTarget` or `InventoryManager.Update` works, but the self-owned MonoBehaviour is cleaner.

## Approach ranking for a blast-radius preview
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

| Approach | Cost | Notes |
|---|---|---|
| (a) Translucent sphere/box via `GameObject.CreatePrimitive` + `Unlit/Color`, collider destroyed, pooled and re-scaled per frame | lowest | Copies BlueprintMod's `CreateBoxMarkers` pattern. A `PrimitiveType.Sphere` scaled to `2 * radius` *is* the blast volume. A solid translucent sphere reads fuzzy; mitigate with low alpha (~0.15) or draw a `GL.LINES` lat/long wireframe sphere instead (then it is essentially the `Wireframe` recipe, still cheap). |
| (b) Reuse the game's ghost material (`new Material(CursorManager.Instance.CursorShader)` + `_Offset` + `_Color`) on a generated sphere mesh | medium | Visually consistent with build mode, but you still generate/scale the sphere yourself, the ghost shader is tuned for opaque-ish structure silhouettes not an analytic sphere, and `CursorManager.Instance` needs a null guard. Strictly dominated by (a) for this use. |
| (c) Tint each affected Thing red via a `MaterialPropertyBlock` (`_Color`), restoring on exit from the set | highest | Most informative (player sees exactly which builds die), but the game has no reusable highlight system, so you own the per-frame add/restore bookkeeping. BlueprintMod does the tint pattern, but only on its own ghost clones, not live Things. Layer this on top of (a) only after the un-tint bookkeeping is solid. |

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

- 2026-04-29: page created from a research pass on rendering primitives for a blast-radius preview (game version 0.2.6228.27061). Sources: `Assets.Scripts.Inventory.InventoryManager` construction-cursor setup, `Assets.Scripts.CursorManager`, `Assets.Scripts.Wireframe` / `WireframeGenerator` (all in `Assembly-CSharp`), and the third-party `BlueprintMod` (`BlueprintGuidelines`, `BlueprintPreview`, `BlueprintWireBatcher`, `GuidelineAnimator`). No prior page on world-space preview rendering existed; no conflicts. Emissive-glow specifics cross-referenced against `../GameSystems/RenderingPipelineAndGlow.md` (consistent).

## Open questions

None at creation.
