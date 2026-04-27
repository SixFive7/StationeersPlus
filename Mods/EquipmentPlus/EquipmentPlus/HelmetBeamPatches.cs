using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using System.Collections.Generic;
using UnityEngine;

namespace EquipmentPlus
{
    /// <summary>
    /// Per-character helmet beam settings keyed by Human.ReferenceId. Local
    /// player drives input on this client; remote players' entries arrive
    /// via SetBeamSettingsMessage rebroadcasts from the host. Save/load
    /// round-trip is handled by HelmetBeamSideCar.
    ///
    /// On Ctrl+LeftShift+scroll the dispatcher (ScrollDispatchPatch) calls
    /// HelmetBeamPatches.HandleScroll(human, direction). We either turn the
    /// helmet on at the preserved beam (if currently off and any direction is
    /// scrolled) or adjust the beam by Step (if currently on). Beam math
    /// mirrors Better Headlamp's AdjustFocusExternal: tightest = brightest +
    /// longest range, widest = dimmest + shortest range, with linear
    /// interpolation when AutoBrightness is on.
    ///
    /// Apply path: Postfix on Human.LateUpdate writes spotAngle / intensity /
    /// range to every Human's helmet light every frame (local + remote
    /// players, so visual sync renders remote beams at the right angle).
    /// Vanilla light initialization on helmet equip / on/off transition writes
    /// default values that overwrite our state; reapplying every frame keeps
    /// our values stable.
    /// </summary>
    internal struct BeamSettings
    {
        internal float SpotAngle;
        internal float Intensity;
        internal float Range;

        internal static BeamSettings MostWide()
        {
            return new BeamSettings
            {
                SpotAngle = EquipmentPlusPlugin.CfgBeamMaxAngle?.Value ?? 90f,
                Intensity = EquipmentPlusPlugin.CfgBeamMinIntensity?.Value ?? 1f,
                Range     = EquipmentPlusPlugin.CfgBeamMinRange?.Value     ?? 30f,
            };
        }
    }

    internal static class HelmetBeamState
    {
        internal static readonly Dictionary<long, BeamSettings> PerCharacter =
            new Dictionary<long, BeamSettings>();

        internal static BeamSettings GetOrInit(Human human)
        {
            if (human == null) return BeamSettings.MostWide();
            long id = human.ReferenceId;
            if (!PerCharacter.TryGetValue(id, out var settings))
            {
                settings = BeamSettings.MostWide();
                PerCharacter[id] = settings;
            }
            return settings;
        }

        internal static void Set(Human human, BeamSettings settings)
        {
            if (human == null) return;
            PerCharacter[human.ReferenceId] = settings;
        }
    }

    internal static class HelmetBeamPatches
    {
        // Public entry point called by ScrollDispatchPatch on Alt+scroll.
        // direction: -1 = wheel-up (tighten), +1 = wheel-down (widen).
        internal static void HandleScroll(Human human, int direction)
        {
            if (human == null) return;
            if (!TryGetActiveHelmet(human, out var helmet, out var light))
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] headlamp bail: no active helmet (or no controllable Light component)");
                return;
            }

            // Power-state proxy: a helmet with a controllable light needs an
            // OnOff state machine to be toggleable. If it doesn't have one,
            // we treat scroll as a no-op. The "no power" case (helmet has
            // OnOff but battery is dead) is not explicitly checked here; we
            // toggle OnOff and trust vanilla to refuse if no power. See TODO.
            if (!helmet.HasOnOffState)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] headlamp bail: helmet has no OnOff state");
                return;
            }

            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.scroll] headlamp: helmet={helmet.GetType().Name} OnOff={helmet.OnOff} dir={direction}");

            var settings = HelmetBeamState.GetOrInit(human);

            if (!helmet.OnOff)
            {
                // Light off: any scroll direction turns it on at the preserved
                // beam (per spec C=iii). Scroll never turns the light off; the
                // existing helmet-toggle binding is the only off path.
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] headlamp: OFF + scroll -> ON at preserved beam (angle={settings.SpotAngle:F1})");
                helmet.OnOff = true;
                ApplyToLight(light, settings);
                return;
            }

            // Light on: adjust beam by Step. direction is +1 for wheel-up
            // (tighten = decrease angle) and -1 for wheel-down (widen). Subtract
            // direction*step so wheel-up shrinks the angle.
            float step     = EquipmentPlusPlugin.CfgBeamStep?.Value     ?? 2.5f;
            float minAngle = EquipmentPlusPlugin.CfgBeamMinAngle?.Value ?? 20f;
            float maxAngle = EquipmentPlusPlugin.CfgBeamMaxAngle?.Value ?? 90f;

            float lo = Mathf.Min(minAngle, maxAngle);
            float hi = Mathf.Max(minAngle, maxAngle);
            float newAngle = Mathf.Clamp(settings.SpotAngle - direction * step, lo, hi);

            // Clamp at both ends — wheel-up at tightest is no-op (A=i),
            // wheel-down at widest is no-op (B=i; light does not turn off
            // via scroll).
            if (newAngle == settings.SpotAngle)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] headlamp: clamped at angle={settings.SpotAngle:F1} (no change)");
                return;
            }

            settings.SpotAngle = newAngle;

            if (EquipmentPlusPlugin.CfgBeamAutoBrightness?.Value ?? true)
            {
                float minIntensity = EquipmentPlusPlugin.CfgBeamMinIntensity?.Value ?? 1f;
                float maxIntensity = EquipmentPlusPlugin.CfgBeamMaxIntensity?.Value ?? 2.5f;
                float minRange     = EquipmentPlusPlugin.CfgBeamMinRange?.Value     ?? 30f;
                float maxRange     = EquipmentPlusPlugin.CfgBeamMaxRange?.Value     ?? 300f;

                // t: 0 at maxAngle (widest), 1 at minAngle (tightest).
                float t = Mathf.InverseLerp(maxAngle, minAngle, newAngle);
                settings.Intensity = Mathf.Lerp(minIntensity, maxIntensity, t);
                settings.Range     = Mathf.Lerp(minRange,     maxRange,     t);
            }

            HelmetBeamState.Set(human, settings);
            ApplyToLight(light, settings);
            PushSettingsToHost(human, settings);

            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] headlamp: angle -> {settings.SpotAngle:F1}, intensity -> {settings.Intensity:F2}, range -> {settings.Range:F0}");
        }

        // Forward the local player's new beam settings to the host so the
        // host's PerCharacter dict reflects this character at save-snapshot
        // time. On host (single-player or listen server's own character) the
        // dict was already mutated by HelmetBeamState.Set above; sending
        // would be a self-loop.
        internal static void PushSettingsToHost(Human human, BeamSettings settings)
        {
            if (human == null) return;
            if (!NetworkManager.IsClient) return;
            new SetBeamSettingsMessage
            {
                HumanReferenceId = human.ReferenceId,
                SpotAngle = settings.SpotAngle,
                Intensity = settings.Intensity,
                Range     = settings.Range,
            }.SendToHost();
        }

        // Per-Human cache of the helmet's controllable Light so
        // HelmetBeamApplyPatch.Postfix does not call GetComponentsInChildren<Light>
        // every LateUpdate frame. Keyed by Human.ReferenceId because the apply
        // path now runs for every Human in the scene (visual sync of remote
        // players' beams), so a single static cache would thrash between
        // characters. Invalidated per Human on helmet swap by comparing
        // helmet instance ID.
        private struct LightCacheEntry
        {
            internal Light Light;
            internal int HelmetInstanceId;
        }
        private static readonly Dictionary<long, LightCacheEntry> _lightCache =
            new Dictionary<long, LightCacheEntry>();

        internal static bool TryGetActiveHelmet(Human human, out DynamicThing helmet, out Light light)
        {
            helmet = null;
            light  = null;
            if (human == null) return false;
            var slot = human.HelmetSlot;
            if (slot == null) return false;

            helmet = slot.Get() as DynamicThing;
            if (helmet == null)
            {
                _lightCache.Remove(human.ReferenceId);
                return false;
            }

            int helmetId = helmet.GetInstanceID();
            if (!_lightCache.TryGetValue(human.ReferenceId, out var entry)
                || entry.Light == null
                || entry.HelmetInstanceId != helmetId)
            {
                entry = new LightCacheEntry
                {
                    Light = FindControllableLight(helmet),
                    HelmetInstanceId = helmetId,
                };
                _lightCache[human.ReferenceId] = entry;
            }
            light = entry.Light;
            return light != null;
        }

        private static Light FindControllableLight(Component root)
        {
            // Mirror Better Headlamp: prefer a Spot light, fall back to the
            // first Light found.
            Light fallback = null;
            var lights = root.GetComponentsInChildren<Light>(true);
            foreach (var l in lights)
            {
                if (l == null) continue;
                if (l.type == LightType.Spot) return l;
                if (fallback == null) fallback = l;
            }
            return fallback;
        }

        internal static void ApplyToLight(Light light, BeamSettings settings)
        {
            if (light == null) return;
            if (light.type == LightType.Spot)
                light.spotAngle = settings.SpotAngle;
            light.intensity = settings.Intensity;
            light.range     = settings.Range;
        }
    }

    /// <summary>
    /// Postfix on Human.LateUpdate ensures our beam settings survive vanilla
    /// resets. Vanilla light initialization (helmet equip, helmet on/off
    /// transition) writes default values that overwrite our settings;
    /// reapplying every frame keeps our values stable. Applies to every
    /// Human (not just LocalHuman) so remote players' helmet beams render
    /// at their custom angle / intensity / range; remote settings reach the
    /// dict via SetBeamSettingsMessage rebroadcasts from the host.
    /// </summary>
    [HarmonyPatch(typeof(Human), "LateUpdate")]
    public class HelmetBeamApplyPatch
    {
        [UsedImplicitly]
        public static void Postfix(Human __instance)
        {
            if (__instance == null) return;
            if (!HelmetBeamState.PerCharacter.TryGetValue(__instance.ReferenceId, out var settings))
                return;

            if (!HelmetBeamPatches.TryGetActiveHelmet(__instance, out var helmet, out var light))
                return;
            if (!helmet.HasOnOffState || !helmet.OnOff) return;

            HelmetBeamPatches.ApplyToLight(light, settings);
        }
    }
}
