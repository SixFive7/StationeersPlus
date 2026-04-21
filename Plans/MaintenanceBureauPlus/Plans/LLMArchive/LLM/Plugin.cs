using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Assets.Scripts.Objects;
using LaunchPadBooster;
using System;
using System.IO;
using System.Threading;
using UnityEngine;

namespace LLM
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class LlmPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.llm";
        public const string PluginName = "LLM";
        public const string PluginVersion = "0.1.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);

        internal static ManualLogSource Log;
        internal static LlmEngine Engine;

        // Set to true by the background loader thread once the model is ready.
        // Checked by Update() to apply Harmony patches on the main thread.
        private volatile bool _modelReady;
        private volatile bool _patchesApplied;

        // Server settings
        internal static ConfigEntry<string> ModelFileName;
        internal static ConfigEntry<string> BotName;
        internal static ConfigEntry<string> SystemPrompt;
        internal static ConfigEntry<int> MaxTokens;
        internal static ConfigEntry<float> Temperature;
        internal static ConfigEntry<int> InferenceThreads;
        internal static ConfigEntry<int> ContextSize;
        internal static ConfigEntry<string> TriggerPrefix;

        void Awake()
        {
            Log = Logger;
            BindConfig();
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var pluginDir = Path.GetDirectoryName(Info.Location);
            var modelsDir = Path.Combine(pluginDir, "models");
            var modelPath = Path.Combine(modelsDir, ModelFileName.Value);

            if (!File.Exists(modelPath))
            {
                Log.LogError($"Model file not found: {modelPath}");
                Log.LogError($"Download a GGUF model and place it in: {modelsDir}");
                Log.LogError("Recommended: qwen2.5-1.5b-instruct-q4_k_m.gguf");
                return;
            }

            Log.LogInfo($"Loading model in background: {modelPath}");

            // Capture config values now (main thread) so the background thread
            // doesn't touch BepInEx ConfigEntry from a non-Unity thread.
            var ctxSize = ContextSize.Value;
            var threads = InferenceThreads.Value;

            var loaderThread = new Thread(() =>
            {
                try
                {
                    Engine = new LlmEngine();
                    Engine.Load(modelPath, ctxSize, threads);
                    Log.LogInfo("Model loaded. Patches will be applied on the next frame.");
                    _modelReady = true;
                }
                catch (Exception e)
                {
                    Log.LogFatal($"Background model load failed: {e}");
                }
            })
            {
                IsBackground = true,
                Name = "LLM-ModelLoad",
                Priority = System.Threading.ThreadPriority.BelowNormal
            };
            loaderThread.Start();
        }

        void Update()
        {
            // Once the background loader signals ready, apply patches on the main thread.
            // Harmony.PatchAll() must run on the main thread.
            if (_modelReady && !_patchesApplied)
            {
                _patchesApplied = true;
                try
                {
                    var harmony = new Harmony(PluginGuid);
                    harmony.PatchAll();
                    Log.LogInfo($"LLM ready. Say something starting with \"{TriggerPrefix.Value}\" in chat.");
                }
                catch (Exception e)
                {
                    Log.LogFatal($"Failed to apply patches: {e}");
                }
            }

            // Drain LLM responses back to the main thread for chat dispatch
            ChatPatch.DrainResponses();
        }

        void OnDestroy()
        {
            Engine?.Dispose();
            Engine = null;
        }

        private void BindConfig()
        {
            ModelFileName = Config.Bind(
                "Server", "Model File Name",
                "qwen2.5-1.5b-instruct-q4_k_m.gguf",
                "(Server-side) GGUF model file name inside the models/ folder next to the plugin DLL.");

            BotName = Config.Bind(
                "Server", "Bot Name",
                "SATCOM",
                "(Server-side) Name the bot uses when sending chat messages.");

            SystemPrompt = Config.Bind(
                "Server", "System Prompt",
                "You are SATCOM, a satellite communications relay orbiting the planet. " +
                "You answer colonists' questions with a terse, slightly sarcastic military tone. " +
                "You have intermittent signal quality and sometimes garble words. " +
                "Keep responses under two sentences. You know about atmospheric pressure, " +
                "temperature, gas mixtures, farming, smelting, and electronics. " +
                "If asked something you don't know, blame signal interference.",
                "(Server-side) System prompt that defines the bot's personality. " +
                "Keep it short; the model is small.");

            MaxTokens = Config.Bind(
                "Server", "Max Tokens", 128,
                "(Server-side) Maximum number of tokens the model generates per response. " +
                "Higher values produce longer responses but take more time.");

            Temperature = Config.Bind(
                "Server", "Temperature", 0.8f,
                "(Server-side) Sampling temperature. Higher is more creative, lower is more predictable. " +
                "Range 0.1 to 2.0.");

            InferenceThreads = Config.Bind(
                "Server", "Inference Threads", 4,
                "(Server-side) CPU threads used for inference. " +
                "Set to roughly half your server's physical cores.");

            ContextSize = Config.Bind(
                "Server", "Context Size", 2048,
                "(Server-side) Context window size in tokens. " +
                "2048 is plenty for short chat exchanges. " +
                "Higher values use more RAM.");

            TriggerPrefix = Config.Bind(
                "Server", "Trigger Prefix", "@sat",
                "(Server-side) Chat messages starting with this prefix are forwarded to the model. " +
                "Other messages are ignored. Set to empty string to respond to everything.");
        }
    }
}
