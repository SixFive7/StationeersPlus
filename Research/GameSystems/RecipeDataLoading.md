---
title: RecipeData loading and GameData XML overlay
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-15
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:58351-58364 (RecipeData class)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:58394-58449 (GameData class, recipe list fields)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:59145-59267 (LoadDataFiles / LoadDataFilesAtPath)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:59281-59518 (LoadXmlFileData - per-fabricator dispatch)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:212779-212803 (DynamicThingRecipeComparable.AddRecipe)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:215088-215104 (IQuantityRecipeComparable.AddRecipe)
  - .work/revolt-source/Assets/GameData/electronics.xml (Re-Volt real-world usage example)
related:
  - ../GameClasses/MultiMergeConstructor.md
tags: [prefab, harmony]
---

## RecipeData class

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

Defined inside `WorldManager` at line 58351:

```csharp
[XmlRoot]
public class RecipeData
{
    [XmlElement]
    public string PrefabName;

    [XmlElement]
    public Recipe Recipe;

    [XmlElement]
    public MachineTier RecipeTier;

    [XmlElement]
    public float Output = 1f;
}
```

`Recipe` is a large value-type struct (`public struct Recipe : IEquatable<Recipe>` at line 134855) whose fields map directly to reagent names as XML element names (Iron, Copper, Silicon, Solder, etc.). `Time` and `Energy` are also fields on `Recipe`. The XML deserializer matches element names case-insensitively to the struct's public fields, so `<Iron>15</Iron>` sets `recipe.Iron = 15`.

`RecipeTier` is `MachineTier` enum. When set, `DynamicThingRecipeComparable.AddRecipe` writes it back to the prefab's `RecipeTier` field. When absent (default `MachineTier.Undefined`) the existing prefab tier is preserved.

`Output` defaults to 1. Used by `IQuantityRecipeComparable` (furnace ingot outputs) to scale the output quantity.

## GameData class - all recipe list fields

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
[XmlRoot]
public class GameData
{
    public List<RecipeData> CentrifugeRecipes       = new List<RecipeData>();
    public List<RecipeData> FurnaceRecipes          = new List<RecipeData>();
    public List<RecipeData> AdvancedFurnaceRecipes  = new List<RecipeData>();
    public List<RecipeData> ArcFurnaceRecipes       = new List<RecipeData>();
    public List<RecipeData> MicrowaveRecipes        = new List<RecipeData>();
    public List<RecipeData> PackagingMachineRecipes = new List<RecipeData>();
    public List<RecipeData> AutolatheRecipes        = new List<RecipeData>();
    public List<RecipeData> AutomatedOvenRecipes    = new List<RecipeData>();
    public List<RecipeData> ElectronicsPrinterRecipes = new List<RecipeData>();
    public List<RecipeData> SecurityPrinterRecipes  = new List<RecipeData>();
    public List<RecipeData> RocketManufactoryRecipes = new List<RecipeData>();
    public List<RecipeData> HydraulicPipeBenderRecipes = new List<RecipeData>();
    public List<RecipeData> ToolManufactoryRecipes  = new List<RecipeData>();
    public List<RecipeData> ChemistryRecipes        = new List<RecipeData>();
    public List<RecipeData> IngotRecipes            = new List<RecipeData>();
    public List<RecipeData> RecycleRecipes          = new List<RecipeData>();
    public List<RecipeData> TerraformingManufactoryRecipes = new List<RecipeData>();
    public List<RecipeData> PaintMixRecipes         = new List<RecipeData>();
    // plus non-recipe lists: WorldSettings, Traders, SpaceMaps, ThingMods, etc.
}
```

The XML root element is `<GameData>`. Each recipe list is a direct child list element whose item tag matches `RecipeData` (the class name). Example: `<AutolatheRecipes><RecipeData>...</RecipeData></AutolatheRecipes>`.

## Load pipeline

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

Entry point: `WorldManager.LoadGameDataAsync()` (line 58982) calls:

```csharp
BeforeLoadDataFiles();   // clears _blueprintsToGenerate, DataResolver
LoadDataFiles();
AfterLoadDataFiles();    // DataResolver.ResolveAll()
// ...
OnGameDataLoaded?.Invoke();
```

`LoadDataFiles()` at line 59145:

```csharp
private static void LoadDataFiles()
{
    LoadDataFilesAtPath(Path.Combine(Application.streamingAssetsPath, "Worlds"));
    LoadDataFilesAtPath(Path.Combine(Application.streamingAssetsPath, "Data"));
    foreach (ModData mod in WorkshopMenu.ModsConfig.Mods)
    {
        if (mod.Enabled && !(mod is CoreModData))
        {
            LoadDataFilesAtPath(Path.Combine(mod.DirectoryPath, "GameData"));
        }
    }
}
```

Key facts:
- Vanilla data loads first (streamingAssets Worlds, then Data).
- Every enabled non-core mod's `GameData/` subfolder is then iterated in mod-list order.
- `LoadDataFilesAtPath` calls `Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories)`, so any `.xml` file anywhere under `GameData/` is picked up.
- Each XML file is processed in two passes: `LoadXmlFileDataFirstPass` (handles `CustomThingData` only), then `LoadXmlFileData` (handles all recipe lists, world settings, traders, etc.).

`LoadDataFilesAtPath` at line 59223:

```csharp
private static void LoadDataFilesAtPath(string path)
{
    if (!Directory.Exists(path)) return;
    string[] files = Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories);
    // First pass: CustomThingData only
    foreach (string text in files)
    {
        if (!text.ToLower().Contains("gamedata\\language"))
            LoadXmlFileDataFirstPass(Serializers.GameData, text);
    }
    // Second pass: all recipe lists + everything else
    foreach (string text2 in files)
    {
        if (!text2.ToLower().Contains("gamedata\\language"))
            LoadXmlFileData(Serializers.GameData, text2);
    }
    if (Directory.Exists(path + "\\Language"))
        Localization.ProcessNewPages(Settings.CurrentData.LanguageCode);
}
```

Files matching `gamedata\language` (case-insensitive) are skipped in the recipe passes and handled by the localization system instead.

## LoadXmlFileData: per-fabricator recipe dispatch

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`LoadXmlFileData` (line 59281) deserializes the file into a `GameData` object, then calls `<Fabricator>.RecipeComparable.AddRecipe(recipeData, modAbout)` for each list. Abbreviated mapping (all confirmed at lines 59297-59393):

| XML list element | Fabricator class | RecipeComparable type |
|---|---|---|
| `AutolatheRecipes` | `Autolathe` | `DynamicThingRecipeComparable` |
| `ToolManufactoryRecipes` | `ToolManufactory` | `DynamicThingRecipeComparable` |
| `ElectronicsPrinterRecipes` | `ElectronicsPrinter` | `DynamicThingRecipeComparable` |
| `FurnaceRecipes` | `Furnace` + `AdvancedFurnace` | `DynamicThingRecipeComparable` |
| `AdvancedFurnaceRecipes` | `AdvancedFurnace` | `DynamicThingRecipeComparable` |
| `ArcFurnaceRecipes` | `ArcFurnace` | `DynamicThingRecipeComparable` |
| `MicrowaveRecipes` | `Microwave` | `DynamicThingRecipeComparable` |
| `CentrifugeRecipes` | `Centrifuge` | `DynamicThingRecipeComparable` |
| `AutomatedOvenRecipes` | `AutomatedOven` | `DynamicThingRecipeComparable` |
| `PackagingMachineRecipes` | `BasicPackagingMachine` + `AdvancedPackagingMachine` | `DynamicThingRecipeComparable` |
| `SecurityPrinterRecipes` | `SecurityPrinter` | `DynamicThingRecipeComparable` |
| `RocketManufactoryRecipes` | `RocketManufactory` | `DynamicThingRecipeComparable` |
| `TerraformingManufactoryRecipes` | `TerraformingManufactory` | `DynamicThingRecipeComparable` |
| `ChemistryRecipes` | `ChemistryStation` | `DynamicThingRecipeComparable` |
| `HydraulicPipeBenderRecipes` | `HydraulicPipeBender` | `DynamicThingRecipeComparable` |
| `PaintMixRecipes` | `PaintMixer` | `DynamicThingRecipeComparable` |
| `IngotRecipes` | `Ingot` | `IngotRecipeComparable` |
| `RecycleRecipes` | `Recycler` (direct) | n/a |
| `ReagentGrinderRecipes` | `ReagentProcessor` | (ProcessingData, different comparable) |

## DynamicThingRecipeComparable.AddRecipe: patch-by-replace semantics

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

Full method at line 212779:

```csharp
public override bool AddRecipe(WorldManager.RecipeData recipe, ModAbout mod)
{
    DynamicThing dynamicThing = Prefab.Find(recipe.PrefabName) as DynamicThing;
    if (!dynamicThing)
    {
        dynamicThing = Resources.Load<DynamicThing>("Objects/" + recipe.PrefabName);
        if (!dynamicThing) return false;
    }
    if (recipe.RecipeTier != MachineTier.Undefined)
        dynamicThing.RecipeTier = recipe.RecipeTier;

    if (AllRecipes.ContainsKey(dynamicThing))
    {
        ConsoleWindow.PrintAction(GameStrings.PatchingRecipe.AsString(mod?.Name ?? ..., recipe.PrefabName));
        RemoveRecipe(recipe);   // removes old entry from AllRecipes and from Recycler
    }
    AllRecipes.Add(dynamicThing, recipe.Recipe);
    Recycler.AddRecycleRecipe(Animator.StringToHash(recipe.PrefabName), new ReagentMixture(recipe.Recipe));
    base.AddRecipe(recipe, mod);
    return true;
}
```

Critical implication: if a `RecipeData` for `PrefabName` already exists in `AllRecipes`, the old entry is silently removed and replaced by the new one. A mod GameData XML file that contains a `RecipeData` with the same `PrefabName` as a vanilla recipe will therefore completely replace that vanilla recipe. The log message "Patching recipe" is printed to the in-game console.

The Autolathe class is at line 370128: `public class Autolathe : SimpleFabricatorBase`. Its `RecipeComparable` is `static readonly DynamicThingRecipeComparable RecipeComparable = new DynamicThingRecipeComparable("Autolathe")`.

The ToolManufactory is at line 403109: `public class ToolManufactory : SimpleFabricatorBase`. Its static `RecipeComparable = new DynamicThingRecipeComparable("ToolManufactory")`.

## GameData XML overlay: the BepInEx / StationeersLaunchPad route

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

The vanilla `LoadDataFiles` loop iterates `WorkshopMenu.ModsConfig.Mods` (line 59149). StationeersLaunchPad registers BepInEx plugin mods into that list and sets `mod.DirectoryPath` to the BepInEx plugin folder. The game then calls `LoadDataFilesAtPath(Path.Combine(mod.DirectoryPath, "GameData"))`. This means a BepInEx mod can ship a `GameData/` subfolder alongside its DLL and the game will load it automatically on startup without any Harmony patch.

Re-Volt confirms this in practice: `.work/revolt-source/Assets/GameData/electronics.xml` contains `<ElectronicsPrinterRecipes>` entries. The game loads them via the same vanilla path. Re-Volt uses `StationeersMods.Interface.ModBehaviour` + `[StationeersMod]` attribute (not `BepInPlugin`), but StationeersLaunchPad registers both plugin types. The `ContentHandler` passed to `OnLoaded` handles prefab injection; the `GameData/` folder is loaded by the vanilla pipeline independently.

For a pure BepInEx mod using StationeersLaunchPad, the same mechanism works: place `GameData/myrecipes.xml` inside the mod's plugin folder (the same folder as the DLL). No Harmony patch needed.

## Harmony patch route: targeting DynamicThingRecipeComparable.AddRecipe

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

If a GameData XML overlay is not suitable (e.g., the mod needs to compute ingredient quantities at runtime), a Harmony postfix on `WorldManager.LoadXmlFileData` or a postfix on `DynamicThingRecipeComparable.AddRecipe` can mutate recipes after loading.

The most surgical approach: postfix `WorldManager.LoadXmlFileData` and call `AddRecipe` directly on the target fabricator's `RecipeComparable` with a replacement `RecipeData`. Since `AddRecipe` performs the patch-by-replace check internally, this is idempotent and consistent with the vanilla patching mechanism.

Alternative: postfix `Autolathe.RecipeComparable.AddRecipe` (or whichever fabricator), intercept the specific `PrefabName`, mutate `recipe.Recipe` before calling the original (prefix), or replace the dict entry after the fact.

The `Recipe` struct fields corresponding to XML ingredient names (from the constructor signature at line 135217, abbreviated): `iron`, `copper`, `silicon`, `solder`, `steel`, `lead`, `nickel`, `gold`, `silver`, `electrum`, `invar`, `constantan`, `plastic`, `stellite`, `waspaloy`, `inconel`, `hastelloy`, `astroloy`, `cobalt`, `carbon`, `uranium`, `hydrocarbon`, `oil`, `alcohol`, `salicylicacid`, `fenoxitone`, `flour`, `milk`, `egg`, `potato`, `tomato`, `pumpkin`, `rice`, `corn`, `wheat`, `biomass`, `soy`, `mushroom`, `sugar`, `cocoa`, `cheese`, plus `time`, `energy`, `temperature`, `pressure`, `requiredMix`.

## XML file format for a GameData overlay

<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

Minimum viable file to replace an Autolathe recipe:

```xml
<?xml version="1.0" encoding="utf-8"?>
<GameData xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
          xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <AutolatheRecipes>
    <RecipeData>
      <PrefabName>ItemCableCoilHeavy</PrefabName>
      <Recipe>
        <Time>30</Time>
        <Energy>500</Energy>
        <Copper>20</Copper>
        <Iron>10</Iron>
      </Recipe>
    </RecipeData>
  </AutolatheRecipes>
</GameData>
```

Only the recipe lists that need changes need to appear in the file. Other lists deserialize to empty and the dispatch loop skips them. The file must be placed at `<mod plugin dir>/GameData/<anyname>.xml`; subdirectory depth does not matter since `GetFiles` uses `SearchOption.AllDirectories`.

The `PrefabName` value must match the prefab's hash-name exactly (case-sensitive after hash; verify against `Prefab.Find` behavior). The heavy cable coil prefab name was not found in a grep of the decompile under the search terms tried; verify in-game or against a GameData dump.

## Pitfall: XML comments cannot contain a double-hyphen (XmlSerializer is strict)
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`WorldManager.LoadXmlFileData` deserializes each GameData file via `XmlSerializer`, which enforces XML 1.0 strictly. The XML 1.0 spec forbids the sequence `--` anywhere inside a comment (because `-->` is reserved as the comment terminator) and forbids ending a comment with `-`. A `GameData/*.xml` file whose `<!-- ... -->` header contains `--` anywhere (e.g. `<!-- Power Grid Plus -- super-heavy cable costs more -->`) makes the whole file fail to deserialize and the recipe overlay is silently NOT applied.

Symptom in the Unity Player log (`%LOCALAPPDATA%Low/Rocketwerkz/rocketstation/Player.log`):

```
An error occurred while deserializing a file!: <path>\GameData\<file>.xml
  - There is an error in XML document (<line>, <col>). : An XML comment cannot contain '--', and '-'
  cannot be the last character. Line <line>, position <col>.
Failed to load <path>\GameData\<file>.xml
```

Stack trace ends in `System.Xml.XmlTextReaderImpl.Throw` -> `XmlException` -> `Rethrow as InvalidOperationException`. The error is logged via `StationeersLaunchPad.LogWrapper.LogException` but does NOT abort the rest of the mod load. Other GameData files in the same mod still load. The mod's DLL still applies its Harmony patches. Only the failing recipe overlay is dropped.

Important: BepInEx LogOutput.log does NOT surface this error -- the failure happens in the Unity main-thread Mono runtime and is logged via Unity's `Debug.LogException`, which routes to Player.log, not BepInEx. Diagnosing it requires reading Player.log directly. A mod that ships a `GameData/` overlay with this bug will look clean in the BepInEx log (plugin loaded, patches applied) while silently failing to apply its overlay.

**Fix**: use a single hyphen, colon, semicolon, or period instead of `--` inside any `<!-- ... -->` block in a GameData XML file. The repo-wide style rule that prefers `--` as a sentence connector (see root `CLAUDE.md` "no AI tells in committed text") does not extend into XML comments; the parser cannot accept it. Inside `<RecipeData>` content (text between tags) `--` is fine; only the comment body is restricted.

Note: `XmlReaderSettings.CheckCharacters` would let the loader tolerate this if `WorldManager` were calling `XmlReader.Create` with that override, but it uses `XmlSerializer.Deserialize(Stream)` directly, which always uses the strict defaults.

## Verification history

- 2026-05-12: page created from reading Assembly-CSharp.decompiled.cs (version 0.2.6228.27061) and Re-Volt source at .work/revolt-source. No prior page existed. All sections new.
- 2026-05-15: added "Pitfall: XML comments cannot contain a double-hyphen (XmlSerializer is strict)" section after a real-world hit in Power Grid Plus's `GameData/cable-recipes.xml`. Sourced from the user's Player.log error message ("An XML comment cannot contain '--'") plus the XML 1.0 spec for `<!-- ... -->` comment content. The finding generalises to every mod that ships a GameData XML overlay; this is the only failure mode that the BepInEx log does not surface.

## Open questions

- Exact prefab name for the super-heavy cable coil (searched `CableCoilHeavy`, `HeavyCableCoil`, `ItemCableCoilHeavy` in the decompile; grep returned no results, suggesting it may be loaded from a prefab asset rather than a coded class). Needs verification via in-game F8 dump or InspectorPlus.
- Whether `mod.DirectoryPath` for a BepInEx plugin points to the plugin subfolder (e.g., `BepInEx/plugins/PowerGridPlus/`) or the BepInEx root. Verify by logging `mod.DirectoryPath` in a test plugin's `Awake`.
