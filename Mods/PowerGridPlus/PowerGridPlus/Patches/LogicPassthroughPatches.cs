using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Makes bridging devices logic-transparent: a logic reader on one side of the bridge sees
    ///     devices on the other side. Covered bridges:
    ///
    ///     - Transformer / AreaPowerControl: bridge between InputNetwork and OutputNetwork on a single device.
    ///     - Battery: bridge between InputNetwork (charging side) and OutputNetwork (discharging side).
    ///     - PowerTransmitter / PowerReceiver pair: bridge from the TX cable network (tx.InputNetwork)
    ///       to the RX cable network (rx.OutputNetwork) via the wireless link.
    ///
    ///     Mechanism: postfix on <see cref="CableNetwork.RefreshPowerAndDataDeviceLists"/>. For each
    ///     supported bridging device sitting on the local network, find its "other" side cable network
    ///     and append every entry in that network's <see cref="CableNetwork.DeviceList"/> (deduped)
    ///     into the local data device list. The bridging device itself is already in both sides'
    ///     <see cref="CableNetwork.DeviceList"/> via its two cable connections (or via the wireless
    ///     link for TX/RX pairs), so its own <see cref="LogicType"/> slots become readable from both
    ///     sides as a side effect.
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
            if (!Settings.EnableTransformerLogicPassthrough.Value
                && !Settings.EnableAreaPowerControlLogicPassthrough.Value
                && !Settings.EnableBatteryLogicPassthrough.Value
                && !Settings.EnablePowerTransmitterLogicPassthrough.Value)
                return;

            var devices = __instance.DeviceList;
            for (int i = devices.Count - 1; i >= 0; i--)
            {
                var device = devices[i];
                if (device == null) continue;

                CableNetwork other = null;

                if (device is Transformer transformer)
                {
                    if (!Settings.EnableTransformerLogicPassthrough.Value) continue;
                    if (PassthroughModeStore.GetMode(transformer) == 0) continue;
                    other = (transformer.InputNetwork == __instance) ? transformer.OutputNetwork : transformer.InputNetwork;
                }
                else if (device is AreaPowerControl apc)
                {
                    if (!Settings.EnableAreaPowerControlLogicPassthrough.Value) continue;
                    other = (apc.InputNetwork == __instance) ? apc.OutputNetwork : apc.InputNetwork;
                }
                else if (device is Battery battery)
                {
                    if (!Settings.EnableBatteryLogicPassthrough.Value) continue;
                    if (PassthroughModeStore.GetMode(battery) == 0) continue;
                    other = (battery.InputNetwork == __instance) ? battery.OutputNetwork : battery.InputNetwork;
                }
                else if (device is PowerTransmitter tx)
                {
                    if (!Settings.EnablePowerTransmitterLogicPassthrough.Value) continue;
                    if (PassthroughModeStore.GetMode(tx) == 0) continue;
                    // TX's cable side is its InputNetwork; partner cable is rx.OutputNetwork.
                    other = tx.LinkedReceiver?.OutputNetwork;
                }
                else if (device is PowerReceiver rx)
                {
                    if (!Settings.EnablePowerTransmitterLogicPassthrough.Value) continue;
                    if (PassthroughModeStore.GetMode(rx) == 0) continue;
                    // RX's cable side is its OutputNetwork; partner cable is tx.InputNetwork.
                    other = rx.LinkedPowerTransmitter?.InputNetwork;
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
