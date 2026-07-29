using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Assets.Scripts.Util;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SprayPaintPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInIncompatibility("net.elmo.stationeers.ColorCycler")]
    [BepInIncompatibility("net.elmo.stationeers.NetworkPainter")]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class SprayPaintPlusPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.spraypaintplus";
        public const string PluginName = "SprayPaintPlus";
        public const string PluginVersion = "1.11.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);

        internal static ManualLogSource Log;

        // Settings come in client/server pairs: a capability works only when both
        // halves allow it. Everything paired resolves through SettingsMerge; nothing
        // outside that file reads a paired entry's Value directly.
        //
        // Three settings are deliberately unpaired. PaintSingleItemByDefault and
        // InvertColorScrollDirection are pure input mapping, where a server has no
        // sensible opinion. SuppressSprayPaintPollution is server-only because the
        // atmosphere is shared, so a personal opt-out would change the air for everyone.

        // Client-only
        internal static ConfigEntry<bool> InvertColorScrollDirection;
        internal static ConfigEntry<bool> PaintSingleItemByDefault;

        // Client halves
        internal static ConfigEntry<ColorCyclingMode> ClientColorCycling;
        internal static ConfigEntry<bool> ClientColorPicking;
        internal static ConfigEntry<bool> ClientUnlimitedSprayPaintUses;
        internal static ConfigEntry<bool> ClientGlowPaint;
        internal static ConfigEntry<bool> ClientNetworkPainting;
        internal static ConfigEntry<bool> ClientNetworkPaintPipes;
        internal static ConfigEntry<bool> ClientNetworkPaintCables;
        internal static ConfigEntry<bool> ClientNetworkPaintChutes;
        internal static ConfigEntry<bool> ClientNetworkPaintWalls;
        internal static ConfigEntry<bool> ClientNetworkPaintRails;
        internal static ConfigEntry<bool> ClientNetworkPaintLargeStructures;
        internal static ConfigEntry<bool> ClientNetworkPaintElevators;
        internal static ConfigEntry<bool> ClientNetworkPaintLadders;
        internal static ConfigEntry<bool> ClientNetworkPaintStairs;
        internal static ConfigEntry<bool> ClientNetworkPaintStairwells;

        // Server halves
        internal static ConfigEntry<ColorCyclingMode> ServerColorCycling;
        internal static ConfigEntry<bool> ServerColorPicking;
        internal static ConfigEntry<bool> ServerUnlimitedSprayPaintUses;
        internal static ConfigEntry<bool> ServerSuppressSprayPaintPollution;
        internal static ConfigEntry<bool> ServerGlowPaint;
        internal static ConfigEntry<bool> ServerNetworkPainting;
        internal static ConfigEntry<bool> ServerNetworkPaintPipes;
        internal static ConfigEntry<bool> ServerNetworkPaintCables;
        internal static ConfigEntry<bool> ServerNetworkPaintChutes;
        internal static ConfigEntry<bool> ServerNetworkPaintWalls;
        internal static ConfigEntry<bool> ServerNetworkPaintRails;
        internal static ConfigEntry<bool> ServerNetworkPaintLargeStructures;
        internal static ConfigEntry<bool> ServerNetworkPaintElevators;
        internal static ConfigEntry<bool> ServerNetworkPaintLadders;
        internal static ConfigEntry<bool> ServerNetworkPaintStairs;
        internal static ConfigEntry<bool> ServerNetworkPaintStairwells;

        private static readonly string[] ConflictingAssemblies = { "ColorCycler", "NetworkPainter" };

        void Awake()
        {
            Log = Logger;
            BindConfig();

            // StationeersLaunchPad loads mods progressively; conflicting assemblies may not exist
            // yet when our Awake() fires. Prefab.OnPrefabsLoaded fires after StationeersLaunchPad
            // finishes loading all mods. No patches are applied until the check passes.
            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            var conflicts = new List<string>();
            foreach (var name in ConflictingAssemblies)
            {
                if (AppDomain.CurrentDomain.GetAssemblies()
                    .Any(a => a.GetName().Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    conflicts.Add(name);
                    Log.LogError($"CONFLICT: {name}.dll is loaded. SprayPaintPlus replaces it.");
                }
            }

            if (conflicts.Count > 0)
            {
                Log.LogFatal("SprayPaintPlus NOT LOADED. Disable the conflicting mods and restart.");
                StartCoroutine(RepeatWarning(string.Join(", ", conflicts)));
                return;
            }

            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<SprayCanColorMessage>();
                MOD.Networking.RegisterMessage<PaintModifierMessage>();
                MOD.Networking.RegisterMessage<SettingBlockedNotice>();

                // Push the fifteen server-half settings down to a joining client as
                // part of the world snapshot. It has to ride the join payload rather
                // than an INetworkMessage broadcast: NetworkManager.PlayerConnected
                // fires before the joiner is in NetworkBase.Clients, so a SendAll
                // from there reaches everyone except the player who needs it.
                // See SettingsConfigSync.
                MOD.Networking.JoinSuffixSerializer = SettingsConfigSync.Instance;

                // Register GlowThingSaveData via LaunchPadBooster so XmlSaveLoad
                // ExtraTypes picks it up, AND inject directly as a fallback
                // for load-order races. See Research/GameSystems/SaveDataRegistration.md.
                MOD.AddSaveDataType<GlowThingSaveData>();
                RegisterSaveDataTypeLate(typeof(GlowThingSaveData));

                // Build the color-to-DLC map before the patches go live so the very first
                // scroll is already gated. Build() is a no-op-and-retry if GameManager or the
                // prefab registry is not ready yet; the accessors rebuild lazily in that case.
                DlcPaintGate.Build();

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();
                Log.LogInfo("Patches applied successfully");

                // The support line, so a bug report from a single-player or host
                // session carries the same settings dump a joining client gets.
                // Those sessions never receive a join payload, so nothing else
                // would ever emit it for them. Info level, log only.
                WarningNotifier.LogEffectiveSettings();
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
            }
        }

        private static void RegisterSaveDataTypeLate(Type t)
        {
            try
            {
                var extraTypesField = AccessTools.Field(typeof(XmlSaveLoad), "ExtraTypes");
                var current = extraTypesField.GetValue(null) as Type[];
                if (current == null)
                {
                    extraTypesField.SetValue(null, new[] { t });
                }
                // Array.IndexOf, not LINQ Contains: on an array the compiler prefers
                // MemoryExtensions.Contains(ReadOnlySpan<T>, T), which UniTask.dll makes
                // visible, and then fails with CS7069 because net472's mscorlib has no
                // Span<T>. Array.IndexOf has no Span overload and is identical here.
                else if (Array.IndexOf(current, t) < 0)
                {
                    var next = new Type[current.Length + 1];
                    Array.Copy(current, next, current.Length);
                    next[current.Length] = t;
                    extraTypesField.SetValue(null, next);
                }

                // Force the WorldData XmlSerializer to be regenerated on next
                // access with the updated ExtraTypes. The field is private.
                var worldDataField = AccessTools.Field(typeof(Serializers), "_worldData");
                worldDataField?.SetValue(null, null);
            }
            catch (Exception e)
            {
                Log.LogWarning($"Late save-type registration failed: {e.Message}");
            }
        }

        private static IEnumerator RepeatWarning(string conflicts)
        {
            var msg = $"[SprayPaintPlus] NOT LOADED! Conflicting mods: {conflicts}. " +
                      "Disable them and restart.";
            while (true)
            {
                Debug.LogError(msg);
                yield return new WaitForSeconds(5f);
            }
        }

        // Description prefix convention for paired settings: the terse marker stays so a
        // power user reading the generated .cfg still sees the scope at a glance, and the
        // friendly explanation follows it. Unpaired settings keep the original two markers.
        private void BindConfig()
        {
            const string ClientPaired = "(Client-local, server must also allow) ";
            const string ServerPaired = "(Server-authoritative, client must also enable) ";

            // ---- Client - Color Cycling ----------------------------------------
            ClientColorCycling = Config.Bind(
                "Client - Color Cycling", "Color Cycling", ColorCyclingMode.AllColors,
                new ConfigDescription(
                    ClientPaired +
                    "How the mouse wheel changes a spray can's color. Cycles within paint family " +
                    "keeps a base-color can on the twelve base colors and a metallic can on the " +
                    "four metallic ones. Can cannot change color turns the wheel off, so you print " +
                    "one can per color. If the server is set to something stricter, the stricter " +
                    "setting applies and you are told when you join.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ClientColorPicking = Config.Bind(
                "Client - Color Cycling", "Color Picking", true,
                new ConfigDescription(
                    ClientPaired +
                    "Right-click a painted object with a spray can in hand to copy its color onto " +
                    "the can. Hold Ctrl to copy the color it was built with instead. Does nothing " +
                    "when Color Cycling is set to Can cannot change color.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            // ---- Client - Consumables ------------------------------------------
            ClientUnlimitedSprayPaintUses = Config.Bind(
                "Client - Consumables", "Unlimited Spray Paint Uses", true,
                new ConfigDescription(
                    ClientPaired +
                    "Keeps your own spray cans from being used up. Turn it off to have your cans " +
                    "deplete normally even on a server that allows unlimited use.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            // ---- Client - Glow Paint -------------------------------------------
            ClientGlowPaint = Config.Bind(
                "Client - Glow Paint", "Glow Paint", true,
                new ConfigDescription(
                    ClientPaired +
                    "Use the Spray Paint Gun to add and remove a glow on already-painted objects. " +
                    "Turn it off to get the base game gun that loads a spray can. Glow that other " +
                    "players apply stays visible to you whatever this is set to.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            // ---- Client - Network Painting --------------------------------------
            ClientNetworkPainting = Config.Bind(
                "Client - Network Painting", "Network Painting", true,
                new ConfigDescription(
                    ClientPaired +
                    "One spray stroke paints a whole connected set: a pipe run, a cable network, " +
                    "a staircase, the walls of a room. Turn it off to always paint one item at a " +
                    "time. The entries below leave out individual kinds of network; each one also " +
                    "has to be allowed by the server.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ClientNetworkPaintPipes = Config.Bind(
                "Client - Network Painting", "Network Paint Pipes", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes pipe networks (pipes, passive vents, hydroponic trays) when your " +
                    "stroke paints a whole network. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            ClientNetworkPaintCables = Config.Bind(
                "Client - Network Painting", "Network Paint Cables", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes cable networks when your stroke paints a whole network. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            ClientNetworkPaintChutes = Config.Bind(
                "Client - Network Painting", "Network Paint Chutes", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes chute networks when your stroke paints a whole network. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));

            ClientNetworkPaintWalls = Config.Bind(
                "Client - Network Painting", "Network Paint Walls", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes all same-type walls bounding the same room when your stroke paints a " +
                    "whole network. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 50)));

            ClientNetworkPaintRails = Config.Bind(
                "Client - Network Painting", "Network Paint Rails", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes every rail, junction, bypass and dock on one robotic arm assembly. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 60)));

            ClientNetworkPaintLargeStructures = Config.Bind(
                "Client - Network Painting", "Network Paint Large Structures", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes connected large structures such as frames and girders. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 70)));

            ClientNetworkPaintElevators = Config.Bind(
                "Client - Network Painting", "Network Paint Elevators", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes every shaft and level segment of one elevator. The carriage is " +
                    "painted on its own. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 80)));

            ClientNetworkPaintLadders = Config.Bind(
                "Client - Network Painting", "Network Paint Ladders", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes the whole ladder column and its end caps. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 90)));

            ClientNetworkPaintStairs = Config.Bind(
                "Client - Network Painting", "Network Paint Stairs", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes a whole staircase across its width and its climb. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 100)));

            ClientNetworkPaintStairwells = Config.Bind(
                "Client - Network Painting", "Network Paint Stairwells", true,
                new ConfigDescription(
                    ClientPaired +
                    "Includes every adjacent stairwell block, all eight types, any orientation. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 110)));

            // ---- Client - Preferences -------------------------------------------
            PaintSingleItemByDefault = Config.Bind(
                "Client - Preferences", "Paint Single Item By Default", false,
                new ConfigDescription(
                    "(Client-local) Painting targets a single item by default and Shift paints the " +
                    "whole network instead. When disabled (default), painting targets the whole " +
                    "network and Shift restricts to a single item. Purely local; the server has no say.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            InvertColorScrollDirection = Config.Bind(
                "Client - Preferences", "Invert Color Scroll Direction", false,
                new ConfigDescription(
                    "(Client-local) Reverses the mouse wheel direction when scrolling through spray " +
                    "can colors. Purely local; the server has no say.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            // ---- Server - Color Cycling ------------------------------------------
            ServerColorCycling = Config.Bind(
                "Server - Color Cycling", "Color Cycling", ColorCyclingMode.AllColors,
                new ConfigDescription(
                    ServerPaired +
                    "The most permissive color cycling allowed on this server. Cycles within paint " +
                    "family makes players print a metallic can to reach metallic colors. Can cannot " +
                    "change color turns the wheel off for everyone, so a can keeps whatever color it " +
                    "has now. Metallic Paints DLC rules apply on top of this whatever it is set to.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ServerColorPicking = Config.Bind(
                "Server - Color Cycling", "Color Picking", true,
                new ConfigDescription(
                    ServerPaired +
                    "Allows right-click color copying from a painted object onto a spray can. " +
                    "Turn it off to keep colors coming only from printed cans.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            // ---- Server - Consumables --------------------------------------------
            ServerUnlimitedSprayPaintUses = Config.Bind(
                "Server - Consumables", "Unlimited Spray Paint Uses", true,
                new ConfigDescription(
                    ServerPaired +
                    "Makes spray cans infinite. When disabled, spray cans are consumed after their " +
                    "normal number of uses. Players can still choose to have their own cans deplete.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ServerSuppressSprayPaintPollution = Config.Bind(
                "Server - Consumables", "Suppress Spray Paint Pollution", true,
                new ConfigDescription(
                    "(Server-authoritative) Stops spray cans releasing pollutant gas into the " +
                    "atmosphere when used. There is no player-side version of this one: the " +
                    "atmosphere is shared, so one player opting out would change the air for everybody.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            // ---- Server - Glow Paint ----------------------------------------------
            ServerGlowPaint = Config.Bind(
                "Server - Glow Paint", "Glow Paint", true,
                new ConfigDescription(
                    ServerPaired +
                    "Allows the Spray Paint Gun to add and remove glow. When off, the gun works as " +
                    "it does in the base game and loads a spray can.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            // ---- Server - Network Painting -----------------------------------------
            ServerNetworkPainting = Config.Bind(
                "Server - Network Painting", "Network Painting", true,
                new ConfigDescription(
                    ServerPaired +
                    "Allows one stroke to paint a whole connected set. When disabled, only the " +
                    "targeted item is painted regardless of modifiers. The entries below choose " +
                    "which kinds of network qualify on this server.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ServerNetworkPaintPipes = Config.Bind(
                "Server - Network Painting", "Network Paint Pipes", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes pipe networks (pipes, passive vents, hydroponic trays) when painting " +
                    "a whole network. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            ServerNetworkPaintCables = Config.Bind(
                "Server - Network Painting", "Network Paint Cables", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes cable networks when painting a whole network. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            ServerNetworkPaintChutes = Config.Bind(
                "Server - Network Painting", "Network Paint Chutes", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes chute networks when painting a whole network. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));

            ServerNetworkPaintWalls = Config.Bind(
                "Server - Network Painting", "Network Paint Walls", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes all same-type walls bounding the same room when painting a whole " +
                    "network. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 50)));

            ServerNetworkPaintRails = Config.Bind(
                "Server - Network Painting", "Network Paint Rails", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes every rail, junction, bypass and dock on one robotic arm assembly. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 60)));

            ServerNetworkPaintLargeStructures = Config.Bind(
                "Server - Network Painting", "Network Paint Large Structures", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes connected large structures such as frames and girders. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 70)));

            ServerNetworkPaintElevators = Config.Bind(
                "Server - Network Painting", "Network Paint Elevators", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes every shaft and level segment of one elevator. The carriage is " +
                    "painted on its own. No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 80)));

            ServerNetworkPaintLadders = Config.Bind(
                "Server - Network Painting", "Network Paint Ladders", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes the whole ladder column and its end caps. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 90)));

            ServerNetworkPaintStairs = Config.Bind(
                "Server - Network Painting", "Network Paint Stairs", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes a whole staircase across its width and its climb: flights set side by " +
                    "side to widen it and flights run up or down to lengthen it. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 100)));

            ServerNetworkPaintStairwells = Config.Bind(
                "Server - Network Painting", "Network Paint Stairwells", true,
                new ConfigDescription(
                    ServerPaired +
                    "Includes every adjacent stairwell block, all eight types, any orientation. " +
                    "No effect if Network Painting is off.",
                    null,
                    new KeyValuePair<string, int>("Order", 110)));
        }
    }
}
