using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Registration-time wiring guard (POWER.md decision 31): the enforcement backstop for
    ///     placement paths that never see the build cursor (blueprint printers, creative spawns,
    ///     other mods calling <c>OnServer.Create</c>). Vanilla registration has NO occupancy or tier
    ///     check of any kind (Research/GameSystems/StructureRegistration.md), and BlueprintMod's
    ///     paste path skips CanConstruct entirely (Research/Patterns/BlueprintModPlacement.md), which
    ///     is how the 2026-07-13 incident printed normal cable into a super-heavy mainline.
    ///
    ///     Two violation classes, one refusal:
    ///
    ///     CELL THEFT. <c>SmallCell</c> holds ONE cable slot and <c>SmallCell.Add</c> is a plain
    ///     overwrite (Research/GameClasses/SmallCell.md), so a cable registered into an occupied cell
    ///     silently steals the cell pointer and orphans the prior cable into an untargetable ghost.
    ///     The <see cref="SmallCellAddTheftCapture"/> prefix observes the overwrite as it happens
    ///     (the only moment the victim is still reachable) and records the pair; no mutation happens
    ///     mid-registration. Only DIFFERENT-tier theft is captured: vanilla's own
    ///     MultiMergeConstructor replace flow legitimately re-occupies same-tier cells.
    ///
    ///     MIXED-TIER MERGE. After vanilla's <c>Cable.OnRegistered</c> merge lands, the resulting
    ///     network is inspected; a newcomer whose registration made the network mixed-tier is
    ///     refused. During normal play a mixed merge can only come from a cursor-less path (the
    ///     cursor reject in VoltageTierPatches blocks hand placement first), so refusing the
    ///     newcomer is always the right victim choice.
    ///
    ///     The refusal (in <see cref="CableRegistrationGuard"/>, a Cable.OnRegistered postfix: the
    ///     merged network is inspectable there and OnServer.Destroy defers safely to end-of-frame,
    ///     Research/GameClasses/Cable.md "Registration-time guard"): re-seat every stolen cell back
    ///     to its victim FIRST (so the thief's own deregistration, which clears the cell only on
    ///     reference equality, becomes a no-op), then refund the cable as its construction kit at
    ///     the spot, destroy the cable, and print a plain-text line to the player-visible console.
    ///     A refused BlueprintMod paste entry costs the printer exactly one Failed count: its loop
    ///     is one-attempt-per-entry with no retry.
    ///
    ///     Load and join traffic is exempt (GameState gate): a save carrying illegal wiring must
    ///     load intact; healing it is the demand-gated escalation's job (VoltageTier
    ///     ResolveMixedTierNetwork), not the guard's. Known gap, accepted: rocket movement
    ///     re-registers cables through GridController.Attach, which fires neither SmallCell.Add
    ///     consumption nor OnRegistered; a violation created by a landing rocket is caught by the
    ///     per-tick escalation instead.
    /// </summary>
    internal static class WiringGuard
    {
        // Test seam: ScenarioRunner suspends the guard (via reflection) while it builds
        // deliberately-broken states for the escalation fixtures.
        internal static bool Suspended = false;

        internal struct Theft
        {
            public Cable Thief;
            public Cable Victim;
            public SmallCell Cell;
        }

        // Thefts observed during the current registration, consumed by the OnRegistered postfix.
        // Main-thread producer and consumer; the lock is cheap insurance, and the cap sweeps
        // entries whose consumer never fired (e.g. the rocket Attach path, which skips OnRegistered).
        internal static readonly List<Theft> Pending = new List<Theft>();

        internal static bool Enabled =>
            !Suspended && GameManager.RunSimulation && GameManager.GameState == GameState.Running;
    }

    [HarmonyPatch(typeof(SmallCell), nameof(SmallCell.Add))]
    public static class SmallCellAddTheftCapture
    {
        [HarmonyPrefix]
        public static void Prefix(SmallCell __instance, SmallGrid smallGridObjectGrid)
        {
            if (!(smallGridObjectGrid is Cable thief)) return;
            var victim = __instance.Cable;
            if (victim == null || ReferenceEquals(victim, thief) || victim.IsBeingDestroyed) return;

            // UNGATED tier-cache eviction on every cable-slot overwrite: the victim's network sees no
            // membership event when its member loses its seat (a cross-network theft mutates only the
            // cell), so without this the cached TierInfo hides the theft until some unrelated event.
            // Deliberately runs even while the guard is suspended: eviction is observation, not refusal.
            var victimNet = victim.CableNetwork;
            if (victimNet != null)
                VoltageTierEnforcer.InvalidateNet(victimNet.ReferenceId);

            if (!WiringGuard.Enabled) return;
            if (victim.CableType == thief.CableType) return;   // same-tier replace flows are vanilla-legal
            lock (WiringGuard.Pending)
            {
                if (WiringGuard.Pending.Count > 64) WiringGuard.Pending.Clear();
                WiringGuard.Pending.Add(new WiringGuard.Theft { Thief = thief, Victim = victim, Cell = __instance });
            }
        }
    }

    [HarmonyPatch(typeof(Cable), nameof(Cable.OnRegistered))]
    public static class CableRegistrationGuard
    {
        [HarmonyPostfix]
        public static void Postfix(Cable __instance)
        {
            if (!WiringGuard.Enabled) return;
            if (__instance == null || __instance.IsBeingDestroyed) return;

            List<WiringGuard.Theft> thefts = null;
            lock (WiringGuard.Pending)
            {
                for (int i = WiringGuard.Pending.Count - 1; i >= 0; i--)
                {
                    if (!ReferenceEquals(WiringGuard.Pending[i].Thief, __instance)) continue;
                    (thefts ?? (thefts = new List<WiringGuard.Theft>())).Add(WiringGuard.Pending[i]);
                    WiringGuard.Pending.RemoveAt(i);
                }
            }

            bool mixed = false;
            var otherTier = __instance.CableType;
            var network = __instance.CableNetwork;
            if (network != null)
            {
                var info = VoltageTierEnforcer.GetTierInfo(network);
                if (info.Mixed)
                {
                    mixed = true;
                    otherTier = OtherTier(network, __instance.CableType);
                }
            }
            if (thefts == null && !mixed) return;

            // Re-seat every stolen cell before the thief goes away: with the victim back in the
            // slot, the thief's own deregistration (reference-equality clear) leaves it alone.
            if (thefts != null)
            {
                foreach (var theft in thefts)
                {
                    if (theft.Victim == null || theft.Victim.IsBeingDestroyed || theft.Cell == null) continue;
                    if (!ReferenceEquals(theft.Cell.Cable, __instance)) continue;
                    theft.Cell.Add(theft.Victim);
                    theft.Victim.SmallCell = theft.Cell;
                }
            }

            bool refunded = DropAsKit(__instance);
            string reason = thefts != null
                ? $"stacked on a {VoltageTier.TierWord(thefts[0].Victim.CableType)} cable"
                : $"joining a {VoltageTier.TierWord(otherTier)} network";
            PlayerConsole.Broadcast(
                $"Illegal cable placement at {VoltageTier.Coords(__instance)}: refused {VoltageTier.TierWord(__instance.CableType)} cable {reason}"
                + (refunded ? ", dropped as kit" : ""));
            OnServer.Destroy(__instance);
        }

        private static Cable.Type OtherTier(CableNetwork network, Cable.Type mine)
        {
            lock (network.CableList)
            {
                foreach (var cable in network.CableList)
                {
                    if (cable != null && cable.CableType != mine)
                        return cable.CableType;
                }
            }
            return mine;
        }

        /// <summary>
        ///     Refund the cable as its own construction kit at its position. The kit identity is the
        ///     cable's serialized build-state tool entry (BuildStates[0].Tool: ToolEntry x
        ///     EntryQuantity, which auto-matches the 2-3 unit junction costs), spawned via the
        ///     canonical vanilla drop pattern (Research/GameClasses/Structure.md,
        ///     ToolUse.SpawnItem). Host-side create replicates to clients automatically.
        /// </summary>
        private static bool DropAsKit(Cable cable)
        {
            try
            {
                var states = cable.BuildStates;
                var tool = states != null && states.Count > 0 ? states[0]?.Tool : null;
                var entry = tool?.ToolEntry;
                if (entry == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Kit refund skipped for {cable.PrefabName}: no BuildStates[0].Tool.ToolEntry.");
                    return false;
                }
                int quantity = Math.Max(1, tool.EntryQuantity);
                var item = OnServer.Create<Item>(entry, cable.ThingTransformPosition, Quaternion.identity);
                if (item is Stackable stack && quantity > 1)
                    stack.SetQuantity(quantity);
                return item != null;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PowerGridPlus] Kit refund failed: {ex.Message}");
                return false;
            }
        }
    }
}
