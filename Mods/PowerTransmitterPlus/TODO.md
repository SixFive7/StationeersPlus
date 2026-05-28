# Power Transmitter Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

## Migrate to the shared MainThreadDispatcher in Patterns/Threading/

- **Replace the private `MainThreadDispatcher.cs` with the shared `Patterns/Threading/MainThreadDispatcher.cs`.** A generalized copy now lives at `Patterns/Threading/MainThreadDispatcher.cs` (namespace `StationeersPlus.Shared`, with a parameterized GameObject name and an error sink), linked into PowerGridPlus. PowerTransmitterPlus still carries its own `PowerTransmitterPlus/MainThreadDispatcher.cs`. When a PowerTransmitterPlus touch is already happening (to avoid a needless rebuild + release), drop the private copy: link the shared file in the `.csproj` (`<Compile Include="..\..\..\Patterns\Threading\MainThreadDispatcher.cs" Link="Patterns\MainThreadDispatcher.cs" />`), change `MainThreadDispatcher.Init()` in `Plugin.cs` to `StationeersPlus.Shared.MainThreadDispatcher.Init("PowerTransmitterPlus_MainThreadDispatcher", msg => Log?.LogError(msg))`, point the `MainThreadDispatcher.Enqueue(...)` call sites in `BeamManager.cs` at the shared type, delete `MainThreadDispatcher.cs`, and remove its `<Compile Include>` entry. Behaviorally identical (the link pattern gives each mod its own per-assembly static state). See `Research/Patterns/MainThreadDispatcher.md` and `Patterns/Threading/README.md`.

## Client-side LinkedPowerTransmitter mirror (wireless link symmetry)

Not needed for PowerTransmitterPlus's own features today (see "why optional" below); tracked because PowerTransmitterPlus is the natural owner of dish-link state, and a sibling mod (Power Grid Plus) currently works around this gap itself. Pick this up only when a PowerTransmitterPlus touch is already happening (avoid a needless rebuild + release).

**The vanilla gap.** A wireless TX/RX dish link is established host-side only: `PowerTransmitter.TryContactReceiver` is gated on `GameManager.RunSimulation` (true only on the host), and it sets both `tx.LinkedReceiver = rx` and `rx.LinkedPowerTransmitter = tx`. Replication to clients is asymmetric:
- `PowerTransmitter.LinkedReceiver` IS replicated: its setter sets `NetworkUpdateFlags |= NetworkUpdateType.Thing.WirelessPower.Receiver`, and `PowerTransmitter.BuildUpdate` / `ProcessUpdate` carry `LinkedReceiver?.ReferenceId` (decompile ~387130-387146). `SerializeOnJoin` carries it too.
- `PowerReceiver.LinkedPowerTransmitter` is NOT replicated: its setter (decompile ~386871) sets no `NetworkUpdateFlags`, and nothing serializes it. It is assigned only inside the host-only `TryContactReceiver`.

So on a multiplayer client, `tx.LinkedReceiver` points at the receiver, but `rx.LinkedPowerTransmitter` stays null. Any code that resolves the link starting from the RECEIVER side gets null on a client.

**Why optional for PowerTransmitterPlus.** PowerTransmitterPlus only reads `tx.LinkedReceiver` for beam endpoints / visibility (`BeamVisibility`, `BeamManager`). Its reads of `rx.LinkedPowerTransmitter` (owning-transmitter resolution in `RotationPatches` / `OnOffPatches`, and `MicrowaveLinkedPartner` in `LogicReadoutPatches`) are all null-guarded and degrade to a no-op or 0 on a client. Auto-aim uses `DishTarget` / `RayTransform`, not the back-reference. So PowerTransmitterPlus-only games are functionally fine with the null back-reference.

**When it matters / how to fix.** Any consumer that needs the RX->TX direction on a client. Fix: a client-only Harmony postfix on `PowerTransmitter.ProcessUpdate` (gated `if (!NetworkManager.IsServer)`) that mirrors the back-reference after vanilla applies the replicated `LinkedReceiver`:
- when `tx.LinkedReceiver != null` and `tx.LinkedReceiver.LinkedPowerTransmitter != tx`, set `tx.LinkedReceiver.LinkedPowerTransmitter = tx`;
- when the link just changed and the previous receiver still back-points at this tx, clear it.
Make it idempotent (only write when the value differs). The RX setter's side effect `InputNetwork = value.OutputNetwork` is the correct client-side state (matches host); its `OnServer.Interact` is `RunSimulation`-gated and skipped on the client. This mirrors PowerTransmitterPlus's existing host-side write in `LinkPatch.cs` (`bestRx.LinkedPowerTransmitter = __instance`) onto the client.

**How Power Grid Plus does it now.** `Mods/PowerGridPlus/PowerGridPlus/Patches/DishLinkPatches.cs` (`RefreshLink`) performs exactly this client-gated, idempotent mirror, because Power Grid Plus's logic-passthrough merge needs the link symmetric on clients (otherwise the receiver-cable-side network cannot see the transmitter-cable-side devices: `GetOtherSide(rx)` reads `rx.LinkedPowerTransmitter`). Power Grid Plus also dirties + refreshes the affected cable networks' device-list consumers on the same link change.

**Composition if both mods are active.** Both would write the same value, so they compose without conflict: Power Grid Plus's `!= tx` guard skips when the back-reference is already correct, so whichever mod's postfix runs first wins and the other is a harmless no-op. If PowerTransmitterPlus implements this mirror (becoming the single owner of dish-link state), Power Grid Plus's mirror becomes redundant and could be dropped from `DishLinkPatches` to centralise ownership; coordinate that change across both mods in the same pass. Until then, leave Power Grid Plus's mirror in place. Do NOT have either mod clear the back-reference in a way the other would fight; keep both idempotent.

## Visualize source power vs delivered power as a spatial gradient along the beam

The beam currently shows one signal: delivered power, capped at 5 kW (vanilla `MaxPowerTransmission`). It modulates pulse-train scroll speed via `sqrt(intensity)` where `intensity = delivered_W / 5000` and is `Mathf.Clamp01`'d. This caps the entire visual at a single power axis with a tiny dynamic range and hides the much larger source-side power the distance-cost model can reach (a 5 kW link at 10 km pulls 255 kW at the source with default `k=5`).

Replace this with a spatial gradient that uses the length of the beam itself as the visual axis: source end driven by `source_draw_W`, receiver end by `delivered_W`. A long lossy link reads as a wide bright red firehose at the transmitter that tapers to a normal blue trickle at the receiver. A short efficient link reads uniform blue thin end-to-end. PowerGridPlus or other mods that lift caps just push both ends further up the same curve, with no architectural assumption about a ceiling.

This is one feature with phased rollout (see Phasing below). Each phase is independently shippable as a v1.8.x increment.

### Foundational research (do not re-derive)

**Frame-rate flicker analysis.** The pulse train is a 32x1 cosine texture that scrolls along the line via material UV offset. Per-frame phase advance is `Time.deltaTime * sqrt(intensity) * scrollMps / wavelength` in cycles per frame (see [BeamPulseTrain.cs](PowerTransmitterPlus/BeamPulseTrain.cs) around the `Update()` method). At 30 FPS the Nyquist limit is half the frame rate (15 Hz stripe frequency); above that the pattern aliases (flicker, reverse, freeze). With default `StripeWavelength = 2.0 m` the hard wall is 30 m/s on-screen, comfortable safe ceiling around 24 m/s. The shipped default `ScrollSpeed = 25` deliberately sits just under it. Because `BeamPulseTrain.SetIntensity` does `Mathf.Clamp01(intensity)`, the speed already saturates at `ScrollSpeed * sqrt(1)` regardless of how high underlying power goes. Do NOT remove that clamp; this feature relies on the existing speed saturation.

**Cable wattage anchors** (from [Research/GameClasses/Cable.md](../../Research/GameClasses/Cable.md), extracted 2026-05-22 via UnityPy + generated type tree against `Assembly-CSharp.dll`):

| Cable.Type | MaxVoltage | Notes |
|---|---:|---|
| `normal` | 5,000 W | matches vanilla `PowerTransmitter.MaxPowerTransmission = 5000f` |
| `heavy` | 100,000 W | 20x normal |
| `superHeavy` | 500,000 W | 100x normal, "vanilla super-heavy ceiling" |

**Source-draw range with the distance-cost model.** The patch in [DistanceCostPatches.cs](PowerTransmitterPlus/DistanceCostPatches.cs) computes `source_draw = delivered * (1 + k * distance_m / 1000)` where `k` is the configurable per-km overhead factor (default 5). With default `k`:

| Distance @ 5 kW delivered | Source draw |
|---|---:|
| 0 m | 5 kW |
| 1 km | 30 kW |
| 5 km | 130 kW |
| 10 km | 255 kW |
| ~20 km | ~505 kW |

So with default config the source-draw axis runs roughly 0 to 500 kW (super-heavy ceiling) before requiring extreme distances. PowerGridPlus or higher `k` push this further.

**Current beam architecture.**
- [BeamManager.cs](PowerTransmitterPlus/BeamManager.cs) creates the shared `Particles/Additive` material (HDR-capable). Shader fallback chain: `Legacy Shaders/Particles/Additive` -> `Particles/Additive` -> `Sprites/Default` -> `Hidden/Internal-Colored`. Holds the `BeamColor` property: reads `BeamColorHex` config (default `000DFF`), applies `EmissionIntensity` boost (default 10x).
- [BeamLine.cs](PowerTransmitterPlus/BeamLine.cs) builds the LineRenderer with `positionCount = 2` (just transmitter and receiver), sets `startColor`/`endColor` to a single color, sets `startWidth`/`endWidth` to a single width. Color and width are static today: set once at creation, never updated.
- [BeamPulseTrain.cs](PowerTransmitterPlus/BeamPulseTrain.cs) caches `_lr.material` on Awake (per-instance clone, safe for per-beam color/width writes without affecting other beams).
- [VisualiserPatches.cs](PowerTransmitterPlus/VisualiserPatches.cs) patches `WirelessPower.VisualizerIntensity` setter and calls `BeamManager.SetLineIntensity(transmitter, value)`. The `value` passed here is `delivered_W / 5000`, clamped to [0,1] downstream.
- [BeamVisualConfigSync.cs](PowerTransmitterPlus/BeamVisualConfigSync.cs) holds server-authoritative config accessors. Host broadcasts via [BeamVisualConfigMessage.cs](PowerTransmitterPlus/BeamVisualConfigMessage.cs) on client connect and on every config change. No per-tick traffic.

### Design

One canonical power -> visual transfer function, sampled independently at both ends. Same curve, two inputs. Source end (t=0 along the line) is `source_draw_W`; receiver end (t=1) is `delivered_W`. Between them, each LineRenderer position samples the curve at an "imagined power at this point" interpolated linearly between the two endpoint signals.

No hard cap on either input. The curve saturates at its configured `VisualMaxPowerW`. PowerGridPlus removing the delivered cap just lets the receiver end climb the same curve the source end does. Both ends can sit at fully saturated red wide if the user pushes enough power.

The pulse train (texture scroll) stays as-is, modulating brightness in time. The spatial gradient rides on top via vertex-color * texture-brightness in the additive shader. No conflict between the two channels.

### Mechanism

Unity's `LineRenderer.colorGradient` is sampled per-position-vertex only; the GPU linearly interpolates between adjacent vertex colors. With only 2 positions a blue<->red lerp passes through a dim purple midpoint. Fix: use N positions (default N=5, max 8 because `Gradient.colorKeys` caps at 8 in Unity). For each position i in 0..N-1:

```
position_t       = i / (N - 1)
imagined_power_W = lerp(source_draw_W, delivered_W, position_t)
normalized_t     = NormalizePower(imagined_power_W)         // log10, clamped [0,1]
gradient color key at position_t = SampleColor(normalized_t)
widthCurve key    at position_t = SampleWidth(normalized_t)
```

`SampleColor` interpolates the three configured anchors (Low/Mid/High = blue/magenta/red by default). The magenta anchor at `normalized_t = 0.5` keeps the path bright across the blue-to-red transition.

Width: linear `widthCurve` (resolved design decision). `SampleWidth` returns `WidthBase` over `[0, WidthBloomThreshold]` and ramps linearly to `WidthMax` over `[WidthBloomThreshold, 1]`. Default `WidthBloomThreshold = 0.7` puts width bloom onset roughly at the heavy -> super-heavy transition in the vanilla calibration.

Source-nozzle bloom (resolved design decision: yes): when source-end `normalized_t > NozzleBloomThreshold` (default 0.85), enable `LineRenderer.numCapVertices` rounded cap at t=0 and override the t=0 gradient color with an extra-bright variant. Cosmetic flare at the transmitter when source draw approaches the configured ceiling.

### Canonical transfer function (all defaults tunable via config)

Power-to-normalized:

```
normalized_t = clamp((log10(P_W) - log10(MinPowerW)) / (log10(MaxPowerW) - log10(MinPowerW)), 0, 1)
```

| Knob | Default | Purpose |
|---|---|---|
| `VisualMinPowerW` | 100 | Below this both ends sit at Low color, base width |
| `VisualMaxPowerW` | 500000 | "Fully saturated red, full bloom width" power (vanilla super-heavy ceiling) |
| `ColorLowHex` | reuses existing `BeamColorHex` (default `000DFF`) | Color at normalized_t = 0 |
| `ColorMidHex` | new, default `FF00FF` (magenta) | Color at normalized_t = 0.5 |
| `ColorHighHex` | new, default `FF0000` (red) | Color at normalized_t = 1 |
| `WidthBase` | reuses existing `BeamWidth` (default 0.1) | Width at normalized_t <= bloom threshold |
| `WidthMax` | new, default 0.25 | Width at normalized_t = 1 |
| `WidthBloomThreshold` | new, default 0.7 | Normalized_t where width starts to grow |
| `LinePositionCount` | new, default 5 | Positions sampled along the line (max 8) |
| `SourceNozzleBloom` | new, default true | Enable source-end cap-vertex flare |
| `NozzleBloomThreshold` | new, default 0.85 | Normalized source power at which the flare activates |
| `EmissionIntensity` | unchanged (default 10.0) | Continues to multiply all RGB |

Note that `VisualMaxPowerW` is the saturation point of the curve, not a cap on the input signal. PowerGridPlus + 1 MW link with default `VisualMaxPowerW=500000` saturates both ends at full red; raise the knob to differentiate higher loads.

### File-by-file change list

| File | Change |
|---|---|
| [PowerTransmitterPlus/Plugin.cs](PowerTransmitterPlus/Plugin.cs) | Add `Config.Bind` entries for the new knobs. Use existing `Server - Visual` section (avoid proliferating headers in the LaunchPad settings panel; rely on `("Order", n)` tags to group). Keep existing `BeamColorHex` / `BeamWidth` / `EmissionIntensity` as-is for backwards compatibility (they become the low/base anchors, no BepInEx rename, no value reset). |
| [PowerTransmitterPlus/BeamVisualConfigSync.cs](PowerTransmitterPlus/BeamVisualConfigSync.cs) | Add `GetEffective*` accessors for each new knob, mirroring the existing host-override pattern (`UseHostValues` clauses). |
| [PowerTransmitterPlus/BeamVisualConfigMessage.cs](PowerTransmitterPlus/BeamVisualConfigMessage.cs) | Extend wire format with the new fields. Append fields rather than rearranging to keep older clients best-effort compatible (existing reader stops after known fields). |
| [PowerTransmitterPlus/BeamManager.cs](PowerTransmitterPlus/BeamManager.cs) | Add `SetLinePower(transmitter, delivered_W, source_draw_W)`. Add static helpers `NormalizePower(p_W)`, `SampleColor(t)`, `SampleWidth(t)`, `BuildGradient(delivered_W, source_draw_W, N)`, `BuildWidthCurve(delivered_W, source_draw_W, N)`. |
| [PowerTransmitterPlus/BeamLine.cs](PowerTransmitterPlus/BeamLine.cs) | `positionCount = LinePositionCount`. Place positions evenly between transmitter and receiver in `useWorldSpace=true`. Add `ApplyPowerVisual(delivered_W, source_draw_W)` that calls `BuildGradient` / `BuildWidthCurve` and assigns them, plus handles nozzle bloom (`numCapVertices` and a boosted t=0 color when above threshold). |
| [PowerTransmitterPlus/BeamPulseTrain.cs](PowerTransmitterPlus/BeamPulseTrain.cs) | No changes. Pulse speed continues to use `Mathf.Clamp01(intensity)` and `sqrt(intensity)`, saturating at the flicker-safe cap regardless of underlying power. |
| [PowerTransmitterPlus/VisualiserPatches.cs](PowerTransmitterPlus/VisualiserPatches.cs) | Where it currently calls `BeamManager.SetLineIntensity(transmitter, value)`, also compute `delivered_W = value * 5000` (or read the raw delivered watts directly from the transmitter if cleaner), compute `source_draw_W` (see next), and call `BeamManager.SetLinePower(transmitter, delivered_W, source_draw_W)`. The pulse-train `SetIntensity` call continues to receive the existing intensity value unchanged. |
| [PowerTransmitterPlus/DistanceCostPatches.cs](PowerTransmitterPlus/DistanceCostPatches.cs) | Expose `source_draw_W` for the visual layer. The value is computed locally inside `GetUsedPower` as `delivered * multiplier`. Two options: (a) cache the last computed source draw on the transmitter as a sidecar field; (b) re-derive it from `delivered_W * (1 + k * distance_m / 1000)` in `VisualiserPatches` using already-known inputs. Sidecar is cleaner; re-derive is simpler. See Open Items below. |
| [README.md](README.md) | Document the new settings group, the transfer function, the spatial gradient story. Update the settings table. |
| [CHANGELOG.md](CHANGELOG.md) | New top entry. v1.8.0 (visible visual overhaul + new settings). No BepInEx reset caveat: only adding fields, not renaming existing ones. |
| [RESEARCH.md](RESEARCH.md) | New section documenting the spatial gradient mechanism, the canonical transfer function, the per-position sampling math, and the multiplayer sync addendum. |

### Multiplayer sync

All new knobs flow through the existing server-authoritative `BeamVisualConfigMessage`; host broadcasts on connect and on every config change. No new per-tick traffic. Clients compute their local `source_draw` on the fly:

```
source_draw_W = delivered_W * (1 + k * distance_m / 1000)
```

where `delivered_W` comes from the already-synced `VisualizerIntensity * 5000`, `k` is the synced PowerTransmitterPlus distance-cost overhead factor, and `distance_m` is computed from already-synced transmitter and receiver positions. Client and host arrive at the same visual deterministically.

If option (a) sidecar field is chosen for source-draw plumbing on the transmitter, the field is server-set in `DistanceCostPatches` and synced via whatever transmitter-state sync the game already uses, plus deterministic re-derivation as fallback for clients that join mid-tick. Option (b) re-derivation avoids the sync question entirely.

### Phasing

- **Phase 1 -- Infrastructure.** Bump `positionCount` to N=5, distribute positions evenly along the line, build and assign a uniform `colorGradient` + `widthCurve` from the current single-color/single-width values. No new signals captured yet. Done when: beam renders visually identical to today; no visible regression on existing links.
- **Phase 2 -- Canonical curve + single-player.** Capture `source_draw_W` (per file change list above), implement `NormalizePower` / `SampleColor` / `SampleWidth`, per-position sampling, gradient + width curve build, wire to `SetLinePower`. Done when: short link is uniform blue, long lossy link shows red-to-blue gradient, very-long link saturates both ends; no flicker introduced (pulse train unchanged so this should hold by construction).
- **Phase 3 -- Multiplayer sync.** Add new fields to `BeamVisualConfigMessage`, extend `BeamVisualConfigSync` accessors, verify clients render the same gradient as host via InspectorPlus on a dedicated-server test. Done when: host + client agree on beam visuals across a 10 km link with a config change applied mid-session.
- **Phase 4 -- Source-nozzle bloom + polish.** Enable `numCapVertices` cap on the LineRenderer at t=0, boosted gradient key when `NozzleBloomThreshold` exceeded. Done when: nozzle flare visible at extreme source draw, absent at moderate draw, smooth threshold crossing without popping.

### Open items to confirm before writing code

- Section name in the LaunchPad panel: stay with the existing `Server - Visual` section (plan default), or create a new `Server - Power Visual` group. Repo CLAUDE.md notes section header proliferation is a UX cost.
- Existing `BeamColorHex` and `BeamWidth` semantically become the low/base anchors (plan default, no rename, no BepInEx reset). Alternative: rename to `ColorLowHex` / `WidthBase` for clarity, which would reset existing user configs.
- Defaults for `ColorMidHex`, `ColorHighHex`, `WidthMax`, `WidthBloomThreshold`. Currently placeholders (`FF00FF` / `FF0000` / 0.25 / 0.7). User can request sample renders before locking, or accept and tune in playtest.
- Source-draw plumbing choice: sidecar field on transmitter (cleaner separation, requires sync verification) vs. re-derive in `VisualiserPatches` from `delivered + k + distance` (simpler, one fewer touchpoint). Both are correct; pick on style during Phase 2.

### Verification approach

Use [InspectorPlus](../InspectorPlus/) snapshots paired with the dedicated server at [DedicatedServer/](../../DedicatedServer/):

- Drop a request `{ "types": ["BeamLine"], "fields": ["_lineRenderer"], "maxDepth": 3, "includePrivate": true }` (or the type that owns the LineRenderer reference, verify in Phase 1) to inspect `colorGradient` / `widthCurve` / `positionCount` after a controlled power level change. Note: prefab templates are NOT visible through InspectorPlus (it uses `FindObjectsOfType`, not `FindObjectsOfTypeAll`); only placed transmitters with active beams are reachable.
- Before/after snapshot pairs at known power levels (e.g. 5 kW short link, 5 kW @ 10 km link, mod-uncapped high-power link) to confirm the gradient builds match the expected output of `NormalizePower` -> `SampleColor` / `SampleWidth`.
- Headless dedi needs `Force Unpause Without Client = true` in `install/BepInEx/config/net.inspectorplus.cfg` for request-file processing without a connected client (already configured on this repo's dedi as of 2026-05-22).
- For multiplayer (Phase 3), snapshot host and client simultaneously and diff the LineRenderer state for the same transmitter.

### Related research

- Beam internals and current architecture: [Mods/PowerTransmitterPlus/RESEARCH.md](RESEARCH.md)
- Cable wattages and `MaxVoltage` field: [Research/GameClasses/Cable.md](../../Research/GameClasses/Cable.md)
- Power transmitter internals (`VisualizerIntensity`, `MaxPowerTransmission`, distance-loss curve): [Research/GameClasses/PowerTransmitter.md](../../Research/GameClasses/PowerTransmitter.md)
- LaunchPad settings panel grouping conventions: [Research/Patterns/StationeersLaunchPadSettingsGrouping.md](../../Research/Patterns/StationeersLaunchPadSettingsGrouping.md)
- InspectorPlus capabilities and limits (scene-only via `FindObjectsOfType`): [Research/Workflows/InspectorPlusUsage.md](../../Research/Workflows/InspectorPlusUsage.md)
- Dedicated server driving procedure and session lock: [DedicatedServer/CLAUDE.md](../../DedicatedServer/CLAUDE.md), [DedicatedServer/session.lock.template](../../DedicatedServer/session.lock.template)
