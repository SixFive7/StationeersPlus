# Patterns/Logic

Shared conventions, documentation, and code for the Stationeers `LogicType` enum across every SixFive7 mod.

The single source of truth for which `ushort` value is assigned to which custom `LogicType` lives in [`LogicTypeNumbers.cs`](LogicTypeNumbers.cs) in this folder. Each mod's `.csproj` links that file via:

```xml
<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />
```

so every mod's `LogicTypeRegistry.cs` references `StationeersPlus.Shared.LogicTypeNumbers.<Name>` instead of redeclaring a literal integer.

## Why this exists

`LogicType` is a vanilla `ushort` enum (0-65535). The vanilla game uses 0-349 densely. Modders extend the enum at runtime by patching `ProgrammableChip.AllConstants`, `Logicable.LogicTypes`, `EnumCollections.LogicTypes`, and `ScreenDropdownBase.LogicTypes`. The integer assigned to each new name is permanent: savegames serialise per-Thing logic overrides keyed by it, IC10 scripts compile against it, multiplayer sync depends on host and client agreeing on it. Renumbering breaks every save and every script that uses the name.

Without a single catalogue, two mods can collide on the same integer, with no compiler error and no runtime warning. The conflict only surfaces when both mods are loaded and a chip writes a value: the wrong device's slot updates, or a `CanLogicRead` patch silently shadows another. Centralising the assignment list here prevents that for mods in this monorepo and documents what to avoid for everything else.

## Assignment table

Append at the next free slot. Increments of 1. Compact packing - a mod may have gaps where other mods own intermediate values.

| Value | Mod | Name | Read / Write | Notes |
|---|---|---|---|---|
| 6571 | PowerTransmitterPlus | MicrowaveSourceDraw | R | Watts pulled from source cable network. |
| 6572 | PowerTransmitterPlus | MicrowaveDestinationDraw | R | Watts delivered to receiver's downstream network. |
| 6573 | PowerTransmitterPlus | MicrowaveTransmissionLoss | R | Source draw minus destination draw. |
| 6574 | PowerTransmitterPlus | MicrowaveEfficiency | R | Delivered / source, 0..1. |
| 6575 | PowerTransmitterPlus | MicrowaveAutoAimTarget | R / W | Target Thing.ReferenceId. Set 0 to disable. |
| 6576 | PowerTransmitterPlus | MicrowaveLinkedPartner | R | Linked partner's ReferenceId, or 0 when unlinked. |
| 6577 | PowerGridPlus | LogicPassthroughMode | R / W | 0 = vanilla logic-opaque transformer, 1 = logic-transparent. |
| 6578 | PowerGridPlus | Priority | R / W | Per-transformer dispatch priority (int >= 0, default 100). Strict-priority allocation: highest priority gets first dibs on input-network supply; lower-priority transformers get the leftover. A transformer that cannot get its full OutputMaximum from the input network sheds for 10 seconds. |
| 6579 | PowerGridPlus | Shedding | R | Returns 1 when the transformer is currently shed (browned out) by the strict-priority allocation, 0 otherwise. Read-only. Server-derived; replicated to clients. |

**Next free slot: 6580.**

## Rules for adding a new entry

1. Pick the next free integer in the table above (currently `6578`). Do not skip; do not pick from a "preferred band" - the catalogue is one flat list.
2. Add a `public const ushort` to [`LogicTypeNumbers.cs`](LogicTypeNumbers.cs) in the correct mod section. Append a new section header if it is the first entry for a mod.
3. Update the table above with the value, mod, name, read/write, and a one-line description.
4. Update the "Next free slot" line above.
5. In the consuming mod, the per-mod `LogicTypeRegistry.cs` declares its own `LogicType` and `CustomLogicType` records but reads the integer from `LogicTypeNumbers`:

   ```csharp
   using StationeersPlus.Shared;

   internal const ushort LogicPassthroughModeValue = LogicTypeNumbers.LogicPassthroughMode;
   internal static readonly LogicType LogicPassthroughMode = (LogicType)LogicPassthroughModeValue;
   ```

6. The mod's `.csproj` already has the `<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />` entry once the mod has joined this catalogue. A new mod adds it the first time it registers a `LogicType`.

7. Commit the `Patterns/Logic/` change and the mod change together so the catalogue and the implementation always match. The catalogue commit happens first; the mod's reference commit follows.

## Never bump an existing value

`LogicType` integers are baked into saves, IC10 source code (`s d0 LogicPassthroughMode 1`), and multiplayer state sync. A renumber from 6577 to 6578 silently breaks every existing save that wrote a value with the old number, every chip script that names the constant, and every joining client whose mod still uses the old integer. The vanilla game does not validate `LogicType` values against a registry; a stale integer becomes a phantom slot that no `CanLogicRead` claim covers, which usually surfaces as `0` reads everywhere.

Add new entries at the end. Never reorder, renumber, or reuse a retired entry's value. If a value is truly orphaned (a mod removed it), leave the row in the table marked `RETIRED` so the integer is never re-used.

## Known third-party reservations

A scan of 124 mods (top 90 most-subscribed Stationeers Workshop mods on `steamcommunity.com/workshop/browse?appid=544550` sorted by all-time most-subscribed, plus 34 additional mods this developer was subscribed to at scan time), run 2026-05-14 against game build 0.2.6228.27061, identified the third-party `LogicType` reservations below. Methodology in [Scan history](#scan-history) at the bottom of this page.

These bands are not under SixFive7 control. **Do not pick any integer listed below for a SixFive7 entry.**

### Vanilla Stationeers (0..349)

The `LogicType` enum in `Assembly-CSharp.dll`. Used densely; treat the entire 0-349 range as taken. Source of truth: the game's own assembly.

### Stationeers Logic Extended (1000..1830), 230 names

[Workshop ID 3625190467](https://steamcommunity.com/sharedfiles/filedetails/?id=3625190467) by ~ThunderDuck~. The mod registers 230 `LogicType` integers via reflection-append on `ProgrammableChip.AllConstants`, covering 35 device categories. Source of truth: the mod's own `Documentation/CustomLogicTypes.md` and `Data/SLE_LogicTypes.json` (ships inside the Workshop download).

**Per-device range summary:**

| Range | Device category | Count |
|---|---|---|
| 1000-1004 | ContactSelection (SatelliteDish) | 5 |
| 1010-1020 | ContactProperties (SatelliteDish) | 11 |
| 1030-1032 | DishState (SatelliteDish) | 3 |
| 1100-1103 | Centrifuge | 4 |
| 1110-1117 | RealtimeData (DaylightSensor) | 8 |
| 1120-1123 | WindTurbine | 4 |
| 1130 | SolidFuelGenerator | 1 |
| 1140-1141 | WeatherStation | 2 |
| 1150-1151 | DeepMiner | 2 |
| 1160-1169 | HydroponicsDevice | 10 |
| 1180-1189 | Harvester | 10 |
| 1200-1205 | GasFuelGenerator | 6 |
| 1210-1215 | Battery | 6 |
| 1220-1228 | SolarPanel | 9 |
| 1230-1233 | H2Combustor | 4 |
| 1240-1243 | Electrolyzer | 4 |
| 1250-1292 | PipeAnalyzer | 31 |
| 1300-1311 | Furnace | 12 |
| 1320-1323 | AdvancedFurnace | 4 |
| 1330-1333 | ArcFurnace | 4 |
| 1400-1416 | Filtration | 13 |
| 1500-1503 | AirConditioner | 4 |
| 1520-1523 | WallCooler | 4 |
| 1530-1534 | WallHeater | 5 |
| 1540-1542 | ActiveVent | 3 |
| 1600-1604 | StateChangeDevice | 5 |
| 1610-1615 | Fabricator | 6 |
| 1620-1627 | AdvancedComposter | 8 |
| 1700-1707 | RobotMining | 8 |
| 1720-1726 | Quarry | 7 |
| 1740-1745 | HorizontalQuarry | 6 |
| 1760-1764 | Recycler | 5 |
| 1780-1785 | StirlingEngine | 6 |
| 1800-1803 | RocketMiner | 4 |
| 1820-1824 | LandingPad | 5 |
| 1830 | APC | 1 |

There are gaps inside `1000-1830` (e.g. `1005-1009`, `1021-1029`, `1033-1099`, `1131-1139`, `1142-1149`, `1152-1159`, `1170-1179`, `1190-1199`, `1206-1209`, `1216-1219`, `1229`, `1234-1239`, `1244-1249`, `1293-1299`, `1312-1319`, `1324-1329`, `1334-1399`, `1417-1499`, `1504-1519`, `1524-1529`, `1535-1539`, `1543-1599`, `1605-1609`, `1616-1619`, `1628-1699`, `1708-1719`, `1727-1739`, `1746-1759`, `1765-1779`, `1786-1799`, `1804-1819`, `1825-1829`). **Do not park new SixFive7 entries in those gaps:** the mod author has clearly reserved the whole band by device category, and a future Stationeers Logic Extended release is likely to consume them.

**Full per-name catalogue** (230 entries, verbatim from the SLE DLL's `Register(new LogicTypeInfo(...))` calls):

| Value | Name | Access | Type | Category | Description |
|---|---|---|---|---|---|
| 1000 | ContactIndex | R/W | int | ContactSelection | Select contact by index (0-based) |
| 1001 | ContactCount | R | int | ContactSelection | Total visible contacts |
| 1002 | FilterMode | R/W | int | ContactSelection | Filter type: 0=All, 1=ShuttleType, 2=Resolved, 3=Unresolved, 4=Contacted, 5=NotContacted |
| 1003 | FilterValue | R/W | int | ContactSelection | Filter parameter value (e.g., ShuttleType when FilterMode=1) |
| 1004 | FilteredCount | R | int | ContactSelection | Count of contacts matching current filter |
| 1010 | ContactShuttleType | R | int | ContactProperties | ShuttleType enum: 0=None, 1=Small, 2=SmallGas, 3=Medium, 4=MediumGas, 5=Large, 6=LargeGas, 7=MediumPlane, 8=LargePlane |
| 1011 | ContactLifetime | R | float | ContactProperties | Seconds until contact leaves range |
| 1012 | ContactDegreeOffset | R | float | ContactProperties | Alignment angle in degrees (lower = better aligned) |
| 1013 | ContactResolved | R | bool | ContactProperties | 1 if contact is resolved, 0 if not |
| 1014 | ContactContacted | R | bool | ContactProperties | 1 if trader has been contacted, 0 if not |
| 1015 | ContactResolutionProgress | R | float | ContactProperties | Resolution progress 0.0-1.0 |
| 1016 | ContactMinWattsResolve | R | float | ContactProperties | Minimum watts required to resolve this contact |
| 1017 | ContactMinWattsContact | R | float | ContactProperties | Minimum watts required to contact this trader |
| 1018 | ContactSecondsToContact | R | float | ContactProperties | Seconds required to establish contact |
| 1019 | ContactTraderHash | R | int | ContactProperties | Trader type hash (same as game's ContactTypeId) |
| 1020 | ContactReferenceId | R | long | ContactProperties | Unique reference ID of contact |
| 1030 | DishWattageOnContact | R | float | DishState | Actual watts reaching selected contact |
| 1031 | DishIsInterrogating | R | bool | DishState | 1 if dish is currently interrogating a contact |
| 1032 | DishInterrogatingId | R | long | DishState | ReferenceId of contact being interrogated, 0 if none |
| 1100 | CentrifugeProcessing | R | int | Centrifuge | Processing progress 0-100%. Works on Centrifuge and CombustionCentrifuge |
| 1101 | CentrifugeRPM | R | float | Centrifuge | Current centrifuge RPM |
| 1102 | CentrifugeReagentTotal | R | float | Centrifuge | Total reagent quantity in centrifuge |
| 1103 | CentrifugeLidClosed | R | bool | Centrifuge | 1 if lid is closed, 0 if open |
| 1110 | TimeOfDay | R | float | RealtimeData | Time of day 0-1 (0=sunrise, 0.25=noon, 0.5=sunset, 0.75=midnight) |
| 1111 | IsEclipse | R | bool | RealtimeData | 1 if eclipse is occurring, 0 if not |
| 1112 | EclipseRatio | R | float | RealtimeData | Eclipse intensity 0.0-1.0 (0=no eclipse, 1=full eclipse) |
| 1113 | DaysPast | R | int | RealtimeData | Number of days since world creation |
| 1114 | DayLengthSeconds | R | int | RealtimeData | Length of a day in seconds |
| 1115 | Latitude | R | float | RealtimeData | World latitude in degrees |
| 1116 | Longitude | R | float | RealtimeData | World longitude in degrees |
| 1117 | WeatherSolarRatio | R | float | RealtimeData | Weather solar ratio 0-1 (1=full sun, lower during storms) |
| 1120 | WindSpeed | R | float | WindTurbine | Current global wind strength 0-1 |
| 1121 | MaxPower | R | float | WindTurbine | Current max power output (storm-aware) |
| 1122 | TurbineSpeed | R | float | WindTurbine | Current turbine blade rotation speed 0-1 |
| 1123 | AtmosphericPressure | R | float | WindTurbine | Clamped atmospheric pressure in kPa |
| 1130 | FuelTicks | R | int | SolidFuelGenerator | Fuel buffer remaining in ticks |
| 1140 | WeatherWindStrength | R | float | WeatherStation | Current weather event wind strength 0-1 |
| 1141 | DaysSinceLastWeatherEvent | R | float | WeatherStation | Days since last weather event ended |
| 1150 | MiningProgress | R | float | DeepMiner | Mining cycle progress 0-100% |
| 1151 | CurrentOreHash | R | int | DeepMiner | Hash of current ore type in export slot |
| 1160 | LightExposure | R | float | HydroponicsDevice | Current light exposure level (grow light + solar) |
| 1161 | IsLitByGrowLight | R | bool | HydroponicsDevice | 1 if lit by a powered grow light, 0 if not |
| 1162 | WaterMoles | R | float | HydroponicsDevice | Water amount in internal atmosphere (moles) |
| 1163 | PlantIsFertilized | R | bool | HydroponicsDevice | 1 if plant has been fertilized, 0 if not |
| 1164 | PlantGrowthEfficiency | R | int | HydroponicsDevice | Plant overall growth efficiency 0-100% |
| 1165 | BreathingEfficiency | R | int | HydroponicsDevice | Plant breathing/gas efficiency 0-100% |
| 1166 | TemperatureEfficiency | R | int | HydroponicsDevice | Plant temperature efficiency 0-100% |
| 1167 | PlantLightEfficiency | R | int | HydroponicsDevice | Plant light efficiency 0-100% |
| 1168 | PlantPressureEfficiency | R | int | HydroponicsDevice | Plant pressure efficiency 0-100% |
| 1169 | HydrationEfficiency | R | int | HydroponicsDevice | Plant hydration/water efficiency 0-100% |
| 1180 | HasTray | R | bool | Harvester | 1 if harvester is positioned over a hydroponics tray, 0 if not |
| 1181 | IsHarvesting | R | bool | Harvester | 1 if currently performing harvest operation, 0 if not |
| 1182 | IsPlanting | R | bool | Harvester | 1 if currently performing plant operation, 0 if not |
| 1183 | ArmState | R | int | Harvester | Arm state: 0=Idle, 1=Planting, 2=Harvesting |
| 1184 | HasImportPlant | R | bool | Harvester | 1 if plant/seed is ready in import slot, 0 if not |
| 1185 | ImportPlantHash | R | int | Harvester | PrefabHash of plant/seed in import slot |
| 1186 | HasFertilizer | R | bool | Harvester | 1 if fertilizer is in tray's fertilizer slot, 0 if not |
| 1187 | FertilizerCycles | R | float | Harvester | Remaining fertilizer cycles |
| 1188 | FertilizerHarvestBoost | R | float | Harvester | Fertilizer harvest yield multiplier |
| 1189 | FertilizerGrowthSpeed | R | float | Harvester | Fertilizer growth speed multiplier |
| 1200 | CombustionEnergy | R | float | GasFuelGenerator | Combustion energy produced (before 17% efficiency conversion) |
| 1201 | IsValidAtmosphere | R | bool | GasFuelGenerator | 1 if atmosphere meets pressure/temp requirements, 0 if not |
| 1202 | DoShutdown | R | bool | GasFuelGenerator | 1 if conditions will trigger shutdown, 0 if not |
| 1203 | MinTemperature | R | float | GasFuelGenerator | Minimum operating temperature in Kelvin |
| 1204 | MaxTemperature | R | float | GasFuelGenerator | Maximum operating temperature in Kelvin |
| 1205 | MinPressure | R | float | GasFuelGenerator | Minimum operating pressure in Pa |
| 1210 | PowerDelta | R | float | Battery | Power deficit (PowerStored - PowerMaximum). Negative when not full |
| 1211 | BatteryIsSubmerged | R | bool | Battery | 1 if battery is submerged in liquid (short circuit risk), 0 if not |
| 1212 | InputSubmergedTicks | R | int | Battery | Number of ticks input connection has been submerged |
| 1213 | OutputSubmergedTicks | R | int | Battery | Number of ticks output connection has been submerged |
| 1214 | BatteryIsEmpty | R | bool | Battery | 1 if battery is empty (Mode == 0), 0 if not |
| 1215 | BatteryIsCharged | R | bool | Battery | 1 if battery is fully charged (Mode == 6), 0 if not |
| 1220 | SolarVisibility | R | float | SolarPanel | Sun visibility factor 0-1 (affected by obstructions) |
| 1221 | SolarDamageRatio | R | float | SolarPanel | Damage ratio 0-1 (0=undamaged, 1=fully damaged) |
| 1222 | SolarDamageTotal | R | float | SolarPanel | Total damage points accumulated |
| 1223 | SolarHealth | R | int | SolarPanel | Current health as percentage 0-100 |
| 1224 | SolarEfficiency | R | int | SolarPanel | Current efficiency as percentage 0-100 (includes damage) |
| 1225 | SolarIsOperable | R | bool | SolarPanel | 1 if panel is operable, 0 if not |
| 1226 | SolarIsBroken | R | bool | SolarPanel | 1 if panel is broken, 0 if not |
| 1227 | SolarMovementSpeedH | R | float | SolarPanel | Horizontal rotation speed in degrees/sec |
| 1228 | SolarMovementSpeedV | R | float | SolarPanel | Vertical rotation speed in degrees/sec |
| 1230 | H2CombustorProcessedMoles | R | float | H2Combustor | Moles processed this atmospheric tick |
| 1231 | H2CombustorUsedPower | R | float | H2Combustor | Power consumed this tick (3600W active, 50W idle) |
| 1232 | H2CombustorIsOperable | R | bool | H2Combustor | 1 if device is operable (connections valid), 0 if not |
| 1233 | H2CombustorCodeError | R | int | H2Combustor | IC chip error state code |
| 1240 | ElectrolyzerProcessedMoles | R | float | Electrolyzer | Moles processed this atmospheric tick |
| 1241 | ElectrolyzerUsedPower | R | float | Electrolyzer | Power consumed this tick (3600W active, 50W idle) |
| 1242 | ElectrolyzerIsOperable | R | bool | Electrolyzer | 1 if device is operable (connections valid), 0 if not |
| 1243 | ElectrolyzerCodeError | R | int | Electrolyzer | IC chip error state code |
| 1250 | PartialPressureO2 | R | float | PipeAnalyzer | Partial pressure of Oxygen in kPa |
| 1251 | PartialPressureCO2 | R | float | PipeAnalyzer | Partial pressure of Carbon Dioxide in kPa |
| 1252 | PartialPressureN2 | R | float | PipeAnalyzer | Partial pressure of Nitrogen in kPa |
| 1253 | PartialPressureVolatiles | R | float | PipeAnalyzer | Partial pressure of Volatiles in kPa |
| 1254 | PartialPressureN2O | R | float | PipeAnalyzer | Partial pressure of Nitrous Oxide in kPa |
| 1255 | PartialPressurePollutant | R | float | PipeAnalyzer | Partial pressure of Pollutant in kPa |
| 1256 | PartialPressureH2 | R | float | PipeAnalyzer | Partial pressure of Hydrogen in kPa |
| 1257 | PartialPressureSteam | R | float | PipeAnalyzer | Partial pressure of Steam/Water in kPa |
| 1258 | PartialPressureToxins | R | float | PipeAnalyzer | Partial pressure of Toxins in kPa |
| 1260 | TotalMolesGasses | R | float | PipeAnalyzer | Total moles of gases only (excludes liquids) |
| 1261 | TotalMolesLiquids | R | float | PipeAnalyzer | Total moles of liquids only (excludes gases) |
| 1262 | LiquidVolumeRatio | R | float | PipeAnalyzer | Ratio of liquid volume to total volume 0-1 |
| 1263 | PressureGasses | R | float | PipeAnalyzer | Gas-only pressure in kPa (excludes liquid pressure) |
| 1264 | LiquidPressureOffset | R | float | PipeAnalyzer | Liquid pressure offset in kPa |
| 1270 | PipeCombustionEnergy | R | float | PipeAnalyzer | Energy from combustion reactions in joules |
| 1271 | CleanBurnRate | R | float | PipeAnalyzer | Clean burn rate/efficiency 0-1 |
| 1272 | Inflamed | R | bool | PipeAnalyzer | 1 if atmosphere is inflamed/on fire, 0 if not |
| 1273 | Suppressed | R | int | PipeAnalyzer | Fire suppression ticks remaining |
| 1280 | EnergyConvected | R | float | PipeAnalyzer | Heat energy being convected in watts |
| 1281 | EnergyRadiated | R | float | PipeAnalyzer | Heat energy being radiated in watts |
| 1282 | IsAboveArmstrong | R | bool | PipeAnalyzer | 1 if pressure is above Armstrong limit (6.3kPa), 0 if not |
| 1283 | Condensation | R | bool | PipeAnalyzer | 1 if condensation is occurring, 0 if not |
| 1284 | NetworkContentType | R | int | PipeAnalyzer | Network content type: 0=Gas, 1=Liquid |
| 1285 | PipeMaxPressure | R | float | PipeAnalyzer | Maximum burst pressure for connected pipe type in kPa |
| 1286 | PipeStressRatio | R | float | PipeAnalyzer | Current pressure / max pressure ratio (0-1+, >=0.8 = stressed) |
| 1287 | PipeIsStressed | R | bool | PipeAnalyzer | 1 if pipe is stressed (high pressure OR liquid in gas pipe), 0 if not |
| 1288 | PipeDamageRatio | R | float | PipeAnalyzer | Pipe damage ratio 0-1 (1 = burst/destroyed) |
| 1289 | PipeIsBurst | R | bool | PipeAnalyzer | 1 if pipe has burst, 0 if intact |
| 1290 | PipeDamageType | R | int | PipeAnalyzer | Damage type bitmask: 1=Pressure, 2=Liquid, 4=Frozen |
| 1291 | NetworkHasFault | R | bool | PipeAnalyzer | 1 if any pipe in network has a fault, 0 if not |
| 1292 | NetworkWorstDamage | R | float | PipeAnalyzer | Highest damage ratio of any pipe in the network 0-1 |
| 1300 | FurnaceTemperature | R | float | Furnace | Internal furnace temperature in Kelvin |
| 1301 | FurnacePressure | R | float | Furnace | Internal furnace pressure in kPa |
| 1302 | FurnaceTotalMoles | R | float | Furnace | Total moles of gas inside furnace |
| 1303 | FurnaceInflamed | R | bool | Furnace | 1 if furnace is actively burning/smelting, 0 if not |
| 1304 | FurnaceMode | R | bool | Furnace | 1 if valid recipe detected, 0 if not |
| 1305 | ReagentQuantity | R | float | Furnace | Total quantity of minerals loaded in furnace |
| 1306 | FurnaceOverpressure | R | bool | Furnace | 1 if furnace is in dangerous overpressure state, 0 if not |
| 1307 | CurrentRecipeEnergy | R | float | Furnace | Energy required to complete current recipe |
| 1308 | FurnaceStressed | R | bool | Furnace | 1 if pressure is at warning level (66% of max), 0 if not |
| 1309 | FurnaceHasBlown | R | bool | Furnace | 1 if furnace has already exploded, 0 if not |
| 1310 | FurnaceMaxPressure | R | float | Furnace | Maximum pressure differential before explosion (60795 kPa) |
| 1311 | FurnaceVolume | R | float | Furnace | Internal furnace volume in litres |
| 1320 | MinSettingInput | R | float | AdvancedFurnace | Minimum input setting value |
| 1321 | MaxSettingInput | R | float | AdvancedFurnace | Maximum input setting value |
| 1322 | MinSettingOutput | R | float | AdvancedFurnace | Minimum output setting value |
| 1323 | MaxSettingOutput | R | float | AdvancedFurnace | Maximum output setting value |
| 1330 | ArcFurnaceActivate | R | int | ArcFurnace | Arc furnace activate state: 0=idle, 1=smelting |
| 1331 | ImportStackSize | R | int | ArcFurnace | Quantity of items in import slot |
| 1332 | SmeltingPower | R | float | ArcFurnace | Power being consumed for smelting this tick |
| 1333 | ArcFurnaceIsSmelting | R | bool | ArcFurnace | 1 if arc furnace is actively smelting, 0 if not |
| 1400 | FilterSlotIndex | R/W | int | Filtration | Select filter slot by index (0-based) |
| 1401 | FilterSlotCount | R | int | Filtration | Total number of gas filter slots |
| 1402 | HasEmptyFilter | R | bool | Filtration | 1 if any filter is empty, 0 if all have charge |
| 1403 | IsFullyConnected | R | bool | Filtration | 1 if all pipe networks are connected, 0 if not |
| 1404 | FilterPowerUsed | R | float | Filtration | Power consumed during filtration this tick |
| 1405 | FiltrationProcessedMoles | R | float | Filtration | Moles processed during this atmospheric tick |
| 1410 | FilterQuantity | R | float | Filtration | Filter charge remaining 0.0-1.0 |
| 1411 | FilterIsLow | R | bool | Filtration | 1 if filter charge is low (<=5%), 0 if not |
| 1412 | FilterIsEmpty | R | bool | Filtration | 1 if filter is completely empty, 0 if not |
| 1413 | FilterTypeHash | R | int | Filtration | Chemistry.GasType hash of filtered gas |
| 1414 | FilterLife | R | int | Filtration | Filter life tier: 0=Normal, 1=Medium, 2=Large, 3=SuperHeavy |
| 1415 | FilterUsedTicks | R | int | Filtration | Ticks used since last degradation |
| 1416 | FilterMaxTicks | R | int | Filtration | Max ticks before degradation based on filter life |
| 1500 | ACEnergyMoved | R | float | AirConditioner | Joules of thermal energy moved this tick |
| 1501 | ACIsFullyConnected | R | bool | AirConditioner | 1 if both pipe networks are connected, 0 if not |
| 1502 | ACPowerUsed | R | float | AirConditioner | Power consumed during operation this tick |
| 1503 | ACEfficiency | R | float | AirConditioner | Current cooling/heating efficiency 0-1 |
| 1520 | CoolerIsEnvironmentOkay | R | bool | WallCooler | 1 if room environment is valid for operation, 0 if not |
| 1521 | CoolerIsPipeOkay | R | bool | WallCooler | 1 if pipe environment is valid for operation, 0 if not |
| 1522 | CoolerPowerUsed | R | float | WallCooler | Power consumed during cooling this tick |
| 1523 | CoolerEnergyMoved | R | float | WallCooler | Joules of thermal energy moved this tick |
| 1530 | HeaterIsEnvironmentOkay | R | bool | WallHeater | 1 if room environment is valid for operation, 0 if not |
| 1531 | HeaterIsPipeOkay | R | bool | WallHeater | 1 if pipe environment is valid for operation, 0 if not |
| 1532 | HeaterPowerUsed | R | float | WallHeater | Power consumed during heating this tick |
| 1533 | HeaterEnergyTransfer | R | float | WallHeater | Joules of thermal energy transferred per tick |
| 1534 | HeaterMaxTemperature | R | float | WallHeater | Maximum temperature limit in Kelvin (hardcoded 850K) |
| 1540 | VentFlowStatus | R | int | ActiveVent | Flow indicator: 0=Idle, 1=InwardOff, 2=InwardOn, 3=OutwardOff, 4=OutwardOn, 5=BlockedIn, 6=BlockedOut |
| 1541 | VentIsConnected | R | bool | ActiveVent | 1 if vent has valid atmosphere connection, 0 if not |
| 1542 | VentPowerUsed | R | float | ActiveVent | Power consumed during operation this tick |
| 1600 | ChamberEnergyTransfer | R | float | StateChangeDevice | Heat energy transfer rate in joules/tick |
| 1601 | ChamberIsOperable | R | bool | StateChangeDevice | 1 if device is operable (structure complete, powered), 0 if not |
| 1602 | ChamberVolume | R | float | StateChangeDevice | Internal chamber volume in litres |
| 1603 | ChamberHeatExchangeRatio | R | float | StateChangeDevice | Heat exchange efficiency 0-1 (based on pressures) |
| 1604 | ChamberLiquidRatio | R | float | StateChangeDevice | Liquid volume ratio 0-1 in internal atmosphere |
| 1610 | FabricatorCurrentIndex | R | int | Fabricator | Index of currently selected recipe (0 to RecipeCount-1) |
| 1611 | FabricatorRecipeCount | R | int | Fabricator | Total number of recipes available at current tier |
| 1612 | FabricatorCurrentTier | R | int | Fabricator | Current machine tier (0=basic, 1=tier1, 2=tier2) |
| 1613 | FabricatorTimeMultiplier | R | float | Fabricator | Build time multiplier for current tier |
| 1614 | FabricatorPowerUsed | R | float | Fabricator | Power consumed during production this tick |
| 1615 | FabricatorMakingIndex | R | int | Fabricator | Index of recipe currently being fabricated |
| 1620 | ComposterGrindProgress | R | float | AdvancedComposter | Grinding progress for current item (0 to 1.5 seconds) |
| 1621 | ComposterBatchProgress | R | float | AdvancedComposter | Batch processing time (0 to 60 seconds until fertilizer) |
| 1622 | ComposterDecayCount | R | int | AdvancedComposter | Count of items that boost GrowthSpeed |
| 1623 | ComposterNormalCount | R | int | AdvancedComposter | Count of items that boost HarvestQuantity |
| 1624 | ComposterBiomassCount | R | int | AdvancedComposter | Count of items that boost GrowthCycles |
| 1625 | ComposterPowerUsed | R | float | AdvancedComposter | Power consumed during processing this tick |
| 1626 | ComposterCanProcess | R | bool | AdvancedComposter | 1 if ready to process (3+ items), 0 if not |
| 1627 | ComposterIsOperable | R | bool | AdvancedComposter | 1 if device is operable (input connected), 0 if not |
| 1700 | RobotIsStorageEmpty | R | bool | RobotMining | 1 if robot storage is empty, 0 if not |
| 1701 | RobotIsStorageFull | R | bool | RobotMining | 1 if robot storage is full, 0 if not |
| 1702 | RobotIsOperable | R | bool | RobotMining | 1 if robot is operable (chip valid, no errors), 0 if not |
| 1703 | RobotBatteryRatio | R | float | RobotMining | Battery charge ratio 0-1 (current/max) |
| 1704 | RobotIsBusy | R | bool | RobotMining | 1 if robot is busy pathfinding/working, 0 if idle |
| 1705 | RobotDamageRatio | R | float | RobotMining | Robot damage ratio 0-1 (0=undamaged, 1=destroyed) |
| 1706 | RobotStorageCount | R | int | RobotMining | Number of ore slots currently filled |
| 1707 | RobotStorageCapacity | R | int | RobotMining | Total ore storage slot capacity |
| 1720 | QuarryDrillState | R | int | Quarry | Drill state: 0=Idle, 1=Drilling, 2=Transporting, 3=Delivering |
| 1721 | QuarryOreCount | R | int | Quarry | Number of ore items mined and ready for export |
| 1722 | QuarryDepth | R | float | Quarry | Current drill depth in voxels |
| 1723 | QuarryMaxDepth | R | float | Quarry | Maximum drill depth reached |
| 1724 | QuarryIsDrillFinished | R | bool | Quarry | 1 if drill has finished mining, 0 if not |
| 1725 | QuarryIsTransporting | R | bool | Quarry | 1 if transporting ore to output, 0 if not |
| 1726 | QuarryIsDelivering | R | bool | Quarry | 1 if delivering ore to chute, 0 if not |
| 1740 | OgreState | R | int | HorizontalQuarry | State: 0=Idle, 1=Mining, 2=Returning, 3=Delivering, 4=AwaitingExport |
| 1741 | OgreOreCount | R | int | HorizontalQuarry | Number of ore items mined and ready for export |
| 1742 | OgrePosition | R | float | HorizontalQuarry | Current horizontal position along mining path |
| 1743 | OgreMiningComplete | R | bool | HorizontalQuarry | 1 if mining path complete, 0 if mining in progress |
| 1744 | OgreIsReturning | R | bool | HorizontalQuarry | 1 if ogre is returning to start position, 0 if not |
| 1745 | OgreQueueFull | R | bool | HorizontalQuarry | 1 if ore queue is full, 0 if not |
| 1760 | RecyclerReagentTotal | R | float | Recycler | Total reagent quantity from all processed items |
| 1761 | RecyclerIsExporting | R | bool | Recycler | 1 if currently exporting reagents, 0 if not |
| 1762 | RecyclerAtCapacity | R | bool | Recycler | 1 if output is at capacity, 0 if not |
| 1763 | RecyclerIdleTicks | R | int | Recycler | Number of ticks spent idle |
| 1764 | RecyclerIsProcessing | R | bool | Recycler | 1 if device is currently processing an item, 0 if not |
| 1780 | StirlingHotTemperature | R | float | StirlingEngine | Hot side (input) temperature in Kelvin |
| 1781 | StirlingColdTemperature | R | float | StirlingEngine | Cold side (output) temperature in Kelvin |
| 1782 | StirlingTemperatureDelta | R | float | StirlingEngine | Temperature differential between hot and cold sides |
| 1783 | StirlingEfficiency | R | float | StirlingEngine | Current operating efficiency 0-1 |
| 1784 | StirlingMaxPower | R | float | StirlingEngine | Maximum power output at current conditions in watts |
| 1785 | StirlingIsConnected | R | bool | StirlingEngine | 1 if both atmospheres are connected, 0 if not |
| 1800 | RocketMiningProgress | R | float | RocketMiner | Current mining progress 0-1 |
| 1801 | RocketNextOreHash | R | int | RocketMiner | Hash of next ore type to be produced |
| 1802 | RocketMiningQuantity | R | int | RocketMiner | Quantity of ore mined this cycle |
| 1803 | RocketIsMining | R | bool | RocketMiner | 1 if rocket miner is actively mining, 0 if not |
| 1820 | PadContactStatus | R | int | LandingPad | Contact status: 0=NoContact, 1=Approaching, 2=WaitingApproach, 3=WaitingDoors, 4=Landed |
| 1821 | PadIsTraderReady | R | bool | LandingPad | 1 if trader is ready to trade, 0 if not |
| 1822 | PadHasContact | R | bool | LandingPad | 1 if pad has an active contact, 0 if not |
| 1823 | PadIsLocked | R | bool | LandingPad | 1 if pad is locked, 0 if not |
| 1824 | PadWaypointHeight | R | float | LandingPad | Virtual waypoint height setting (0-50) |
| 1830 | APCMaximumPower | R | float | APC | Total maximum power (Battery + Network capacity) |

### IC10 Inspector/Debugger (500..502), soft reservation, NOT registered

[Workshop ID 3508602436](https://steamcommunity.com/sharedfiles/filedetails/?id=3508602436). The mod uses `(LogicType)500`, `(LogicType)501`, `(LogicType)502` as `private const LogicType` constants and reads/writes them via the standard `Logicable` / `ISetable` interfaces. It does NOT register the integers with `ProgrammableChip.AllConstants`, `EnumCollections.LogicTypes`, or any other game registry. The values are invisible to IC10 chips and to other mods that enumerate the registry, but they ARE serialized into per-Thing save-game state.

Treat 500-502 as reserved. A future SixFive7 mod that picked 500/501/502 would not collide at the registry level (IC10 Inspector/Debugger never claimed those slots), but would collide at the save-game level on any installation where IC10 Inspector/Debugger has touched a device.

| Value | Name (in mod source) | Use |
|---|---|---|
| 500 | LOGIC_TYPE / SETDEVICE_LOGIC_TYPE | Primary debugger logic slot, also reused for "set device by reference id" |
| 501 | SETSELDEVICE_LOGIC_TYPE | Selected-device command channel |
| 502 | COMMAND_LOGIC_TYPE | Generic command channel |

### Adjacent ushort enums (not LogicType, but worth noting)

The reflection-append pattern that mods use against `EnumCollections.LogicTypes` also applies to sibling ushort enums in the same family. The scan flagged one such case:

| Workshop ID | Mod | Enum | Value | Name |
|---|---|---|---|---|
| 3457324551 | [FPGA](https://steamcommunity.com/sharedfiles/filedetails/?id=3457324551) | `EnumCollections.SlotClasses` | 105 | FPGAChip |

This is a `Class` (item-class) registration, not a `LogicType` registration. It is listed here so that any future SixFive7 mod extending `SlotClasses` (or related sibling enums) avoids 105 by default.

## Scan history

| Date | Game version | Mods scanned | Confirmed `LogicType` registrars | Method |
|---|---|---|---|---|
| 2026-05-14 | 0.2.6228.27061 | 124 | 2 (Stationeers Logic Extended, PowerTransmitterPlus) plus 1 numeric-cast-only mod (IC10 Inspector/Debugger 500-502) and 1 adjacent-enum registrar (FPGA SlotClasses 105). Of 124 scanned, 120 only consume vanilla LogicTypes (0-349) and add nothing. | Steam Workshop browsed via Playwright MCP, sorted by all-time most-subscribed; top 90 IDs captured. Each fetched via `steamcmd +login anonymous +workshop_download_item 544550 <id>`; for IDs where anonymous fetch yielded only a stub, the developer's own subscribed copy under `E:\Steam\steamapps\workshop\content\544550\<id>` was used instead. Plus 34 additional subscribed mods outside the top 90. DLLs were scanned for the four canonical registration markers (`AllConstants`, `InternalEnums`, `EnumCollections.LogicTypes`, `ScriptEnum<LogicType>`) using both ASCII (for type/method names in the `#Strings` heap) and UTF-16 LE (for string literals in the `#US` heap). DLLs that hit any marker were decompiled with `ilspycmd 10.0.0.8330` and inspected for whether they actually mutate the registry (registrar) or merely read from it (consumer). |

The scan covered the most-subscribed end of the Workshop and the developer's working mod set. Mods outside that pool that register new LogicTypes will not appear in the reservations above. Re-run the scan periodically (or before reserving a new band high in the range) and append a row here.

## Discovery for agents

The root `CLAUDE.md` "Workflow: shared patterns under `Patterns/`" section points here. Agents touching any `LogicType`-related code, any `LogicTypeRegistry.cs`, or any new logic extension MUST read this file before assigning a number.
