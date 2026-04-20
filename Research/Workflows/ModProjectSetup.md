---
title: Mod Project Setup
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:350-368
  - Plans/RepairPrototype/plan.md:793-800
  - Plans/RepairPrototype/plan.md:804-818
related:
  - ../Patterns/HarmonyPatchTypes.md
  - ../Patterns/UnityFakeNull.md
tags: [harmony, launchpad, packaging]
---

# Mod Project Setup

Framework stack, assembly references, plugin template, Config.Bind usage, and Harmony patch types used by every Stationeers mod in this monorepo. Reach for this page when scaffolding a new mod or when a contributor asks "what does the baseline look like."

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Seeding a new mod from `Mods/Template/` and wondering what the framework baseline is.
- Adding a BepInEx config option and wondering where the file lands and how to wire it up.
- Writing a first Harmony patch and needing the patch-type vocabulary.

## Framework stack
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| Component | Version | Role |
|---|---|---|
| BepInEx | 5.4.21+ (x64, Mono) | Plugin loader |
| Harmony (HarmonyX) | 2.x (bundled with BepInEx) | Runtime method patching |
| StationeersLaunchPad | Latest | Mod loader UI + config |
| .NET Framework | 4.5.2 | Target framework |
| Unity | 2021.2.x (game version) | Engine (relevant for asset work only) |

### Assembly references needed

From the Stationeers install at `$(StationeersPath)\rocketstation_Data\Managed\`:

- `Assembly-CSharp.dll` (game code)
- `UnityEngine.dll` + `UnityEngine.CoreModule.dll`
- `com.unity.multiplayer-hlapi.Runtime.dll` (networking)

From `$(StationeersPath)\BepInEx\core\`:

- `0Harmony.dll`
- `BepInEx.dll`

Community package for game assemblies: https://github.com/ilodev/stationeers.modding.assemblies

## Plugin template
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
[BepInPlugin("com.author.modname", "Mod Name", "1.0")]
public class MyPlugin : BaseUnityPlugin
{
    public static MyPlugin Instance;
    void Awake()
    {
        Instance = this;
        var harmony = new Harmony("com.author.modname");
        harmony.PatchAll();
    }
}
```

## Harmony patch types
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- **Prefix:** Runs before original. Return `false` to skip original. Gets `__instance`, can modify params.
- **Postfix:** Runs after original. Can modify `__result`.
- **Transpiler:** Modifies IL code at load time.
- **Reverse Patch:** Calls private/internal methods from mod code.

For the traps around inherited methods, `__instance` typing, and attribute placement, see [HarmonyPatchTypes.md](../Patterns/HarmonyPatchTypes.md).

## Config.Bind pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
var config = Config.Bind("Section", "Key", defaultValue, "Description");
// Auto-saved to BepInEx/Config/org.author.modname.cfg
```

Every config value lives in a single `.cfg` file under the Stationeers install's `BepInEx/Config/` folder named after the plugin's GUID. Subscribers can edit the file directly; the plugin re-reads on load.

## Private field access
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// Harmony convention: 4 underscores + field name
static FieldInfo myField = AccessTools.Field(typeof(WeatherManager), "_stormWindStrength");
myField.SetValue(__instance, newValue);

// Or via parameter naming in patch method:
public static void Patch(float ____privateFieldName) { ... }
```

## Key singletons
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- `GameManager.IsServer` / `NetworkManager.IsServer`
- `WorldManager.IsPaused`, `WorldManager.Instance.GameMode`
- `WeatherManager.Instance`
- `WorldManager.Instance.SourcePrefabs` - master prefab list

## Known gotchas
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- `Device.AllDevices` can contain duplicates (use HashSet to dedup)
- `AtmosphericsManager.AllAtmospheres` can contain nulls (must filter)
- Power calculations can produce `NaN` (guard against this)
- Power tick runs on background thread -- can't use `UnityEngine.Random`
- Atmosphere may be null until a player logs in on dedicated servers

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- After the initial scaffold compiles, drop the DLL into the Stationeers install's `BepInEx/plugins/<ModName>/` folder, launch the game, and confirm the BepInEx log records the plugin's Awake.
- After the first `Config.Bind` call, confirm the expected `.cfg` file appears under the Stationeers install's `BepInEx/Config/` folder with the configured section, key, default value, and description comment.
- After the first `harmony.PatchAll()` call, confirm Harmony's own startup log lists the patched methods.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Target `.NET Framework 4.5.2`; newer target frameworks produce assemblies the game cannot load.
- Reference game assemblies via `$(StationeersPath)` (see `Directory.Build.props.template` at the repo root). Hardcoded absolute paths break clones on any other machine.
- `harmony.PatchAll()` reflects over the plugin assembly looking for `[HarmonyPatch]` classes. Classes that target types the game no longer exposes throw at startup; guard with `[HarmonyPatch]` on `TargetMethod()` plus a `Prepare()` check when reflecting against a moving target.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0229d (framework stack), F0229k (Config.Bind), and F0229l (plugin template + patch types) in `Plans/RepairPrototype/plan.md`. Per Phase 2 misfit resolutions M2 / M3 / M4.

## Open questions

None at creation.
