# PowerTransmitter Plus: Research Reference

PowerTransmitter Plus extends the vanilla microwave power pair (`PowerTransmitter` / `PowerReceiver` / `WirelessPower`) with a configurable distance-cost model, a visible power beam with pulse-train visualiser, on-the-fly logic readouts (source draw, destination draw, loss, efficiency, auto-aim target, linked partner), auto-aim (write a `ReferenceId` to aim the dish), IC10 named constants, and server-authoritative multiplayer sync for both gameplay and visuals. The mod is BepInEx plus StationeersLaunchPad, server-authoritative for simulation with client-side visuals. First-time readers: architecture and threading in `## Architecture`; the four interlocking distance-cost patches plus logic readout, auto-aim, and logic-system bootstrap patches in `## Harmony patches catalog`; pitfalls in `## Pitfalls / dead ends`; decompiled game internals (`PowerTransmitter` class hierarchy, `WirelessPower` base, LogicType registries, `TryContactReceiver` raycast, dish transform hierarchy, IC10 syntax-highlighting pipeline, LaunchPadBooster networking) live on the central pages pointed to from `## Relevant central pages`.

## Architecture

Mod identity:

| Field | Value |
|---|---|
| Display name | Power Transmitter Plus |
| Code name | PowerTransmitterPlus |
| Plugin GUID | `net.powertransmitterplus` |
| Author | SixFive7 |
| Custom LogicType reserved band | `6571 - 6599` |
| Safely outside of | vanilla (0-349) and Stationeers Logic Extended (1000-1830) |
| Target framework | .NET Framework 4.7.2, classic-style csproj |
| Hard dependency | `stationeers.launchpad` (StationeersLaunchPad) |

Feature pillars (server-authoritative BepInEx mod enhancing the Microwave Power Transmitter / Receiver pair):

1. A visible colored beam between any aligned, linked, powered transmitter / receiver pair.
2. A texture-scroll pulse train along the beam whose speed scales with delivered power (`sqrt(intensity) x configured m/s`).
3. Replacement of the vanilla distance-based capacity derate with a source-draw overhead: per watt delivered, the source pulls `1 + k x distance_km` watts (server-authoritative `k`, live-broadcast on change).
4. Six new LogicTypes on both transmitter and receiver: `MicrowaveSourceDraw` (6571), `MicrowaveDestinationDraw` (6572), `MicrowaveTransmissionLoss` (6573), `MicrowaveEfficiency` (6574), `MicrowaveAutoAimTarget` (6575, writable), `MicrowaveLinkedPartner` (6576, read-only). Readable from configuration tablet and from IC10 by name. Auto-aim writes a target Thing's `ReferenceId` and slews the dish via the vanilla servo; `TryContactReceiver` handles link establishment. `MicrowaveLinkedPartner` returns the `ReferenceId` of the currently linked partner dish (0 when unlinked).
5. Server-authoritative visual sync: in multiplayer, the host's beam visual settings (width, color, emission intensity, stripe wavelength, scroll speed) are always broadcast to all clients via `BeamVisualConfigMessage`, overriding client-local config.

The mod preserves vanilla gameplay rules everywhere possible: the `TryContactReceiver` raycast still decides when pairs link (so "obstacle in the path" behavior is intact), the dish slew servo still animates rotations, `LinkedReceiver` / `LinkedPowerTransmitter` are never written directly.

### Plugin wiring

`PowerTransmitterPlusPlugin : BaseUnityPlugin`. Hard-depends on `stationeers.launchpad`. GUID `net.powertransmitterplus`.

`Awake()`:
1. Capture `Logger` into `Log`.
2. Bind config entries (see Configuration subsection).
3. Init `MainThreadDispatcher`.
4. Hook `DistanceConfigSync.HookHostBroadcast()` (wires `SettingChanged` to broadcast).
5. Subscribe `Prefab.OnPrefabsLoaded += OnAllModsLoaded`.

`OnAllModsLoaded()` (deferred until StationeersLaunchPad finishes loading all mods):
1. Set `MOD.Networking.Required = true`.
2. Register `DistanceConfigMessage` with `MOD.Networking.RegisterMessage<T>()`.
3. `new Harmony(PluginGuid).PatchAll()`.
4. Call `Ic10ConstantsPatcher.Apply()` (must run AFTER `PatchAll` because it reflects into the game's static array).

Non-Harmony reflection injection:

- `Ic10ConstantsPatcher.Apply()`: called from `Plugin.OnAllModsLoaded` after `PatchAll`. Reflects into `ProgrammableChip.AllConstants` (a `public static ProgrammableChip.Constant[]`) and assigns a new merged array. Then calls `ExtendSyntaxHighlighting()` which finds `ScriptEnum<LogicType>` (index 0) and `BasicEnum<LogicType>` (index 4) in `ProgrammableChip.InternalEnums` and extends their private `_types`/`_names` arrays so custom LogicType names get colored on in-game screens. Idempotent.
- `AutoAimState.ParentRotatableField`: one-shot `AccessTools.Field(typeof(RotatableBehaviour), "_parentRotatable")` used by the reset postfixes to map a `RotatableBehaviour` back to its owning `WirelessPower`.

### Threading model (CRITICAL)

`PowerTick.ApplyState()` runs on a UniTask **ThreadPool worker** thread. Methods called from there (`UsePower`, `GetUsedPower`, `ReceivePower`, `GetGeneratedPower`, `VisualizerIntensity` setter) all execute on a background thread. **Any Unity API call from those threads, `new GameObject`, `Shader.Find`, `Transform.position`, `LineRenderer.SetPosition`, `Material.SetXxx`, hard-crashes the native Unity player.**

`MainThreadDispatcher` is a `MonoBehaviour` on a `DontDestroyOnLoad` GameObject. It maintains a `ConcurrentQueue<Action>` drained in `Update()`. Every Harmony postfix that touches Unity API enqueues onto this dispatcher. Closure runs on main thread one frame later. ~1 frame latency, fully safe.

Field reads/writes (managed memory, no Unity P/Invoke) ARE safe from background threads. That is why the `_powerProvided` reflection in `DistanceCostPatches` works without the dispatcher.

See central `Patterns/MainThreadDispatcher.md` for the dispatcher pattern and `GameSystems/PowerTickThreading.md` for the game-side threading fact (including the representative crash stack).

### Server / client roles

Stationeers is **server-authoritative for simulation**. Only the server runs the power tick; clients receive synced state. The `DistanceCostPatches` (4 patches) only meaningfully execute on the server. Clients run the patches but they are no-ops because power-tick code does not run on clients.

Detection: `Assets.Scripts.Networking.NetworkManager.IsServer` (true on host or single-player) and `NetworkManager.IsActive` (true in multiplayer either side). Single-player has `IsActive = false`. Guards that check `!IsServer` must use `IsActive && !IsServer` to avoid the `NetworkRole.None` trap (central `Patterns/SinglePlayerNetworkRole.md`).

Client-side display values for the readouts are computed from already-synced game state (`OutputNetwork.CurrentLoad`, `_linkedReceiverDistance`) plus the host's `k` value. The `k` value is pushed via `DistanceConfigMessage` on `PlayerConnected` and on every `SettingChanged` event.

Auto-aim rides entirely on pre-existing infrastructure: `SetLogicValue` is server-authoritative; `TargetHorizontal` / `TargetVertical` writes set `NetworkUpdateFlags |= 256` which the existing delta-state serialization ships to clients.

### Patch family overview

| Family | Files | Purpose |
|---|---|---|
| Visual on/off | `VisualiserPatches.cs`, `RotationPatches.cs` | Show/hide beam tied to game's `VisualizerIntensity` setter; refresh endpoints when dish rotates |
| Power-flow simulation | `DistanceCostPatches.cs` | 4 patches replacing vanilla distance derate with source-draw multiplier |
| Logic readout (UI/IC10) | `LogicReadoutPatches.cs` | `CanLogicRead` postfix + `GetLogicValue` prefix on `WirelessPower` (base class); branches on instance type inside |
| Auto-aim logic write | `AutoAimPatches.cs` | `SetLogicValue` prefix intercepts `MicrowaveAutoAimTarget`; `RotatableBehaviour` target setter postfixes clear the cache on manual override. Per-dish cache via `ConditionalWeakTable` |
| Logic system bootstrap | `Ic10ConstantsPatcher.cs`, `LogicableInitializePatch.cs`, `EnumNamePatches.cs`, `StationpediaPatches.cs` | Teach the game about our `LogicType` values 6571-6576 everywhere the game looks them up by name: compiler constants, tablet arrays, enum name resolution, screen syntax highlighting, Stationpedia |
| Multiplayer sync (k) | `DistanceConfigMessage.cs`, `DistanceConfigSync.cs` | Server-authoritative `k` push to clients via LaunchPadBooster networking |
| Multiplayer sync (visuals) | `BeamVisualConfigMessage.cs`, `BeamVisualConfigSync.cs` | Server-authoritative beam visual config push to clients via LaunchPadBooster networking |
| Foundation | `Plugin.cs`, `MainThreadDispatcher.cs`, `LogicTypeRegistry.cs`, `BeamManager.cs`, `BeamLine.cs`, `BeamPulseTrain.cs` | Wiring, registry, beam GameObjects |

### File walkthrough

`Plugin.cs` - See Plugin wiring above.

`MainThreadDispatcher.cs` - Singleton `MonoBehaviour` on a `DontDestroyOnLoad` GameObject. `Init()` is idempotent. `Enqueue(Action)` is thread-safe via `ConcurrentQueue<Action>`. `Update()` drains the queue and catches per-action exceptions.

`BeamManager.cs` - Static class. Holds `Dictionary<PowerTransmitter, BeamLine> Beams`, a `SharedMaterial` (lazy-created with shader-fallback chain), `BeamColor` (parsed from config hex x emission intensity), and `StripeTexture` (lazy-created 32x1 cosine grayscale, repeat-wrapped, used by the pulse train). Public surface (all enqueue to dispatcher): `SetLineIntensity(transmitter, intensity)` and `RefreshIfVisible(transmitter)`.

`BeamLine.cs` - Per-transmitter wrapper. Owns a child `GameObject` parented to `transmitter.transform`, a `LineRenderer` with `useWorldSpace = true, positionCount = 2`, and a `BeamPulseTrain` MonoBehaviour. Beam alpha is permanently 1 when visible: the pulse train is the only power-level indicator.

`BeamPulseTrain.cs` - MonoBehaviour on the BeamLine GameObject. In `Awake`: sets `textureMode = Stretch`, clones material via `_lr.material`, sets `mainTexture = BeamManager.StripeTexture`. In `Update`: reads positions, computes `tiles = distance / wavelength` and `offset = -Time.time * sqrt(intensity) * scrollMps / wavelength`, writes `mainTextureScale` and `mainTextureOffset`. `OnDestroy` destroys the cloned material.

`VisualiserPatches.cs` - Single Harmony patch on `WirelessPower.VisualizerIntensity` setter. Postfix: if `__instance is PowerTransmitter`, call `BeamManager.SetLineIntensity(...)`. This fires from the ThreadPool worker; safe because `SetLineIntensity` enqueues to the dispatcher.

`RotationPatches.cs` - Harmony postfixes on `WirelessPower.Horizontal` and `Vertical` setters. If dish is a `PowerTransmitter` and currently visible, call `BeamManager.RefreshIfVisible` to re-cache beam endpoints.

`DistanceCostPatches.cs` - Four power-tick patches implementing the source-draw overhead model. See Harmony patches catalog for math. `DistanceCostShared` (static helper): `PowerProvidedField` and `LinkedDistanceField` reflections, `GetWirelessOutputNetwork(t)` via `Traverse` (field-or-property tolerant), `GetMultiplier(t)` reads distance via reflection and multiplies by `DistanceConfigSync.GetEffectiveK()`.

`LogicTypeRegistry.cs` - Constants for all custom LogicType values (6571-6576). `List<CustomLogicType> All` with name / value / description per entry. `Dictionary<ushort, CustomLogicType> ByValue` index. `IsCustom(LogicType)` and `TryGetName(LogicType, out string)` helpers.

`DistanceConfigMessage.cs` - `INetworkMessage` from LaunchPadBooster. Single field `float K`. `Process(long hostId)`: if NOT server, calls `DistanceConfigSync.OnHostConfigReceived(K)` (guard against self-echo).

`DistanceConfigSync.cs` - Static class. Holds `_syncedHostK : float?` (null until first message arrives). `GetEffectiveK()` returns local config on host or single-player, synced value (or local fallback) on client. `OnHostConfigReceived(float k)` stores value and logs change. `HookHostBroadcast()` subscribes to `DistanceCostFactor.SettingChanged` to call `BroadcastIfHost()`. `BroadcastIfHost()` if `IsServer`, builds and `SendAll(0L)`s a `DistanceConfigMessage`. `[HarmonyPatch(typeof(NetworkManager), "PlayerConnected")]` postfix calls `BroadcastIfHost()` on every join.

`BeamVisualConfigMessage.cs` - `INetworkMessage` carrying five fields: `BeamWidth`, `BeamColorHex`, `EmissionIntensity`, `StripeWavelength`, `ScrollSpeed`. Serialized via `RocketBinaryWriter/Reader`. `Process()` ignores on server; on client calls `BeamVisualConfigSync.OnHostConfigReceived()`.

`BeamVisualConfigSync.cs` - Static sync manager mirroring the `DistanceConfigSync` pattern. Stores host-pushed visual values and a `_received` flag (set on first message). `UseHostValues`: true when `_received` AND `NetworkManager.IsActive` AND NOT `IsServer`. `GetEffective*()` methods (BeamWidth, BeamColorHex, EmissionIntensity, StripeWavelength, ScrollSpeed) return synced values when on a client with host values received, local config otherwise. Called by `BeamManager`, `BeamLine`, and `BeamPulseTrain` instead of reading `Plugin` config directly. `OnHostConfigReceived()` stores values, logs, then calls `BeamManager.InvalidateAllBeams()` to force beam recreation with updated visuals. `HookHostBroadcast()` wires `SettingChanged` on all five visual config entries to call `BroadcastIfHost()`. `BroadcastIfHost()` if `IsServer`, builds and `SendAll(0L)`s a `BeamVisualConfigMessage` with current visual values.

`Ic10ConstantsPatcher.cs` - Static `Apply()`. Two jobs: (1) one-time reflection injection into `ProgrammableChip.AllConstants` (a `public static ProgrammableChip.Constant[]`). Merges our entries into the array. The MIPS compiler resolves any name token against this array via `OrdinalIgnoreCase` string comparison. (2) `ExtendSyntaxHighlighting()`: extends `ProgrammableChip.InternalEnums` entries for `ScriptEnum<LogicType>` (bare names like `MicrowaveSourceDraw`, colored orange) and `BasicEnum<LogicType>` (dotted names like `LogicType.MicrowaveSourceDraw`, colored teal). Both classes snapshot `Enum.GetValues`/`GetNames` at construction into private readonly `_types`/`_names` arrays. Our custom values are not in the real enum, so without this extension they receive no `<color>` tag and inherit the screen's default red text color. Idempotent guard: `_applied` bool.

`LogicableInitializePatch.cs` - Postfix on `Logicable.Initialize`. Three steps: (1) appends to `Logicable.LogicTypes` (`LogicType[]`) and `Logicable.LogicTypeNames` (`string[]`) via reflection; rebuilds `LogicTypeNamesRedirects` if present; (2) calls `ExtendEnumCollection(...)` which reflects into `EnumCollections.LogicTypes` and extends `Values`, `ValuesAsInts`, `Names`, `PaddedNames`, and the `<Length>k__BackingField`; (3) calls `ExtendScreenDropdownBase(...)` which reflects into `ScreenDropdownBase.LogicTypes` and `LogicTypeNames` to add custom entries to the motherboard condition/action dropdown UI. Steps 1 and 2 are required: step 1 drives `Logicable.NextLogicType` cycling; step 2 drives the configuration tablet cartridge UI. Step 3 extends the motherboard screen dropdowns. Idempotent guard: `_injected` bool.

`EnumNamePatches.cs` - Three Harmony postfixes, all gated on `__result != null`: (1) `Enum.GetName(Type, object)` for direct `Enum.GetName(typeof(LogicType), value)` calls; (2) `EnumCollection<LogicType, ushort>.GetName`; (3) `EnumCollection<LogicType, ushort>.GetNameFromValue`. For unknown LogicType values, looks up our registry and substitutes the name. Without these, the UI displays raw integers like "6571" instead of "MicrowaveSourceDraw".

`LogicReadoutPatches.cs` - `LogicReadoutCompute` static helper:
- `delivered = transmitter.OutputNetwork.CurrentLoad`
- `multiplier = 1 + k x distance / 1000` where `k = DistanceConfigSync.GetEffectiveK()`
- `sourceDraw = delivered x multiplier`
- `loss = delivered x (multiplier - 1)`
- `efficiency = delivered > 0 ? 1 / multiplier : 0`
- All return 0 if `transmitter == null`, `!OnOff`, `Error == 1`, `LinkedReceiver == null`, or `OutputNetwork == null`.

Two patches on the `WirelessPower` base class: `WirelessPowerCanLogicReadPatch` (Postfix) returns `true` for custom LogicTypes on `PowerTransmitter` / `PowerReceiver` instances; `WirelessPowerGetLogicValuePatch` (Prefix, returns false) reads `MicrowaveAutoAimTarget` from the per-dish cache and `MicrowaveLinkedPartner` directly from the instance's linked partner field (both are per-dish, not forwarded through the link); for the power readouts, `PowerTransmitter` reads `__instance`, `PowerReceiver` resolves through `LinkedPowerTransmitter`.

The base-class targeting is required because `CanLogicRead` / `GetLogicValue` are declared `override` on `WirelessPower` and the subclasses inherit without re-overriding; Harmony's attribute-based lookup uses `AccessTools.DeclaredMethod` which does not match inherited methods. See `Patterns/HarmonyInheritedMethods.md`.

No caching, no per-tick state. Snap-to-zero is automatic when delivered is 0.

`AutoAimPatches.cs` - Implements `MicrowaveAutoAimTarget` (LogicType 6575, writable). Writing a Thing's `ReferenceId` aims the dish at it; writing 0 disables. The base-game `TryContactReceiver` raycast decides link establishment.

`AutoAimState` (static):
- `ConditionalWeakTable<WirelessPower, StrongBox<long>> _target`: per-dish cache. Lifetime-tied to the dish instance, self-cleans on GC.
- `[ThreadStatic] bool _suppressReset`: re-entry flag set during our own servo writes.
- `HandleWrite(dish, newId)`:
  - Cache hit -> early return.
  - `newId == 0` -> cache 0, no aim change.
  - `Thing.Find(newId) == null || target == dish` -> return WITHOUT updating cache (so a later rewrite of the same id re-attempts lookup).
  - Otherwise: pivot-to-pivot geometry -> set `RotatableBehaviour.TargetHorizontal` / `TargetVertical` under suppression flag.

Geometry, both endpoints are rotation-invariant:
- `from = dish.transform.position` (the placed-structure root; NOT `RayTransform`, which moves with H/V)
- `to = target.transform.position` (NOT `RayTransform` / `DishTarget`: those are `Head` children and swing with the target's current aim)
- `d_local = dish.transform.InverseTransformDirection((to - from).normalized)`
- `V = 0.5 + asin(d_local.y) / pi` in `[0, 1]`
- `H = (atan2(d_local.x, d_local.z) / (2 pi) + 1) mod 1`
- At the poles (`|d_local.y| ~ 1`), `H` is undefined. Keep the current value.

Reading `MicrowaveAutoAimTarget` is handled in `LogicReadoutPatches.cs` (per-dish lookup on `__instance`).

`StationpediaPatches.cs` - Best-effort Stationpedia integration via `AccessTools.TypeByName` and `TargetMethod()`+`Prepare()`. Adds in-game wiki entries for each custom LogicType. Failure is non-fatal.

### Configuration

All in `BepInEx/config/net.powertransmitterplus.cfg`.

| Section | Key | Default | Description |
|---|---|---|---|
| Visual | `Beam Width` | `0.1` | Thickness in world units. Matches vanilla's prefab `widthMultiplier` |
| Visual | `Beam Color` | `000DFF` | Hex RGB. Normalized cyan-blue from game's runtime emission |
| Visual | `Emission Intensity` | `10.0` | HDR brightness multiplier. Matches game's HDR intensity |
| Pulse | `Stripe Wavelength` | `2.0` | Meters between pulse peaks. World-space, same on 5m and 200m beams |
| Pulse | `Scroll Speed` | `25.0` | m/s at full power (5 kW delivered). Scales with `sqrt(intensity)`; draws above 5 kW (enabled by the distance-cost patches) exceed this |
| Pulse | `Trough Brightness` | `0.5` | 0..1, beam brightness between pulses. Affects cached stripe texture (regenerates on game restart; see pitfall below) |
| Distance | `Cost Factor (k)` | `5.0` | **Server-authoritative.** Per-km overhead on source draw |

The beam shader is fixed to the fallback chain `Legacy Shaders/Particles/Additive` -> `Particles/Additive` -> `Sprites/Default` -> `Hidden/Internal-Colored` (see `BeamManager.SharedMaterial`). Not user-configurable: Stationeers ships a single Unity build, no alternative in that build looks meaningfully better than Additive, and a misconfigured value would either fall back silently or degrade the beam look.

`k` multiplier table. `m = 1 + k x dist_m / 1000`. For 200 W receiver demand:

| Distance | k=0.5 | k=1 | k=2 | k=4 | k=5 | k=10 |
|---:|---:|---:|---:|---:|---:|---:|
| 0 m | 200 W | 200 W | 200 W | 200 W | 200 W | 200 W |
| 100 m | 210 W | 220 W | 240 W | 280 W | 300 W | 400 W |
| 500 m | 250 W | 300 W | 400 W | 600 W | 700 W | 1.2 kW |
| 1 km | 300 W | 400 W | 600 W | 1.0 kW | 1.2 kW | 2.2 kW |
| 5 km | 700 W | 1.2 kW | 2.2 kW | 4.2 kW | 5.2 kW | 10.2 kW |
| 10 km | 1.2 kW | 2.2 kW | 4.2 kW | 8.2 kW | 10.2 kW | 20.2 kW |

For 1 kW receiver demand: scale by 5x. For 15 kW: scale by 75x. Default `k=5` gives 1 km = 6:1, 5 km = 26:1.

### Multiplayer sync flows

**Distance-cost k sync.** Host-authoritative. Host pushes `k` via `DistanceConfigMessage` on `PlayerConnected` postfix and on every config change. Clients store and use the host value for distance-cost math (which runs client-side for readouts).

```
Host:
  On DistanceCostFactor.SettingChanged     -> DistanceConfigSync.BroadcastIfHost()
  On NetworkManager.PlayerConnected (postfix) -> BroadcastIfHost()
  BroadcastIfHost(): if IsServer, new DistanceConfigMessage{K=k}.SendAll(0L)

Client:
  DistanceConfigMessage.Process(hostId):
    if !IsServer, DistanceConfigSync.OnHostConfigReceived(K)
  OnHostConfigReceived(k): _syncedHostK = k

Effective k decision:
  !NetworkManager.IsActive  -> local (single-player)
  IsServer                  -> local (host)
  else (client)             -> _syncedHostK ?? local
```

**Visual config sync.** Same pattern: `BeamVisualConfigMessage`, `BeamVisualConfigSync`. Host push on connect plus on change.

```
Host:
  On BeamWidth/BeamColorHex/EmissionIntensity/
     StripeWavelength/ScrollSpeed.SettingChanged -> BeamVisualConfigSync.BroadcastIfHost()
  On NetworkManager.PlayerConnected (postfix)    -> BroadcastIfHost()
  BroadcastIfHost(): if IsServer, new BeamVisualConfigMessage{...}.SendAll(0L)

Client:
  BeamVisualConfigMessage.Process(hostId):
    if !IsServer, BeamVisualConfigSync.OnHostConfigReceived(msg)
  OnHostConfigReceived(msg):
    store all values, set _received = true
    call BeamManager.InvalidateAllBeams() to force beam recreation

Effective value decision (per GetEffective* method):
  _received AND IsActive AND !IsServer -> synced value from host
  else                                 -> local config
```

**Auto-aim sync (no new infrastructure).** `WirelessPower.SetLogicValue` is server-authoritative in vanilla. Our prefix runs on the server. The ensuing writes to `RotatableBehaviour.TargetHorizontal` / `TargetVertical` set `NetworkUpdateFlags |= 256`, which the existing delta-state serialization ships to clients. `WirelessPower.ProcessUpdate` reads the flag and writes those targets on the client; the client's local servo then slews the dish. No new `INetworkMessage`.

**Why on-the-fly (not cached) computation for readouts.** Readouts compute directly from `OutputNetwork.CurrentLoad` and `_linkedReceiverDistance` in the `GetLogicValue` prefix. Both are already client-synced via cable network and wireless link state respectively. Clients have everything they need to display the same numbers as the server given matching `k`. No `PowerStatsTracker` dictionary, no per-tick stamping, no age-out. Snap-to-zero is automatic when delivered = 0.

See `Protocols/PowerTransmitterPlusNetworking.md` for the full message schema and flow, and `Protocols/LaunchPadBoosterNetworking.md` for the underlying networking primitives (`INetworkMessage`, `SendAll`, `SendToHost`, handshake, `Required` flag).

## Design decisions

### Applied

| Decision | Rationale |
|---|---|
| Beam color `000DFF` x intensity `10.0` | Extracted from game's runtime EmissionColor (cyan-blue HDR `(0, 0.4915, 10)`) |
| Beam width `0.1` | Matches game's prefab `widthMultiplier` exactly |
| Beam shader `Legacy Shaders/Particles/Additive` | Glowy laser look, no HDR-bloom dependency |
| Beam alpha permanently 1 when visible | Beam means "link is up" indicator; the pulse train carries throughput information. Intent, not workaround. |
| Pulse train via texture scroll, not gradient | Length-invariant; constant world-space wavelength on any beam length |
| Pulse intensity ramp `sqrt(intensity)` | Vanilla `VisualizerIntensity` rarely exceeds 0.3 in real bases; `sqrt` makes low values still visible |
| Scroll speed default `25.0 m/s` | At 5 kW (intensity = 1) this is clearly energetic; the distance-cost patches allow > 5 kW, which pushes speed higher organically |
| `k = 5` distance-cost default | Gives 1 km = 6:1, 5 km = 26:1; meaningful but not punishing |
| LogicType values `6571 - 6575` (reserved `6571 - 6599`) | Safely outside vanilla (0-349) and Stationeers Logic Extended (1000-1830) |
| `MicrowaveDestinationDraw` added (redundant with `PowerActual`) | Clearer naming on receiver side |
| `MicrowaveEfficiency` as a fourth readout | Ratio of delivered/source purely derivable from distance and `k`, but convenient to expose directly rather than requiring an IC10 division every tick |
| Pivot-to-pivot aim geometry, NOT `RayTransform` or `DishTarget` | `RayTransform` / `DishTarget` are `Head` children. Their world positions swing with dish rotation. Aiming from or at them produces self-referential error and locks aim onto the target's CURRENT pose. `dish.transform.position` -> `target.transform.position` makes both endpoints rotation-invariant, so a dish targets correctly even when the other side is still pointing the wrong way |
| Don't touch `LinkedReceiver` / `LinkedPowerTransmitter` from auto-aim | The vanilla `TryContactReceiver` raycast handles link/unlink based on alignment, including the "obstacle C in the path" case. Writing link fields directly bypasses the physics check |
| Auto-aim via servo setter writes under a `[ThreadStatic]` suppression flag | Re-uses existing servo delta-state for multiplayer sync; no new network message needed for aim |
| Four-patch power-tick quartet treated as an atomic set | Disabling any one produces observable breakage. See Pitfalls section below. |
| Multiplayer server broadcast (rather than client-side config) | Guarantees all clients see the same gameplay numbers as the host |
| `MOD.Networking.Required = true` | LaunchPad version handshake catches clients with missing or mismatched installs |
| Visual sync always active in multiplayer | Keeps all players on the same page visually. Simplest model: host is authoritative for visuals just like for gameplay (k). No toggle to explain, no split behavior |
| Visual sync invalidates all beams on receipt | Beam color and width are set in the `BeamLine` constructor and not updated thereafter. Destroying and letting `SetLineIntensityOnMain` recreate them is the simplest path to apply new visuals without adding per-frame config reads to the line renderer |
| Visual sync does not sync Trough Brightness | Baked into the cached `StripeTexture` created once at first beam. Invalidating that cache safely across all beam instances adds complexity for a setting players rarely change |
| `MicrowaveLinkedPartner` is per-dish (not forwarded through the link) | A transmitter returns its receiver's id and vice versa. Forwarding through the link would require picking one side, which is ambiguous on the receiver (it could in theory be linked to multiple transmitters, though vanilla only allows one) |
| On-the-fly readout computation, no cache | Readouts compute from `OutputNetwork.CurrentLoad` and `_linkedReceiverDistance` in the `GetLogicValue` prefix. Both are already client-synced. No `PowerStatsTracker` dictionary, no per-tick stamping, no age-out. Snap-to-zero is automatic when delivered = 0. |

### Reference patterns adopted from other mods

- **Stationeers Logic Extended (Workshop ID 3625190467, author ThunderDuck)** establishes the pattern for mod-authored custom LogicTypes, and this mod adopts it in full: registry of `LogicTypeInfo` entries hardcoded inline; reflection injection into `ProgrammableChip.AllConstants`; postfix on `Logicable.Initialize` to extend tablet UI arrays; postfix on `Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName / GetNameFromValue`; per-device `CanLogicRead` postfix plus `GetLogicValue` prefix; postfix on `Stationpedia.PopulateLogicVariables`. Stationeers Logic Extended has NO public extensibility API, so every mod that wants custom LogicTypes reimplements the registration pattern from scratch. `Animator.StringToHash(name)` is the value stored in `Constant.Hash`, used for the `#hash` MIPS directive; pure name lookups do not require it.
- **SprayPaintPlus (same author, earlier mod)** contributed: BepInPlugin skeleton (`[BepInDependency("stationeers.launchpad", HardDependency)]`, `BaseUnityPlugin`, `Prefab.OnPrefabsLoaded += OnAllModsLoaded`); Harmony attribute-based patching with `[UsedImplicitly]` on Postfix/Prefix methods; `INetworkMessage` Serialize/Deserialize/Process protocol; `MOD.Networking.Required = true; MOD.Networking.RegisterMessage<T>()`; `About.xml` with `ModID` matching `PluginGuid`, `InGameDescription` with TMP rich-text. SprayPaintPlus only does client-to-server messages; this mod is the first in the collection with a server-to-client broadcast (the `k` sync).

### Rejected or deferred

- **Custom shader for in-beam pulse animation matching the vanilla `Custom_PowerTransmission`**: the vanilla shader's name is stripped from the build, so `Shader.Find` cannot resolve it. Texture-scroll on the standard additive shader is the workaround and is sufficient.
- **User-configurable `Shader Name`**: exposed briefly in 1.1.x, removed in 1.1.2. The fallback chain is kept as defense-in-depth for future Unity upgrades (legacy shader packages can be stripped between versions) but the user-facing setting served no realistic alternative, since every subscriber runs the same Unity build and no shader outside the chain improves the look.
- **MIPS name registration for vanilla value `159` (the unused slot)**: using our own `6571+` band is cleaner.
- **`RotationPatches` on `WirelessOutputNetwork` field changes**: unnecessary since beam endpoints come from `RayTransform`, which is a Transform reference.
- **`PowerStatsTracker` dictionary for readouts**: rejected; redundant with already-synced fields.
- **Per-tick visualiser stamping**: rejected; `VisualizerIntensity` setter already fires from ThreadPool and is the single source of truth.
- **Client-side distance-cost override for bandwidth savings**: rejected; host-authoritative `k` is a one-time push via `DistanceConfigMessage`, cost is negligible.

## Harmony patches catalog

Every patch lists its target method, patch type, and a one-to-two-sentence effect. Vanilla method bodies are NOT re-derived here; see `Relevant central pages` for `PowerTransmitter.md` (method bodies, raycast, transforms, constants, prefab extraction) and other game-internals pages.

### Visual patches

| Patch class | Target method | Type | Effect |
|---|---|---|---|
| `VisualizerIntensitySetterPatch` | `WirelessPower.VisualizerIntensity` (setter) | Postfix | Cast to `PowerTransmitter`; `BeamManager.SetLineIntensity`. Drives beam show/hide and current intensity. |
| `WirelessPowerHorizontalSetterPatch` | `WirelessPower.Horizontal` (setter) | Postfix | If `PowerTransmitter` and beam visible, refresh endpoints. |
| `WirelessPowerVerticalSetterPatch` | `WirelessPower.Vertical` (setter) | Postfix | Same. |

**Depends on:** [../../Research/GameClasses/PowerTransmitter.md](../../Research/GameClasses/PowerTransmitter.md) (class hierarchy, `WirelessPower` and `PowerTransmitter` members, prefab-extraction LineRenderer/Material values).

### Distance-cost quartet

All four patches on `PowerTransmitter` are a single model. See `Research/GameClasses/PowerTransmitter.md` for vanilla method bodies (`GetGeneratedPower`, `UsePower`, `GetUsedPower`, `ReceivePower`) and the `distance-cost-quartet` section for the vanilla-vs-patched flow diagram and energy-conservation argument.

Vanilla power tick:

```
WirelessOutputNetwork tick:
  GetGeneratedPower -> Min(5000, InputNetwork.PotentialLoad) - distance_loss
  Receiver demands D, gets D up to ceiling
  UsePower(WirelessOutputNetwork, D) -> _powerProvided += D

InputNetwork tick:
  GetUsedPower(InputNetwork) -> Min(5000, _powerProvided) = D
  ReceivePower(InputNetwork, D) -> _powerProvided -= D
                                  -> VisualizerIntensity = D / 5000
```

After equilibrium each tick: `_powerProvided` returns to 0.

Patched flow with multiplier `m = 1 + k x dist_m / 1000`:

```
WirelessOutputNetwork tick:
  GetGeneratedPower -> Min(5000, InputNetwork.PotentialLoad)   (patch 1: drop loss)
  Receiver demands D, gets D
  UsePower(WirelessOutputNetwork, D) -> _powerProvided += D
                                       (patch 2: also += D x (m-1))
                                       -> _powerProvided = D x m

InputNetwork tick:
  GetUsedPower(InputNetwork) -> Min(5000, _powerProvided)
                              (patch 3: lifted to uncapped _powerProvided = D x m)
  ReceivePower(InputNetwork, D x m) -> _powerProvided -= D x m  (back to 0)
                                      -> VisualizerIntensity = (D x m) / 5000
                                      (patch 4: overridden to D / 5000)
```

Energy conservation: `_powerProvided` net-zeros each tick.

| # | Patch class | Target method | Type | What |
|---|---|---|---|---|
| 1 | `GeneratedPowerNoDistanceDeratePatch` | `PowerTransmitter.GetGeneratedPower` | Prefix (return false) | Replicate vanilla guards. Return `Min(MaxPowerTransmission, InputNetwork.PotentialLoad)` with no loss subtraction. |
| 2 | `UsePowerInflateDebtPatch` | `PowerTransmitter.UsePower` | Postfix | Skip if `powerUsed <= 0` / Error / !OnOff / wrong network. Compute multiplier; if > 1, add `powerUsed x (multiplier - 1)` to `_powerProvided`. |
| 3 | `GetUsedPowerLiftCapPatch` | `PowerTransmitter.GetUsedPower` | Postfix | Skip if Error / !OnOff / no InputNetwork. Read `_powerProvided`. If `debt > __result`, set `__result = debt`. |
| 4 | `ReceivePowerVisualizerFixPatch` | `PowerTransmitter.ReceivePower` | Postfix | Skip if multiplier <= 1. Compute `delivered = powerAdded / multiplier`, set `VisualizerIntensity = delivered / MaxPowerTransmission`. |

All four patches are required as a set. Disabling any one produces observable breakage. See Pitfalls below.

Source comment from `DistanceCostPatches.cs:10-35`:

```
// Replaces vanilla's distance-based capacity derate on PowerTransmitter.
//
// Vanilla model:
//   delivered_max = 5000 - distance * 10
//   source_draw   = delivered
//
// New model (this mod):
//   delivered_max = 5000  (uncapped by distance)
//   source_draw   = delivered * (1 + k * distance_m / 1000)
//
// Where k is the configurable per-km overhead factor.
//
// Implementation hinges on PowerTransmitter._powerProvided, the private
// float "debt accumulator" between the wireless-output tick and the
// source-input tick:
//   - UsePower(WirelessOutputNetwork, delivered):  _powerProvided += delivered
//   - GetUsedPower(InputNetwork):                  returns _powerProvided
//   - ReceivePower(InputNetwork, paid):            _powerProvided -= paid
//
// We inflate the debt at UsePower time so that the source pays
// delivered * multiplier instead of just delivered. We also lift the
// MaxPowerTransmission cap on GetUsedPower so the inflated debt can be
// settled in one tick. Finally, we override VisualizerIntensity in
// ReceivePower to reflect *delivered*, not *source_draw*, so the visualizer
// remains a meaningful "throughput" indicator instead of saturating at
// 1/multiplier on long beams.
```

**Depends on:** [../../Research/GameClasses/PowerTransmitter.md](../../Research/GameClasses/PowerTransmitter.md) (vanilla `GetGeneratedPower` / `UsePower` / `GetUsedPower` / `ReceivePower` bodies; `_powerProvided` debt accumulator; `MaxPowerTransmission`, `_MaxTransmitterDistance`, `PowerLossOverDistance` constants).

### Logic-readout patches

| # | Patch class | Target | Type |
|---|---|---|---|
| 5 | `WirelessPowerCanLogicReadPatch` | `WirelessPower.CanLogicRead` | Postfix (branches on `__instance is PowerTransmitter / PowerReceiver`) |
| 6 | `WirelessPowerGetLogicValuePatch` | `WirelessPower.GetLogicValue` | Prefix (same branch; reads AutoAim cache per-dish before the transmitter-side resolution) |
| 9 | `LogicableInitializePatch` | `Logicable.Initialize` | Postfix (one-shot, idempotent); also extends `EnumCollections.LogicTypes` and `ScreenDropdownBase.LogicTypes` in-line |
| 10 | `EnumGetNamePatch` | `Enum.GetName(Type, object)` | Postfix |
| 11 | `EnumCollectionGetNamePatch` | `EnumCollection<LogicType,ushort>.GetName` | Postfix |
| 12 | `EnumCollectionGetNameFromValuePatch` | `EnumCollection<LogicType,ushort>.GetNameFromValue` | Postfix |
| 13 | `StationpediaPopulateLogicVariablesPatch` | `Stationpedia.PopulateLogicVariables` (via `TargetMethod`) | Postfix (`Prepare`-gated) |
| 14 | `PlayerConnectedSyncPatch` | `NetworkManager.PlayerConnected` | Postfix |

All `WirelessPower` patches target the base class directly (not `PowerTransmitter` / `PowerReceiver`) because those subclasses inherit without re-overriding. HarmonyX's attribute-based lookup uses `AccessTools.DeclaredMethod` and does not match inherited methods. Virtual dispatch runs the postfix/prefix for any subclass instance.

**Depends on:** [../../Research/GameSystems/LogicType.md](../../Research/GameSystems/LogicType.md) (three-plus-one LogicType registries, reserved bands, `Enum.GetValues` bootstrapping), [../../Research/GameSystems/IC10SyntaxHighlighting.md](../../Research/GameSystems/IC10SyntaxHighlighting.md) (`ProgrammableChip.InternalEnums` `ScriptEnum`/`BasicEnum` pipeline), [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md) (why base-class targeting is required).

### Auto-aim patches

| # | Patch class | Target | Type |
|---|---|---|---|
| 15 | `WirelessPowerSetLogicValuePatch` | `WirelessPower.SetLogicValue` | Prefix (false for `MicrowaveAutoAimTarget`; passes through for everything else) |
| 16 | `WirelessPowerCanLogicWritePatch` | `WirelessPower.CanLogicWrite` | Postfix (marks 6575 writable on TX and RX) |
| 17 | `RotatableTargetHorizontalResetPatch` | `RotatableBehaviour.TargetHorizontal` (setter) | Postfix (clears auto-aim cache on any external override) |
| 18 | `RotatableTargetVerticalResetPatch` | `RotatableBehaviour.TargetVertical` (setter) | Postfix (same) |

`AutoAimState._target` is a `ConditionalWeakTable<WirelessPower, StrongBox<long>>`. The reset postfixes catch manual aim writes from every source (tablet, IC10 `s d0 Horizontal ...`, dish UI buttons, scroll-wheel) because they all funnel through `RotatableBehaviour.TargetHorizontal` / `TargetVertical`.

**Depends on:** [../../Research/GameClasses/PowerTransmitter.md](../../Research/GameClasses/PowerTransmitter.md) (pivot-to-pivot aim geometry, dish transform hierarchy, `TryContactReceiver` raycast, Head-child transforms move with rotation), [../../Research/GameClasses/RotatableBehaviour.md](../../Research/GameClasses/RotatableBehaviour.md) (`TargetHorizontal` / `TargetVertical` setters and the servo delta-state that Auto-aim rides), [../../Research/Patterns/ConditionalWeakTableCache.md](../../Research/Patterns/ConditionalWeakTableCache.md) (per-dish cache lifecycle tied to GC).

### StationpediaPatches.cs stub

Best-effort Stationpedia integration via `AccessTools.TypeByName` and `TargetMethod()`+`Prepare()`. Adds in-game wiki entries for each custom LogicType. Failure is non-fatal. Custom link handler avoids vanilla `LateUpdate`.

**Depends on:** [../../Research/Patterns/BestEffortIntegration.md](../../Research/Patterns/BestEffortIntegration.md) (TypeByName fallback plus Prepare gating for version-resilient integration with optional dependencies).

## Relevant central pages

Entries are linked and tagged with one-line "why this mod cares." A reader looking for "where is the knowledge about X" scans this section first.

### GameClasses

- [../../Research/GameClasses/PowerTransmitter.md](../../Research/GameClasses/PowerTransmitter.md) - Vanilla class hierarchy (`Thing` -> `Device` -> `ElectricalInputOutput` -> `WirelessPower` -> `PowerTransmitter`/`PowerReceiver`), field inventory, method bodies (`GetGeneratedPower` / `UsePower` / `GetUsedPower` / `ReceivePower`), `TryContactReceiver` raycast, dish transform hierarchy, pivot-to-pivot aim geometry, constants table, prefab-extraction values for LineRenderer and `Custom_PowerTransmission` material. Every patch in the distance-cost quartet, the auto-aim patches, and the logic-readout patches depend on facts here.
- [../../Research/GameClasses/RotatableBehaviour.md](../../Research/GameClasses/RotatableBehaviour.md) - `TargetHorizontal` / `TargetVertical` setters and the servo delta-state (`NetworkUpdateFlags |= 256`) that Auto-aim rides for multiplayer sync without adding a new message.

### GameSystems

- [../../Research/GameSystems/LogicType.md](../../Research/GameSystems/LogicType.md) - Three-plus-one parallel LogicType registries (`Logicable.LogicTypes`, `EnumCollections.LogicTypes`, `ScreenDropdownBase.LogicTypes`, `ProgrammableChip.AllConstants` plus `InternalEnums`), reserved-band table, AllConstants MIPS-name resolution. Our six LogicType injections (6571-6576) depend on this entire mechanism, plus the `ProgrammableChip` `AllConstants` array and `InternalEnums` list which `Ic10ConstantsPatcher` mutates directly.
- [../../Research/GameSystems/PowerTickThreading.md](../../Research/GameSystems/PowerTickThreading.md) - `PowerTick.ApplyState` runs on ThreadPool worker; the single fact behind our `MainThreadDispatcher` requirement and the reason every beam write must be enqueued.
- [../../Research/GameSystems/IC10SyntaxHighlighting.md](../../Research/GameSystems/IC10SyntaxHighlighting.md) - `ProgrammableChip.InternalEnums` `ScriptEnum<LogicType>` (index 0, orange) / `BasicEnum<LogicType>` (index 4, teal) pipeline; why missing extensions produce red-text "invalid" rendering for our 6571-6576 names.
- [../../Research/GameSystems/NetworkRoles.md](../../Research/GameSystems/NetworkRoles.md) - NetworkManager role flags matrix (`IsActive`, `IsServer`, `IsClient`, single-player `None`); basis for the `IsActive && !IsServer` client-guard pattern used in `DistanceConfigSync.GetEffectiveK` and `BeamVisualConfigSync.UseHostValues`.
- [../../Research/GameSystems/NetworkUpdateFlags.md](../../Research/GameSystems/NetworkUpdateFlags.md) - 16-bit delta-state bitmask; `NetworkUpdateFlags |= 256` is the servo-target bit our auto-aim relies on.
- [../../Research/GameSystems/ThirdPartyModIdentities.md](../../Research/GameSystems/ThirdPartyModIdentities.md) - Stationeers Logic Extended identity (Workshop ID 3625190467, author ThunderDuck), the pattern donor for custom LogicType registration.

### Patterns

- [../../Research/Patterns/BestEffortIntegration.md](../../Research/Patterns/BestEffortIntegration.md) - `StationpediaPatches.cs` uses this (TypeByName fallback plus Prepare gating) for optional Stationpedia hooks; custom link handler avoids vanilla `LateUpdate` via the same pattern.
- [../../Research/Patterns/ConditionalWeakTableCache.md](../../Research/Patterns/ConditionalWeakTableCache.md) - Per-dish auto-aim cache lifecycle tied to GC (`ConditionalWeakTable<WirelessPower, StrongBox<long>>`) with no manual cleanup required.
- [../../Research/Patterns/Float16Quantization.md](../../Research/Patterns/Float16Quantization.md) - `0.202` is the float16-rounded `0.2`; helps diagnose "weird value just above expected" reports on synced `k` and visual config values.
- [../../Research/Patterns/HarmonyFieldOrProperty.md](../../Research/Patterns/HarmonyFieldOrProperty.md) - `Traverse.Field(...).GetValue<T>()` with a property fallback, used for `WirelessOutputNetwork` access when the field-vs-property declaration is uncertain.
- [../../Research/Patterns/HarmonyInheritedMethods.md](../../Research/Patterns/HarmonyInheritedMethods.md) - Why our logic-readout and auto-aim patches target `WirelessPower` directly and not `PowerTransmitter` / `PowerReceiver`.
- [../../Research/Patterns/MainThreadDispatcher.md](../../Research/Patterns/MainThreadDispatcher.md) - The `ConcurrentQueue<Action>` drain pattern we use to bridge `PowerTick` ThreadPool writes onto the Unity main thread.
- [../../Research/Patterns/ServerAuthoritativeSimulation.md](../../Research/Patterns/ServerAuthoritativeSimulation.md) - Vanilla simulation runs on the server; our four distance-cost patches only meaningfully execute on the host.
- [../../Research/Patterns/SinglePlayerNetworkRole.md](../../Research/Patterns/SinglePlayerNetworkRole.md) - `NetworkRole.None` single-player trap that our guards avoid.
- [../../Research/Patterns/StationeersNamespaces.md](../../Research/Patterns/StationeersNamespaces.md) - Namespaces that are easy to get wrong: `EnumCollection<,>` in `Assets.Scripts` (not `.Util`), `ProgrammableChip` in `Assets.Scripts.Objects.Electrical` (not `Motherboards`), `PowerTransmitterVisualiser` in the global namespace.
- [../../Research/Patterns/UnityMaterialPerInstance.md](../../Research/Patterns/UnityMaterialPerInstance.md) - `renderer.material` versus `sharedMaterial` plus `OnDestroy` cleanup, followed in `BeamLine` and `BeamPulseTrain` to avoid material leaks.

### Protocols

- [../../Research/Protocols/LaunchPadBoosterNetworking.md](../../Research/Protocols/LaunchPadBoosterNetworking.md) - `IModNetworking.Required`, `RegisterMessage<T>`, `SendToHost` / `SendToClient` / `SendAll` semantics used by our two messages; `MOD.Networking.Required = true` version handshake.
- [../../Research/Protocols/PowerTransmitterPlusNetworking.md](../../Research/Protocols/PowerTransmitterPlusNetworking.md) - Our two custom messages (`DistanceConfigMessage`, `BeamVisualConfigMessage`) with connect-time push flow and `SettingChanged` broadcast.

### Workflows

- [../../Research/Workflows/ModProjectSetup.md](../../Research/Workflows/ModProjectSetup.md) - BepInEx plus HarmonyX plus StationeersLaunchPad scaffold this mod uses, `Config.Bind` pattern, plugin template, patch-type taxonomy.

## Pitfalls / dead ends

### Beam alpha is permanently 1

The beam's `LineRenderer` color alpha is held at 1 whenever the beam is visible. The pulse train is the sole power-level indicator, not beam dimming. This is the intended design, not a workaround.

### PowerTick runs on ThreadPool worker

`PowerTick.ApplyState` runs on a UniTask ThreadPool worker thread. Any Unity API call from that thread will hard-crash the native player. Apply `MainThreadDispatcher` discipline to any Unity-API-touching code paths. See `GameSystems/PowerTickThreading.md` for the representative crash stack (Shader.Find -> BeamManager.SharedMaterial -> BeamLine.ctor -> VisualizerIntensitySetterPatch.Postfix -> PowerTransmitter.ReceivePower -> PowerTick.ConsumePower / ApplyState -> CableNetwork.OnPowerTick -> Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable).

### `Material.material` vs `Material.sharedMaterial`

`renderer.material` getter clones the shared material on first access and caches the clone on the renderer (subsequent reads return the same clone). `renderer.sharedMaterial` gives the original. Use `_lr.material` once in `Awake` to get a per-instance copy, store the reference. Must `Destroy(_instanceMaterial)` in `OnDestroy` to avoid a leak. See `Patterns/UnityMaterialPerInstance.md`.

### Namespaces that are easy to get wrong

See `Patterns/StationeersNamespaces.md` for the full reference. Summary:

| Type | Namespace | Common mistake |
|---|---|---|
| `EnumCollection<,>` | `Assets.Scripts` | Not `Assets.Scripts.Util` |
| `ProgrammableChip` | `Assets.Scripts.Objects.Electrical` | Not `Motherboards` |
| `ProgrammableChip.Constant` | nested in `ProgrammableChip` | Must qualify as `ProgrammableChip.Constant` |
| `LogicType` | `Assets.Scripts.Objects.Motherboards` | (Most Logic types ARE in `Motherboards`, just not the chip) |
| `PowerTransmitterVisualiser` | global namespace | NOT `Assets.Scripts.Objects.Electrical` despite the dish being there |

### `WirelessOutputNetwork` access

`PowerTransmitter.UsePower` checks `cableNetwork == WirelessOutputNetwork`, but it is unclear whether `WirelessOutputNetwork` is a public field or property. Use `Traverse.Create(t).Field("WirelessOutputNetwork").GetValue<CableNetwork>()` with a property fallback. See `Patterns/HarmonyFieldOrProperty.md`.

### `_powerProvided` debt accounting

`_powerProvided` is the debt accumulator between two networks in the vanilla flow. Our four distance-cost patches all depend on each other:

- Patch 2 alone (add to `_powerProvided`) without patch 3 (lift cap): debt grows over time because `Min(5000, ...)` cap means source cannot pay the inflated amount.
- Patch 3 alone: no behavior change unless debt is inflated by patch 2.
- Patch 4 alone: visualizer wrong on long beams, gameplay unchanged.
- Patch 1 alone: loss still applied elsewhere, breaks accounting.

Do not disable any one without considering the others.

### `0.202` is float16 quantization

Network serialization quantizes floats to half-precision. `0.2` is not exactly representable; the nearest representable above is `0.2002...`, which prints as `0.202`. Useful for diagnosing "weird value just above the expected" reports. See `Patterns/Float16Quantization.md`.

### Stripe trough brightness changes require game restart

`BeamManager.StripeTexture` is created once and cached. Changing `Trough Brightness` only takes effect on game restart (the static `_stripeTexture` field is null then).

### Harmony attribute lookup and inherited methods

`[HarmonyPatch(typeof(Subclass), "InheritedMethodName")]` throws `Undefined target method for patch method ...` at `PatchAll` time because HarmonyX's attribute path calls `AccessTools.DeclaredMethod`, which only finds methods declared directly on the target type. If the method is inherited without override, target the class that actually declares the override. Virtual dispatch runs the postfix/prefix for any subclass instance. Applies to `WirelessPower.CanLogicRead` / `GetLogicValue` / `SetLogicValue` / `CanLogicWrite`. All inherited by `PowerTransmitter` and `PowerReceiver` without override. See `Patterns/HarmonyInheritedMethods.md`.

### `DishForward = DishTransform.up` is a lie for aim purposes

`WirelessPower.Vertical` and `Horizontal` setters both update `DishForward = DishTransform.up`, which reads naturally when inferring aim direction. It is wrong for the raycast. The base-game link raycast uses `RayTransform.forward`, NOT `DishTransform.up`. These two vectors are ORTHOGONAL in the local Head frame (forward is local `+Z`, up is local `+Y`). Using `DishForward` restricts aim to the upper hemisphere only, contradicting observed in-game behavior.

Rule: the dish's true aim direction is the raycast's direction vector. Check the actual raycast call site before deriving aim math.

### Transforms under `Head` move with dish rotation

`Line` (`RayTransform`), `DishTarget`, and `Transmitter` are all children of `Head`, which rotates via `DishTransform.localRotation = Euler(Lerp(90, -90, V), 0, 0)`. Their world positions therefore change as the dish rotates. Any aim algorithm that treats them as fixed produces:

- Self-referential error when used as RAY ORIGIN: aim computed from current `RayTransform` position goes stale once the dish rotates and the `RayTransform` moves. Observed as ~0.3 degree drift that prevented link-raycast hits.
- Pose-lock-in when used as RAY TARGET: aiming at the other dish's `RayTransform` / `DishTarget` locks onto that dish's CURRENT pose. When the target later rotates to a correct aim, your aim is pointing at empty space.

Rule: use `dish.transform.position` (the placement-anchored root) as both origin and target for aim computation. Invariant under all dish rotation.

### There are four separate LogicType registries

The game stores LogicType names/values in four independent locations, each populated from `Enum.GetValues`/`GetNames` at class load. All four must be extended for full coverage:

1. `Logicable.LogicTypes` / `LogicTypeNames`: drives `NextLogicType` cycling in the tablet.
2. `EnumCollections.LogicTypes`: drives `ConfigCartridge` (tablet cartridge UI).
3. `ScreenDropdownBase.LogicTypes` / `LogicTypeNames`: drives motherboard condition/action dropdown menus.
4. `ProgrammableChip.InternalEnums` entries `ScriptEnum<LogicType>` and `BasicEnum<LogicType>`: drive **syntax highlighting** on all in-game screens (computers, laptops, wall-mounted screens). If not extended, custom LogicType names receive no `<color>` tag and inherit the screen's default red text color, appearing "invalid" even though they compile and execute correctly.

See `GameSystems/LogicType.md` and `GameSystems/IC10SyntaxHighlighting.md`.
