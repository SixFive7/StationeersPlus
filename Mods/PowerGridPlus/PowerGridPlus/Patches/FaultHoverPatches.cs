using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Appends the fault / info block (POWER.md §11.1 via <see cref="FaultHover"/>, the locked
    ///     merged template both hover surfaces share) to the body hover of every power device that
    ///     can be in a fault lockout. The UI re-polls GetPassiveTooltip every frame while the player
    ///     hovers, so the countdown ticks smoothly. The block is multiline (the merged
    ///     state-plus-title line over the diagnostics); the tooltip's Extended field is a
    ///     TextMeshPro string, so "\n" renders as a line break.
    ///
    ///     <para>Patch targets (the virtual-dispatch trap, POWERTODO 0.2): Thing.GetPassiveTooltip is
    ///     virtual and overridden along the hierarchy, so the postfix must attach to the override
    ///     that actually RUNS for each class. The seven targets below cover every faultable class:
    ///     Device (WindTurbineGenerator + LargeWindTurbineGenerator, RadioscopicThermalGenerator,
    ///     PowerGeneratorSlot + SolidFuelGenerator, TurbineGenerator, and any modded producer with no
    ///     own override), ElectricalInputOutput (Battery, Transformer, PowerTransmitter,
    ///     PowerReceiver, both RocketPowerUmbilical halves), and the five classes with their own
    ///     override: AreaPowerControl, SolarPanel, PowerGeneratorPipe (GasFuelGenerator),
    ///     StirlingEngine, PowerConnector. Burned-cable hovers live in BurnReasonPatches
    ///     (Structure.GetPassiveTooltip), not here.</para>
    ///
    ///     <para>Outermost-only guard: several targets sit on one inheritance chain and the derived
    ///     overrides call <c>base.GetPassiveTooltip</c> (AreaPowerControl -&gt;
    ///     ElectricalInputOutput -&gt; Device), and Harmony detours base calls exactly like virtual
    ///     dispatch, so one player poll can run this postfix at every patched level of the chain.
    ///     The depth counter below lets only the OUTERMOST invocation append, so the block lands
    ///     exactly once per hover. Tooltip polling is main-thread only; a plain static suffices.</para>
    ///
    ///     <para>Exactly one block per hover: the highest-precedence active fault (CYCLE &gt;
    ///     CURRENT-MISMATCH &gt; CABLE-OVERLOADED &gt; DEVICE-OVERLOADED &gt; DEPRIORITIZED, §11.5),
    ///     else the dead-input cue, else the transformer throttle note; devices with no active
    ///     state are untouched (idle hover stays pure vanilla, the RocketPowerUmbilicalFemale rule
    ///     in §5.0.2 generalised).</para>
    ///
    ///     <para>No-ALT visibility during a lockout fault: without ALT the HUD path is
    ///     InventoryManager.NormalModeThing (decompile 287864), which displays a body tooltip only
    ///     when its Title is non-empty (the `!cursorPassiveTooltip2.Title.Equals(string.Empty)`
    ///     branch at 287963); a healthy Transformer/Battery body falls through the
    ///     GetPassiveTooltip chain to the all-empty struct, so an Extended-only block never
    ///     shows. Holding ALT (KeyMap.MouseControl = LeftAlt, 44018) switches to InputMouse.Idle
    ///     (239679), which hands the tooltip to HandleToolTipDisplay unconditionally (239721),
    ///     which is why the block used to render only under ALT. While a LOCKOUT fault is active
    ///     (never for the two info states) this postfix therefore also fills the empty Title with
    ///     the device's DisplayName, exactly what vanilla does for a damaged Structure (314440)
    ///     and for the APC body (390800), so the untouched vanilla display gates show the tooltip
    ///     without ALT. The separate State row stays empty: the block's own first line already
    ///     carries the switch word ("On - Cable overloaded fault: ..."), so filling the row would
    ///     say "On" twice.</para>
    ///
    ///     <para>Alignment: the tooltip's Extended field renders in a centered TextMeshProUGUI
    ///     (TooltipExtended, 253982), so multiline blocks jitter horizontally as the countdown
    ///     narrows ("59.98s" -&gt; "9.98s"). The whole mod block is wrapped in a TMP
    ///     &lt;align=left&gt; span, opened at the start of its own line, so only OUR lines go
    ///     left-aligned; any vanilla Extended content above keeps the component's alignment.</para>
    /// </summary>
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
            yield return AccessTools.Method(typeof(SolarPanel), name);
            yield return AccessTools.Method(typeof(PowerGeneratorPipe), name);
            yield return AccessTools.Method(typeof(StirlingEngine), name);
            yield return AccessTools.Method(typeof(PowerConnector), name);
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
            long refId = FaultHover.ResolveFaultRefId(__instance);
            int tick = ElectricityTickCounter.CurrentTick;
            // One block per hover: the highest-precedence active fault, else the dead-input cue,
            // else the throttle note (all resolved inside FaultHover.TryGetMergedBlock).
            if (!FaultHover.TryGetMergedBlock(refId, tick, __instance, out var block, out var kind))
                return;   // idle device: hover stays pure vanilla

            // Left-align our lines only (the countdown's varying width jitters a centered block).
            // The span opens at the start of our first line, so vanilla Extended content above
            // (damage text on a damaged structure) keeps its own alignment.
            __result.Extended = AppendLine(__result.Extended, "<align=left>" + block + "</align>");

            // Fault-only presentation extra (see the class doc): a non-empty Title makes the
            // vanilla no-ALT display gate (NormalModeThing 287963) show this tooltip. It respects
            // content a vanilla override already put there (damaged-structure Title, port labels)
            // and stays off for the two info states, so a non-faulted device's tooltip behavior
            // is bit-identical to before.
            if (FaultHover.IsLockoutFault(kind) && string.IsNullOrEmpty(__result.Title))
                __result.Title = __instance.DisplayName;
        }

        // Runs on both normal and exceptional exit, so the depth can never leak upward and
        // permanently suppress the hover.
        [HarmonyFinalizer]
        public static Exception Finalizer(Exception __exception)
        {
            _depth--;
            return __exception;
        }

        private static string AppendLine(string existing, string line)
            => string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
    }
}
