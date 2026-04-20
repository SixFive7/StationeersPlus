using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.IO;
using UnityEngine;

namespace InspectorPlus
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class InspectorPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.inspectorplus";
        public const string PluginName = "InspectorPlus";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyboardShortcut> SnapshotKey;

        private static string _watchDir;
        private static string _outputDir;
        private FileSystemWatcher _watcher;

        void Awake()
        {
            Log = Logger;

            SnapshotKey = Config.Bind(
                "General", "Snapshot Key", new KeyboardShortcut(KeyCode.F8),
                "Press this key in-game to write a full snapshot to the output folder.");

            _watchDir = Path.Combine(Paths.BepInExRootPath, "inspector", "requests");
            _outputDir = Path.Combine(Paths.BepInExRootPath, "inspector", "snapshots");
            Directory.CreateDirectory(_watchDir);
            Directory.CreateDirectory(_outputDir);

            MainThreadDispatcher.Initialize();
            StartFileWatcher();

            Log.LogInfo($"InspectorPlus {PluginVersion} loaded. Watching: {_watchDir}");
        }

        void Update()
        {
            if (SnapshotKey.Value.IsDown())
            {
                Log.LogInfo("Snapshot key pressed, taking full snapshot...");
                TakeSnapshot(null);
            }
        }

        void OnDestroy()
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }

        private void StartFileWatcher()
        {
            try
            {
                _watcher = new FileSystemWatcher(_watchDir, "*.json");
                _watcher.Created += OnRequestFileCreated;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed to start file watcher: {ex.Message}");
            }
        }

        private void OnRequestFileCreated(object sender, FileSystemEventArgs e)
        {
            // FileSystemWatcher fires on a background thread.
            // Queue the work onto Unity's main thread so we can safely
            // access GameObjects and Components.
            MainThreadDispatcher.Enqueue(() =>
            {
                try
                {
                    Log.LogInfo($"Request file detected: {e.Name}");
                    var requestJson = File.ReadAllText(e.FullPath);
                    var request = SnapshotRequest.Parse(requestJson);
                    TakeSnapshot(request);
                    File.Delete(e.FullPath);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Error processing request {e.Name}: {ex}");
                }
            });
        }

        private void TakeSnapshot(SnapshotRequest request)
        {
            try
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                var outputPath = Path.Combine(_outputDir, $"snapshot_{timestamp}.json");

                var json = ObjectWalker.Walk(request);
                File.WriteAllText(outputPath, json);

                Log.LogInfo($"Snapshot written: {outputPath}");
            }
            catch (Exception ex)
            {
                Log.LogError($"Snapshot failed: {ex}");
            }
        }
    }
}
