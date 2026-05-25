# tools/save-edit

Offline editor for Stationeers save zips. Lets an agent (or developer) change world state without launching the game.

## When to use this

Use save-edit for OFFLINE world manipulation:

- Set OnOff on a specific Battery / Transformer / APC to control which sources or loads are active in a verification scenario.
- Add a fuse next to an existing cable (clone an existing fuse Thing and reposition).
- Build paired before / after scenes from one template (the developer plays once to make the seed save, the agent then derives variants).
- Audit an existing save: list every Thing of a given type, count APCs, find all `CableNetworkId` values used.

For taking RUNTIME snapshots in a known post-tick state, use PgpVerifyHelper instead (`Plans/PgpVerifyHelper/`). The two compose: save-edit sets up the world, PgpVerifyHelper observes it under simulation.

## Save zip shape

A Stationeers save is a plain ZIP archive containing:

| File | Content |
|---|---|
| `world_meta.xml` | small XML, save metadata (world name, version, stats) |
| `world.xml` | ALL game state: every Thing, network ids, atmospheres, players |
| `terrain.dat` | binary voxel data |
| `preview.png` | thumbnail |
| `screenshot.png` | screenshot |

Things live in `world.xml` under `<AllThings>` as `<ThingSaveData xsi:type="...SaveData">` elements. Each Thing has a common header (`ReferenceId`, `PrefabName`, `WorldPosition`, `WorldRotation`, `States`, `DamageState`, `CurrentBuildState`) plus a type-specific tail (e.g. `<CableNetworkId>` on cables, `<PowerStored>` on batteries). Network ids appear in two places: on each Thing that belongs to that network, and in the top-level `<CableNetworks>` / `<RocketNetworks>` / `<RoboticArmNetworks>` lists. Keep both sides in sync when editing or the loader may reject or mis-rebuild.

Full schema reference: [Research/Protocols/SaveFileStructure.md](../../Research/Protocols/SaveFileStructure.md), [Research/Protocols/WorldXml.md](../../Research/Protocols/WorldXml.md).

## CLI

```
python tools/save-edit/stationeers_save.py extract <save.zip> <out_dir> [--force]
python tools/save-edit/stationeers_save.py repack  <in_dir> <save.zip>
python tools/save-edit/stationeers_save.py list    <save.zip> [--prefab P] [--type CableSaveSaveData] [--limit N]
python tools/save-edit/stationeers_save.py show    <save.zip> --ref <ReferenceId>
python tools/save-edit/stationeers_save.py set     <save.zip> <out.zip> --ref <ReferenceId> --field <path> --value <V>
python tools/save-edit/stationeers_save.py clone   <save.zip> <out.zip> --ref <template-ref> --pos X,Y,Z [--rot QX,QY,QZ,QW]
python tools/save-edit/stationeers_save.py add-network  <save.zip> <out.zip> --id <NetworkId>
python tools/save-edit/stationeers_save.py drop-network <save.zip> <out.zip> --id <NetworkId>
```

- `--field` accepts an XPath inside the Thing element. `OnOff`, `CurrentBuildState`, `DamageState/Burn`, `WorldPosition/x` are all valid.
- `clone` deep-copies one Thing to a new world position and assigns a fresh `ReferenceId` (max existing + 1). Network references on the source are NOT auto-rewired; if you clone a cable you usually want to either keep it on the source's `CableNetworkId` (i.e. extend that network) or set it to a fresh id from `add-network` (i.e. create a new isolated network).
- `add-network` / `drop-network` only touch the top-level `<CableNetworks>` list. They do not touch any Thing's `CableNetworkId`; pair with `set` to rewire individual cables.

The CLI is the simple path. For multi-step edits, import the module:

```python
from stationeers_save import Save

s = Save.open("DedicatedServer/data/saves/Luna/Luna.save")
try:
    new_id = s.next_reference_id()
    print(f"next ReferenceId: {new_id}")

    # Find every Battery and turn them off
    for t in s.things(xsi_type="StructureSaveData"):
        if "Battery" in t.prefab_name:
            try:
                t.set("OnOff", "false")
            except KeyError:
                pass  # not every Thing has OnOff

    s.repack("out.zip")
finally:
    s.close()
```

## Tier-3 rule

Save edits are always done on a COPY of a tier-2 source save into the tier-3 dedicated-server saves folder (`DedicatedServer/data/saves/`). The launcher loads from that folder; the developer's client save folder is tier-1 and never touched. See the repo-root CLAUDE.md "Workflow: save file access tiers".

The CLI's `extract` writes to whatever path you give it; `repack` writes a new ZIP and never overwrites unless that ZIP is the destination you named. The extract / repack pair never mutates the input save.

## Round-trip caveats

- The Python ElementTree serializer re-emits XML more compactly than the game's serializer: identical content, smaller bytes. The game's loader accepts both shapes.
- Whitespace inside text nodes is preserved (`xml.etree` defaults). If a future version of Stationeers changes from text-node values to attribute values, the schema reference pages in `Research/Protocols/` should be updated and the parser adjusted.
- `terrain.dat` is binary and is copied through unchanged. This tool does NOT understand terrain edits; for that, see `Research/Protocols/TerrainDat.md` and the `terrain_reset.py` family of scripts.

## Examples for verification scenarios

**Battery efficiency loss (Power Grid Plus round 1, sub-check 6).**
This one does not need a save-edit; the `BatteryChargeEfficiency` value is a BepInEx config, not save data. Edit `<dedi>/install/BepInEx/config/net.powergridplus.cfg`, set `Battery Charge Efficiency = 0.5`, restart the dedi server, and observe the delta with PgpVerifyHelper's `battery-charge-snapshot` scenario.

**APC idle drain (Power Grid Plus round 1, sub-check 8).**
The Luna save has APCs. Clone an APC + a Battery onto an isolated cable network for a controlled test:

```
python tools/save-edit/stationeers_save.py list Luna.save --prefab StructureAreaPowerControl --limit 5
python tools/save-edit/stationeers_save.py show Luna.save --ref <one of those>
# pick a world position with empty cells nearby, then:
python tools/save-edit/stationeers_save.py clone Luna.save Luna-apc-test.save --ref <apc-ref> --pos X,Y,Z
```

**Cable burn under sustained overload (Power Grid Plus round 1, sub-check 2).**
Construct a single-network normal-cable spur with a known load:

```
# 1. Identify a normal-cable Thing to clone for the spur. Heavy/SuperHeavy networks burn at much higher thresholds; normal at 5 kW.
python tools/save-edit/stationeers_save.py list Luna.save --prefab StructureCableStraight --limit 5
# 2. Make a small network: clone a Cable to a fresh position, give it a new CableNetworkId:
python tools/save-edit/stationeers_save.py add-network Luna.save Luna-burn.save --id 999999
python tools/save-edit/stationeers_save.py clone Luna-burn.save Luna-burn.save --ref <cable-ref> --pos X,Y,Z
python tools/save-edit/stationeers_save.py set Luna-burn.save Luna-burn.save --ref <new-ref> --field CableNetworkId --value 999999
# 3. Repeat for the load (a high-draw device) and source. Wire all to network 999999.
```

The "wire all to network 999999" step is the hard part: connections in vanilla code resolve via `Cable.OnRegistered` which walks adjacent cells. For the agent to compose this from XML alone, the Things must be geometrically adjacent (cells 2 units apart on each axis, see `Research/Protocols/WorldXml.md` for the cell grid scale). Easier: have the developer build the seed scene once, then derive variants by editing only `OnOff` and `Setting` values.

This is why PgpVerifyHelper is the longer-term tooling target: spawning at runtime through the game's registration paths means the agent does not have to get the grid-adjacency math right.
