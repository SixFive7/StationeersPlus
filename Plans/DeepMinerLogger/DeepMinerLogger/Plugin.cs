using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace DeepMinerLogger
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.deepminerlogger";
        public const string PluginName = "DeepMinerLogger";
        public const string PluginVersion = "0.1.1";

        internal static ManualLogSource Log;
        internal static string LogDirectory;

        private void Awake()
        {
            Log = Logger;

            try
            {
                var root = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
                while (root != null && !string.Equals(Path.GetFileName(root), "BepInEx", StringComparison.OrdinalIgnoreCase))
                {
                    root = Path.GetDirectoryName(root);
                }
                if (root == null)
                {
                    root = Path.Combine(Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? ".", "..");
                }
                LogDirectory = Path.Combine(root, "DeepMinerLogger", "logs");
                Directory.CreateDirectory(LogDirectory);
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to resolve/create log directory: {e}");
                LogDirectory = Path.Combine(Path.GetTempPath(), "DeepMinerLogger_logs");
                try { Directory.CreateDirectory(LogDirectory); } catch { }
            }

            Log.LogInfo($"{PluginName} v{PluginVersion} loaded. Log directory: {LogDirectory}");

            MinerLogger.Initialize(Log, LogDirectory);

            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll();

            Log.LogInfo($"{PluginName}: Harmony patches applied.");
        }
    }
}
