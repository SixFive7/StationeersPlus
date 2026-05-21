using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace InspectorPlus
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class InspectorPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.inspectorplus";
        public const string PluginName = "InspectorPlus";
        public const string PluginVersion = "1.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyboardShortcut> SnapshotKey;
        internal static ConfigEntry<bool> ForceUnpauseWhenHeadless;

        internal static string _watchDir;
        internal static string _outputDir;
        private FileSystemWatcher _watcher;

        void Awake()
        {
            Log = Logger;

            SnapshotKey = Config.Bind(
                "Client - Snapshots", "Snapshot Key", new KeyboardShortcut(KeyCode.F8),
                new ConfigDescription(
                    "(Client-local) Press this key in-game to write a full scene snapshot to BepInEx/inspector/snapshots/.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ForceUnpauseWhenHeadless = Config.Bind(
                "Server - Headless", "Force Unpause Without Client", false,
                new ConfigDescription(
                    "(Server-authoritative) Headless dedicated servers only: force the simulation to run with no client connected, so request-file snapshots can be captured by automated tooling without a player joining. Off by default. No effect on a client or single-player.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            _watchDir = Path.Combine(Paths.BepInExRootPath, "inspector", "requests");
            _outputDir = Path.Combine(Paths.BepInExRootPath, "inspector", "snapshots");
            Directory.CreateDirectory(_watchDir);
            Directory.CreateDirectory(_outputDir);

            MainThreadDispatcher.Initialize();
            StartFileWatcher();

            Log.LogInfo($"InspectorPlus {PluginVersion} loaded. Watching: {_watchDir}");

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(InspectorPlusPlugin).Assembly);
                Log.LogInfo("Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony patch failed: {ex}");
            }

            // Update() and coroutines do not fire reliably inside the dedicated
            // server's headless main loop, so request polling also runs from a
            // coroutine on the long-lived MainThreadDispatcher GameObject.
            try
            {
                MainThreadDispatcher.StartPollingCoroutine(_watchDir, ProcessRequestFileInline);
            }
            catch (Exception ex)
            {
                Log.LogError($"Coroutine polling start failed: {ex}");
            }
        }

        void Update()
        {
            try
            {
                if (SnapshotKey.Value.IsDown())
                {
                    Log.LogInfo("Snapshot key pressed, taking full snapshot...");
                    TakeSnapshot(null);
                }
            }
            catch { }
            Poll();
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

            // Headless / Mono FileSystemWatcher is unreliable. Always do an
            // initial scan and then poll the directory in Update(). Process
            // inline on the main thread (no dispatcher hop) so a non-running
            // dispatcher Update can't swallow the work.
            try
            {
                var initial = Directory.GetFiles(_watchDir, "*.json");
                foreach (var existing in initial)
                {
                    ProcessRequestFileInline(existing);
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Initial request scan failed: {ex}");
            }
        }

        internal static void ProcessRequestFileInline(string fullPath)
        {
            try
            {
                var requestJson = File.ReadAllText(fullPath);
                var request = SnapshotRequest.Parse(requestJson);
                TakeSnapshot(request);
                File.Delete(fullPath);
            }
            catch (Exception ex)
            {
                Log.LogError($"Processing {Path.GetFileName(fullPath)} failed: {ex}");
            }
        }

        // Called from a Harmony postfix on ElectricityManager.ElectricityTick (see
        // RequestPollOnTickPatch.cs). The dedicated server's headless main loop does
        // not drive MonoBehaviour.Update() or coroutines reliably, so the Update- and
        // coroutine-based polling can stall after world load. ElectricityTick runs on
        // the server's game-thread tick driver whenever the simulation is running, so
        // it is a reliable main-thread pump for the request-file scan.
        internal static void ProcessPendingRequests()
        {
            try
            {
                if (string.IsNullOrEmpty(_watchDir)) return;
                var files = Directory.GetFiles(_watchDir, "*.json");
                if (files.Length == 0) return;
                foreach (var f in files) ProcessRequestFileInline(f);
            }
            catch (Exception ex)
            {
                Log.LogError($"ProcessPendingRequests failed: {ex}");
            }
        }

        private float _pollAccum;
        private const float PollIntervalSeconds = 2f;

        private void Poll()
        {
            _pollAccum += Time.unscaledDeltaTime;
            if (_pollAccum < PollIntervalSeconds) return;
            _pollAccum = 0f;
            try
            {
                var files = Directory.GetFiles(_watchDir, "*.json");
                if (files.Length > 0)
                {
                    foreach (var existing in files)
                    {
                        ProcessRequestFileInline(existing);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"Request poll failed: {ex}");
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

        internal static void TakeSnapshot(SnapshotRequest request)
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
