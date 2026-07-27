---
title: DLC gating
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: DLC.DLCManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: DLC.SharedDLCManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: DLC.DLCType
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing (_dlcType field)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.AvailableDLCMessage
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Data\paints.xml
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Language\english.xml
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GameManager (IsBatchMode, RunSimulation, SetMatchMode, ClearGameAll)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.MessageBase (DeserializeReceivedData)
related:
  - ../GameClasses/ColorSwatch.md
  - ../GameClasses/SprayCan.md
  - ../GameClasses/Thing.md
tags: [network, prefab]
---

# DLC gating

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

How the game decides whether a player may obtain DLC-locked content. Two managers cooperate: `DLCManager` holds the local player's Steam entitlements, `SharedDLCManager` holds the union of every connected player's entitlements for the current session. Enforcement happens at a small number of acquisition sites; there is no enforcement at the point where DLC-derived content is applied to an existing object.

This page exists because a mod that hands the player DLC-derived content through a path the game does not gate silently bypasses the entitlement check. The gap documented in "Where the game does NOT check" is the one that matters for mod authors.

## DLCType enum

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`[Flags] public enum DLCType`, backing store is used as a `ushort` on the wire:

```
[Flags]
public enum DLCType
{
    None = 0,
    Zrilian = 1,
    HemDroid = 2,
    HumanCharacter = 4,
    CountryOveralls = 8,
    BobbleHeadEva = 0x10,
    IcarusSuit = 0x20,
    BobbleHeadHard = 0x40,
    BobbleHeadMarine = 0x80,
    MetallicPaints = 0x100
}
```

`DLCManager.AllDLC` is the OR of every named value:

```
public static readonly DLCType AllDLC = DLCType.Zrilian | DLCType.HemDroid | DLCType.HumanCharacter | DLCType.CountryOveralls | DLCType.BobbleHeadEva | DLCType.IcarusSuit | DLCType.BobbleHeadHard | DLCType.BobbleHeadMarine | DLCType.MetallicPaints;
```

## Steam app IDs per DLC

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`DLCManager.FetchOwnershipFromSteam` maps each flag to a Steam app ID via `NetworkManager.CurrentTransport.IsDlcInstalled(uint)`. `DLCManager.GetStorePageLink(DLCType)` returns the matching store URL.

| DLCType | Steam app ID | Store link |
|---|---|---|
| `HemDroid` | 1038500 | https://store.steampowered.com/app/1038500 |
| `Zrilian` | 1038400 | https://store.steampowered.com/app/1038400 |
| `HumanCharacter` | 2089290 | https://store.steampowered.com/app/2089290 |
| `CountryOveralls` | 2542990 | https://store.steampowered.com/app/2542990 |
| `BobbleHeadMarine` | 3196220 | https://store.steampowered.com/app/3196220 |
| `BobbleHeadEva` | 3166330 | https://store.steampowered.com/app/3166330 |
| `BobbleHeadHard` | 3196210 | https://store.steampowered.com/app/3196210 |
| `IcarusSuit` | 1149460 | https://store.steampowered.com/app/1149460 |
| `MetallicPaints` | 4842920 | https://store.steampowered.com/app/4842920 |

The fallback link for an unmatched `DLCType` is `https://store.steampowered.com/dlc/544550/Stationeers/`.

`DLCManager.GrantFullOwnership()` sets `_ownedDLC = AllDLC`. It exists in the class but has no call site in `DLCManager.Initialize`, which calls `FetchDlcOwnership()` and therefore `FetchOwnershipFromSteam()` only.

## DLCManager: local entitlements

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

```
private static DLCType _ownedDLC;

public static DLCType GetOwnedDLC() => _ownedDLC;

public static void Initialize() => FetchDlcOwnership();

private static bool CheckAccess(DLCType dlcType)
{
    if (dlcType == DLCType.None)
    {
        return true;
    }
    return (dlcType & _ownedDLC) != 0;
}

public static bool CheckAccess(KitItem kitItem)
{
    if ((bool)kitItem)
    {
        return CheckAccess(kitItem.DlcType);
    }
    return true;
}

public static bool CheckAccess(Thing thing)
{
    if (!thing)
    {
        return true;
    }
    return CheckAccess(thing.DLCType);
}
```

`DLCType.None` always passes. A null / destroyed `Thing` or `KitItem` always passes.

All three types live in the bare `DLC` namespace (`DLC.DLCManager`, `DLC.SharedDLCManager`, `DLC.DLCType`), NOT under `Assets.Scripts` where most game code sits. A mod needs `using DLC;`.

### Initialization timing

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`DLCManager.Initialize()` is called from a manager's `private async void Start()`, in a startup sequence alongside `ControllerAxisItem.InitializeJoysticks()`, `InputMouse.Initialize()`, `Settings.Initialize()`, and `Stationpedia.Initialize()`. Until it runs, `_ownedDLC` is `0` and every `CheckAccess` call for a non-`None` `DLCType` returns false.

This matters for BepInEx mods: plugin `Awake()` runs during the BepInEx chainloader, before Unity `Start()` on scene objects, so **entitlement is not yet known at plugin `Awake` time**. Any mod that wants to branch on DLC ownership must defer the read, for example to `Prefab.OnPrefabsLoaded` or to first use, rather than sampling it while binding config. A concrete consequence: StationeersLaunchPad's settings panel supports a `Disabled` tag for rendering a config entry read-only (see `../Patterns/StationeersLaunchPadSettingsGrouping.md`), but the tag has to be supplied inside the `ConfigDescription` at `Config.Bind` time, which is too early to test ownership. Computing it there would grey the entry out for players who do own the DLC.

## SharedDLCManager: session-wide entitlement pool

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

Stationeers shares DLC across a multiplayer session: if any connected player owns a DLC, every player in that session can use its content. `SharedDLCManager` holds the union.

```
private static ushort _sharedDLC;

public static ushort SharedDLC
{
    get { return _sharedDLC; }
    set
    {
        _sharedDLC = value;
        if (NetworkManager.IsServer && NetworkServer.HasClients())
        {
            NetworkUpdateFlags |= 256;
        }
    }
}

public static void AddSharedDLC(ushort clientOwnedDLC) => SharedDLC |= clientOwnedDLC;

public static void HostFinishedLoad()
{
    if (!GameManager.IsBatchMode && GameManager.RunSimulation)
    {
        SharedDLC = (ushort)DLCManager.GetOwnedDLC();
    }
}

public static void ClientFinishedLoad()
{
    DLCType ownedDLC = DLCManager.GetOwnedDLC();
    NetworkClient.SendToServer(new AvailableDLCMessage { DLCType = (ushort)ownedDLC });
}

public static bool CheckSharedAccess(DLCType dlcType)
{
    DLCType sharedDLC = (DLCType)SharedDLC;
    return CheckAccess(dlcType, sharedDLC);
}

private static bool CheckAccess(DLCType dlcType, DLCType ownedDlc)
{
    if (dlcType == DLCType.None)
    {
        return true;
    }
    return (dlcType & ownedDlc) != 0;
}

public static void ClearAll() => SharedDLC = 0;
```

Lifecycle:

- `SharedDLCManager.ClearAll()` runs on world teardown, resetting the pool to 0.
- `HostFinishedLoad()` seeds the pool from the host's own entitlements. Its sole call site is decompile line 268799, at the end of the world-load path, immediately after `World.OnLoadingFinished`. Of the two guard terms only `!IsBatchMode` can fail on a server: `GameManager.RunSimulation` is `=> !NetworkManager.IsClient` (203945), which is always true on a server. So a dedicated server does NOT seed the pool from the server process; the pool starts empty and fills only from connecting clients. See "Dedicated server behavior" below for what `IsBatchMode` actually keys off, which is broader than the `-batchmode` flag.
- `ClientFinishedLoad()` makes each client send `AvailableDLCMessage` to the server with its own `DLCType` bitmask.
- `AvailableDLCMessage.Process` calls `SharedDLCManager.AddSharedDLC(DLCType)`, ORing the client's entitlements into the pool.
- The pool syncs back to clients as delta state under network update bit 256.

Important consequence: the pool only grows during a session. A player who owns the DLC joining and then leaving leaves the pool with the bit still set until `ClearAll()` runs.

Exhaustive write-site list for the pool, from a whole-file search of the decompile:

```
192430:  private static ushort _sharedDLC;          (declaration)
192442:  _sharedDLC = value;                        (setter body)
192452:  SharedDLC |= clientOwnedDLC;               (AddSharedDLC)
192459:  SharedDLC = (ushort)DLCManager.GetOwnedDLC();  (HostFinishedLoad)
192493:  SharedDLC = reader.ReadUInt16();           (DeserializeDeltaState, client side)
192499:  SharedDLC = 0;                             (ClearAll)
```

There is no disconnect, leave, or player-removal path that clears or recomputes the pool, and no per-player subtraction is even possible because no per-player entitlement record exists anywhere (see "Not caller-scoped" below).

### Dedicated server behavior

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

`IsBatchMode` keys off more than the `-batchmode` command-line flag. `GameManager.SetMatchMode()` (204290-204304) runs at `AfterAssembliesLoaded`, so it is settled long before any world load:

```
[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
private static void SetMatchMode()
{
    int isBatchMode;
    if (!Application.isBatchMode)
    {
        RuntimePlatform platform = Application.platform;
        isBatchMode = ((platform == RuntimePlatform.LinuxServer || platform == RuntimePlatform.WindowsServer) ? 1 : 0);
    }
    else
    {
        isBatchMode = 1;
    }
    IsBatchMode = (byte)isBatchMode != 0;
}
```

A dedicated-server build therefore has `IsBatchMode == true` from its platform alone, with or without `-batchmode` on the command line.

Trace for a dedicated server that owns nothing, with one connected client that owns a DLC:

1. Server process starts. `_sharedDLC` is 0 (static default).
2. World load completes and calls `SharedDLCManager.HostFinishedLoad()` (268799). `IsBatchMode` is true, so the guard fails and the pool is not seeded. It stays 0.
3. Each connecting client, at the very end of its own join, calls `ClientFinishedLoad()` and sends its `AvailableDLCMessage`. Non-owning clients contribute 0.
4. The owning client's message lands. The server runs `AvailableDLCMessage.Process`, which calls `AddSharedDLC`, which ORs the bit in.
5. `CheckSharedAccess` on the server now returns true for that DLC.

Two windows follow from this, both worth knowing:

- **Before the owning client is fully joined, the answer is false.** `ClientFinishedLoad()` is the last step before the client is announced ready: decompile 213241 sits immediately before `UpdateHandshakeState(HandshakeType.ClientReady)` at 213243. So during the entire join (world stream, thing processing, character request) the owning client is connected but has not yet contributed its bit. Any code sampling the pool on client-connect rather than client-ready reads false.
- **After the owning client disconnects, the answer stays true.** Per the write-site list above, nothing removes a bit. The entitlement persists for the remaining lifetime of the loaded world, until `ClearAll()` runs from `GameManager.ClearGameAll()` (204810) on teardown.

Consequence for server operators and mod authors: while at least one owning client is fully joined, **every** connected player can fabricate and spawn that DLC's content, not just the owner. The pool is broadcast back to all clients under delta bit 256, so each client's local gate passes too. That is the designed shared-DLC behavior and it holds on a dedicated server that owns nothing itself.

One coupling to note: the dirty flag that triggers the broadcast is only raised when `NetworkManager.IsServer && NetworkServer.HasClients()` (192443). In practice `HasClients()` is true when a client's own message arrives over its own connection, but the pool can change without being marked dirty if that ever fails, in which case the server would allow the DLC while no client's local gate had been updated.

`AddSharedDLC` uses `|=`, which invokes the property setter even when the value does not change. Every later client's join message therefore re-dirties the pool and re-broadcasts it, which is how a client joining after the owner receives an already-populated pool. There is no `SharedDLC` field in the join package itself.

### Not caller-scoped

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

There is no way to ask "does THIS player own it". Only "does anyone in the session own it, or has since world load".

- `CheckSharedAccess(DLCType)` (192472) takes no player or caller argument.
- `AvailableDLCMessage.Process(long hostId)` (277481) receives the sender id and discards it, calling `AddSharedDLC(DLCType)` and nothing else.
- The per-connection `Client` type carries no DLC field, and `Thing._dlcType` describes the object, not the owner.

`AvailableDLCMessage` is a server-processed message. `MessageBase.DeserializeReceivedData` (39287-39308) carries a whitelist of message types allowed to process off the server, and `AvailableDLCMessage` is not on it. Note that the whitelist only controls an error print: `messageProcessable.Process(hostId)` at 39306 sits outside the `if`, so processing runs regardless.

For a mod that needs per-player entitlement on a dedicated server, the only point where the sender id and the bitmask coexist is inside `AvailableDLCMessage.Process`, so a Harmony prefix or postfix there is the single interception point. The mod would have to build and maintain its own player-to-`DLCType` map.

### AvailableDLCMessage

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

```
public class AvailableDLCMessage : ProcessedMessage<AvailableDLCMessage>
{
    public ushort DLCType;

    public override void Process(long hostId)
    {
        SharedDLCManager.AddSharedDLC(DLCType);
    }

    public override void Deserialize(RocketBinaryReader reader) => DLCType = reader.ReadUInt16();

    public override void Serialize(RocketBinaryWriter writer) => writer.WriteUInt16(DLCType);
}
```

The server accepts the client's claimed bitmask without verification. Entitlement is client-asserted, not server-validated.

### Delta-state serialization

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

```
public static void SerializeDeltaState(RocketBinaryWriter writer)
{
    writer.WriteUInt16(NetworkUpdateFlags);
    if (IsNetworkUpdateRequired(256, NetworkUpdateFlags))
    {
        writer.WriteUInt16(SharedDLC);
    }
    NetworkUpdateFlags = 0;
}

public static void DeserializeDeltaState(RocketBinaryReader reader)
{
    ushort networkUpdateType = reader.ReadUInt16();
    if (IsNetworkUpdateRequired(256, networkUpdateType))
    {
        SharedDLC = reader.ReadUInt16();
    }
}
```

### dlc console command

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`DLCCommand` (`CommandScope.InGame | CommandScope.HostOrSinglePlayer`) prints the session pool:

- Help text: "Provides DLC debug functions. Host or singleplayer only."
- Argument: `shared : print the shared (server-union) owned DLC`
- `HandleShared()` returns `((DLCType)SharedDLCManager.SharedDLC).ToString()`.

`dlc shared` is the fastest in-game way to read the current pool while testing entitlement behavior.

## Thing.DLCType

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

Every `Thing` carries its DLC requirement as a serialized field set on the prefab:

```
[SerializeField]
private DLCType _dlcType;

public DLCType DLCType => _dlcType;
```

Read-only at runtime (no public setter). `DLCType.None` on all non-DLC content. This is the single per-object source of truth the gates below consult.

## Where the game checks

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

Three enforcement sites exist in `Assembly-CSharp`, all of them at the moment content is ACQUIRED:

1. **Console / creative spawn.** `SpawnDynamicThingMaxStack(long parentId, string prefabName)`:

```
else if (!SharedDLCManager.CheckSharedAccess(dynamicThing.DLCType))
{
    ConsoleWindow.PrintError("error DLC not owned for " + prefabName, suppressStacktrace: true);
}
```

This is the source of the in-game red console line `error DLC not owned for ItemSprayCanMetallicObsidian`.

2. **Fabrication.** The manufactory / fabricator interaction path checks the recipe's product before allowing `Activate`:

```
DynamicThing product = GetProduct(CurrentIndex);
if ((object)product != null && !SharedDLCManager.CheckSharedAccess(product.DLCType))
{
    return delayedActionInstance.Fail(GameStrings.RequireDlcToFabricate);
}
```

A recipe may therefore exist in the data files for every player while remaining unfabricatable without the entitlement.

3. **Character customisation.** `HasDLC(KitItem kitItem)` uses the LOCAL check, not the shared pool:

```
private bool HasDLC(KitItem kitItem)
{
    bool num = DLCManager.CheckAccess(kitItem);
    if (!num)
    {
        _currentDlcLink = DLCManager.GetStorePageLink(kitItem.DlcType);
    }
    return num;
}
```

Cosmetic character content is gated on personal ownership (`DLCManager.CheckAccess`); in-world content is gated on the session pool (`SharedDLCManager.CheckSharedAccess`). The two are deliberately different and a mod should copy whichever matches the content it is handling.

## Where the game does NOT check

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

There is no DLC check anywhere on the paint-application path. A full-text search of `Assembly-CSharp` for `CheckSharedAccess` and `DLCManager.CheckAccess` returns only the four call sites above (three enforcement sites plus the definition). None of the following consults `DLCType`:

- `Thing.SetCustomColor(int index, bool emissive = false)`
- `OnServer.SetCustomColor(Thing thing, int colorIndex)`
- `ISprayer.DoSpray(Thing thing, ISprayer sprayer, bool doAction)`
- `ColorSwatch` itself (the class has no `DLCType` field; see `../GameClasses/ColorSwatch.md`)
- `GameManager.CustomColors`, which holds every swatch regardless of entitlement

The design is coherent for vanilla: the only way to reach a DLC paint color is to hold the matching spray can, and both routes to that can (console spawn, fabrication) are gated. Ownership of the color is expressed entirely through ownership of the item.

The gap this leaves for mods: `GameManager.CustomColors` is an ungated list, and any code that applies a color by index reaches DLC colors with no check. A mod that lets the player pick a color by index rather than by holding a can (a color cycler, a color picker UI, an eyedropper, a logic-driven paint writable) bypasses entitlement without touching any gated code path. Vanilla has no backstop to catch it.

Mod authors handling colors by index should reproduce the vanilla gate themselves. The check that matches vanilla in-world behavior is `SharedDLCManager.CheckSharedAccess(dlcType)`. Resolving a color index to a `DLCType` requires going through the spray can prefab that carries that color, because the swatch itself does not record one:

```
foreach (Thing thing in Prefab.AllPrefabs)
{
    if (thing is SprayCan prefabCan && prefabCan.PaintMaterial != null)
    {
        // prefabCan.PaintMaterial identifies the swatch; prefabCan.DLCType is the gate
    }
}
```

`GameManager.GetColorSwatch(Material)` is the game's own material-to-swatch lookup, used by `ISprayer.DoSpray`, so a per-swatch `Normal` material is a valid key for this mapping. Confirmed at runtime on 2026-07-25 in game version 0.2.6403.27689: all 16 swatch `Normal` materials are distinct assets and all 16 `SprayCan` prefabs resolve one-to-one onto them, yielding a complete and unambiguous color-index-to-`DLCType` map (indices 0-11 `None`, indices 12-15 `MetallicPaints`). Method, caveats, and the full table are on `../GameClasses/ColorSwatch.md` under "Metallic swatch addition".

Both the swatches and the can prefabs are present regardless of entitlement, so the mapping can be built on any install, including one that does not own the DLC.

## Metallic Paints DLC content

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`DLCType.MetallicPaints` (0x100, Steam app 4842920) covers four spray cans. `rocketstation_Data/StreamingAssets/Data/paints.xml` lists Tool Manufactory recipes for all sixteen cans, twelve vanilla plus these four:

- `ItemSprayCanMetallicBronze`
- `ItemSprayCanMetallicGold`
- `ItemSprayCanMetallicObsidian`
- `ItemSprayCanMetallicSilver`

All four recipes are `Time 5`, `Energy 500`, `Iron 1`, identical to the vanilla cans. The recipes ship to every player; the fabricator gate in "Where the game checks" is what stops a non-owner from producing them.

`rocketstation_Data/StreamingAssets/Language/english.xml` carries the four keys with descriptions of the form "Metallic obsidian spray paint. Using it with a spray gun will extend the usage greatly."

The corresponding color swatches carry `ColorSwatch.PaintOnly = true`, which drives the metallic shader response (`_MaskMetallic` and `_MaskSmoothness` set to 0.85) and excludes them from logic color dropdowns. `PaintOnly` is a rendering and logic-selectability flag, not an entitlement flag: it happens to coincide with the DLC set today but carries no `DLCType`. See `../GameClasses/ColorSwatch.md`.

The four swatches sit at `CustomColors` indices 12-15 in the order `ColorObsidian`, `ColorSilver`, `ColorBronze`, `ColorGold`, confirmed at runtime. Note the swatch names drop the `Metallic` prefix the prefab names carry, and the swatch order matches neither alphabetical order nor the `paints.xml` recipe order, so neither identifier nor index can be derived from the other.

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-07-27 -->

- 2026-07-27: added "Dedicated server behavior" and "Not caller-scoped" subsections, and widened the `HostFinishedLoad` lifecycle bullet. Findings: `GameManager.RunSimulation` is `!NetworkManager.IsClient` (203945) and so always passes on a server, leaving `!IsBatchMode` as the only term that blocks self-seeding; `IsBatchMode` is set by `SetMatchMode()` (204290-204304) from `Application.isBatchMode` OR `RuntimePlatform.LinuxServer`/`WindowsServer`, so a dedicated-server build sets it without the `-batchmode` flag; `HostFinishedLoad`'s sole call site is 268799; the client sends its bitmask at 213241, immediately before `UpdateHandshakeState(HandshakeType.ClientReady)` at 213243, so the pool is empty for the whole of an owning client's join; `AvailableDLCMessage` is absent from the `MessageBase.DeserializeReceivedData` whitelist (39302) though `Process` at 39306 runs regardless of that check; `Process` discards `hostId` and no per-player entitlement record exists, so the check cannot be made caller-scoped without a mod-maintained map; `ClearAll`'s sole caller is `GameManager.ClearGameAll` (204756, call at 204810). Also resolved the second open question: it hypothesised that a dedicated server "grants DLC content only while an owning client is connected", which the exhaustive write-site list disproves, and which already contradicted the verified line in this section stating the pool only grows. Replaced with a narrower open question about live confirmation. All quotes re-read first-hand against the 0.2.6403.27689 decompile rather than taken from a sub-agent summary.
- 2026-07-25: independent re-verification of the "Where the game does NOT check" claim against the 0.2.6403.27689 decompile. `CheckSharedAccess` resolves to exactly three occurrences (console spawn gate at 40154, definition at 192472, fabricator gate at 420505). `CheckAccess` resolves to the definitions and internal calls at 192370 / 192396 / 192400 / 192405 / 192411 / 192475 / 192507 plus exactly one external caller at 194337 (`DLCManager.CheckAccess(kitItem)` inside `HasDLC`). No additional enforcement site exists, confirming that no DLC check runs on any paint-application path.
- 2026-07-25: corrected the namespace on all three types. The page was created citing `Assets.Scripts.DLCManager` / `SharedDLCManager` / `DLCType`; they are actually in the bare `DLC` namespace (decompile line 192302 opens `namespace DLC`). Found while writing a mod against the page, which is exactly the sort of error that costs a later reader a build failure. Also added the "Initialization timing" subsection: `DLCManager.Initialize()` runs from a manager's `async void Start()`, so entitlement is still zero during BepInEx plugin `Awake`, which rules out testing ownership at `Config.Bind` time.
- 2026-07-25: added runtime confirmation of the color-index-to-`DLCType` map, gathered by the `spp-color-swatch-probe` ScenarioRunner scenario on the headless dedicated server (fresh Mars2 world, game version 0.2.6403.27689). All 16 swatch `Normal` materials are distinct assets and all 16 `SprayCan` prefabs resolve one-to-one onto them, so the prefab-derived gate described in "Where the game does NOT check" is implementable as written. Metallic swatches confirmed at indices 12-15 in the order Obsidian, Silver, Bronze, Gold. Swatches and prefabs confirmed present regardless of entitlement. Two open questions resolved and removed. Full table and method on `../GameClasses/ColorSwatch.md`.
- 2026-07-25: page created. Decompile findings sourced from Assembly-CSharp.dll (`DLCManager` and `DLCType` at decompile line 192304-192427, `SharedDLCManager` at 192428-192515, `Thing._dlcType` / `Thing.DLCType` at 316896 / 317376, `SpawnDynamicThingMaxStack` gate at 40154, fabricator gate at 420505, `HasDLC(KitItem)` at 194335, `AvailableDLCMessage` at 277477, `DLCCommand` at 97470). Data-file findings sourced from `StreamingAssets/Data/paints.xml` and `StreamingAssets/Language/english.xml`. The "Where the game does NOT check" claim rests on an exhaustive text search of the decompile for `CheckSharedAccess` and `DLCManager.CheckAccess`, which returns only those call sites.

## Open questions

- `DLCManager.GrantFullOwnership()` has no observed call site. Whether it is dead code, called via reflection, or reached from a build-conditional path has not been traced.
- The dedicated-server pool behavior in "Dedicated server behavior" is derived from code, not yet observed in a live session. The `dlc shared` console command is the intended runtime probe: its scope is `CommandScope.InGame | CommandScope.HostOrSinglePlayer` (97480), so it runs on the dedicated-server console, though reaching it from a connected admin client needs `serverrun`.
