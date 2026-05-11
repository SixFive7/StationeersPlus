using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace InspectorPlus
{
    // Diagnostic-only Harmony patches that log dish state from the live
    // simulation. PowerTransmitter.OnPowerTick fires reliably on the headless
    // dedicated server every power tick; that gives us a guaranteed main-thread
    // hook regardless of MonoBehaviour Update lifecycle quirks. Logged values
    // are throttled per-instance so the log doesn't drown.
    internal static class DishProbe
    {
        private static readonly FieldInfo LinkedReceiverField =
            AccessTools.Field(typeof(PowerTransmitter), "_linkedReceiver");
        private static readonly FieldInfo LinkedDistanceField =
            AccessTools.Field(typeof(PowerTransmitter), "_linkedReceiverDistance");
        private static readonly FieldInfo PowerProvidedField =
            AccessTools.Field(typeof(PowerTransmitter), "_powerProvided");
        private static readonly FieldInfo TryTargetCountField =
            AccessTools.Field(typeof(PowerTransmitter), "_tryTargetCount");

        private static int _tickCounter;

        public static void OnTransmitterTick(PowerTransmitter tx)
        {
            // Use the periodic OnPowerTick of any one TX as a heartbeat to
            // dump the FULL set of TXs and RXs from Thing.AllThings. This
            // bypasses the "only cable-connected dishes tick" issue.
            if ((_tickCounter++ % 60) != 0) return;
            DumpAllDishes();
        }

        public static void DumpAllDishes()
        {
            int txCount = 0, rxCount = 0;
            var snapshot = new System.Collections.Generic.List<Thing>();
            OcclusionManager.AllThings.ForEach(t => { if (t != null) snapshot.Add(t); });
            foreach (var t in snapshot)
            {
                if (t is PowerTransmitter tx)
                {
                    var rx = LinkedReceiverField?.GetValue(tx) as PowerReceiver;
                    var dist = LinkedDistanceField != null ? (float)LinkedDistanceField.GetValue(tx) : -1f;
                    var pwr = PowerProvidedField != null ? (float)PowerProvidedField.GetValue(tx) : 0f;
                    InspectorPlusPlugin.Log.LogInfo(
                        $"[DishProbe] TX id={tx.ReferenceId} OnOff={tx.OnOff} Powered={tx.Powered} " +
                        $"hasInputNet={(tx.InputNetwork != null)} hasOutNet={(tx.OutputNetwork != null)} " +
                        $"LinkedRx={(rx != null ? rx.ReferenceId.ToString() : "null")} " +
                        $"dist={dist:F1} powerProvided={pwr:F1} vis={tx.VisualizerIntensity:F2}");
                    txCount++;
                    if (tx.OnOff && rx == null && tx.RayTransform != null)
                        DiagnoseUnlinkedTx(tx);
                }
                else if (t is PowerReceiver rx)
                {
                    var lpt = rx.LinkedPowerTransmitter;
                    var dt = rx.DishTarget;
                    string dishInfo;
                    if (dt == null) dishInfo = "DishTarget=null";
                    else
                    {
                        var dtCol = dt.GetComponent<Collider>();
                        var dtPos = dt.position;
                        dishInfo = $"DishTarget pos={dtPos} active={dt.gameObject.activeInHierarchy} " +
                                   $"colliderEnabled={(dtCol != null ? dtCol.enabled : false)} " +
                                   $"colliderInLookup={(dtCol != null && Thing._colliderLookup.ContainsKey(dtCol))}";
                    }
                    InspectorPlusPlugin.Log.LogInfo(
                        $"[DishProbe] RX id={rx.ReferenceId} OnOff={rx.OnOff} Powered={rx.Powered} " +
                        $"hasInputNet={(rx.InputNetwork != null)} hasOutNet={(rx.OutputNetwork != null)} " +
                        $"LinkedTx={(lpt != null ? lpt.ReferenceId.ToString() : "null")} " +
                        $"vis={rx.VisualizerIntensity:F2} {dishInfo}");
                    rxCount++;
                }
            }
            InspectorPlusPlugin.Log.LogInfo($"[DishProbe] === total: {txCount} TXs, {rxCount} RXs ===");
        }

        private static void DiagnoseUnlinkedTx(PowerTransmitter tx)
        {
            var rayT = tx.RayTransform;
            var origin = rayT.position;
            var dir = rayT.TransformDirection(Vector3.forward);
            string hitDesc;
            if (Physics.Raycast(origin, dir, out var hit, float.PositiveInfinity))
            {
                Thing hitThing;
                Thing._colliderLookup.TryGetValue(hit.collider, out hitThing);
                var hitT = hit.transform;
                var hitName = hitT != null ? hitT.name : "(null)";
                var hitGo = hit.collider != null ? hit.collider.gameObject.name : "(null)";
                var hitThingDesc = hitThing != null
                    ? $"{hitThing.GetType().Name}#{hitThing.ReferenceId}"
                    : "(not a Thing)";
                bool isReceiver = hitThing is PowerReceiver;
                bool hitIsDishTarget = isReceiver
                    && ((PowerReceiver)hitThing).DishTarget != null
                    && hit.transform == ((PowerReceiver)hitThing).DishTarget;
                hitDesc = $"hit at {hit.distance:F1}m: collider='{hitGo}' transform='{hitName}' thing={hitThingDesc} isDishTarget={hitIsDishTarget}";

                if (isReceiver)
                {
                    var rxRay = ((PowerReceiver)hitThing).RayTransform;
                    if (rxRay != null)
                    {
                        var fwdAngle = Vector3.Angle(rayT.forward, rxRay.forward);
                        var withinAntiparallel = Mathf.Abs(180f - fwdAngle) <= 7f;
                        hitDesc += $" fwdAngle={fwdAngle:F1}deg antiparallel7deg={withinAntiparallel}";
                    }
                }
            }
            else
            {
                hitDesc = "raycast missed (no hit)";
            }
            InspectorPlusPlugin.Log.LogInfo(
                $"[DishProbe]   ^TX {tx.ReferenceId} ray origin={origin} dir={dir} -> {hitDesc}");
        }
    }

    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.OnPowerTick))]
    public static class PowerTransmitterOnPowerTickProbe
    {
        public static void Postfix(PowerTransmitter __instance)
        {
            DishProbe.OnTransmitterTick(__instance);
        }
    }

    [HarmonyPatch(typeof(Assets.Scripts.Networks.ElectricityManager), nameof(Assets.Scripts.Networks.ElectricityManager.ElectricityTick))]
    public static class ElectricityTickProbe
    {
        private static int _tickN;
        public static void Postfix()
        {
            _tickN++;
            if (_tickN <= 3 || _tickN % 60 == 0)
            {
                InspectorPlusPlugin.Log.LogInfo($"[DishProbe] ElectricityTick #{_tickN} fired");
                if (_tickN <= 3) DishProbe.DumpAllDishes();
            }
        }
    }

    // The dedicated server, on a fresh load, leaves WorldManager.IsGamePaused=true
    // until a client joins; that holds the GameTick driver in its pause spin and
    // ElectricityTick / OnPowerTick never fire. We force-unpause once StartGame
    // has run so simulation proceeds without a client connection.
    [HarmonyPatch(typeof(Assets.Scripts.GameManager), nameof(Assets.Scripts.GameManager.StartGame))]
    public static class StartGameUnpauseProbe
    {
        public static void Postfix()
        {
            try
            {
                global::WorldManager.SetGamePause(pauseGame: false);
                Assets.Scripts.GameManager.UnpauseGameTick();
                InspectorPlusPlugin.Log.LogInfo("[DishProbe] Force-unpaused after StartGame");
            }
            catch (System.Exception ex)
            {
                InspectorPlusPlugin.Log.LogError($"[DishProbe] force-unpause failed: {ex}");
            }
        }
    }
}
