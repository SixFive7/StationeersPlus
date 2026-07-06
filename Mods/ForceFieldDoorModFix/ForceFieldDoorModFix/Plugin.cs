using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace ForceFieldDoorModFix
{
    // Temporary third-party compatibility shim for ForceFieldDoorMod (Steam Workshop 3328065049).
    // See ForceFieldDoorPatch for the actual fix. This mod has no settings and does nothing unless
    // a broken copy of ForceFieldDoorMod is present.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.forcefielddoormodfix";
        public const string PluginName = "Force Field Door Mod Fix";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            _harmony = new Harmony(PluginGuid);
            ForceFieldDoorPatch.Install(_harmony);
        }
    }
}
