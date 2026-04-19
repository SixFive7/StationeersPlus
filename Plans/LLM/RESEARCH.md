# LLM Mod - Research

Technical internals, decompiled game structures, and design decisions for the LLM chat companion mod.

## Chat system internals

### Message flow

Stationeers chat uses the game's native network message system, not a separate chat protocol. The flow:

1. Player types in chat UI, client constructs a `ChatMessage` and sends it to the server via `NetworkClient.SendToServer(chatMessage)`
2. Server receives the message, calls `ChatMessage.Process(hostId)`
3. `Process()` prints to the server console, then broadcasts to all clients via `NetworkServer.SendToClients(this, NetworkChannel.GeneralTraffic, -1L)`
4. Each client receives the broadcast, prints to their console, and shows a chat bubble above the sender's character model

### ChatMessage class

**Namespace:** `Assets.Scripts.Networking`
**Base class:** `ProcessedMessage<ChatMessage>` (which extends `MessageBase<T>`)
**MessageFactory index:** 80

Fields:
- `long HumanId` - the sender's `Thing.ReferenceId`. Set to `-1` for server/batch messages (no associated human entity)
- `string DisplayName` - the sender's display name. "Server" for batch mode, player name otherwise
- `string ChatText` - the message body

Serialization order (must match exactly): `HumanId`, `DisplayName`, `ChatText`.

`Process()` logic:
```
PrintToConsole()                    // always: writes "DisplayName: ChatText" to console in cyan
if (NetworkManager.IsServer)
    NetworkServer.SendToClients()   // server broadcasts to all connected clients
Thing.Find<Human>(HumanId)          // look up sender entity
if (human exists && not local player)
    human.SetChatText(ChatText)     // show chat bubble above their head
```

When `HumanId` is `-1`, `Thing.Find<Human>(-1)` returns null, so no chat bubble appears. The message still shows in every player's console/chat log. This is the correct behavior for a bot: text in chat, no floating bubble.

### ChatStatusMessage class

**Namespace:** `Assets.Scripts.Networking`
**MessageFactory index:** 81

Sent when a player starts/stops typing. Fields: `long HumanId`, `bool IsTyping`. Triggers the "..." typing indicator bubble above a player's head. Not relevant for the bot (we never show typing status).

### SayCommand (server-side chat sending)

**Namespace:** `Util.Commands`

The `say` console command demonstrates the server-side send pattern:

```csharp
ChatMessage chatMessage = new ChatMessage
{
    ChatText = input,
    DisplayName = GameManager.IsBatchMode ? "Server" : Human.LocalHuman.DisplayName,
    HumanId = GameManager.IsBatchMode ? -1 : Human.LocalHuman.ReferenceId
};
// On dedicated server (IsServer && IsBatchMode):
chatMessage.PrintToConsole();
NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
```

Key detail: on a dedicated server (`GameManager.IsBatchMode`), there is no `Human.LocalHuman`. The code uses `HumanId = -1` and `DisplayName = "Server"`. The bot follows this same pattern.

### ChatCanvas and ChatWindow

`ChatCanvas` is attached to each `Human` entity and manages the floating chat bubble UI. `ChatWindow` handles the typewriter-style text animation. Neither is relevant for the bot since `HumanId = -1` produces no bubble.

### Client-side processing exception

In `NetworkBase.DeserializeReceivedData()`, most message types log an error if processed on a non-server peer. `ChatMessage` is explicitly exempted from this check, meaning clients process it normally (print to console + show bubble). This is standard for broadcast messages.

### NetworkChannel enum

```
GeneralTraffic = 134    (reliable, used for chat)
PlayerJoin = 135
StateTick = 136
Unreliable = 137
SteamP2P* = 138-140
```

Chat uses `GeneralTraffic` (reliable delivery).

## Hook strategy

### Intercepting player messages

Harmony Postfix on `ChatMessage.Process(long hostId)`. Fires after the game's own processing (console print + broadcast) is complete. The Postfix reads `__instance.DisplayName` and `__instance.ChatText` to check the trigger prefix. Only fires on the server (`NetworkManager.IsServer` guard).

Why Postfix, not Prefix: let the game finish its normal broadcast first. The bot response arrives asynchronously anyway, so there is no need to block or modify the original message flow.

### Sending bot responses

Construct a new `ChatMessage` with:
- `HumanId = -1` (no human entity)
- `DisplayName = configured bot name`
- `ChatText = model output`

Then call `PrintToConsole()` (server console) and `NetworkServer.SendToClients(msg, NetworkChannel.GeneralTraffic, -1L)` (broadcast to all clients). This exactly mirrors `SayCommand.Say()` for batch/server mode.

### Thread safety

LLM inference runs on a dedicated background thread (`Thread`, not `Task`, to avoid Unity's SynchronizationContext). Responses are enqueued into a `ConcurrentQueue<string>`. The plugin's `Update()` (main thread, every frame) drains the queue and sends chat messages. All game API calls (ChatMessage construction, NetworkServer.SendToClients) happen on the main thread.

## LLamaSharp integration

### Library choice

LLamaSharp provides C# bindings for llama.cpp. It targets .NET Standard 2.0, compatible with the game's .NET Framework 4.7.2 runtime. The `LLamaSharp.Backend.Cpu` NuGet package bundles the native llama.cpp shared library for CPU-only inference.

### Inference approach

Uses `StatelessExecutor` rather than `InteractiveExecutor`. Each request builds a fresh prompt from the system prompt + player message. This avoids context window management across unrelated player messages and simplifies the threading model (no shared mutable state in the executor).

Trade-off: stateless means no conversation memory across messages. A player asking a follow-up gets no context from their previous question. This fits the "unreliable satellite relay" lore (each transmission is independent).

### Prompt template

Qwen2.5-Instruct uses ChatML format:
```
<|im_start|>system
{system prompt}<|im_end|>
<|im_start|>user
[{playerName}]: {message}<|im_end|>
<|im_start|>assistant
```

Anti-prompts: `<|im_end|>` and `\n\n` to stop generation cleanly.

### Resource budget

- Model file: ~1.1 GB on disk (Q4_K_M quantization)
- RAM at runtime: ~2-3 GB resident
- CPU: configurable thread count, runs at `BelowNormal` priority to yield to game simulation
- Inference speed on modern server CPU: ~5-15 tokens/sec, producing a 50-token response in 3-10 seconds

### Deployment structure

```
BepInEx/plugins/LLM/
  LLM.dll
  LLamaSharp.dll
  llama.dll              (native, from LLamaSharp.Backend.Cpu runtimes/)
  models/
    qwen2.5-1.5b-instruct-q4_k_m.gguf
  About/
    About.xml
```

The native `llama.dll` must be in the same directory as `LLM.dll` or in a `runtimes/` subfolder matching the platform RID (`win-x64`). LLamaSharp probes both locations. For simplicity, flatten it next to the DLL.

## Design decisions

### Trigger prefix instead of responding to all chat

Responding to every chat message would flood the channel and burn CPU on irrelevant chatter between players. The `@sat` prefix gives players explicit control. An empty prefix config makes it respond to everything, for servers that want that.

### No conversation memory

Each message is independent. Alternatives considered:
- Sliding context window: track last N messages per player. Adds state management complexity, context window math, and the model's small context (2048 tokens) fills fast with multi-turn chat.
- Per-player sessions with timeout: even more state. Not worth it for a 1.5B model that barely handles single-turn well.

The satellite relay framing makes this a feature: "Signal quality too poor for session persistence."

### HumanId -1 for bot messages

Using `-1` means no chat bubble appears above any entity. This is intentional: the bot is a satellite, not a physical entity in the world. The message appears in the chat log only, matching how the "Server" label works on dedicated servers.

### BelowNormal thread priority

The inference thread runs at `ThreadPriority.BelowNormal` so it yields CPU time to the game's simulation tick. Even when the model is mid-generation, the server's physics, atmosphere, and networking get priority. The trade-off is slightly slower inference (seconds, not noticeable given the "satellite delay" framing).

### Queue-based request processing

One request at a time, FIFO. If three players message the bot simultaneously, requests queue and process sequentially. A concurrent approach would require multiple model contexts (multiplied RAM) for marginal benefit. Sequential processing means the second and third players wait longer, which fits the "shared satellite bandwidth" lore.

## Consciousness / stun system internals

Consciousness in Stationeers is not a standalone stat. It is driven entirely by the **stun damage channel** on the **Brain organ's DamageState**. There is no separate "consciousness" float anywhere.

### Entity state machine

`EntityState` enum (`Assets.Scripts.Objects.Entities`):
- `Alive` (0)
- `Dead` (1)
- `Unconscious` (2)
- `Decay` (3)

State transitions live in `Human.OnLifeTick()`:

- **Alive -> Unconscious**: when `OrganBrain.DamageState.Stun >= 100`, or `>= 90` if inside a powered `ILifeSuspender`
- **Unconscious -> Alive**: when `OrganBrain.DamageState.Stun < 50`
- Hysteresis band 50-99: player stays in whichever state they were already in

### Where stun actually lives

The indirection chain:

1. `Entity.DamageState` is an `EntityDamageState`
2. `EntityDamageState.Stun` is a **proxy** that reads from `ParentEntity.OrganBrain.DamageState.Stun`
3. `EntityDamageState.Damage()` with `DamageUpdateType.Stun` **forwards** to `ParentEntity.OrganBrain.DamageState.Damage()`
4. `Brain.DamageState` is an `OrganicDamageState` containing `_stunDamage` (`ThingDamageValue`)
5. `ThingDamageValue` stores a clamped float `Value` in range [0, MaxDamage]. MaxDamage defaults to 200

The real stun value is at: `human.OrganBrain.DamageState.Stun`

Both paths work for writing:
- Direct: `human.OrganBrain.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun)`
- Via entity (auto-forwards): `human.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun)`

### ILifeSuspender and IsSleeping

`ILifeSuspender` interface (`Assets.Scripts.Objects.Electrical`): single property `bool IsSuspendingLife { get; }`

Implementors:
- `Sleeper`: `IsSuspendingLife => Powered;`
- `CryoTube`: `IsSuspendingLife => Powered;`
- `Bed`: `IsSuspendingLife => true;` (always active, no power needed)

`Entity.IsSleeping` is true when state is `Unconscious` AND the entity is inside a powered `ILifeSuspender`. This is the "good" unconscious (halved metabolic rates, no respawn prompt).

### How the sleeper causes unconsciousness

The sleeper itself does not set entity state. The mechanism:

1. `SpawnPointAtmospherics.OnAtmosphericTick()`: when powered and player is inside, increments stun by 10 per atmospheric tick (or snaps to 100 if close enough)
2. Once stun reaches 90, `Human.OnLifeTick()` transitions to `EntityState.Unconscious` (threshold is 90 inside an `ILifeSuspender`, not 100)
3. `Brain.OnLifeTick()`: when `IsSleeping` is true, stun does NOT naturally decay. The `!flag3` (where flag3 = IsSleeping) check prevents the decrement
4. Player stays asleep indefinitely until removed from the sleeper

### Waking up

`Human.OnExitInventory()`: exiting an `ILifeSuspender` forces `EntityState.Alive` immediately, bypassing the stun < 50 check.

Natural wake-up: stun decays at 3 per life tick when not sleeping. Once it drops below 50, `Human.OnLifeTick()` transitions to Alive.

### All sources of stun damage

| Source | Location | Mechanism | Amount |
|---|---|---|---|
| Sleeper / CryoTube | `SpawnPointAtmospherics.OnAtmosphericTick` | Increment per atmo tick when powered | +10/tick |
| Oxygen deprivation | `Brain.OnLifeTick` | When `Oxygenation <= 0` | +3/tick (scaled by offline metabolism) |
| Nitrous oxide (N2O) | `Entity.OnLifeTick` | `PartialPressureNitrousOxide / 5` (or `/16` with suit) | Variable, applied when > 1 |
| Robot no battery | `Brain.OnLifeTick` | Per life tick when robot battery empty | +3 (scaled) |
| Collision/impact | `Human.OnCollisionEnter` | Through suit `BruteDamagePassthroughAsStun` (default 4x multiplier) | Variable |
| Explosion | `Explosion.Process` | Direct stun increment | = explosion force |
| Stun pill | `StunPill.OnUseSecondary` | Direct set | +1000 (instant KO) |
| CryoTube revive | `CryoTube.ReviveOccupant` | Sets stun to MaxDamage on revive | Set to 200 |
| Entity death | `Human.OnEntityDeath` | Stun set to max | Set to MaxDamage |

### Stun recovery (natural decay)

`Brain.OnLifeTick()`, non-robot path: when stun > 0, state is Alive or Unconscious, and NOT sleeping, stun decrements by 3 per life tick. Halved by offline metabolism scaling for disconnected players.

When `IsSleeping` is true, decay is blocked entirely. The player stays at their current stun level until removed from the sleeper.

### Visual effects

`Entity.OnCameraUpdate()` applies post-processing based on stun level:
- Vignette intensity: 0 to 0.5 over stun 0-80
- Vignette blur: 0 to 1 over stun 0-50
- Color saturation: 1 to 0 over stun 0-100
- Brightness: 0.98 to 0 over stun 0-100

The screen progressively darkens and desaturates as stun increases. At 100, the screen is black.

### IsSleeping effects on metabolism

When `IsSleeping` is true:
- Brain damage rate halved (0.5x multiplier)
- Dehydration damage rate: 0.033 instead of 0.1
- Nutrition damage rate: 0.033 instead of 0.1
- Hydration loss halved
- Nutrition loss halved
- Stun does NOT decay (keeps player asleep)
- No respawn prompt shown

When inside a powered `ILifeSuspender` (separate check from `IsSleeping`):
- Nutrition/dehydration/mood/hygiene ticks skipped entirely

### Modding approaches

**Knock a player unconscious without a sleeper:**
```csharp
// All public API, no reflection needed
human.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun);
// OnLifeTick transitions to Unconscious next tick
```

**Wake a player up:**
```csharp
human.OrganBrain.DamageState.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Stun);
// OnLifeTick transitions to Alive next tick (0 < 50)
```

**Gradual consciousness loss (like the sleeper):**
```csharp
human.DamageState.Damage(ChangeDamageType.Increment, 10f, DamageUpdateType.Stun);
// Repeat each tick. Player sees screen darken progressively.
// At 100 they go unconscious.
```

**Keep a player unconscious without a sleeper (prevent stun decay):**
Harmony postfix on `Brain.OnLifeTick()` to re-set stun after the natural decrement, or Harmony prefix to skip the decrement block entirely.

**Simulate IsSleeping benefits without a sleeper:**
Harmony postfix on `Entity.IsSleeping` getter to return true under custom conditions. This halves metabolic rates and blocks stun recovery, but does NOT skip the nutrition/dehydration ticks (that check is `RootParent is ILifeSuspender`, separate from `IsSleeping`).

**Set state directly (fragile, not recommended):**
```csharp
human.State = EntityState.Unconscious;
// Works but OnLifeTick immediately reverts to Alive if stun < 50
// Must also maintain stun >= 50 to keep the state
```

### Key member visibility

| Member | Declared in | Visibility |
|---|---|---|
| `Entity.State` | Entity | public get/set |
| `Entity.OrganBrain` | Entity | public field |
| `Entity.IsSleeping` | Entity | public property |
| `Human.OnLifeTick()` | Human | public override |
| `Brain.OnLifeTick()` | Brain | public override |
| `EntityDamageState.Damage()` | EntityDamageState | public override |
| `OnServer.SetEntityState()` | OnServer | public static |

Everything needed is public. No reflection required for basic stun manipulation.

## Damage and repair system

### Damage storage

Each `Thing` has a `DamageState` with up to 9 damage channels stored as `ThingDamageValue` fields (float clamped to [0, MaxDamage], default MaxDamage = 200):

| Channel | Flag | Used by |
|---|---|---|
| Burn | 0x02 | All things (ThingDamageState) |
| Brute | 0x04 | All things (ThingDamageState) |
| Oxygen | 0x08 | Items, organs (OrganicDamageState) |
| Hydration | 0x10 | Items, organs |
| Radiation | 0x20 | Items, organs |
| Starvation | 0x40 | Entities (EntityDamageState) |
| Toxic | 0x80 | Items, organs |
| Stun | 0x100 | Entities (brain) |
| Decay | 0x200 | Items, organs |

`DamageState.Total` = sum of all channels except Stun, clamped to [0, MaxDamage]. When Total >= MaxDamage, the thing is destroyed (`IsBroken = true`).

### DamageState class hierarchy

```
IndestructableDamageState          (base: all 9 channels, Damage() gated by RunSimulation)
  ThingDamageState                 (destructible: Heal/HealAll for Burn+Brute, destroy logic)
    OrganicDamageState             (adds Oxygen/Toxic/Radiation/Hydration/Stun/Decay)
      EntityDamageState            (adds Starvation, routes Stun/Oxygen to Brain organ)
```

Runtime types:
- `Thing` base: creates `ThingDamageState` (Burn + Brute only)
- `Item` override: creates `OrganicDamageState` (all channels)
- `Entity`/`Human`: creates `EntityDamageState`

### HealAll method

`ThingDamageState.HealAll(float minDamageRemaining = 0f)`: sets Burn and Brute to 0, clears `_isDestroyed` flag. `OrganicDamageState.HealAll` adds: Oxygen, Toxic, Radiation, Hydration, Stun, Decay all set to 0. `EntityDamageState.HealAll` adds Starvation.

Calls `Damage(ChangeDamageType.Set, ...)` internally, so it goes through `GameManager.RunSimulation` gate. Server/host only.

### In-game repair

Duct tape: calls `thing.DamageState.HealAll()` when damage ratio is small enough, or `Heal(amount)` for partial repair. Welding torch on structures: triggers `DamageState.HealAll()` through the construction system. Both use the same `HealAll()` method.

### Enumerating all things

```csharp
using PooledSpan<Thing> span = OcclusionManager.AllThings.AsPooledSpan();
foreach (Thing thing in span.Collection)
{
    if (thing == null) continue;
    // ...
}
```

`PooledSpan` rents from `ArrayPool`, so the collection may contain trailing nulls. Always null-check.

### Repair all damaged items from a mod

```csharp
using PooledSpan<Thing> span = OcclusionManager.AllThings.AsPooledSpan();
foreach (Thing thing in span.Collection)
{
    if (thing == null) continue;
    if (thing.DamageState.Indestructable) continue;
    if (thing.IsBroken) continue;              // skip destroyed
    if (thing.DamageState.Total <= 0f) continue; // skip undamaged
    thing.DamageState.HealAll();
}
```

### Caveats

- Server-side only. `HealAll()` internally calls `Damage()` which checks `GameManager.RunSimulation`.
- Destroyed items (`IsBroken`): Total >= MaxDamage. Healing resets the numbers but doesn't rebuild structures in broken mesh state. Skip them.
- Decay extra flag: `Item.IsDecayed` is a separate bool. `HealAll()` zeros decay damage but does NOT reset `IsDecayed`. Set `((Item)thing).IsDecayed = false` separately to un-decay items.
- Structures with broken meshes: `CurrentBuildStateIndex < 0`. Need rebuilding, not healing. `Structure.IsBroken` catches these.

## LanderCapsule (respawn drop pod)

### Overview

The `LanderCapsule` (`Assets.Scripts.Objects`, inherits `DynamicThing`) is the drop pod that delivers players from orbit. It is completely independent of the death/respawn system. The respawn flow uses it through XML spawn data config, but the capsule itself has no awareness of whether the player is alive, dead, or respawning.

### Key constants

```
KinematicStartHeight = 100m     (teleport distance above ground)
DURATION = 10s                  (total descent time)
ENGINE_START_TIME = 3s          (engines fire at this point)
DESCENT_SHAKE = 0.03            (camera shake during freefall)
ENGINES_SHAKE = 0.2             (camera shake when engines fire)
DOOR_EJECT_FORCE = 9f           (force on door when blown off)
```

### LanderMode enum

```
AtRest = 0      (on ground, idle)
Descending = 1  (in-flight descent)
Venting = 2     (door ejected, gas particles)
```

### Descent sequence

Triggered by setting `InteractableType.Mode` to 1 (`Descending`):

1. `BeginDescent()`: sets `isKinematic = true`, saves ground position, teleports capsule 100m straight up, locks the door (`InteractLock = 1`), starts `ControlledDescent()` coroutine
2. `ControlledDescent()`: lerps position from start (high) to target (ground) over 10 seconds using `EaseOutQuad(t) = 1 - (1-t)^2` (fast start, decelerating end). At t > 3s: enables Activate (fires engines, starts `EntryEffects`). Sets `ControlledDescentLerp` each frame (0 to 1) which drives thruster intensity. Camera shake increases from 0.03 to 0.2.
3. `TerminateDescent()`: disables kinematic (physics resume), disables entry effects, clears camera shake, sets mode to AtRest
4. `WaitThenOpen()`: waits 3 seconds, opens capsule (`InteractOpen = 1`), waits 500ms, unlocks (`InteractLock = 0`)
5. Door ejection: door slot occupant gets `MoveToWorld` with forward force of 9. Mode changes to Venting, gas particles emit for 3 seconds

### Player experience timeline

| Time | Event |
|---|---|
| 0s | Pulled into capsule seat, camera switches to interior view, teleported 100m up |
| 0-3s | Rapid freefall descent, gentle camera shake (0.03) |
| 3-10s | Engines fire, thruster effects visible, shake intensifies (0.2), descent decelerates |
| 10s | Ground contact, physics settle |
| 13s | Door blows off, gas venting particles |
| 13.5s | Lock released, player can exit |

### EntryEffects

`EntryEffects` component on the capsule controls re-entry fire/thruster visuals. `EnableEffects()` / `DisableEffects()` toggle linked GameObjects. `SetIntensity(lerpFactor)` scales thruster transforms using `EaseOutQuart(lerp) * 0.7` with random jitter.

### SpaceSuitRespawn effect

Visual materialization effect for suit appearance. `SpawnEffectTime = 5s` fade-in, `PauseTime = 1s`, `HideEffectTime = 3s` fade-out. Animates a `_cutoff` shader property. Only relevant for actual respawns (new suit created).

### Triggering without death/respawn

The capsule can be created and triggered on any living player. Nothing gets reset: stats, inventory, gear, timers all preserved. The player is just sitting in a pod that drops from 100m.

```csharp
// Create capsule at player's position
var pos = human.ThingTransform.position;
var rot = human.ThingTransform.rotation;
var capsule = OnServer.Create<LanderCapsule>(Prefab.Find<LanderCapsule>(), pos, rot);

// Move player into the seat (Slots[1])
OnServer.MoveToSlot(human, capsule.Slots[1]);

// Trigger descent (capsule teleports 100m up and drops back)
OnServer.Interact(capsule.InteractMode, 1);
```

Three calls. The capsule handles everything else.

### Using as time-skip cover

Players are locked inside the capsule for ~13.5 seconds (descent + door open + unlock). During this window:
- Players can look around (FreeLook = true) but cannot exit or interact
- World changes can be made: repair items, advance sun, drain hunger, spawn debris
- The 13-second window can be extended by patching `WaitThenOpen()` to delay longer than 3 seconds, or by combining with stun (knock them to ~80 stun inside the capsule for a groggy descent, wake-up takes additional seconds)

### Respawn flow (for reference, NOT needed for the drop effect)

The full respawn creates an entirely new `Human` via `Human.CreateCharacter()`. This resets all stats, creates new organs, empties inventory (old items go into a CardboardBox on the ground). The old body becomes a `DynamicBodyBag`. None of this happens when you just create a `LanderCapsule` and move a living player into it.

## Player visual effects

### CameraFilterPack library

The game ships 274 shader-based camera effects as MonoBehaviour components (CameraFilterPack third-party library). All follow the same pattern: `AddComponent<>()` to the main camera, set parameters, enable/disable.

Effects already active on `CameraController.Instance`:

| Field | Type | Purpose |
|---|---|---|
| `NightVisionFX` | `CameraFilterPack_NightVisionFX` | Night vision goggles |
| `WaterVisionFX` | `CameraFilterPack_Light_Water` | Underwater blur/tint |
| `LavaVisionFX` | `CameraFilterPack_NightVisionFX` | Under-lava vision |
| `SensorLensesVisionFX` | `CameraFilterPack_TV_80` | Sensor lenses scan lines |
| `SolarStormDistortionFX` | `CameraFilterPack_FX_Drunk` | Solar storm wobble |
| `SolarStormChromaticAberration` | `VignetteAndChromaticAberration` | Solar storm chromatic aberration |
| `CameraVignette` | `VignetteAndChromaticAberration` | Stun vignette + blur |
| `CameraColorControl` | `CameraFilterPack_Color_BrightContrastSaturation` | Brightness/saturation/contrast |

Adding a new effect at runtime:
```csharp
var cam = CameraController.Instance.MainCamera;
var fx = cam.gameObject.AddComponent<CameraFilterPack_Distortion_Dream>();
fx.Distortion = 5f;
// Remove: fx.enabled = false; or Object.Destroy(fx);
```

Note: `Entity.OnCameraUpdate` runs every frame and resets `CameraVignette` and `CameraColorControl` based on actual stun value. To override these, use a Harmony postfix on `Entity.OnCameraUpdate`.

### Best distortion effects for gameplay use

| Effect class | Parameters | Use case |
|---|---|---|
| `CameraFilterPack_FX_Drunk` | `Fade`, `Distortion`, `Speed`, `Wavy` | Gas exposure, disorientation |
| `CameraFilterPack_Distortion_Dream` | `Distortion` (1-10) | Dream sequence, vision |
| `CameraFilterPack_FX_EarthQuake` | `Speed`, `X`, `Y` | Seismic event, explosion aftermath |
| `CameraFilterPack_Blur_GaussianBlur` | `Size` (1-16) | Unconsciousness without stun, transition |
| `CameraFilterPack_Noise_TV` | `Fade` (0-1) | Signal interference, EMP |
| `CameraFilterPack_TV_VHS` | `Cryptage`, `Parasite` | Corrupted feed |
| `CameraFilterPack_FX_Glitch1` | `Glitch` (0-1) | Digital glitch, power surge |
| `CameraFilterPack_TV_BrokenGlass` | `Broken_Small/Medium/High/Big` | Visor crack, decompression |
| `CameraFilterPack_Vision_Tunnel` | `Value`, `Value2`, `Intensity` | Tunnel vision, focus |
| `CameraFilterPack_Distortion_Heat` | `Distortion` (1-100) | Heat shimmer |
| `CameraFilterPack_Color_GrayScale` | `_Fade` (0-1) | Desaturation, fading |
| `CameraFilterPack_AAA_Blood_Hit` | `Hit_Full`, `Hit_Left/Right/Up/Down` | Damage flash |

### Camera manipulation

- `CameraController.SetCameraShake(float intensity)`: public static. Intensity 0-2.5, decays over time. Used by explosions.
- `CameraController.Instance.SetThirdPersonCamera(bool)`: public. Forces third-person view.
- `CameraController.SetFieldOfView(float fov)`: public static. Extreme values (130+) create fisheye distortion.
- `CameraController.Instance.RotationX / RotationY`: public fields. Can spin the camera.

### Helmet frost

`FirstPersonHelmetOverlay.CurrentFrostSetting`: public static float. Set to -1 for full frost overlay, 0 for clear. Driven by atmosphere temperature in `InventoryManager` (maps 0C-20C range).

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

## Time-skip world manipulation systems

### Systems that can be modified to fake elapsed time

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

## Rejected direction: text-to-speech

Client-side TTS was investigated and **explicitly rejected**. Documented here so future contributors do not re-walk the same dead ends.

### The goal

Have chat messages from the bot spoken aloud through client speakers, ideally through a radio/satellite filter to match the SATCOM theme.

### Why it was rejected

Every viable path has a disqualifying problem. Combined, they make TTS too fragile to ship as a dependable feature.

**`System.Speech.Synthesis` does not work under Unity Mono.** Unity runs on Mono, not the full .NET Framework CLR. `System.Speech` is implemented via COM interop (`CoCreateInstance` for `SpVoice`). Mono's COM interop does not support this. The failure mode is a `TypeLoadException` at assembly load, before any code runs. This is documented across multiple BepInEx and Unity projects (MissionPlanner, ARKStatsExtractor). Even referencing the type from a code path that never executes causes the mod to fail to load.

**SAPI voices available through `GetInstalledVoices()` are limited.** On a clean Windows 10 or 11 install, SAPI exposes only Microsoft David (en-US male) and Microsoft Zira (en-US female). Windows 11 ships higher-quality "natural voices" (Aria, Jenny, Guy) but these are locked to the Narrator app, registered under `Speech_OneCore` registry key, invisible to SAPI. Installing language packs adds OneCore voices, not SAPI voices. Multilingual support through SAPI is not practical.

**SAPI does not handle language mismatch gracefully.** Given French text and an English voice, SAPI applies English phoneme rules to French orthography and spells out unknown words letter-by-letter. The output is garbled nonsense, not accented speech. This forces either a system prompt that constrains the LLM to English, or a language detection step with fallback logic for every response.

**Windows N/KN editions (EU/Korea) are uncertain.** SAPI is not explicitly listed among removed Media Feature Pack components, but Cortana speech features are documented as not working on N editions. Testing would be required on every edition variant to be sure.

**The native C++ DLL bridge workaround adds maintenance burden.** The proven pattern (Weisshaar blog, UnityWindowsTTS, UnityAccessibilityLib) is to write a small C++ DLL that calls SAPI via native COM, expose C functions via P/Invoke. Works reliably. But it means:
- A second build toolchain (MSVC + C++ project) alongside the .NET Framework mod
- Shipping an x64 native DLL per platform
- Debugging across the managed/native boundary when something breaks
- No Unity audio pipeline integration (audio goes directly to OS device)

**Neural TTS engines all have blockers.**
- **Piper**: high-quality voices, 100+ languages, but uses espeak-ng for phonemization. espeak-ng is GPL 3.0, which is incompatible with the mod's Apache 2.0 license. Distributing espeak-ng would force the mod to GPL. The license is one-way compatible (Apache into GPL, not the reverse).
- **Kokoro via KokoroSharp**: state-of-the-art voices, pure C# NuGet, but depends on ONNX Runtime. ONNX Runtime >= 1.15.0 has documented crash issues with Unity Mono (`System.Buffer.InternalMemcpy` failures, issue #18441 on microsoft/onnxruntime). Adds ~320 MB model and a new native dependency stack that conflicts with how LLamaSharp is currently wired up. Also uses espeak-ng for phonemization (same GPL issue).
- **Sherpa-ONNX**: comprehensive but heavyweight, requires Unity 2022.3+, same ONNX/Mono risk.

**Cloud TTS APIs (OpenAI, Azure, ElevenLabs)** would add:
- Network dependency (defeats "no downloads at runtime")
- API key management per player
- Per-token costs
- Privacy implications (sending chat text to a third-party)
- Latency (network round-trip per response)

### Combined risk assessment

Every viable path requires one of:
- A native C++ DLL we build and maintain ourselves
- A GPL license that changes the mod's licensing
- An ONNX Runtime stack with known Mono crashes
- A cloud service with API keys and network dependency
- Acceptance that non-English speakers get garbled output

None of these match the mod's design goals: zero runtime downloads, Apache 2.0 licensing, universal compatibility, self-contained distribution.

### Decision

**Ship text-only.** The bot's responses appear as chat messages, identical to player chat. Players read them. The SATCOM "satellite relay" framing already fits a text-only medium (signal comes through as text on the station console).

If a contributor later wants to add TTS as an optional client-side add-on, the path of least resistance is the native C++ DLL bridge to SAPI (option 1 from the research). The radio filter effect (low-pass at 3kHz + reverb) through Unity's audio filters would make David/Zira sound appropriately robotic. But it is a separate mod, not part of this one.
