// NEW-3: three-tier transmission-voltage gating. See VoltageTier for the policy.

using System;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    [HarmonyPatch]
    public static class VoltageTierPatches
    {
        private const string DifferentVoltageMessage = "Cannot connect: different transmission voltage -- use a transformer.";

        /// <summary>
        ///     After a cable registers and merges, if it ended up in a network that now holds more than one
        ///     tier, burn the lowest-tier cable at the junction so the network splits back into single-tier
        ///     pieces. Preferentially burns the cable that just registered, since that is the bridge the
        ///     player placed.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(Cable), "OnRegistered")]
        public static void Cable_OnRegistered_Postfix(Cable __instance)
        {
            if (!Settings.EnableVoltageTiers.Value)
                return;
            if (!GameManager.RunSimulation)
                return;

            var network = __instance.CableNetwork;
            if (network == null)
                return;

            VoltageTier.ResolveMixedTierNetwork(network, preferVictim: __instance);
        }

        /// <summary>
        ///     Best-effort build-time rejection: if the cable being previewed would merge with a network
        ///     that already carries a different tier, refuse the placement with a player-visible message.
        ///     If connectivity can't be determined for the preview cable, this does nothing and the
        ///     reactive burn-on-join in <see cref="Cable_OnRegistered_Postfix"/> handles it after placement.
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
                var connected = CableNetwork.ConnectedNetworks(__instance);
                if (connected == null)
                    return;

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
            catch (Exception)
            {
                // Preview cable not gridded yet (or the API shape differs) -- leave placement allowed;
                // the reactive backstop will still split any mixed-tier network that results.
            }
        }
    }
}
