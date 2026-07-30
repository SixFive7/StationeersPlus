using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using StationeersPlus.Shared;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace EquipmentPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInIncompatibility("BetterAdvancedTablet")]
    [BepInIncompatibility("com.doggo.improved_configuration")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class EquipmentPlusPlugin : BaseUnityPlugin, IJoinSuffixSerializer
    {
        public const string PluginGuid = "net.equipmentplus";
        public const string PluginName = "EquipmentPlus";
        public const string PluginVersion = "1.0.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);
        internal static ManualLogSource Log;

        // MonoBehaviour reference used as the entry point for StartCoroutine
        // calls from non-MonoBehaviour helpers (e.g. ScrollDispatchPatches'
        // AutoEquipTabletCoroutine for multiplayer-safe deferred-check moves).
        internal static EquipmentPlusPlugin Instance;

        internal const int MaxExtraSlots = 100;

        // Helmet beam config (Alt + scroll). Per-character beam state is held
        // in HelmetBeamState; these are the bounds and step size applied to
        // every character's adjustment. Defaults mirror Better Headlamp's.
        public static ConfigEntry<float> CfgBeamStep;
        public static ConfigEntry<float> CfgBeamMinAngle;
        public static ConfigEntry<float> CfgBeamMaxAngle;
        public static ConfigEntry<bool>  CfgBeamAutoBrightness;
        public static ConfigEntry<float> CfgBeamMinIntensity;
        public static ConfigEntry<float> CfgBeamMaxIntensity;
        public static ConfigEntry<float> CfgBeamMinRange;
        public static ConfigEntry<float> CfgBeamMaxRange;

        // Conflicting mod assembly names (case-insensitive). StationeersLaunchPad bypasses BepInIncompatibility.
        private static readonly (string Assembly, string DisplayName)[] ConflictingMods =
        {
            ("betteradvancedtablet", "Better Advanced Tablet"),
            ("improvedconfiguration", "ImprovedConfiguration"),
            // Slot Configuration Cartridge (Workshop 3578912665) by Otis B., absorbed into BES.
            // Its GUID is uk.org.marginal.stationeers.SlotConfigurationCartridge.
            ("slotconfigurationcartridge", "Slot Configuration Cartridge"),
            ("betterheadlampmod", "Better Headlamp"),
        };

        void Awake()
        {
            Instance = this;
            Log = Logger;
            // Display name, not the code name: this string becomes the bracketed
            // prefix on every line the mod puts in front of a player.
            PlayerMessage.Init("Equipment Plus", Logger);
            BindHelmetBeamConfig();
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void BindHelmetBeamConfig()
        {
            const string section = "Client - Helmet Beam";
            CfgBeamStep = Config.Bind(section, "Step", 2.5f,
                new ConfigDescription(
                    "(Client-local) Degrees changed per scroll tick when adjusting the beam (Alt + Scroll). Wheel-up tightens, wheel-down widens.",
                    new AcceptableValueRange<float>(0.1f, 30f),
                    new KeyValuePair<string, int>("Order", 10)));
            CfgBeamMinAngle = Config.Bind(section, "Min Angle", 20f,
                new ConfigDescription(
                    "(Client-local) Tightest spot angle in degrees. Lower = tighter focused beam.",
                    new AcceptableValueRange<float>(1f, 90f),
                    new KeyValuePair<string, int>("Order", 20)));
            CfgBeamMaxAngle = Config.Bind(section, "Max Angle", 90f,
                new ConfigDescription(
                    "(Client-local) Widest spot angle in degrees. Default-most-wide on first turn-on uses this value.",
                    new AcceptableValueRange<float>(1f, 179f),
                    new KeyValuePair<string, int>("Order", 30)));
            CfgBeamAutoBrightness = Config.Bind(section, "Auto Brightness", true,
                new ConfigDescription(
                    "(Client-local) When enabled, intensity and range scale with beam angle: tightest = brightest + longest range; widest = dimmest + shortest range.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));
            CfgBeamMinIntensity = Config.Bind(section, "Min Intensity", 1f,
                new ConfigDescription(
                    "(Client-local) Minimum brightness, applied when beam is widest (or always, if Auto Brightness is off).",
                    new AcceptableValueRange<float>(0f, 8f),
                    new KeyValuePair<string, int>("Order", 50)));
            CfgBeamMaxIntensity = Config.Bind(section, "Max Intensity", 2.5f,
                new ConfigDescription(
                    "(Client-local) Maximum brightness, applied when beam is tightest (Auto Brightness only).",
                    new AcceptableValueRange<float>(0f, 8f),
                    new KeyValuePair<string, int>("Order", 60)));
            CfgBeamMinRange = Config.Bind(section, "Min Range", 30f,
                new ConfigDescription(
                    "(Client-local) Minimum light range in meters, applied when beam is widest (or always, if Auto Brightness is off).",
                    new AcceptableValueRange<float>(1f, 1000f),
                    new KeyValuePair<string, int>("Order", 70)));
            CfgBeamMaxRange = Config.Bind(section, "Max Range", 300f,
                new ConfigDescription(
                    "(Client-local) Maximum light range in meters, applied when beam is tightest (Auto Brightness only).",
                    new AcceptableValueRange<float>(1f, 1000f),
                    new KeyValuePair<string, int>("Order", 80)));
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var conflicts = new List<string>();
            foreach (var (asmName, displayName) in ConflictingMods)
            {
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name.Equals(asmName, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.LogError($"CONFLICT: {displayName} is loaded. " +
                                 $"EquipmentPlus replaces it, please disable {displayName}.");
                    conflicts.Add(displayName);
                }
            }

            if (conflicts.Count > 0)
            {
                Log.LogFatal("EquipmentPlus NOT LOADED. Disable the conflicting mods and restart.");
                StartCoroutine(RepeatWarning(string.Join(", ", conflicts)));
                return;
            }

            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<SetActiveSensorMessage>();
                MOD.Networking.RegisterMessage<SetLogicSlotFromClientMessage>();
                MOD.Networking.RegisterMessage<SetBeamSettingsMessage>();

                // Push the host's helmet-beam dict into the join handshake
                // so a client reconnecting to a save with their previous
                // beam preference sees it applied immediately. See
                // SerializeJoinSuffix / DeserializeJoinSuffix below and
                // PowerTransmitterPlus's IJoinSuffixSerializer for the
                // pattern. Persistence on disk lives in HelmetBeamSideCar
                // and ActiveSlotSideCar; both are removal-safe (no
                // xsi:type taint in world.xml).
                MOD.Networking.JoinSuffixSerializer = this;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");

                // Fill in the SensorProcessingUnit slot-type icon that
                // vanilla leaves blank. Done directly rather than via a
                // Postfix on Slot.PopulateSlotTypeSprites because that
                // method runs before our plugin is loaded.
                SlotTypeIconPatch.RegisterMissingSensorIcon();

                // Resolve the bare-Shift+scroll modifier conflict between
                // vanilla camera zoom (default key: LeftShift) and our
                // EquipmentPlus lens cycle on Shift+scroll. We rebind
                // KeyMap.ThirdPersonControl from LeftShift to RightShift,
                // but ONLY if the player still has the default LeftShift
                // (i.e. they have not customized this key themselves).
                // See Research/GameSystems/KeyBinding.md for the four-step
                // rebind pattern and Research/GameClasses/CameraController.md
                // for why bare Shift collides.
                EnsureCameraKeyDoesNotConflict();
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
            }
        }

        // IJoinSuffixSerializer: send the host's full helmet-beam dict to
        // joining clients. Runs on the host inside NetworkServer.PackageJoinData
        // (LaunchPadBooster injects per-mod sections into the join writer);
        // runs on the client inside NetworkClient.ProcessJoinData after
        // ProcessThings, so Thing.Find resolves Human ids if we need them.
        // Field order MUST match between Serialize and Deserialize.
        public void SerializeJoinSuffix(RocketBinaryWriter writer)
        {
            var entries = HelmetBeamState.PerCharacter;
            writer.WriteInt32(entries.Count);
            foreach (var pair in entries)
            {
                writer.WriteInt64(pair.Key);
                writer.WriteSingle(pair.Value.SpotAngle);
                writer.WriteSingle(pair.Value.Intensity);
                writer.WriteSingle(pair.Value.Range);
            }
        }

        public void DeserializeJoinSuffix(RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            int applied = 0;
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                var settings = new BeamSettings
                {
                    SpotAngle = reader.ReadSingle(),
                    Intensity = reader.ReadSingle(),
                    Range     = reader.ReadSingle(),
                };
                if (refId == 0L) continue;
                HelmetBeamState.PerCharacter[refId] = settings;
                applied++;
            }
            if (count > 0)
                Log?.LogInfo($"Restored {applied} helmet-beam entries from host join");
        }

        /// <summary>
        /// One-time per-launch rebind of vanilla `KeyMap.ThirdPersonControl`
        /// from LeftShift to RightShift, only when the player has not
        /// customized the key. Resolves the modifier conflict between vanilla
        /// camera zoom (Shift+scroll, inclusive Shift match) and our
        /// EquipmentPlus lens cycle (also Shift+scroll).
        ///
        /// Four-step pattern per `Research/GameSystems/KeyBinding.md`:
        /// mutate `KeyItem.Key`, `KeyMap.ThirdPersonControl`, and
        /// `KeyMap._ThirdPersonControl.AssignKey(...)`, then
        /// `Settings.SaveSettings()` to persist. `keyItem.Changed()` fires
        /// `OnChanged` listeners (UI refresh).
        ///
        /// Verbose step-by-step logging was used for first-launch verification
        /// and has been trimmed to a single outcome line per code path.
        /// Original verbose form preserved in git history if a future
        /// regression needs the same level of trace.
        /// </summary>
        private static void EnsureCameraKeyDoesNotConflict()
        {
            Dictionary<string, KeyItem> lookup;
            try
            {
                lookup = KeyManager.KeyItemLookup;
            }
            catch (Exception e)
            {
                Log.LogError($"[EquipmentPlus.rebind] Failed to access KeyManager.KeyItemLookup: {e.GetType().Name} {e.Message}");
                return;
            }

            if (lookup == null)
            {
                Log.LogWarning("[EquipmentPlus.rebind] KeyManager.KeyItemLookup is null; skipping rebind.");
                return;
            }

            if (!lookup.TryGetValue("ThirdPersonControl", out var keyItem) || keyItem == null)
            {
                Log.LogWarning("[EquipmentPlus.rebind] 'ThirdPersonControl' not found in KeyItemLookup; skipping rebind.");
                return;
            }

            if (keyItem.Key != KeyCode.LeftShift)
            {
                Log.LogInfo($"[EquipmentPlus.rebind] ThirdPersonControl is on {keyItem.Key} (not default LeftShift); leaving player customization alone.");
                return;
            }

            try
            {
                keyItem.Key = KeyCode.RightShift;
                KeyMap.ThirdPersonControl = KeyCode.RightShift;
                KeyMap._ThirdPersonControl?.AssignKey(KeyCode.RightShift);
                keyItem.Changed();
                Settings.SaveSettings();
                Log.LogInfo("[EquipmentPlus.rebind] Rebound ThirdPersonControl LeftShift -> RightShift to resolve scroll-modifier conflict; persisted to setting.xml.");
            }
            catch (Exception e)
            {
                Log.LogError($"[EquipmentPlus.rebind] Rebind failed: {e.GetType().Name} {e.Message}\n{e.StackTrace}");
            }
        }

        /// <summary>
        /// Number of times the conflict banner is repeated in the player's console. Bounded on
        /// purpose: this used to loop forever, which meant a red line every five seconds for the
        /// whole session with no way to silence it. Short enough that the console stays usable.
        /// The permanent record is the LogError line naming each conflict and the LogFatal line
        /// that follows, both in the BepInEx log.
        ///
        /// The count is only the WORLD-ENTRY half of the banner. Do not raise it hoping to make the
        /// banner more noticeable; the reason a bounded banner used to be missed was the anchor, not
        /// the duration, and that is now handled by waiting for a world. See RepeatWarning.
        /// </summary>
        private const int ConflictBannerRepeats = 6;

        // Announced twice, on purpose, because the two moments reach different readers.
        //
        // This coroutine is started from Prefab.OnPrefabsLoaded, which fires at the tail of
        // LoadGameDataAsync during BOOT: loading screen up, before the main menu exists. One line there
        // is worth having, because it lands in StationeersLaunchPad's boot panel and in both log files
        // while someone is plausibly watching the splash. But it is useless as the ONLY announcement:
        // console overlay lines live five seconds, so it is long gone by the time the player has picked
        // a save. The banner the player is actually meant to read therefore waits for a world.
        //
        // On this path the mod returned before PatchAll, so it is completely inert. A player who never
        // gets told is a player wondering why nothing works.
        private static IEnumerator RepeatWarning(string conflicts)
        {
            var msg = $"NOT LOADED! Conflicting mods: {conflicts}. Disable them and restart.";

            // Boot line. Separate throttle key from the banner below so the two never suppress
            // each other.
            PlayerMessage.Error("conflict-banner-boot", Throttle.Never, msg);

            // PlayerMessage owns this test because the obvious-looking gate is the wrong one:
            // GameManager.RunSimulation is only !NetworkManager.IsClient, so it is already true at the
            // main menu and would not delay anything.
            yield return PlayerMessage.WaitForWorld();

            for (int i = 0; i < ConflictBannerRepeats; i++)
            {
                // Throttle.Never because the loop is already the bound: it runs exactly
                // ConflictBannerRepeats times and then the coroutine ends. Handing the count to
                // the helper (Throttle.MaxTimes) would not remove the counter, because only the
                // caller can know which iteration is the last one and Error reports nothing back
                // about what it suppressed. One bound, in the place the doc comment above explains.
                // Red, stack-trace-free and visible without opening the console are the helper's
                // job now; see Patterns/Console/PlayerMessage.cs for why none of that is optional.
                bool last = i == ConflictBannerRepeats - 1;
                var line = last
                    ? $"{msg} (This warning will stop repeating; see the BepInEx log.)"
                    : msg;
                PlayerMessage.Error("conflict-banner", Throttle.Never, line);

                // Realtime, not WaitForSeconds: now that a world is running the player can pause, and
                // pausing sets Time.timeScale to 0, which would stall the banner mid-run. No wait after
                // the last line, which would just idle the coroutine for five seconds before it ends.
                if (!last) yield return new WaitForSecondsRealtime(5f);
            }
        }
    }
}
