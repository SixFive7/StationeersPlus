---
title: Time-Skip World Manipulation
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:582-607
  - Plans/LLM/RESEARCH.md:543-577
related:
  - ../GameSystems/DamageState.md
  - TriggerLanderCapsule.md
  - CameraEffectsRuntime.md
tags: [timeskip, worldgen, entity]
---

# Time-Skip World Manipulation

Catalog of the systems a mod can mutate to fake elapsed time while a cover window (lander capsule, fake loading screen, ragdoll pause) hides the transition. Reach for this recipe when designing a "skip ahead" event: the systems table names the APIs, the APIs reference lists the non-obvious camera / helmet / input / loading / ragdoll / timescale handles you pair with them.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A mod wants to advance world state by hours or days of simulated elapsed time in a single server tick.
- A mod wants a catalog of "here is what you can move forward" to design a coherent time-skip.
- A mod wants the non-obvious handles (camera shake, helmet frost, fake loading screen, ragdoll toggle, `Time.timeScale`) that pair with the world-state mutations for visual coherence.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side code path for anything that mutates world state.
- A cover window: the lander capsule (see [TriggerLanderCapsule.md](TriggerLanderCapsule.md)), fake loading screen, stun + ragdoll, or similar. The table's effects are not network-synced cosmetically in every case; hide the frame where the mutation happens.

## Systems that can be modified to fake elapsed time
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| System | API | Notes |
|---|---|---|
| Sun position | `OrbitalSimulation.SetDayTime(float)` (public static, 0-1 range) | Moves sun to target time, updates all orbital bodies |
| Day counter | `WorldManager.DaysPast` (public static uint, settable) | Triggers `WeatherManager.OnNextDay()` and per-day events |
| Player hunger | `Entity.Nutrition` (public float, auto-syncs) | Clamped 0 to 50 (Human). 0 causes starvation damage |
| Player thirst | `Entity.Hydration` (public float, auto-syncs) | Clamped 0 to 8.75. 0 causes dehydration damage |
| Player hygiene | `Entity.Hygiene` (public float, auto-syncs) | Clamped 0 to 1.5 |
| Player mood | `Entity.Mood` (public float, auto-syncs) | Clamped 0 to 1 |
| Battery charge | `BatteryCell.PowerStored` / `Battery.PowerStored` (public float, settable) | Call `UpdateBatteryState()` after to update visuals |
| Food decay | `item.DamageState.Damage(Increment, amount, DamageUpdateType.Decay)` | Check `item.CanDecay` first. Iterate `Item.AllDecayingItems` |
| Plant growth | `Plant.Stage` (private setter, needs reflection) | Iterate `Plant.AllPlants`. Setter triggers `StageChanged()` |
| Room temperature | `atmosphere.GasMixture.TotalEnergy` (read-modify-write struct) | Slight energy reduction simulates heat loss |
| Pipe gas | `PipeNetwork.AllPipeNetworks` (public static list), each has `.Atmosphere.GasMixture` | Small quantity reduction simulates slow leaks |
| Suit air | `suit.AirTank.InternalAtmosphere.GasMixture` | Read-modify-write the oxygen Mole |
| Item wear | `thing.DamageState.Damage(Increment, 3f, DamageUpdateType.Brute)` | Pick random subset. Keep damage small |
| Weather | `WeatherManager.ImmediatelyActivateWeatherEvent(string id)` (public static) | Storm IDs depend on world type |
| Lights | `OnServer.Interact(light, InteractableType.Activate, 0)` | Toggle off a few random lights |
| Doors | `OnServer.Interact(door, InteractableType.Open, 1)` | Suggest someone walked through |
| Spawn objects | `OnServer.Create<DynamicThing>(prefabName, position, rotation)` | Drop tools/debris near repaired items |
| Player position | `OnServer.MoveInWorld(human, pos + offset, true)` | Slight shift, under 0.3m |
| Days lived | `Entity.DaysLived` (public ushort, settable) | Per-character age counter |
| Trader contacts | `TraderContact.EndLifetime = Time.time` | Expires contact immediately |
| Chat messages | `ChatMessage` with `HumanId = -1` via `NetworkServer.SendToClients` | Post fake timestamped event log entries |
| Room pressure | `atmosphere.Remove(quantity, GasType)` | Slight depressurization |
| Console messages | `ConsoleWindow.Print(string, ConsoleColor)` | Client-local only. For server broadcast, use ChatMessage |

## Camera / helmet / input / loading / ragdoll / timescale APIs
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

### Camera manipulation

- `CameraController.SetCameraShake(float intensity)`: public static. Intensity 0 to 2.5, decays over time. Used by explosions.
- `CameraController.Instance.SetThirdPersonCamera(bool)`: public. Forces third-person view.
- `CameraController.SetFieldOfView(float fov)`: public static. Extreme values (130+) create fisheye distortion.
- `CameraController.Instance.RotationX / RotationY`: public fields. Can spin the camera.

### Helmet frost

`FirstPersonHelmetOverlay.CurrentFrostSetting`: public static float. Set to -1 for full frost overlay, 0 for clear. Driven by atmosphere temperature in `InventoryManager` (maps 0C to 20C range).

### Input lock

`KeyManager.SetInputState(string key, KeyInputState state)`: public static. String-keyed stack.

- `KeyInputState.Paused`: blocks game input, shows cursor. Player can look around but cannot move, interact, or use items.
- `KeyManager.RemoveInputState(string key)`: restores previous state.

### Fake loading screen

`ImGuiLoadingScreen.SetActive(true)`: shows fullscreen loading screen with random background.
`ImGuiLoadingScreen.SetState("text")`: sets status text.
`ImGuiLoadingScreen.FakeProgress()`: auto-advances progress bar.

Completely blocks the view. Pair with `KeyManager.SetInputState` for input lock.

### Alert messages

`AlertMessage.Show(string text, float duration)`: centered text overlay with fade in/out.

### Ragdoll without unconsciousness

`Entity.SetRagdoll(bool active)`: public. Disables animator, enables ragdoll colliders. Can be called independently of entity state. Player retains camera control but body goes limp.

### Time scale

`Time.timeScale = 0.1f`: slows everything. The game normally only uses 0 (paused) and 1 (normal), but fractional values work. Affects all simulation including atmospherics.

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- For each system you mutate, snapshot the relevant field before and after (see [InspectorPlusUsage.md](InspectorPlusUsage.md)).
- Confirm visual followups: battery meters need `UpdateBatteryState()` after a `PowerStored` write; plant stage changes trigger `StageChanged()` through the setter; weather changes are driven through `ImmediatelyActivateWeatherEvent`.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side only. Anything that mutates world state must be server-gated or it will desync multiplayer.
- `Time.timeScale` affects all simulation including atmospherics. Fractional values work even though the vanilla game normally uses only 0 and 1.
- `ConsoleWindow.Print` is client-local only. For a server broadcast, use `ChatMessage`.
- Some setters have extra effects that are not obvious from the field name. `Plant.Stage` triggers `StageChanged()`, `BatteryCell.PowerStored` needs `UpdateBatteryState()` for visuals, `WorldManager.DaysPast` triggers `WeatherManager.OnNextDay()` and per-day events.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0093 (systems table) and F0095t (APIs reference) in `Plans/LLM/RESEARCH.md`.

## Open questions

None at creation.
