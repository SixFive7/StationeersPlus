using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Assets.Scripts.Objects;
using LaunchPadBooster;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MaintenanceBureauPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class MaintenanceBureauPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.sixfive7.maintenancebureauplus";
        public const string PluginName = "MaintenanceBureauPlus";
        public const string PluginVersion = "0.1.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);

        internal static ManualLogSource Log;
        internal static LlmEngine Engine;
        internal static PersonaRegistry Personas;
        internal static PersonaMemoryStore Memory;
        internal static ConversationState Conversation;
        internal static string PluginDir;

        // User-visible settings. Everything else is hardcoded below.
        internal static ConfigEntry<int> MinTurns;
        internal static ConfigEntry<int> MaxTurns;

        // Hardcoded constants. Change requires a code edit + new release.
        public const string ModelFileName = "qwen2.5-1.5b-instruct-q4_k_m.gguf";
        public const int MaxTokensPerReply = 384;
        public const int MaxTokensForClosing = 512;
        public const int MaxTokensForPersonaSelection = 256;
        public const int MaxTokensForSuperSummary = 64;
        public const float Temperature = 0.8f;
        public const int InferenceThreads = 4;
        public const int ContextSize = 4096;
        public const int PersonaMemoryCap = 200;
        // Stun damage at blackout is deliberately well above the 100 unconscious
        // threshold so natural stun decay (3 per life tick) does not wake the
        // player during the LLM closing-message inference wait. The game clamps
        // the channel internally; we never read it back. A later manual write of
        // StunWakeDuringDescent just before the capsule teleport resets the
        // countdown so the player wakes up groggy during the 13.5 s descent.
        public const int StunBlackout = 1000;
        public const int StunWakeDuringDescent = 80;

        private volatile bool _modelReady;
        private volatile bool _patchesApplied;

        void Awake()
        {
            Log = Logger;
            BindConfig();

            PluginDir = Path.GetDirectoryName(Info.Location);

            // Preload LLamaSharp's native libs by absolute path before anything
            // touches LLama.Native.NativeApi. Mono's default P/Invoke probe does
            // not search our BepInEx/plugins/<ModName>/runtimes/win-x64/native/
            // folder, so DllImport("llama") would fail. Windows caches each
            // preloaded module under its short file name; subsequent DllImport
            // lookups for "llama" resolve to the already-loaded llama.dll.
            PreloadNativeLibraries();

            Conversation = new ConversationState();

            Personas = new PersonaRegistry();
            Personas.LoadFromEmbeddedResource();

            var stateDir = Path.Combine(PluginDir, "state");
            var memoryPath = Path.Combine(stateDir, "persona_memory.json");
            Memory = new PersonaMemoryStore(memoryPath, PersonaMemoryCap);
            Memory.Load();

            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private void PreloadNativeLibraries()
        {
            var nativeDir = Path.Combine(PluginDir, "runtimes", "win-x64", "native");
            if (!Directory.Exists(nativeDir))
            {
                Log.LogWarning("Native library directory not found: " + nativeDir);
                return;
            }

            // Load order matters: the CRT shims first, then ggml, then llama, then llava.
            // llama.dll imports ggml.dll and the VC++ runtime, so their base file names
            // must already be registered by the time llama.dll's dependencies resolve.
            var loadOrder = new[]
            {
                "vcruntime140.dll",
                "vcruntime140_1.dll",
                "msvcp140.dll",
                "ggml.dll",
                "llama.dll",
                "llava_shared.dll",
            };

            foreach (var name in loadOrder)
            {
                var path = Path.Combine(nativeDir, name);
                if (!File.Exists(path))
                {
                    Log.LogInfo("Native lib not present (skipping): " + name);
                    continue;
                }
                var handle = LoadLibrary(path);
                if (handle == IntPtr.Zero)
                {
                    var err = Marshal.GetLastWin32Error();
                    Log.LogError("Failed to preload " + name + " (Win32 error: " + err + ")");
                }
                else
                {
                    Log.LogInfo("Preloaded native lib: " + name);
                }
            }
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var modelsDir = Path.Combine(PluginDir, "models");
            var modelPath = Path.Combine(modelsDir, ModelFileName);

            if (!File.Exists(modelPath))
            {
                Log.LogError("Model file not found: " + modelPath);
                Log.LogError("Download a GGUF model and place it at: " + modelPath);
                Log.LogError("Expected filename: " + ModelFileName);
                return;
            }

            Log.LogInfo("Loading model in background: " + modelPath);

            var loaderThread = new Thread(() =>
            {
                try
                {
                    Engine = new LlmEngine();
                    Engine.Load(modelPath, ContextSize, InferenceThreads);
                    Log.LogInfo("Model loaded. Patches apply on the next frame.");
                    _modelReady = true;
                }
                catch (Exception e)
                {
                    Log.LogFatal("Background model load failed: " + e);
                }
            })
            {
                IsBackground = true,
                Name = "MBP-ModelLoad",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            loaderThread.Start();
        }

        void Update()
        {
            if (_modelReady && !_patchesApplied)
            {
                _patchesApplied = true;
                try
                {
                    var harmony = new Harmony(PluginGuid);
                    harmony.PatchAll();
                    Log.LogInfo("Maintenance Bureau is open for business.");
                }
                catch (Exception e)
                {
                    Log.LogFatal("Failed to apply patches: " + e);
                }
            }

            MainThreadQueue.Drain();
        }

        void OnDestroy()
        {
            Engine?.Dispose();
            Engine = null;
            Memory?.Save();
        }

        private void BindConfig()
        {
            MinTurns = Config.Bind(
                "Server - Bureau", "Minimum Turns", 5,
                new ConfigDescription(
                    "(Server-authoritative) Lower bound on conversational hoops per request cycle. The officer may approve earlier only if the player is exceptionally cooperative, never below this bound.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            MaxTurns = Config.Bind(
                "Server - Bureau", "Maximum Turns", 15,
                new ConfigDescription(
                    "(Server-authoritative) Upper bound on conversational hoops per request cycle. At this turn count the officer is forced to approve.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));
        }
    }

    // Simple concurrent queue for dispatching work from worker threads onto the
    // Unity main thread. Drained once per frame by Plugin.Update().
    internal static class MainThreadQueue
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public static void Enqueue(Action a)
        {
            if (a != null) _queue.Enqueue(a);
        }

        public static void Drain()
        {
            while (_queue.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("MainThreadQueue action failed: " + ex.Message);
                }
            }
        }
    }
}
