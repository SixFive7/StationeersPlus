using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects.Electrical;
using Objects.Rockets;

namespace PowerGridPlus.Patches
{
    // Attaches a FaultFlashBehaviour to every device whose on/off button can flash a fault
    // colour (POWER.md §11.4): the six button-bearing segmenting devices (Transformer, Battery,
    // AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale) and the
    // button-bearing producers (PowerGeneratorPipe covers GasFuelGenerator, PowerGeneratorSlot
    // covers SolidFuelGenerator, StirlingEngine). Hover-only devices (SolarPanel, wind turbines,
    // RTG, TurbineGenerator, PowerConnector, RocketPowerUmbilicalFemale) get no flash; their fault
    // state surfaces via FaultHoverPatches.
    //
    // Hook point: Thing.OnRegistered (called for every Thing as it joins the world, both fresh
    // placements and save / join loads). Also initialises Transformer.Setting = OutputMaximum for
    // new constructions (TransformerSettingInitPatch, separate file, same hook).
    //
    // Idempotent: checks for an existing component on the same GameObject to avoid double-attach.
    [HarmonyLib.HarmonyPatch(typeof(Assets.Scripts.Objects.Thing), nameof(Assets.Scripts.Objects.Thing.OnRegistered))]
    public static class TransformerFlashAttachPatches
    {
        private static bool WantsFlash(Assets.Scripts.Objects.Thing thing)
        {
            switch (thing)
            {
                case Transformer _:
                case Assets.Scripts.Objects.Electrical.Battery _:
                case AreaPowerControl _:
                case PowerTransmitter _:
                case PowerReceiver _:
                case RocketPowerUmbilicalMale _:
                case PowerGeneratorPipe _:     // covers GasFuelGenerator
                case PowerGeneratorSlot _:     // covers SolidFuelGenerator
                case StirlingEngine _:
                    return true;
                default:
                    return false;
            }
        }

        public static void Postfix(Assets.Scripts.Objects.Thing __instance, Cell cell)
        {
            if (!WantsFlash(__instance)) return;
            if (__instance.gameObject == null) return;
            var existing = __instance.GetComponent<FaultFlashBehaviour>();
            if (existing != null) return;
            var behaviour = __instance.gameObject.AddComponent<FaultFlashBehaviour>();
            behaviour.Init(__instance);
        }
    }
}
