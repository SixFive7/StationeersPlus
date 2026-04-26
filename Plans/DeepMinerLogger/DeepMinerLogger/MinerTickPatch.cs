using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace DeepMinerLogger
{
    // Postfix on CombustionDeepMiner.OnAtmosphericTick captures state after
    // SpeedTick/HandleShutDown/HandleGasOutput/HandleGasInput have all run this tick,
    // so every value we read reflects the tick's final state.
    [HarmonyPatch(typeof(CombustionDeepMiner), "OnAtmosphericTick")]
    internal static class CombustionDeepMiner_OnAtmosphericTick_Postfix
    {
        private static void Postfix(CombustionDeepMiner __instance)
        {
            // Server/solo only; silently skip on remote clients.
            if (!NetworkManager.IsServer) return;
            MinerLogger.OnTick(__instance);
        }
    }
}
