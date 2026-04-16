# SprayPaintPlus TODO

## Under Revision

### Glow-in-the-dark paint

A special paint mode that makes painted surfaces emit a faint light matching the paint color, visible in unpowered or dark rooms.

**Approach (first experiment): material emission injection**

Harmony postfix on `SetCustomColor`. Get the object's `MeshRenderer.material`, enable the `_EMISSION` shader keyword, set `_EmissionColor` to the paint color scaled by an intensity factor. Track glow-enabled objects in a `HashSet<Thing>` so emission can be cleared on repaint or destruction. Derive glow from the already-synced color index (no new network messages). Persist glow state per-thing for save/load.

**Player activation**

Gated by a new config toggle ("Enable Glow Paint") and a spray modifier (e.g. hold Alt while spraying) so normal paint is unaffected.

**Known constraints**

- Structures with `structureRenderMode != Standard` already throw `NotImplementedException` on `SetCustomColor` and can't be individually recolored; they also can't be individually glowed.
- Accessing `.material` (not `.sharedMaterial`) creates per-instance material copies, increasing draw calls. Budget this against network-paint operations that could touch dozens of objects.
- Emission injection only works if the game's shaders honor the `_EMISSION` keyword. If they don't, fall back to a shader swap (Legacy Shaders/Particles/Additive is confirmed to work at runtime from PowerTransmitterPlus) or a prebuilt emissive material set.

**Alternatives considered (rejected for first pass)**

- Attach `Light` components per painted object. Real illumination but performance disaster with network painting (dozens of overlapping point lights) and save/load reconstruction needed.
- Hybrid emission + capped point lights. Inconsistent UX (some glows cast light, others don't).
- Shader swap to pure additive. Breaks normal shading on the object in lit areas.
- Inject new "glow variants" into `GameManager.Instance.CustomColors`. Doubles the scroll list, risks index-stability issues with other mods and with save files when the mod is uninstalled.

**Multiplayer**

Zero new messages. Server paints, color index syncs via existing flow, each client applies emission locally in a postfix. Glow state is a deterministic function of color index + glow flag per object.

**Open questions**

- Do the game's shaders actually honor `_EMISSION`? Prototype and check.
- Does the glow need to survive save/load, or is it acceptable to reapply on spawn from a per-thing persisted flag?
- Should the intensity be configurable per-player, or fixed?
