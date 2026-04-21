# SprayPaintPlus TODO

## v1.5.0 candidates

### Gun paint-mode toggle (add glow / remove glow)

Nice-to-have: let the player switch the Spray Paint Gun between two modes:

- **Add Glow** (default): paint with the gun applies glow to the target's existing color. This is the v1.4.0 behavior.
- **Remove Glow**: paint with the gun removes glow from the target while keeping the color, effectively the same as painting with a plain spray can but without changing color.

Both modes should respect the existing Shift (single) and Ctrl (checkered) modifiers.

Rough design:

- Per-gun mode state keyed by `SprayGun.ReferenceId`, stored in a new `GunGlowMode` dictionary in `GlowPaintHelpers`.
- Scroll wheel while holding the gun cycles the mode (extend the existing `ColorCyclerPatch` which already runs on `InventoryManager.NormalMode` and already polls scroll for the can's color cycling).
- New LaunchPadBooster message (`GunGlowModeMessage`) to sync the mode change from client to server, mirroring the `SprayCanColorMessage` pattern.
- Server-side mode storage persists per-gun across save/load via a small extension to `GlowThingSaveData` (another bool) or a sibling save-data type on the gun itself.
- `SprayGunGlowPatch.Prefix` reads the mode: add-glow calls `OnServer.SetCustomColor(target, target.CustomColor.Index)` and lets the glow postfix apply glow; remove-glow calls the same but routes through a "force glow off" path (possibly a separate scope counter like `GunRemoveScope` so the postfix knows to call `SetGlow(thing, false)` rather than `SetGlow(thing, true)`).
- UI indicator: tint the gun's in-hand thumbnail or flash a console message on mode change. Feasibility depends on whether the gun's `Thumbnail` field is mutable at runtime like the can's is.

Deferred from v1.4.0 because it is a distinct feature on top of the "gun is a glow applicator" foundation, not a refinement of it. Ships cleanest as its own release.

### Legacy-save eject for loaded-gun saves

v1.4.0 blocks new `SprayCan` insertions into the `SprayGun` via `Thing.CanEnter` + `Slot.AllowMove` patches, but does not touch saves that already have a can loaded in a gun from before the block shipped. Those cans remain visible inside the gun and continue to work, but cannot be re-inserted if removed.

Follow-up: postfix the gun's post-load hook (likely `Thing.OnFinishedLoad`, pending verification of the method signature) to eject orphaned `SprayCan` occupants back to the world via `OnServer.MoveToWorld`. See `Research/Patterns/SlotInsertionBlock.md` section "Legacy-state handling" for the pattern.

Low priority; impacts only players who both (a) had v1.3.x installed AND (b) actually loaded a can into a gun. Fresh v1.4.0 installs never hit this state.

### Bloom attachment verification

`Research/GameClasses/CameraController.md` section "Runtime attachment (partially resolved)" lists three candidate runtime accessors for `UltimateBloom` (`CameraController.Instance.MainCamera.GetComponent<UltimateBloom>()`, `Camera.main.GetComponent<UltimateBloom>()`, `CameraController.Instance.CurrentCamera.GetComponent<UltimateBloom>()`). The decompile-derived `CameraController.Instance.CameraEffects[0].Bloom` path is a dead field at runtime.

Runtime-verify which candidate resolves, so a future feature that wants to validate "is bloom on?" from mod code has a known-good accessor. A minimal probe plugin (similar to the previous `Plans/GlowPaintProbe/`) attaching a one-shot `GetComponent<UltimateBloom>()` check on a Harmony postfix of `InventoryManager.NormalMode` would settle it in a single game-launch cycle. Not blocking; feature ships without this because bloom is visibly working.
