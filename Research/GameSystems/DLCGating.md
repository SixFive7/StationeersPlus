---
title: DLC gating
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.DLCManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.SharedDLCManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.DLCType
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing (_dlcType field)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.AvailableDLCMessage
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Data\paints.xml
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Language\english.xml
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

`DLCType.None` always passes. A null / destroyed `Thing` or `KitItem` always passes. `DLCManager.Initialize()` is called during game startup.

## SharedDLCManager: session-wide entitlement pool

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

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
- `HostFinishedLoad()` seeds the pool from the host's own entitlements. The `!GameManager.IsBatchMode && GameManager.RunSimulation` guard means a dedicated server in batch mode does NOT seed the pool from the server process; the pool starts empty and fills only from connecting clients.
- `ClientFinishedLoad()` makes each client send `AvailableDLCMessage` to the server with its own `DLCType` bitmask.
- `AvailableDLCMessage.Process` calls `SharedDLCManager.AddSharedDLC(DLCType)`, ORing the client's entitlements into the pool.
- The pool syncs back to clients as delta state under network update bit 256.

Important consequence: the pool only grows during a session. A player who owns the DLC joining and then leaving leaves the pool with the bit still set until `ClearAll()` runs.

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

`GameManager.GetColorSwatch(Material)` is the game's own material-to-swatch lookup, used by `ISprayer.DoSpray`, so a per-swatch `Normal` material is a valid key for this mapping.

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

## Verification history

<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

- 2026-07-25: independent re-verification of the "Where the game does NOT check" claim against the 0.2.6403.27689 decompile. `CheckSharedAccess` resolves to exactly three occurrences (console spawn gate at 40154, definition at 192472, fabricator gate at 420505). `CheckAccess` resolves to the definitions and internal calls at 192370 / 192396 / 192400 / 192405 / 192411 / 192475 / 192507 plus exactly one external caller at 194337 (`DLCManager.CheckAccess(kitItem)` inside `HasDLC`). No additional enforcement site exists, confirming that no DLC check runs on any paint-application path.
- 2026-07-25: page created. Decompile findings sourced from Assembly-CSharp.dll (`DLCManager` and `DLCType` at decompile line 192304-192427, `SharedDLCManager` at 192428-192515, `Thing._dlcType` / `Thing.DLCType` at 316896 / 317376, `SpawnDynamicThingMaxStack` gate at 40154, fabricator gate at 420505, `HasDLC(KitItem)` at 194335, `AvailableDLCMessage` at 277477, `DLCCommand` at 97470). Data-file findings sourced from `StreamingAssets/Data/paints.xml` and `StreamingAssets/Language/english.xml`. The "Where the game does NOT check" claim rests on an exhaustive text search of the decompile for `CheckSharedAccess` and `DLCManager.CheckAccess`, which returns only those call sites.

## Open questions

- Exact `GameManager.CustomColors` count and index positions of the four metallic swatches in this version have not been confirmed at runtime. The vanilla twelve were verified at 0.2.6228.27061 (see `../GameClasses/ColorSwatch.md`); whether the metallic swatches append at 12-15, and in which order, needs an in-game enumeration before any code depends on it. Resolve with an InspectorPlus request over `GameManager` reading `CustomColors` (`Name`, `PaintOnly`, `Normal`) before relying on positional indices.
- Whether each metallic swatch has a distinct `Normal` Material asset, or whether the four share one material and are distinguished another way. `GameManager.GetColorSwatch(Material)` taking a Material as its key implies per-swatch distinctness, but this has not been confirmed at runtime for the metallic set specifically.
- `DLCManager.GrantFullOwnership()` has no observed call site. Whether it is dead code, called via reflection, or reached from a build-conditional path has not been traced.
- Behavior of the shared pool on a dedicated server has not been tested in-game. `HostFinishedLoad()` skips seeding under `IsBatchMode`, so the expectation is that a dedicated server grants DLC content only while an owning client is connected, but this has not been confirmed.
