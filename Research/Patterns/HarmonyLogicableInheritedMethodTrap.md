---
title: Harmony patching trap -- inherited Logicable methods on subclasses that don't override
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-17
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.Logicable
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Battery
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerTransmitter
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerReceiver
related:
  - ../GameClasses/Battery.md
  - ../GameClasses/PowerTransmitter.md
tags: [harmony, logic]
---

# Harmony patching trap: inherited Logicable methods on subclasses that don't override

`Device` declares the virtual logic-port API. Decompile line 350229 onward inside `public class Device : SmallGrid, ILogicable, IReferencable, IEvaluable, IConnected, ISlotWriteable, IWreckage, IPowered, IDensePoolable`. (`Logicable` itself, at decompile line 359632, is `public static class Logicable` -- a static helper class, NOT the base class for logic-capable Things. The interface counterpart is `ILogicable`, which `Device` implements.) The four virtual method declarations are:

```csharp
public virtual bool CanLogicRead(LogicType logicType) { ... }
public virtual bool CanLogicWrite(LogicType logicType) { ... }
// equivalent virtual GetLogicValue, SetLogicValue declarations follow
```

The four method names:

- `bool CanLogicRead(LogicType)`
- `bool CanLogicWrite(LogicType)`
- `double GetLogicValue(LogicType)`
- `void SetLogicValue(LogicType, double)`

Many `Device` subclasses (e.g. `Transformer`) override one or more of these to expose subclass-specific logic types. Other subclasses (`Battery` at decompile line 370616, `PowerTransmitter` at line 387065, `PowerReceiver` at line 386861) **do not override** these methods at all -- they inherit them from `Device` directly.

## The trap
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

Harmony's attribute-style patch:

```csharp
[HarmonyPatch(typeof(Battery))]
public static class BatteryFoo
{
    [HarmonyPrefix, HarmonyPatch(nameof(Battery.SetLogicValue))]
    public static bool SetLogicValuePatch(Battery __instance, LogicType logicType, double value) { ... }
}
```

compiles fine -- `nameof(Battery.SetLogicValue)` resolves through inheritance at compile time -- but throws at `Harmony.PatchAll()` time:

```
HarmonyLib.HarmonyException: Patching exception in method null --->
  System.ArgumentException: Undefined target method for patch method
  static bool BatteryFoo::SetLogicValuePatch(Battery __instance, LogicType logicType, double value)
```

Harmony tries to resolve `SetLogicValue` as a **declared** member of `typeof(Battery)`. Because Battery doesn't override it, the lookup returns null and `PatchAll` bails out for the whole batch. Any patches in the same batch processed AFTER the bail point are skipped, and any code in the calling try (e.g. manual init calls below `harmony.PatchAll()`) does not run.

Confirmed in PowerGridPlus v0.1.1 development: a `BatteryPassthroughLogicPatches.SetLogicValuePatch` targeting `Battery.SetLogicValue` triggered exactly this exception. The bail-out cascade silently disabled `LogicableInitializePatch` (no `LogicPassthroughMode` injection into the tablet dropdown), `LogicPassthroughPatches` (no bridging), and the manual `CableCostPatches.ApplyRecipeCost()` / `Ic10ConstantsPatcher.Apply()` calls that follow `PatchAll` in `Plugin.OnPrefabsLoaded`.

It is consistent: the same pattern works for `Transformer.SetLogicValue` because Transformer overrides the method, and fails for Battery / PowerTransmitter / PowerReceiver because they do not.

## Symptoms in the wild

- BepInEx `LogOutput.log` contains `[Fatal :<your-mod>] <your-mod> failed to apply patches: HarmonyLib.HarmonyException: ... System.ArgumentException: Undefined target method for patch method ...`.
- Some of your other patches mysteriously stop working in this session, depending on HarmonyX's class-iteration order during `PatchAll`. Alphabetically earlier patches typically survive; later ones don't.
- Any helper init code below the throwing `PatchAll` call also doesn't run.
- The `<Mod> patches applied` info line never appears.

## Fix patterns

Three working options, pick by taste:

**(a) Patch the base class with a runtime type check.** Cleanest because it covers every subclass in one place:

```csharp
[HarmonyPatch(typeof(Device))]
public static class FooPassthroughLogicPatches
{
    [HarmonyPrefix, HarmonyPatch(nameof(Device.SetLogicValue))]
    public static bool SetLogicValuePatch(Device __instance, LogicType logicType, double value)
    {
        if (logicType != LogicTypeRegistry.LogicPassthroughMode) return true;
        if (__instance is Battery battery) { ... return false; }
        if (__instance is PowerTransmitter tx) { ... return false; }
        if (__instance is PowerReceiver rx) { ... return false; }
        return true;
    }
}
```

The base method exists on Device directly, so the patch resolves regardless of subclass.

**(b) Explicit method discovery via `AccessTools.Method(typeof(Device), name)`.** Use `[HarmonyPatch]` with no method-name argument and provide `TargetMethod()`:

```csharp
[HarmonyPatch]
public static class BatteryPassthroughLogicPatches
{
    static MethodBase TargetMethod() =>
        AccessTools.Method(typeof(Device), nameof(Device.SetLogicValue));

    public static bool Prefix(Device __instance, LogicType logicType, double value) { ... }
}
```

Equivalent to (a) in coverage; verbose if you want separate patch classes per type.

**(c) Pick a method that IS overridden** on the target subclass and check the logic type inside. Rarely the right answer because the override surface differs per type.

PowerGridPlus's `TransformerPassthroughLogicPatches` happens to work with attribute-style patching only because `Transformer.SetLogicValue` is a real override on `Transformer`. The equivalent battery / dish patches must use pattern (a) or (b).

## Verification history

- 2026-05-17: page created after PowerGridPlus's first attempt at extending logic-passthrough to Battery + PowerTransmitter / PowerReceiver bailed `Harmony.PatchAll()` with `Undefined target method for patch method ... Battery::SetLogicValue`. Confirmed (via the in-session BepInEx LogOutput.log of the user's running game) that the bail-out also took down LogicableInitializePatch and the post-`PatchAll` manual init calls in `Plugin.OnPrefabsLoaded`. Initial draft pointed at `Logicable` as the base; corrected after discovering `public static class Logicable` (decompile line 359632) is a static helper class, not the inheritance base. The virtual logic-port methods are actually declared on `Device : SmallGrid, ILogicable, ...` (decompile line 349960+) with the `CanLogicRead` body at line 350229, `CanLogicWrite` at line 350305, `SetLogicValue` at line 350323, `GetLogicValue` at line 350359. Solution adopted: pattern (a) -- patch `Device` directly with runtime type checks (`Battery`, `PowerTransmitter`, `PowerReceiver` all inherit from `Device` via `ElectricalInputOutput` / `WirelessPower`).

## Open questions

None at creation.
