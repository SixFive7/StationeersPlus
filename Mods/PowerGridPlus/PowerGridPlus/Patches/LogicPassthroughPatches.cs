using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Makes Area Power Controllers and Transformers logic-transparent: a logic reader on one
    ///     side of the bridge sees devices on the other side, and the bridging device's own logic
    ///     ports (Setting, Power Actual, ...) are visible from both sides.
    ///
    ///     Mechanism: postfix on <see cref="CableNetwork.RefreshPowerAndDataDeviceLists"/>. For each
    ///     <see cref="ElectricalInputOutput"/> sitting on the local network, find its "other" side
    ///     network and append every entry in that network's <see cref="CableNetwork.DeviceList"/>
    ///     (deduped) into the local data device list. The bridging device itself is already in both
    ///     sides' <see cref="CableNetwork.DeviceList"/> via its two cable connections, so its own
    ///     <see cref="LogicType"/> slots become readable from both sides as a side effect.
    ///
    ///     Mirrors the vanilla <c>HandleDataNetTransmissionDevice</c> rocket-data-link pattern, but
    ///     symmetric: the bridging device acts as both transmitter and receiver against itself.
    /// </summary>
    [HarmonyPatch(typeof(CableNetwork), "RefreshPowerAndDataDeviceLists")]
    public static class LogicPassthroughPatches
    {
        // Capture the data-dirty flag before the base method clears it; the postfix only merges when
        // the data list was actually rebuilt this call, to avoid mutating a stale cached list.
        [HarmonyPrefix]
        public static void Prefix(bool ___DataDeviceListDirty, out bool __state)
        {
            __state = ___DataDeviceListDirty;
        }

        [HarmonyPostfix]
        public static void Postfix(CableNetwork __instance, bool __state, List<Device> ____dataDeviceList)
        {
            if (!__state) return;
            if (!Settings.EnableTransformerLogicPassthrough.Value && !Settings.EnableAreaPowerControlLogicPassthrough.Value)
                return;

            var devices = __instance.DeviceList;
            for (int i = devices.Count - 1; i >= 0; i--)
            {
                var device = devices[i];
                if (device == null) continue;

                CableNetwork other;
                if (device is Transformer transformer)
                {
                    // Feature kill-switch + per-device mode. Default mode per PrefabName
                    // (small transformer + reversed default to 1, others default to 0)
                    // is applied by PassthroughModeStore.GetMode when no override exists.
                    if (!Settings.EnableTransformerLogicPassthrough.Value) continue;
                    if (PassthroughModeStore.GetMode(transformer) == 0) continue;
                    other = (transformer.InputNetwork == __instance) ? transformer.OutputNetwork : transformer.InputNetwork;
                }
                else if (device is AreaPowerControl apc)
                {
                    if (!Settings.EnableAreaPowerControlLogicPassthrough.Value) continue;
                    other = (apc.InputNetwork == __instance) ? apc.OutputNetwork : apc.InputNetwork;
                }
                else
                {
                    continue;
                }

                if (other == null || other == __instance) continue;

                var remote = other.DeviceList;
                for (int j = remote.Count - 1; j >= 0; j--)
                {
                    var remoteDevice = remote[j];
                    if (remoteDevice == null) continue;
                    if (!____dataDeviceList.Contains(remoteDevice))
                        ____dataDeviceList.Add(remoteDevice);
                }
            }
        }
    }
}
