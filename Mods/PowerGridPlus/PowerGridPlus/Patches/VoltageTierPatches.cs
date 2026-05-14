// NEW-3: three-tier transmission-voltage gating. See VoltageTier for the policy.

using System;
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
        ///     <see cref="Power.PowerGridTick"/> remain the authoritative backstop -- they fire on the next
        ///     power tick if power is actually flowing.)
        ///
        ///     Note: this is intentionally cable-side only. Placing a device on a wrong-tier cable is NOT
        ///     rejected at build time; that case is handled reactively by the tick burning the cable next
        ///     to the misplaced device.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Cable), nameof(Cable.CanConstruct))]
        public static void Cable_CanConstruct_Postfix(Cable __instance, ref CanConstructInfo __result)
        {
            if (!Settings.EnableVoltageTiers.Value)
                return;
            if (!__result.CanConstruct)
                return;

            try
            {
                // (a) Cable-to-cable: don't allow placing into a network that already has a different tier.
                var connected = CableNetwork.ConnectedNetworks(__instance);
                if (connected != null)
                {
                    foreach (var network in connected)
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
                // be allowed on the new cable's tier. ConnectedDevices() is the same SmallGrid pipeline as
                // ConnectedCables() and is valid on a cursor ghost (Connection.SetGrids refreshes the
                // ghost's OpenEnds' LocalGrid every frame -- see Research/Patterns/CursorAdjacencyLookup.md).
                var adjacentDevices = __instance.ConnectedDevices();
                if (adjacentDevices != null)
                {
                    foreach (var device in adjacentDevices)
                    {
                        if (device == null)
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
                // reactive burns from PowerGridTick will still resolve any mixed-tier or misplaced-device
                // result once power flows on the resulting network.
            }
        }
    }
}
