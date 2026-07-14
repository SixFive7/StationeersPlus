// Three-tier transmission-voltage gating. See VoltageTier for the policy.

using System;
using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    [HarmonyPatch]
    public static class VoltageTierPatches
    {
        private const string DifferentVoltageMessage = "Wrong voltage -- this cable's tier doesn't match the adjacent cable network. Use a transformer.";

        /// <summary>
        ///     Build-time cursor reject: if the cable being previewed would merge into a network that
        ///     already carries a different tier, OR would sit next to a device the new tier isn't valid for,
        ///     refuse the placement with a player-visible message. (Reactive burns from
        ///     <see cref="VoltageTierEnforcer"/> (atomic PROTECT (wrong-tier burn)) remain the authoritative backstop --
        ///     they fire on the next power tick if power is actually flowing.)
        ///
        ///     Note: this is intentionally cable-side only. Placing a device on a wrong-tier cable is NOT
        ///     rejected at build time; that case is handled reactively by the tick burning the cable next
        ///     to the misplaced device.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Cable), nameof(Cable.CanConstruct))]
        public static void Cable_CanConstruct_Postfix(Cable __instance, ref CanConstructInfo __result)
        {
            // Voltage tiers are always enforced (the EnableVoltageTiers toggle was removed).
            if (!__result.CanConstruct)
                return;

            try
            {
                // (a) Cable-to-cable: don't allow placing into a POWER network that already has a different
                // tier. Port-aware: only considers cables reached via a Power-bit OpenEnd overlap. A purely
                // data-side adjacency (Data-bit only) does NOT engage tier rules, so heavy / normal data
                // cables mix freely. Per Power-bit OpenEnd, SmallCell.Get<Cable>(grid, openEnd) resolves the
                // adjacent cable whose open ends overlap on the Power bit (0.2.6403 removed the
                // list-returning ConnectedCables overloads; this is the same per-cell test the game's
                // Span-based replacement FillConnected<Cable>(NetworkType.Power, ...) performs), so combo
                // (PowerAndData) cables still count on the power side. Connection.GetLocalGrid() computes
                // the grid from the live Transform while the cable is an uninitialized cursor ghost.
                var seenNetworks = new HashSet<CableNetwork>();
                foreach (var openEnd in __instance.OpenEnds)
                {
                    if (openEnd == null || !VoltageTier.ConnectionCarriesPower(openEnd))
                        continue;
                    var c = SmallCell.Get<Cable>(openEnd.GetLocalGrid(), openEnd);
                    if (c == null || ReferenceEquals(c, __instance) || c.CableNetwork == null)
                        continue;
                    seenNetworks.Add(c.CableNetwork);
                }
                if (seenNetworks.Count > 0)
                {
                    foreach (var network in seenNetworks)
                    {
                        if (network == null)
                            continue;
                        lock (network.CableList)
                        {
                            foreach (var cable in network.CableList)
                            {
                                if (cable != null && cable.CableType != __instance.CableType)
                                {
                                    __result = CanConstructInfo.InvalidPlacement(DifferentVoltageMessage);
                                    return;
                                }
                            }
                        }
                    }
                }

                // (b) Cable-to-device: don't allow placing a cable next to an existing device that wouldn't
                // be allowed on the new cable's tier. SmallCell.Get<Device>(grid, openEnd) is the same
                // per-cell lookup as the cable case above and is valid on a cursor ghost
                // (Connection.GetLocalGrid() computes the ghost's grid from the live Transform every
                // frame -- see Research/Patterns/CursorAdjacencyLookup.md).
                var cursorEnds = __instance.OpenEnds;
                if (cursorEnds != null)
                {
                    foreach (var cursorEnd in cursorEnds)
                    {
                        var device = cursorEnd == null ? null : SmallCell.Get<Device>(cursorEnd.GetLocalGrid(), cursorEnd);
                        if (device == null)
                            continue;

                        // Port-aware: skip the entire per-device tier check when the cursor cable would
                        // attach to this device only through a non-power Connection (e.g. a data port).
                        // Tier rules are about power flow; a data-only adjacency does not engage them.
                        // The per-OpenEnd lookup returns the device whenever ANY OpenEnd bitmask overlaps,
                        // so a heavy data cable next to a transformer's data port is reported here even
                        // though the device's power-tier rule should not apply to it.
                        if (!CursorAttachesToPowerPortOf(__instance, device))
                            continue;

                        // (b.1) Transformer: each variant's two cable ports must hold an unordered tier
                        // pair {tierA, tierB}. The cable's tier must be in that pair. For asymmetric
                        // variants (Small, Large) where exactly ONE side is already wired, the new cable's
                        // tier must DIFFER from that side's tier (otherwise the pair would have duplicates,
                        // e.g. both heavy on a Small). When both sides are filled the cable-to-cable
                        // network-tier check covers the extension case; when neither is filled, any tier
                        // in the pair is fine.
                        if (device is Transformer transformer)
                        {
                            var map = VoltageTier.GetTransformerTierMap(transformer.PrefabName);
                            if (map.HasValue)
                            {
                                bool symmetric = map.Value.Input == map.Value.Output;
                                if (__instance.CableType != map.Value.Input && __instance.CableType != map.Value.Output)
                                {
                                    string label = string.IsNullOrEmpty(transformer.DisplayName) ? transformer.PrefabName : transformer.DisplayName;
                                    string pairDesc = symmetric
                                        ? $"{map.Value.Input} cable"
                                        : $"a {map.Value.Input}/{map.Value.Output} cable pair";
                                    __result = CanConstructInfo.InvalidPlacement(
                                        $"Wrong voltage -- {label} only accepts {pairDesc}, not {__instance.CableType}.");
                                    return;
                                }
                                if (!symmetric)
                                {
                                    var inputCable = transformer.InputConnection?.GetCable();
                                    var outputCable = transformer.OutputConnection?.GetCable();
                                    Cable wiredOther = null;
                                    if (inputCable != null && outputCable == null) wiredOther = inputCable;
                                    else if (outputCable != null && inputCable == null) wiredOther = outputCable;
                                    if (wiredOther != null && wiredOther.CableType == __instance.CableType)
                                    {
                                        string label = string.IsNullOrEmpty(transformer.DisplayName) ? transformer.PrefabName : transformer.DisplayName;
                                        __result = CanConstructInfo.InvalidPlacement(
                                            $"Wrong voltage -- {label} already has {wiredOther.CableType} cable on its other side; the two sides must be different tiers.");
                                        return;
                                    }
                                }
                                continue;
                            }
                            // Unknown / modded transformer variant: fall through to the general
                            // IsAllowedOnTier check below, which treats it as tier-exempt.
                        }

                        // (b.2) APC: any tier is fine PER se, but if a side is already wired, the new cable
                        // must match that side's existing tier (because both ports must end up on the same
                        // tier). When both sides are wired and matching, the cable-to-cable check above
                        // already covers extending one of those networks.
                        if (device is AreaPowerControl apc)
                        {
                            var inputTier = VoltageTier.GetUniformTier(apc.InputNetwork);
                            var outputTier = VoltageTier.GetUniformTier(apc.OutputNetwork);
                            if (inputTier.HasValue && inputTier.Value != __instance.CableType)
                            {
                                __result = CanConstructInfo.InvalidPlacement(
                                    $"Wrong voltage -- this Area Power Controller has {inputTier.Value} cable on its other side; both sides must match.");
                                return;
                            }
                            if (outputTier.HasValue && outputTier.Value != __instance.CableType)
                            {
                                __result = CanConstructInfo.InvalidPlacement(
                                    $"Wrong voltage -- this Area Power Controller has {outputTier.Value} cable on its other side; both sides must match.");
                                return;
                            }
                            continue;
                        }

                        if (!VoltageTier.IsAllowedOnTier(device, __instance.CableType))
                        {
                            string label = string.IsNullOrEmpty(device.DisplayName) ? device.PrefabName : device.DisplayName;
                            __result = CanConstructInfo.InvalidPlacement(
                                $"Wrong voltage -- {label} doesn't accept {__instance.CableType} cable.");
                            return;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Preview cable not gridded yet, or an API shape differs -- leave placement allowed; the
                // reactive burns from VoltageTierEnforcer will still resolve any mixed-tier or
                // misplaced-device result once power flows on the resulting network.
            }
        }

        /// <summary>
        ///     True iff <paramref name="cursor"/>'s preview placement would attach to at least one of
        ///     <paramref name="device"/>'s OpenEnds whose ConnectionType includes the Power bit. Used to
        ///     scope the per-device tier rules to power-port adjacencies: a data-only adjacency (cursor's
        ///     open end touching the device only at a Data-bit Connection) should not engage tier rules.
        ///
        ///     ConnectionType is a NetworkType bitmask: pure-data ports have Data only, pure-power ports
        ///     have Power only, and combo ports have PowerAndData (= Power | Data). Cable.IsConnected is
        ///     the same grid-adjacency-plus-bitmask test SmallGrid uses everywhere (decompile line 294154).
        /// </summary>
        private static bool CursorAttachesToPowerPortOf(Cable cursor, Device device)
        {
            if (cursor == null || device == null || device.OpenEnds == null)
                return false;
            for (int i = 0; i < device.OpenEnds.Count; i++)
            {
                var oe = device.OpenEnds[i];
                if (oe == null)
                    continue;
                if (!VoltageTier.ConnectionCarriesPower(oe))
                    continue;
                try
                {
                    if (cursor.IsConnected(oe))
                        return true;
                }
                catch
                {
                    // Either side's grid not initialized yet -- treat as no match and let the next frame
                    // try again. The placement preview re-runs CanConstruct continuously.
                }
            }
            return false;
        }

        /// <summary>
        ///     Restores the Option B immediate re-check (POWER.md §4.3) after 0.2.6403 removed the static
        ///     <c>CableNetwork.OnNetworkChanged</c> event. The event used to fire at the tail of all three
        ///     CableNetwork constructors, of Add(Cable), and of the rebuild BFS; these postfixes decorate
        ///     the surviving mutation points so every membership change (placement, merge, split re-fold,
        ///     save/join load) still calls <see cref="VoltageTierEnforcer.RequestRecheck"/>. The rebuild
        ///     BFS needs no postfix of its own: it always runs through the seed constructor and per-cable
        ///     Add, and RequestRecheck coalesces the burst into one main-thread re-check exactly as it
        ///     coalesced the event's multiple firings per mutation.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(CableNetwork), MethodType.Constructor)]
        public static void CableNetwork_Ctor_Postfix(CableNetwork __instance)
        {
            VoltageTierEnforcer.InvalidateNet(__instance.ReferenceId);
            VoltageTierEnforcer.RequestRecheck();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CableNetwork), MethodType.Constructor, typeof(long))]
        public static void CableNetwork_CtorFromId_Postfix(CableNetwork __instance)
        {
            // The id-carrying constructor resurrects a SAVED network id (Cable.DeserializeSave), so a
            // cached verdict from an earlier life of that id must not survive into this one.
            VoltageTierEnforcer.InvalidateNet(__instance.ReferenceId);
            VoltageTierEnforcer.RequestRecheck();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CableNetwork), MethodType.Constructor, typeof(Cable))]
        public static void CableNetwork_CtorFromCable_Postfix(CableNetwork __instance)
        {
            VoltageTierEnforcer.InvalidateNet(__instance.ReferenceId);
            VoltageTierEnforcer.RequestRecheck();
        }

        [HarmonyPostfix, HarmonyPatch(typeof(CableNetwork), nameof(CableNetwork.Add), typeof(Cable))]
        public static void CableNetwork_Add_Postfix(CableNetwork __instance)
        {
            VoltageTierEnforcer.InvalidateNet(__instance.ReferenceId);
            VoltageTierEnforcer.RequestRecheck();
        }

        // Removal cannot make a uniform network mixed, but destroying a SEATED cable clears its cell
        // and can orphan a co-located theft victim on an UNNAMEABLE other network (the single-slot
        // cell hides it). Two consequences: the eviction is CACHE-WIDE (only a full eviction
        // guarantees that network's StackedTheft flag is recomputed), and a recheck IS scheduled,
        // because the orphan's network is typically device-less and the per-tick backstop never sees
        // device-less nets (GridSnapshot skips DeviceList.Count == 0); the coalesced main-thread
        // recheck over AllCableNetworks is the only eye that can find it. Removals are rare, so this
        // is one lazy recompute wave per topology change, not a per-tick cost.
        [HarmonyPostfix, HarmonyPatch(typeof(CableNetwork), nameof(CableNetwork.Remove), typeof(Cable))]
        public static void CableNetwork_Remove_Postfix()
        {
            VoltageTierEnforcer.InvalidateAll();
            VoltageTierEnforcer.RequestRecheck();
        }
    }
}
