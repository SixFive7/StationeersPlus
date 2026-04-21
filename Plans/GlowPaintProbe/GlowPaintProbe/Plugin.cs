using System;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace GlowPaintProbe
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class GlowPaintProbePlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.glowpaintprobe";
        public const string PluginName = "GlowPaintProbe";
        public const string PluginVersion = "0.1.4";

        internal const string Tag = "[GlowPaintProbe]";

        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyboardShortcut> GlowOnKey;
        internal static ConfigEntry<KeyboardShortcut> GlowOffKey;

        void Awake()
        {
            Log = Logger;

            GlowOnKey = Config.Bind("General", "Glow On Key", new KeyboardShortcut(KeyCode.F9),
                "F9: call SetCustomColor(index, emissive: true) on the look-at Thing.");
            GlowOffKey = Config.Bind("General", "Glow Off Key", new KeyboardShortcut(KeyCode.F10),
                "F10: call SetCustomColor(index, emissive: false) on the look-at Thing.");

            Log.LogInfo($"{Tag} Awake v{PluginVersion}");
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;
            Log.LogInfo($"{Tag} OnPrefabsLoaded fired");

            LogSwatches();

            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo($"{Tag} Harmony patches applied");
            }
            catch (Exception ex)
            {
                Log.LogError($"{Tag} Harmony patch failed: {ex}");
            }
        }

        internal static void LogSwatches()
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.CustomColors == null || gm.CustomColors.Count == 0) return;
            Log.LogInfo($"{Tag} swatch count={gm.CustomColors.Count}");
            for (int i = 0; i < gm.CustomColors.Count; i++)
            {
                var s = gm.CustomColors[i];
                if (s == null) { Log.LogInfo($"{Tag} swatch index={i} null-entry"); continue; }
                var normal = s.Normal != null ? "yes" : "no";
                var emissive = s.Emissive != null ? "yes" : "no";
                Log.LogInfo($"{Tag} swatch index={i} name=\"{s.Name}\" normal={normal} emissive={emissive}");
            }
        }
    }

    [HarmonyPatch(typeof(InventoryManager), "NormalMode")]
    public static class InventoryNormalModePatch
    {
        private static bool _bloomLogged;
        private static int _tickCount;
        private static int _bloomRetryCount;

        [HarmonyPostfix]
        public static void Postfix()
        {
            _tickCount++;
            if (_tickCount == 1) GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} first tick via InventoryManager.NormalMode");
            if (_tickCount == 300 || _tickCount == 1800 || _tickCount == 18000)
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} tick heartbeat={_tickCount}");

            if (!_bloomLogged) TryLogBloom();

            var shortcutF9 = GlowPaintProbePlugin.GlowOnKey.Value.IsDown();
            var shortcutF10 = GlowPaintProbePlugin.GlowOffKey.Value.IsDown();
            var rawF9 = Input.GetKeyDown(KeyCode.F9);
            var rawF10 = Input.GetKeyDown(KeyCode.F10);

            if (shortcutF9 || rawF9)
            {
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} F9 detected (shortcut={shortcutF9}, raw={rawF9}, tick={_tickCount})");
                ApplyGlow(true);
            }
            else if (shortcutF10 || rawF10)
            {
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} F10 detected (shortcut={shortcutF10}, raw={rawF10}, tick={_tickCount})");
                ApplyGlow(false);
            }
        }

        private static void TryLogBloom()
        {
            _bloomRetryCount++;
            var cam = CameraController.Instance;
            if (cam == null)
            {
                if (_bloomRetryCount == 1 || _bloomRetryCount == 300 || _bloomRetryCount == 1800)
                    GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} bloom retry {_bloomRetryCount}: CameraController.Instance is null");
                return;
            }
            if (cam.CameraEffects == null)
            {
                if (_bloomRetryCount == 1 || _bloomRetryCount == 300 || _bloomRetryCount == 1800)
                    GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} bloom retry {_bloomRetryCount}: cam.CameraEffects is null");
                return;
            }
            if (cam.CameraEffects.Count == 0)
            {
                if (_bloomRetryCount == 1 || _bloomRetryCount == 300 || _bloomRetryCount == 1800)
                    GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} bloom retry {_bloomRetryCount}: cam.CameraEffects is empty");
                return;
            }
            var collection = cam.CameraEffects[0];
            var bloom = collection != null ? collection.Bloom : null;
            var present = bloom != null ? "yes" : "no";
            var enabled = bloom != null && bloom.enabled ? "yes" : "no";
            var componentName = bloom != null ? bloom.GetType().Name : "null";
            GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} bloom present={present} enabled={enabled} component={componentName} retries={_bloomRetryCount}");
            _bloomLogged = true;
        }

        private static void ApplyGlow(bool emissive)
        {
            var target = CursorManager.CursorThing;
            if (target == null)
            {
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} no look-at target (CursorManager.CursorThing is null)");
                return;
            }
            if (target.CustomColor == null)
            {
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} target {target.GetType().Name} has no CustomColor (never painted)");
                return;
            }

            var typeName = target.GetType().Name;
            var colorIndex = target.CustomColor.Index;
            var colorName = target.CustomColor.Name;
            GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} target type={typeName} colorIndex={colorIndex} colorName=\"{colorName}\" emissive={emissive}");

            try
            {
                target.SetCustomColor(colorIndex, emissive);
            }
            catch (Exception ex)
            {
                GlowPaintProbePlugin.Log.LogError($"{GlowPaintProbePlugin.Tag} SetCustomColor threw: {ex}");
                return;
            }

            if (target.Renderers == null || target.Renderers.Count == 0)
            {
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} target has no Renderers");
                return;
            }

            for (int i = 0; i < target.Renderers.Count; i++)
            {
                var r = target.Renderers[i];
                if (r == null) { GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} renderer[{i}] is null"); continue; }

                var mats = r.Materials;
                if (mats == null || mats.Length == 0) { GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} renderer[{i}] no materials"); continue; }

                var mat = mats[0];
                if (mat == null) { GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} renderer[{i}] material[0] is null"); continue; }

                var shaderName = mat.shader != null ? mat.shader.name : "null";
                var emCol = mat.GetColor("_EmissionColor");
                var emKw = mat.IsKeywordEnabled("_EMISSION") ? "on" : "off";
                GlowPaintProbePlugin.Log.LogInfo($"{GlowPaintProbePlugin.Tag} renderer[{i}] shader=\"{shaderName}\" material=\"{mat.name}\" _EmissionColor={emCol} _EMISSION={emKw}");
            }
        }
    }
}
