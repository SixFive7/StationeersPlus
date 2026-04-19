using BepInEx;
using HarmonyLib;

namespace {{ModCodeName}}
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.{{modcodename-lowercase}}";
        public const string PluginName = "{{Mod Display Name}}";
        public const string PluginVersion = "0.1.0";

        private Harmony _harmony;

        private void Awake()
        {
            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll();
            Logger.LogInfo($"Loaded {PluginName} v{PluginVersion}");
        }
    }
}
