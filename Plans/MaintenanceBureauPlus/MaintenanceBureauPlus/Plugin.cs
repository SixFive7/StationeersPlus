using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Assets.Scripts.Objects;
using LaunchPadBooster;
using System;
using System.Collections;
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
        internal static BusyResponseRegistry BusyResponses;
        internal static ConversationState Conversation;
        internal static string PluginDir;

        // User-visible settings. Everything else is hardcoded below.
        internal static ConfigEntry<int> MinTurns;
        internal static ConfigEntry<int> MaxTurns;

        // Hardcoded constants. Change requires a code edit + new release.
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

        void Awake()
        {
            Log = Logger;
            // Mirror every Log.* call into StationeersLaunchPad's in-game F3
            // log viewer. BepInEx's ManualLogSource pipeline doesn't reach
            // F3 on its own; this bridge subscribes to LogEvent and forwards
            // each line to LaunchPad's Logger via reflection.
            LaunchPadLog.Hook(Log);
            BindConfig();

            PluginDir = Path.GetDirectoryName(Info.Location);

            // Force-load Microsoft.Bcl.AsyncInterfaces.dll before any type that
            // touches IAsyncEnumerable<T> / IAsyncEnumerator<T> resolves. On
            // Mono / Unity 2022, the JIT otherwise resolves IAsyncEnumerator<T>
            // to a Mono-internal version that lacks MoveNextAsync(), and
            // LLamaSharp.InferAsync() throws "Method not found" at first call.
            PreloadBclAsyncInterfaces();

            // Extract LLamaSharp's native DLLs and the VC++ redist DLLs from
            // embedded resources to <BepInEx>/cache/MaintenanceBureauPlus/natives/.
            // Natives are shipped inside the managed assembly so
            // StationeersLaunchPad's recursive *.dll scan over the mod folder
            // never sees a non-managed DLL; see
            // Research/Workflows/LaunchPadNativeDllTrap.md. Always overwrite and
            // prune superfluous files so the cache matches the current build
            // exactly after every mod update.
            var nativeDir = ExtractNativeLibraries();

            // Preload the extracted native libs by absolute path before anything
            // touches LLama.Native.NativeApi. Mono's default P/Invoke probe does
            // not search the cache dir, so DllImport("llama") would otherwise
            // fail. Windows caches each preloaded module under its short file
            // name; subsequent DllImport lookups for "llama" resolve to the
            // already-loaded llama.dll.
            PreloadNativeLibraries(nativeDir);

            Conversation = new ConversationState();

            Personas = new PersonaRegistry();
            Personas.LoadFromEmbeddedResource();

            BusyResponses = new BusyResponseRegistry();
            BusyResponses.LoadFromEmbeddedResource();

            var stateDir = Path.Combine(PluginDir, "state");
            var memoryPath = Path.Combine(stateDir, "persona_memory.json");
            Memory = new PersonaMemoryStore(memoryPath, PersonaMemoryCap);
            Memory.Load();

            // Register LaunchPadBooster network messages. BureauReplyMessage
            // carries officer reply text from the server to remote clients so
            // their UI can raise the popup locally.
            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<BureauReplyMessage>();
                Log.LogInfo("[DIAG] Registered BureauReplyMessage with LaunchPadBooster networking.");
            }
            catch (Exception e)
            {
                Log.LogError("Network-message registration failed: " + e);
            }

            // Apply Harmony patches eagerly. ChatPatch.Postfix guards on
            // Engine != null && Engine.IsLoaded, so it's a no-op until the
            // model finishes loading in the background.
            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(MaintenanceBureauPlusPlugin).Assembly);
                Log.LogInfo("Harmony patches applied.");
            }
            catch (Exception e)
            {
                Log.LogFatal("Failed to apply Harmony patches: " + e);
            }

            Prefab.OnPrefabsLoaded += OnAllModsLoaded;

            StartWatchdog();

            // Insurance: if BepInEx's BaseUnityPlugin is somehow not receiving
            // Update() calls from Unity, attach an independent MonoBehaviour on
            // its own DontDestroyOnLoad GameObject and drive MainThreadQueue.Drain
            // from that. BaseUnityPlugin's Update will keep running if it works;
            // this just adds a guaranteed-to-tick backup.
            try
            {
                var tickerGO = new UnityEngine.GameObject("MBP_MainThreadTicker");
                UnityEngine.Object.DontDestroyOnLoad(tickerGO);
                tickerGO.AddComponent<MainThreadTicker>();
                Log.LogInfo("MBP_MainThreadTicker GameObject created.");
            }
            catch (Exception e)
            {
                Log.LogError("Failed to create MainThreadTicker: " + e);
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private void PreloadBclAsyncInterfaces()
        {
            var path = Path.Combine(PluginDir, "Microsoft.Bcl.AsyncInterfaces.dll");
            if (!File.Exists(path))
            {
                Log.LogWarning("Microsoft.Bcl.AsyncInterfaces.dll not found at " + path);
                return;
            }
            try
            {
                var asm = System.Reflection.Assembly.LoadFrom(path);
                Log.LogInfo("Force-loaded " + asm.GetName().FullName);
            }
            catch (Exception ex)
            {
                Log.LogError("Failed to force-load Microsoft.Bcl.AsyncInterfaces: " + ex.Message);
            }
        }

        private const string NativeResourcePrefix = "MaintenanceBureauPlus.Natives.";

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        private string ExtractNativeLibraries()
        {
            var nativeDir = Path.Combine(BepInEx.Paths.CachePath, "MaintenanceBureauPlus", "natives");
            Directory.CreateDirectory(nativeDir);

            var asm = typeof(MaintenanceBureauPlusPlugin).Assembly;
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var resourceName in asm.GetManifestResourceNames())
            {
                if (!resourceName.StartsWith(NativeResourcePrefix, StringComparison.Ordinal))
                    continue;

                var fileName = resourceName.Substring(NativeResourcePrefix.Length);
                var destPath = Path.Combine(nativeDir, fileName);

                try
                {
                    using (var src = asm.GetManifestResourceStream(resourceName))
                    using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        src.CopyTo(dst);
                    }
                    written.Add(fileName);
                    Log.LogInfo("Extracted native lib: " + fileName);
                }
                catch (Exception ex)
                {
                    Log.LogError("Failed to extract " + fileName + ": " + ex.Message);
                }
            }

            // Prune any file in the cache dir that the current build did not
            // write. Keeps the cache aligned with the current embedded payload
            // after a mod update that drops or renames a native.
            try
            {
                foreach (var existing in Directory.GetFiles(nativeDir))
                {
                    var name = Path.GetFileName(existing);
                    if (written.Contains(name)) continue;
                    try
                    {
                        File.Delete(existing);
                        Log.LogInfo("Pruned stale native lib: " + name);
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning("Could not prune " + name + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("Cache prune scan failed: " + ex.Message);
            }

            return nativeDir;
        }

        private void PreloadNativeLibraries(string nativeDir)
        {
            if (!Directory.Exists(nativeDir))
            {
                Log.LogWarning("Native library directory not found: " + nativeDir);
                return;
            }

            // Register the cache dir with the Win32 loader so any dependent
            // DLL lookup that happens after the first P/Invoke into llama.dll
            // can resolve transitive imports from the same folder.
            SetDllDirectory(nativeDir);

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

            // By now LaunchPad has registered our LoadedMod. Upgrade the F3
            // bridge from Logger.Global to our mod-specific Logger so the
            // F3 filter dropdown lists "MaintenanceBureauPlus" as a separate
            // entry, in addition to "All".
            LaunchPadLog.TryUpgradeToModLogger();

            // Post-load checkpoint: the first [DIAG] line you should see in
            // F3 after the game finishes loading. If this never appears,
            // Prefab.OnPrefabsLoaded is not firing and something else is wrong.
            Log.LogInfo(
                "[DIAG] Post-load checkpoint: all mods' prefabs loaded. Bureau is initializing.");

            var modelsDir = Path.Combine(PluginDir, "Models");
            var modelPath = ResolveModelPath(modelsDir);
            if (modelPath == null)
            {
                StartCoroutine(RepeatModelErrorWarning());
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

        // Summary of the current model-configuration failure (missing folder,
        // zero .gguf, multiple .gguf). Set by ResolveModelPath; read by
        // RepeatModelErrorWarning to surface the reason in every log line.
        private string _modelErrorMessage;

        // Returns the single .gguf path in modelsDir, or null if the folder is
        // missing, empty, or contains more than one .gguf. Does not throw; the
        // caller renders the plugin inert and starts a repeat-warning
        // coroutine so the problem keeps surfacing in the log. Pattern
        // mirrored from Mods/SprayPaintPlus.
        private string ResolveModelPath(string modelsDir)
        {
            if (!Directory.Exists(modelsDir))
            {
                _modelErrorMessage =
                    "Models directory not found: " + modelsDir +
                    ". Create this folder and place exactly one .gguf model in it.";
                Log.LogFatal(_modelErrorMessage);
                return null;
            }

            var ggufFiles = Directory.GetFiles(modelsDir, "*.gguf");

            if (ggufFiles.Length == 0)
            {
                _modelErrorMessage =
                    "No .gguf model found in " + modelsDir +
                    ". Place exactly one .gguf file here " +
                    "(e.g. qwen2.5-1.5b-instruct-q4_k_m.gguf from HuggingFace).";
                Log.LogFatal(_modelErrorMessage);
                return null;
            }

            if (ggufFiles.Length > 1)
            {
                var names = new string[ggufFiles.Length];
                for (int i = 0; i < ggufFiles.Length; i++)
                    names[i] = Path.GetFileName(ggufFiles[i]);
                _modelErrorMessage =
                    "Multiple .gguf files found in " + modelsDir +
                    " (" + string.Join(", ", names) + "). Keep exactly one.";
                Log.LogFatal(_modelErrorMessage);
                return null;
            }

            return ggufFiles[0];
        }

        private IEnumerator RepeatModelErrorWarning()
        {
            var wait = new UnityEngine.WaitForSeconds(5f);
            while (true)
            {
                Log.LogError("[MaintenanceBureauPlus] NOT LOADED: " + _modelErrorMessage);
                yield return wait;
            }
        }

        private bool _openForBusinessLogged;
        private int _updateTicks;
        private int _fixedUpdateTicks;
        private int _lateUpdateTicks;
        private bool _startFired;

        // Background watchdog: a separate thread that logs every 5 seconds so we
        // know the plugin instance is alive even if Unity isn't calling any of
        // the MonoBehaviour lifecycle methods on it. Started from Awake().
        // Uses a ManualResetEvent (not Thread.Sleep) so OnDestroy can wake it
        // immediately during shutdown instead of waiting for the next 5 s tick.
        private Thread _watchdog;
        private readonly System.Threading.ManualResetEventSlim _watchdogStop =
            new System.Threading.ManualResetEventSlim(false);

        void Start()
        {
            _startFired = true;
            Log.LogInfo("Start() fired. If this appears but Update() ticks do not, Unity is driving some lifecycle but not Update.");
        }

        void Update()
        {
            _updateTicks++;
            if (_updateTicks == 1 || _updateTicks == 60 || _updateTicks == 600)
            {
                Log.LogInfo("Update() tick #" + _updateTicks + ", _modelReady=" + _modelReady);
            }

            if (_modelReady && !_openForBusinessLogged)
            {
                _openForBusinessLogged = true;
                Log.LogInfo("Maintenance Bureau is open for business.");
            }

            MainThreadQueue.Drain();
        }

        void FixedUpdate()
        {
            _fixedUpdateTicks++;
            if (_fixedUpdateTicks == 1)
                Log.LogInfo("FixedUpdate() tick #1.");
        }

        void LateUpdate()
        {
            _lateUpdateTicks++;
            if (_lateUpdateTicks == 1)
                Log.LogInfo("LateUpdate() tick #1.");
        }

        private void StartWatchdog()
        {
            _watchdog = new Thread(() =>
            {
                int tick = 0;
                while (!_watchdogStop.Wait(5000))
                {
                    tick++;
                    try
                    {
                        Log.LogInfo(
                            "[Watchdog #" + tick + "] startFired=" + _startFired +
                            " updateTicks=" + _updateTicks +
                            " fixedTicks=" + _fixedUpdateTicks +
                            " lateTicks=" + _lateUpdateTicks +
                            " modelReady=" + _modelReady +
                            " pendingMainThreadActions=" + MainThreadQueue.Count);
                    }
                    catch { }
                }
            })
            {
                IsBackground = true,
                Name = "MBP-Watchdog",
                Priority = System.Threading.ThreadPriority.BelowNormal,
            };
            _watchdog.Start();
        }

        // Unity calls OnApplicationQuit before tearing down GameObjects. This
        // is the earliest hook to start cleanup so the slow paths (model
        // ref-drop, persona memory save) finish before OnDestroy actually
        // fires. Both methods are guarded so calling them twice is safe.
        void OnApplicationQuit()
        {
            try { Log?.LogInfo("[DIAG] OnApplicationQuit fired."); } catch { }
            ShutdownFast();
        }

        void OnDestroy()
        {
            try { Log?.LogInfo("[DIAG] OnDestroy fired."); } catch { }
            ShutdownFast();
        }

        private bool _shutdownStarted;
        private void ShutdownFast()
        {
            if (_shutdownStarted) return;
            _shutdownStarted = true;

            // Wake the watchdog so it stops on the next instruction instead of
            // sitting in a 5-second wait.
            try { _watchdogStop.Set(); } catch { }

            // Save persona memory first; the model dispose runs reflection-y
            // code that could swallow exceptions, and we want the JSON file on
            // disk no matter what.
            try { Memory?.Save(); } catch (Exception ex) { try { Log?.LogError("Memory.Save failed: " + ex.Message); } catch { } }

            // Engine.Dispose is fire-and-forget: signals the worker, neuters
            // native finalizers, drops references, returns immediately.
            try { Engine?.Dispose(); } catch (Exception ex) { try { Log?.LogError("Engine.Dispose failed: " + ex.Message); } catch { } }
            Engine = null;
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

    // Independent MonoBehaviour attached to a fresh GameObject, not to the
    // BepInEx plugin host. Guaranteed to receive Unity lifecycle callbacks
    // even if the plugin's own Update() is suppressed.
    internal class MainThreadTicker : UnityEngine.MonoBehaviour
    {
        private int _ticks;
        void Update()
        {
            _ticks++;
            if (_ticks == 1 || _ticks == 60 || _ticks == 600 || _ticks == 3600)
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "MainThreadTicker.Update tick #" + _ticks +
                    " pendingActions=" + MainThreadQueue.Count);
            }
            MainThreadQueue.Drain();
        }
    }

    // Simple concurrent queue for dispatching work from worker threads onto the
    // Unity main thread. Drained once per frame by Plugin.Update() and by
    // MainThreadTicker.Update() as a redundant path.
    internal static class MainThreadQueue
    {
        private static readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

        public static int Count { get { return _queue.Count; } }

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
