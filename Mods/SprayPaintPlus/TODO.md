# SprayPaintPlus TODO

## v1.6.0 verification (before tagging)

- [ ] Save a new v1.6.0 world with 3+ glowing Things. Expand the save ZIP and confirm: `world.xml` contains NO `xsi:type="GlowThingSaveData"` attributes; `sprayplus-glow.xml` is present with the expected ReferenceIds.
- [ ] Round-trip: load that save; InspectorPlus snapshot with `types=[Thing]`, `fields=[ReferenceId, CustomColor]` after load; confirm the 3 glowing Things show the emissive material in-game.
- [ ] Removal test: rename `BepInEx/plugins/SprayPaintPlus/` to `SprayPaintPlus.disabled/`, load the save from step 1; confirm the save loads (no "Failed to load the world.xml" error), all Things present without glow. Restore the folder after.
- [ ] Back-compat: load a pre-v1.6.0 save (with `GlowThingSaveData` entries in world.xml); confirm glow restored. Save it. Expand the new ZIP and confirm world.xml no longer contains `xsi:type="GlowThingSaveData"` and the side-car carries the IDs.
- [ ] Auto-save: let an auto-save fire with glowing Things present; repeat the ZIP-content checks on the auto-saved file.
- [ ] Multiplayer host: run a multiplayer session with glowing Things, save on the host, confirm side-car written. `GameManager.AutoSaveNow` guards on `!NetworkManager.IsClient` so client saves are already excluded.
- [ ] Empty set: save a world with zero glowing Things; confirm no `sprayplus-glow.xml` entry exists (or an empty-IDs entry is fine; both are acceptable).

## Post-release follow-up

- [ ] One release cycle after v1.6.0 has propagated: remove `GlowThingSaveData.cs` and the back-compat `ThingDeserializeSaveGlowPatch`. This strands any users who skipped v1.6.0 entirely when loading a v1.5.x save, so keep the back-compat path for at least one minor version.

## New features

- [ ] **Right-click eyedropper on spray can.** While holding a spray can, right-click the Thing under the cursor to copy its color onto the held can. Use case: right-click an existing pipe, then left-click the neighbouring pipe to paint it the same color. Implemented inline in `ColorCyclerPatch` (Option A).
  - Plain right-click: copy `CursorManager.CursorThing.CustomColor.Index` (current paint color).
  - Ctrl+right-click: copy the "as-built" color the target would have after going through its normal build flow (printer emits a kit, kit places the structure, structure inherits the kit's `PaintableMaterial` via `Constructor.Construct` → `Structure.SetStructureData` → `SetCustomColor`). Resolved by `ElectronicReader.GetAllConstructors(target)` → first `Constructor` kit's `PaintableMaterial` → `GameManager.GetColorIndex`. Falls back to `target.PaintableMaterial` when no kit builds this Thing (items, direct-placement types like pipes). Reading `target.PaintableMaterial` directly is WRONG for kit-built structures because kit and structure prefabs have different `PaintableMaterial` slots (e.g. `KitLadder` vs `Ladder`).
  - Shift+right-click: reserved, no-op.
  - Hook: detect `KeyManager.GetMouseDown("Secondary")` inside the existing `InventoryManager.NormalMode` prefix. Read modifier state with the same `KeyManager.GetButton(LeftShift/RightShift/LeftControl/RightControl)` pattern used for paint modifiers.
  - Sync path: reuse `SprayPaintHelpers.UpdateSprayCanServer` (host) / `UpdateSprayCanVisual` + `SprayCanColorMessage.SendToHost()` (remote client). No new network message.
  - Skip cases: null cursor target, `!target.IsPaintable`, `CustomColor == null` (defensive; should not occur post-`Thing.Awake`), resolved index `< 0`, or same index as current can color.
  - Vanilla `SprayCan` does not override `Item.OnUseSecondary`, so there is no vanilla side-effect to coexist with.
  - `Thing.Awake` initializes `CustomColor = GameManager.GetColorSwatch(PaintableMaterial)` for every paintable prefab, so every spawn path (console, creative, fabricator, constructor) starts at the same stable default; the Ctrl-variant's "printer default" read is just `PaintableMaterial`.
