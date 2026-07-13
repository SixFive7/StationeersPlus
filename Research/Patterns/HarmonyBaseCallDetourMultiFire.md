---
title: Harmony base-call detour multi-fire on override chains
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 390800-390810 (AreaPowerControl.GetPassiveTooltip), 395008-395023 (ElectricalInputOutput.GetPassiveTooltip), 371547-371557 (Device.GetPassiveTooltip), 314440-314448 (Structure.GetPassiveTooltip), 319731-319734 (Thing.GetPassiveTooltip)
  - Mods/PowerGridPlus/PowerGridPlus/Patches/FaultHoverPatches.cs:43-91 (shipped depth-guard mitigation)
related:
  - ./HarmonyInheritedMethods.md
  - ./HarmonyLogicableInheritedMethodTrap.md
  - ./HarmonyPatchOrdering.md
  - ../GameClasses/ElectricalInputOutput.md
tags: [harmony]
---

# Harmony base-call detour multi-fire on override chains

Patches installed on MULTIPLE levels of one virtual-override chain each fire once per top-level call whenever the derived overrides call `base.Method(...)`. The patch targeting succeeds at every level (unlike the sibling traps, nothing throws at `PatchAll` time); the failure mode is silent duplication at runtime: a postfix's effect lands once per patched level per call.

## The mechanism
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Harmony (HarmonyX / MonoMod) installs a detour on the compiled body of every patched method. A C# `base.Method(...)` call inside an override compiles to a NON-virtual `call` instruction aimed at the base class's method body; the detour sits on that body, so the base call executes the patched replacement, prefixes and postfixes included. Virtual dispatch is irrelevant to whether a patch fires: any route into the method body (`callvirt`, non-virtual `call`, delegate, reflection) runs the patch.

Consequence: patch classes `A : B : C` on the same virtual method, where `A`'s override calls `base` (landing in `B`'s body) and `B`'s override calls `base` (landing in `C`'s body). One outside call on an `A` instance then runs the patch set of all three levels: the caller enters `A`'s patched body, `A`'s base call enters `B`'s patched body, `B`'s base call enters `C`'s patched body. A postfix installed identically on all three levels fires three times.

A single-level patch is safe even on a base-calling chain: each body is detoured once and entered once per call. The multi-fire needs patches on two or more levels of the same chain.

## Worked example: the GetPassiveTooltip chain
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The hover-tooltip chain for electrical devices (full chain and override census on [ElectricalInputOutput](../GameClasses/ElectricalInputOutput.md)) stacks three base-calling overrides used by PowerGridPlus's fault-hover feature. The base-call sites, verbatim from the 0.2.6403.27689 decompile:

`AreaPowerControl.GetPassiveTooltip` (390800-390810) calls base on BOTH of its paths:

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)      // line 390800
{
    if (hitCollider != null)
    {
        return base.GetPassiveTooltip(hitCollider);                         // line 390804
    }
    PassiveTooltip passiveTooltip = base.GetPassiveTooltip(hitCollider);    // line 390806
    passiveTooltip.Title = DisplayName;
    passiveTooltip.Extended = GetExtendedText().ToString();
    return passiveTooltip;
}
```

`ElectricalInputOutput.GetPassiveTooltip` (395008-395023) tail-calls base after its two port-collider cases:

```csharp
    if (OutputConnection.ConnectionType != NetworkType.None && hitCollider == OutputConnection.Collider)
    {
        PassiveTooltip result = new PassiveTooltip(true);
        result.Title = InterfaceStrings.ConnectionOutput;
        return result;
    }
    return base.GetPassiveTooltip(hitCollider);                             // line 395022
```

`Device.GetPassiveTooltip` (371547-371557) tail-calls base after its open-ends loop (`return base.GetPassiveTooltip(hitCollider);`, 371556); below it, `Structure.GetPassiveTooltip` (314440) calls base at 314447 and `Thing.GetPassiveTooltip` (319731-319734) ends the chain.

With one postfix installed on the `Device`, `ElectricalInputOutput`, and `AreaPowerControl` levels (the PowerGridPlus fault-hover target set), a single hover poll fires it:

- 3 times on an `AreaPowerControl` body (enters at APC, base-calls into ElectricalInputOutput, then into Device);
- 2 times on a `Transformer` or `Battery` body (neither class declares an override, so dispatch enters at ElectricalInputOutput, which base-calls into Device);
- 1 time on a plain one-port device body (enters at Device).

Observed in-game exactly so during the PowerGridPlus fault-hover work before the guard shipped: the appended fault line showed twice on Transformer and Battery bodies and three times on AreaPowerControl bodies.

## Mitigation: outermost-only depth guard
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The shipped fix (`Mods/PowerGridPlus/PowerGridPlus/Patches/FaultHoverPatches.cs`): a prefix increments a static depth counter, the postfix acts only at depth 1 (the outermost invocation), and a finalizer decrements on every exit including throws so the counter can never leak and permanently suppress the patch. Verbatim skeleton (lines 46-90):

```csharp
[HarmonyPatch]
public static class FaultHoverPatches
{
    private static int _depth;

    public static IEnumerable<MethodBase> TargetMethods()
    {
        const string name = "GetPassiveTooltip";
        yield return AccessTools.Method(typeof(Assets.Scripts.Objects.Pipes.Device), name);
        yield return AccessTools.Method(typeof(ElectricalInputOutput), name);
        yield return AccessTools.Method(typeof(AreaPowerControl), name);
        // ... four more single-level targets omitted (SolarPanel, PowerGeneratorPipe,
        //     StirlingEngine, PowerConnector); only the three above share a chain.
    }

    [HarmonyPrefix]
    public static void Prefix()
    {
        _depth++;
    }

    [HarmonyPostfix]
    public static void Postfix(Thing __instance, ref PassiveTooltip __result)
    {
        if (_depth > 1) return;   // inner base-call invocation: the outermost level appends
        if (__instance == null) return;
        // ... single append into __result.Extended ...
    }

    // Runs on both normal and exceptional exit, so the depth can never leak upward and
    // permanently suppress the hover.
    [HarmonyFinalizer]
    public static Exception Finalizer(Exception __exception)
    {
        _depth--;
        return __exception;
    }
}
```

Why the pieces are what they are:

- **Postfix at depth 1, not depth N**: postfixes unwind inner-first (the innermost base call returns before the outer body finishes), so the outermost postfix runs LAST and sees the final `__result` after every vanilla level has contributed. Acting there both deduplicates and operates on the completed value.
- **Finalizer, not a postfix decrement**: a postfix does not run if the original (or an inner patch) throws; a finalizer runs on all exits. Without it, one exception leaves `_depth` elevated forever and the guard silently disables the patch for the rest of the session.
- **Plain `static int`, no `[ThreadStatic]`**: tooltip polling is main-thread only. A patched method reachable from worker threads needs a `[ThreadStatic]` counter (or per-thread state) or concurrent calls corrupt the depth.
- The same guard covers prefix side effects too (the prefix itself still runs at every level; put level-sensitive work behind the same depth check).

The alternative mitigation is structural: patch only ONE level of the chain. That works when a single override covers every instance you care about; the fault-hover case needed multiple entry points because different subclasses enter the chain at different levels (see the census on [ElectricalInputOutput](../GameClasses/ElectricalInputOutput.md)), so the guard was required.

## Distinction from the sibling inheritance traps
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Three distinct Harmony-plus-inheritance failure modes are now documented in this repo:

| Page | Failure point | Symptom |
|---|---|---|
| [HarmonyInheritedMethods](./HarmonyInheritedMethods.md) | Attribute targeting a method the subclass does not declare | `PatchAll` throws `Undefined target method`; `__instance` typing traps |
| [HarmonyLogicableInheritedMethodTrap](./HarmonyLogicableInheritedMethodTrap.md) | Same resolution failure on the Device logic-port API | The `PatchAll` bail-out silently disables sibling patches and post-`PatchAll` init |
| This page | Patches resolve and install on MULTIPLE levels of one chain | No exception; postfix effects duplicate once per patched level per call via detoured base calls |

Patch-pipeline ordering within a single patched method (priorities, `__runOriginal`, `__state` sharing) is on [HarmonyPatchOrdering](./HarmonyPatchOrdering.md); this page is about one logical call entering several patched bodies.

## Verification history

- 2026-07-14: page created from the PowerGridPlus fault-hover work (game version 0.2.6403.27689). Base-call sites quoted verbatim from the 0.2.6403.27689 decompile: AreaPowerControl.GetPassiveTooltip 390800-390810 (base calls at 390804 and 390806), ElectricalInputOutput.GetPassiveTooltip tail base call 395022, Device.GetPassiveTooltip tail base call 371556, Structure 314447, Thing terminal 319731-319734. Multi-fire arithmetic (3x on AreaPowerControl bodies, 2x on Transformer/Battery bodies, 1x on single-level entries) matches the in-game observation that drove the fix: the fault line appended twice on Transformer/Battery hovers and three times on AreaPowerControl hovers before the guard shipped. Mitigation quoted from Mods/PowerGridPlus/PowerGridPlus/Patches/FaultHoverPatches.cs (prefix-increment / postfix-at-depth-1 / finalizer-decrement with main-thread-only rationale). New page; no existing page contradicted (HarmonyInheritedMethods and HarmonyLogicableInheritedMethodTrap cover target-resolution failures, not runtime duplication).

## Open questions

None at creation.
