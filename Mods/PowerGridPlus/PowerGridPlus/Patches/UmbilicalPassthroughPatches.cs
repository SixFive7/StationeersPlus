using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using Objects.Rockets;

namespace PowerGridPlus.Patches
{
    // Umbilical-specific glue for logic passthrough across a docked rocket power-umbilical pair.
    //
    //  - The Female / FemaleSide inherit the four Device logic methods, so their LogicPassthroughMode
    //    read / write / get / set ride the base-Device patches in InheritedLogicablePassthroughLogicPatches
    //    (its IsBridge now includes both umbilical halves). The Male inherits all of them but CanLogicRead.
    //  - The Male OVERRIDES CanLogicRead, so the base-Device CanLogicRead patch is shadowed for it; we
    //    expose LogicPassthroughMode on the Male with a direct patch here.
    //  - Docking / undocking carries no vanilla data-list dirty (the coupling is a battery-buffer transfer
    //    with a null CableNetwork, not a network merge), so the merged device lists never refresh when a
    //    pair connects or disconnects. We re-fire the refresh on SetPartner, mirroring DishLinkPatches.
    [HarmonyPatch]
    public static class UmbilicalPassthroughPatches
    {
        // Male.CanLogicRead is an override, invisible to the base-Device prefix; expose LogicPassthroughMode
        // on it directly. Always readable, matching the other bridges -- the master toggle gates the actual
        // bridging in PassthroughTopology.IsEnabledBridge, not the logic port itself.
        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.CanLogicRead), new[] { typeof(LogicType) })]
        public static void MaleCanReadPassthrough(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.LogicPassthroughMode) __result = true;
        }

        // Dock / undock refresh. The prefix captures the partner BEFORE the SetPartner write so an unpair
        // (SetPartner(null)) still refreshes the side being detached; the postfix refreshes own + old + new
        // partner networks, so a connect, a disconnect, and a re-dock all rebuild the merged device lists on
        // the affected networks. Per-peer: each peer runs FindAndSetOtherUmbilical from geometry and sets
        // its own _partnerUmbilical, so this fires and refreshes locally on the host and on clients alike.
        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.SetPartner))]
        public static void MaleSetPartnerPrefix(RocketPowerUmbilicalMale __instance, out ElectricalInputOutput __state)
            => __state = PassthroughTopology.GetUmbilicalPartner(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.SetPartner))]
        public static void MaleSetPartnerPostfix(RocketPowerUmbilicalMale __instance, ElectricalInputOutput __state)
            => Refresh(__instance, __state);

        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.SetPartner))]
        public static void FemaleSetPartnerPrefix(RocketPowerUmbilicalFemale __instance, out ElectricalInputOutput __state)
            => __state = PassthroughTopology.GetUmbilicalPartner(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.SetPartner))]
        public static void FemaleSetPartnerPostfix(RocketPowerUmbilicalFemale __instance, ElectricalInputOutput __state)
            => Refresh(__instance, __state);

        private static void Refresh(ElectricalInputOutput umbilical, ElectricalInputOutput oldPartner)
        {
            if (umbilical == null) return;
            var nets = new List<CableNetwork>();
            // Own + current partner networks (GetBridgeNetworks reads the post-write partner).
            foreach (var net in PassthroughTopology.GetBridgeNetworks(umbilical))
                AddNet(nets, net);
            // The old partner's own networks too, so an unpair refreshes the detached side.
            if (oldPartner != null)
            {
                AddNet(nets, oldPartner.InputNetwork);
                AddNet(nets, oldPartner.OutputNetwork);
                AddNet(nets, oldPartner.DataCableNetwork);
            }
            if (nets.Count == 0) return;
            for (int i = 0; i < nets.Count; i++)
                nets[i].DirtyPowerAndDataDeviceLists();
            CableNetworkPatches.ScheduleCascade(nets);
        }

        private static void AddNet(List<CableNetwork> list, CableNetwork net)
        {
            if (net != null && !list.Contains(net)) list.Add(net);
        }
    }
}
