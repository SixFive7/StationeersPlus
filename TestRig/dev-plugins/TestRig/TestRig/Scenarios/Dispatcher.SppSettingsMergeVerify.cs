using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Networking;
using BepInEx.Configuration;
using BepInEx.Logging;
using DLC;

namespace TestRig.Scenarios
{
    // Scenario: spp-settings-merge-verify
    //
    // One-shot. Phase 1 of the Spray Paint Plus v1.11.0 playtest plan: everything about
    // the paired client/server settings model that can be established without a client,
    // without the settings panel, and without a second player.
    //
    // Seven phases:
    //   P0  Config binding      33 entries, nine groups, the two enum entries.
    //   P1  Boolean merge       all four client/server combinations, both the locally
    //                           evaluated accessor and the server-side per-player merge.
    //   P2  Cycling ladder      all nine mode combinations, plus the ladder ordering the
    //                           Math.Min depends on.
    //   P3  Authority           IsAuthority on a dedi, and the server half resolving to
    //                           the LOCAL entry under every authority shape. This is the
    //                           v1.2.2 infinite-spray shape and the point of the phase.
    //   P4  Paint families      the 0-11 / 12-15 split, no cross-family false positives,
    //                           and an unmapped index joining the base family.
    //   P5  WithinFamily        NextColorInCycle driven from a base seed and a metallic
    //                           seed, asserting it never leaves the family it started in.
    //   P6  Effective log line  exactly one Info line, with correct client/server/result
    //                           columns.
    //
    // Everything the scenario writes is snapshotted first and restored in a finally:
    // every config entry's value, NetworkManager.NetworkRole, SharedDLCManager.SharedDLC,
    // SettingsMerge's synced half, and the per-player modifier dictionary.
    //
    // NetworkRole is a plain public static field and the three roles are read through
    // IsActive / IsServer, so driving it directly is how the single-player and
    // remote-client shapes get covered on a machine that is neither. The write window is
    // a few reflection calls inside one synchronous tick postfix with nobody connected,
    // and it is restored before the phase returns.
    //
    // Reflection throughout: every type the scenario touches is internal to the mod and
    // ScenarioRunner has no build-time dependency on it. Config values are driven through
    // ConfigEntryBase.BoxedValue so the enum entries need no generic instantiation.
    // Managed state only, so this is safe on the UniTask worker the sim-tick pump uses.

    internal static partial class Dispatcher
    {
        private static bool _sppMergeVerifyFired;

        // Assertion tally for the whole scenario.
        private static int _sppMergePass;
        private static int _sppMergeFail;

        // Info-level events captured off the mod's own log source during P6.
        private static readonly List<string> _sppCapturedInfo = new List<string>();
        private static bool _sppCapturing;

        private const string SPP_MERGE_TAG = "spp-settings-merge";

        private static void Scenario_SppSettingsMergeVerify()
        {
            if (!RequireModAssembly(SPP_ASSEMBLY, "spp-settings-merge-verify")) return;
            if (_sppMergeVerifyFired) return;
            _sppMergeVerifyFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] spp-settings-merge-verify START");

                var asm = GetModAssembly(SPP_ASSEMBLY);
                const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var plugin = asm.GetType("SprayPaintPlus.SprayPaintPlusPlugin");
                var merge = asm.GetType("SprayPaintPlus.SettingsMerge");
                var prefs = asm.GetType("SprayPaintPlus.SettingsMerge+PlayerPrefs");
                var modeT = asm.GetType("SprayPaintPlus.ColorCyclingMode");
                var gate = asm.GetType("SprayPaintPlus.DlcPaintGate");
                var cycler = asm.GetType("SprayPaintPlus.ColorCyclerPatch");
                var warn = asm.GetType("SprayPaintPlus.WarningNotifier");
                var helpers = asm.GetType("SprayPaintPlus.SprayPaintHelpers");

                if (plugin == null || merge == null || prefs == null || modeT == null
                    || gate == null || cycler == null || warn == null || helpers == null)
                {
                    _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | type resolution failed " +
                                   $"(plugin={plugin != null} merge={merge != null} prefs={prefs != null} " +
                                   $"mode={modeT != null} gate={gate != null} cycler={cycler != null} " +
                                   $"warn={warn != null} helpers={helpers != null}), aborting");
                    return;
                }

                // ---- Members ----------------------------------------------------
                var pIsAuthority = merge.GetProperty("IsAuthority", F);
                var pEffCycling = merge.GetProperty("EffectiveColorCycling", F);
                var pEffPicking = merge.GetProperty("EffectiveColorPicking", F);
                var pEffGlow = merge.GetProperty("EffectiveGlowPaint", F);
                var mClearSynced = merge.GetMethod("ClearSynced", F);
                var mServerAllows = merge.GetMethod("ServerAllows", F);
                var mHas = prefs.GetMethod("Has", F);
                var mSameFamily = gate.GetMethod("SameFamily", F);
                var mFamilyOf = gate.GetMethod("FamilyOf", F);
                var mFamilyName = gate.GetMethod("FamilyName", F);
                var mBuildGate = gate.GetMethod("Build", F);
                var mLogEffective = warn.GetMethod("LogEffectiveSettings", F);
                var mNextInCycle = cycler.GetMethod("NextColorInCycle", F, null,
                    new[] { typeof(int), typeof(int), typeof(bool), modeT }, null);
                var fPlayerModifiers = helpers.GetField("PlayerModifiers", F);
                var fLog = plugin.GetField("Log", F);

                if (pIsAuthority == null || pEffCycling == null || pEffPicking == null || pEffGlow == null
                    || mClearSynced == null || mServerAllows == null || mHas == null
                    || mSameFamily == null || mFamilyOf == null || mFamilyName == null || mBuildGate == null
                    || mLogEffective == null || mNextInCycle == null || fPlayerModifiers == null || fLog == null)
                {
                    _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | member resolution failed " +
                                   $"(isAuthority={pIsAuthority != null} effCycling={pEffCycling != null} " +
                                   $"effPicking={pEffPicking != null} effGlow={pEffGlow != null} " +
                                   $"clearSynced={mClearSynced != null} " +
                                   $"serverAllows={mServerAllows != null} has={mHas != null} " +
                                   $"sameFamily={mSameFamily != null} familyOf={mFamilyOf != null} " +
                                   $"familyName={mFamilyName != null} build={mBuildGate != null} " +
                                   $"logEffective={mLogEffective != null} nextInCycle={mNextInCycle != null} " +
                                   $"playerModifiers={fPlayerModifiers != null} log={fLog != null}), aborting");
                    return;
                }

                // ---- Config entries ---------------------------------------------
                var entries = new Dictionary<string, ConfigEntryBase>(StringComparer.Ordinal);
                foreach (var f in plugin.GetFields(F))
                {
                    if (!typeof(ConfigEntryBase).IsAssignableFrom(f.FieldType)) continue;
                    entries[f.Name] = f.GetValue(null) as ConfigEntryBase;
                }

                var playerModifiers = fPlayerModifiers.GetValue(null) as Dictionary<long, ushort>;
                if (playerModifiers == null)
                {
                    _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | PlayerModifiers is not Dictionary<long,ushort>, aborting");
                    return;
                }

                // ---- Snapshot everything this scenario writes --------------------
                var savedValues = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var kvp in entries)
                    if (kvp.Value != null) savedValues[kvp.Key] = kvp.Value.BoxedValue;

                NetworkRole savedRole = NetworkManager.NetworkRole;
                ushort savedPool = SharedDLCManager.SharedDLC;
                var savedModifiers = new Dictionary<long, ushort>(playerModifiers);

                var syncedFields = merge.GetFields(F)
                    .Where(f => f.Name.StartsWith("Synced", StringComparison.Ordinal))
                    .ToArray();
                var savedSynced = syncedFields.ToDictionary(f => f.Name, f => f.GetValue(null), StringComparer.Ordinal);

                _sppMergePass = 0;
                _sppMergeFail = 0;
                _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | entered with role={savedRole} pool=0x{savedPool:X} " +
                              $"configEntries={entries.Count} syncedFields={syncedFields.Length} " +
                              $"playerModifierRows={savedModifiers.Count}");

                try
                {
                    SppMerge_P0_ConfigBinding(entries, modeT, plugin);
                    SppMerge_P1_BooleanTruthTable(entries, merge, mClearSynced, pEffGlow,
                        mServerAllows, mHas, prefs, playerModifiers);
                    SppMerge_P2_CyclingLadder(entries, modeT, mClearSynced, pEffCycling, pEffPicking);
                    SppMerge_P3_Authority(entries, merge, modeT, mClearSynced, pIsAuthority,
                        pEffGlow, pEffCycling, pEffPicking);
                    SppMerge_P4_PaintFamilies(mBuildGate, mFamilyOf, mSameFamily, mFamilyName);
                    SppMerge_P5_WithinFamilyNeverCrosses(mNextInCycle, modeT, mFamilyOf, mSameFamily);
                    SppMerge_P6_EffectiveLogLine(entries, modeT, mClearSynced, mLogEffective, fLog);
                }
                finally
                {
                    foreach (var kvp in savedValues)
                    {
                        try { entries[kvp.Key].BoxedValue = kvp.Value; }
                        catch (Exception e)
                        {
                            _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | restore of '{kvp.Key}' threw: {e.Message}");
                        }
                    }
                    foreach (var f in syncedFields)
                        f.SetValue(null, savedSynced[f.Name]);
                    NetworkManager.NetworkRole = savedRole;
                    SharedDLCManager.SharedDLC = savedPool;
                    playerModifiers.Clear();
                    foreach (var kvp in savedModifiers) playerModifiers[kvp.Key] = kvp.Value;

                    _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | restored role={NetworkManager.NetworkRole} " +
                                  $"pool=0x{SharedDLCManager.SharedDLC:X} configEntries={savedValues.Count} " +
                                  $"playerModifierRows={playerModifiers.Count}");
                }

                _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | RESULT " +
                              $"{(_sppMergeFail == 0 ? "ALL PASS" : "FAILURES PRESENT")} " +
                              $"pass={_sppMergePass} fail={_sppMergeFail} total={_sppMergePass + _sppMergeFail}");
                _log?.LogInfo("[ScenarioRunner] spp-settings-merge-verify END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] spp-settings-merge-verify threw: {e}");
            }
        }

        // ================= P0: config binding =================================

        private static void SppMerge_P0_ConfigBinding(
            Dictionary<string, ConfigEntryBase> entries, Type modeT, Type plugin)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P0 config binding");

            // Every static ConfigEntry field the plugin declares must have survived
            // BindConfig. A null field means Config.Bind threw for that entry.
            var nulls = entries.Where(e => e.Value == null).Select(e => e.Key).ToArray();
            Chk("P0 no-null-entries", nulls.Length == 0,
                nulls.Length == 0 ? "every declared ConfigEntry field is bound"
                                  : "unbound: " + string.Join(", ", nulls));

            Chk("P0 entry-count", entries.Count == 33, $"declared ConfigEntry fields={entries.Count} (want 33)");

            // Reading Value on each is the second half of "bound without error": a bound
            // entry whose stored text failed to parse would throw here.
            int readable = 0;
            foreach (var kvp in entries)
            {
                if (kvp.Value == null) continue;
                try { var _ = kvp.Value.BoxedValue; readable++; }
                catch (Exception e)
                {
                    Chk($"P0 readable[{kvp.Key}]", false, "BoxedValue threw: " + e.Message);
                }
            }
            Chk("P0 all-readable", readable == entries.Count(e => e.Value != null),
                $"entries whose value reads back cleanly={readable}");

            // Nine groups with exactly these names and sizes. The two Paintability groups
            // went away with Extra Paintable Structures: the base game made the steel and
            // iron frame variants paintable itself, so the mod no longer adds any.
            var expected = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                { "Client - Color Cycling", 2 },
                { "Client - Consumables", 1 },
                { "Client - Glow Paint", 1 },
                { "Client - Network Painting", 11 },
                { "Client - Preferences", 2 },
                { "Server - Color Cycling", 2 },
                { "Server - Consumables", 2 },
                { "Server - Glow Paint", 1 },
                { "Server - Network Painting", 11 },
            };

            var actual = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kvp in entries)
            {
                if (kvp.Value == null) continue;
                string section = kvp.Value.Definition.Section;
                actual.TryGetValue(section, out int n);
                actual[section] = n + 1;
            }

            Chk("P0 group-count", actual.Count == 9, $"distinct sections={actual.Count} (want 9)");

            foreach (var want in expected.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                actual.TryGetValue(want.Key, out int got);
                Chk($"P0 group[{want.Key}]", got == want.Value, $"entries={got} (want {want.Value})");
            }
            foreach (var got in actual.Keys.OrderBy(k => k, StringComparer.Ordinal))
                if (!expected.ContainsKey(got))
                    Chk($"P0 group[{got}]", false, "unexpected section name");

            // The panel sorts groups alphabetically, so every Client group must sort
            // ahead of every Server group for the intended layout to hold.
            var sorted = actual.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
            int lastClient = Array.FindLastIndex(sorted, s => s.StartsWith("Client - ", StringComparison.Ordinal));
            int firstServer = Array.FindIndex(sorted, s => s.StartsWith("Server - ", StringComparison.Ordinal));
            Chk("P0 client-groups-sort-first", lastClient >= 0 && firstServer > lastClient,
                $"alphabetical order: {string.Join(" | ", sorted)}");

            // The two enum entries. Binding an enum is the new thing in v1.11.0 and is
            // the only route to a dropdown, so both the type and a clean read matter.
            foreach (var name in new[] { "ClientColorCycling", "ServerColorCycling" })
            {
                entries.TryGetValue(name, out ConfigEntryBase e);
                if (e == null) { Chk($"P0 enum[{name}]", false, "entry missing"); continue; }

                bool typed = e.SettingType == modeT;
                object val = null;
                bool read = true;
                try { val = e.BoxedValue; } catch { read = false; }

                // AcceptableValues must stay null: StationeersLaunchPad renders a
                // dropdown from the enum type, and an AcceptableValueList suppresses it.
                bool noAcceptable = e.Description == null || e.Description.AcceptableValues == null;

                Chk($"P0 enum[{name}]", typed && read && noAcceptable && val != null,
                    $"settingType={e.SettingType?.Name} value={val} defaultValue={e.DefaultValue} " +
                    $"acceptableValuesNull={noAcceptable}");
            }

            // Cross-check against the ConfigFile itself, which sees every bind whether or
            // not the plugin kept a field for it. Guarded: this reaches BepInEx internals
            // and a shape change here should not take the phase down.
            try
            {
                var info = BepInEx.Bootstrap.Chainloader.PluginInfos["net.spraypaintplus"];
                var cfgProp = typeof(BepInEx.BaseUnityPlugin).GetProperty("Config",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var cfg = cfgProp?.GetValue(info.Instance) as IDictionary<ConfigDefinition, ConfigEntryBase>;
                if (cfg == null)
                {
                    _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P0 configfile NOTE: ConfigFile not reachable, cross-check skipped");
                }
                else
                {
                    var fileSections = cfg.Keys.Select(d => d.Section).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
                    Chk("P0 configfile-count", cfg.Count == 33, $"ConfigFile entries={cfg.Count} (want 33)");
                    Chk("P0 configfile-groups", fileSections.Length == 9,
                        $"ConfigFile sections={fileSections.Length}: {string.Join(" | ", fileSections)}");
                }
            }
            catch (Exception e)
            {
                _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P0 configfile NOTE: cross-check threw ({e.Message})");
            }
        }

        // ================= P1: boolean merge truth table =======================

        private static void SppMerge_P1_BooleanTruthTable(
            Dictionary<string, ConfigEntryBase> entries, Type merge, MethodInfo clearSynced,
            PropertyInfo effGlow, MethodInfo serverAllows, MethodInfo has,
            Type prefs, Dictionary<long, ushort> playerModifiers)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P1 boolean merge truth table");
            clearSynced.Invoke(null, null);

            // Locally evaluated accessor. Glow Paint is the representative pair: a plain
            // client AND server with no extra rule layered on top.
            foreach (bool c in new[] { false, true })
                foreach (bool s in new[] { false, true })
                {
                    Set(entries, "ClientGlowPaint", c);
                    Set(entries, "ServerGlowPaint", s);
                    bool got = (bool)effGlow.GetValue(null);
                    bool want = c && s;
                    Chk($"P1 glow[client={OnOff(c)},server={OnOff(s)}]", got == want,
                        $"effective={OnOff(got)} (want {OnOff(want)})");
                }

            // The server-side per-player merge, which is the shape the paint path uses:
            // the server's own half ANDed with the ACTING player's bit rather than the
            // local machine's config. Same truth table, different halves.
            const long TestHuman = 424242L;
            int pipesBit = (int)prefs.GetField("Pipes", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .GetValue(null);
            var serverPipes = entries["ServerNetworkPaintPipes"];
            var syncedPipes = merge.GetField("SyncedNetworkPaintPipes",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            foreach (bool clientBit in new[] { false, true })
                foreach (bool s in new[] { false, true })
                {
                    Set(entries, "ServerNetworkPaintPipes", s);
                    playerModifiers[TestHuman] = clientBit ? (ushort)(1 << pipesBit) : (ushort)0;

                    bool got = (bool)serverAllows.Invoke(null,
                        new object[] { serverPipes, syncedPipes.GetValue(null), TestHuman, pipesBit });
                    bool want = clientBit && s;
                    Chk($"P1 serverAllows-pipes[clientBit={OnOff(clientBit)},server={OnOff(s)}]", got == want,
                        $"allows={OnOff(got)} (want {OnOff(want)})");
                }

            // An unreported player defaults to allowing: the server's own half has already
            // been applied, and a client that never sent its mask must not be restricted
            // into a different game than everyone else.
            playerModifiers.Remove(TestHuman);
            Set(entries, "ServerNetworkPaintPipes", true);
            bool unreported = (bool)serverAllows.Invoke(null,
                new object[] { serverPipes, syncedPipes.GetValue(null), TestHuman, pipesBit });
            Chk("P1 serverAllows-unreported-player", unreported,
                $"allows={OnOff(unreported)} (want on: absent mask means allow)");

            bool hasUnreported = (bool)has.Invoke(null, new object[] { TestHuman, pipesBit });
            Chk("P1 playerPrefs-Has-unreported", hasUnreported,
                $"Has={OnOff(hasUnreported)} (want on)");

            playerModifiers.Remove(TestHuman);
        }

        // ================= P2: color cycling ladder ============================

        private static void SppMerge_P2_CyclingLadder(
            Dictionary<string, ConfigEntryBase> entries, Type modeT, MethodInfo clearSynced,
            PropertyInfo effCycling, PropertyInfo effPicking)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P2 color cycling ladder");
            clearSynced.Invoke(null, null);

            // The ladder ordering is what makes Math.Min mean "stricter wins". If these
            // numbers ever move, every merge in the mod silently inverts.
            int cannot = (int)Enum.Parse(modeT, "CannotChange");
            int within = (int)Enum.Parse(modeT, "WithinFamily");
            int all = (int)Enum.Parse(modeT, "AllColors");
            Chk("P2 ladder-ordering", cannot == 0 && within == 1 && all == 2,
                $"CannotChange={cannot} WithinFamily={within} AllColors={all} (want 0 < 1 < 2)");
            Chk("P2 ladder-member-count", Enum.GetValues(modeT).Length == 3,
                $"members={Enum.GetValues(modeT).Length} (want 3)");

            var names = new[] { "CannotChange", "WithinFamily", "AllColors" };
            for (int ci = 0; ci < 3; ci++)
                for (int si = 0; si < 3; si++)
                {
                    Set(entries, "ClientColorCycling", Enum.Parse(modeT, names[ci]));
                    Set(entries, "ServerColorCycling", Enum.Parse(modeT, names[si]));

                    object got = effCycling.GetValue(null);
                    int gotI = (int)got;
                    int wantI = Math.Min(ci, si);
                    Chk($"P2 cycling[client={names[ci]},server={names[si]}]", gotI == wantI,
                        $"effective={got} (want {names[wantI]}, the stricter of the two)");
                }

            // Color picking is subordinate to the mode: eyedropping IS changing the can's
            // color, so CannotChange from either side must take picking down with it even
            // when both picking halves are on.
            Set(entries, "ClientColorPicking", true);
            Set(entries, "ServerColorPicking", true);
            for (int ci = 0; ci < 3; ci++)
                for (int si = 0; si < 3; si++)
                {
                    Set(entries, "ClientColorCycling", Enum.Parse(modeT, names[ci]));
                    Set(entries, "ServerColorCycling", Enum.Parse(modeT, names[si]));
                    bool got = (bool)effPicking.GetValue(null);
                    bool want = Math.Min(ci, si) != 0;
                    Chk($"P2 picking-under-mode[client={names[ci]},server={names[si]}]", got == want,
                        $"picking={OnOff(got)} (want {OnOff(want)}) with both picking halves on");
                }
        }

        // ================= P3: authority resolution ============================

        private static void SppMerge_P3_Authority(
            Dictionary<string, ConfigEntryBase> entries, Type merge, Type modeT, MethodInfo clearSynced,
            PropertyInfo isAuthority, PropertyInfo effGlow, PropertyInfo effCycling,
            PropertyInfo effPicking)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P3 authority resolution");

            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            var fSyncedGlow = merge.GetField("SyncedGlowPaint", F);
            var fSyncedCycling = merge.GetField("SyncedColorCycling", F);

            // The dedicated server is the authority. NetworkRole is Server here, so this
            // is the natural role and needs no forcing.
            clearSynced.Invoke(null, null);
            Chk("P3 dedi-is-authority", (bool)isAuthority.GetValue(null),
                $"IsAuthority={isAuthority.GetValue(null)} with NetworkRole={NetworkManager.NetworkRole} " +
                $"(IsActive={NetworkManager.IsActive} IsServer={NetworkManager.IsServer})");

            // The core assertion. A synced host value is present and says NO; the local
            // server entry says YES. Under every authority shape the LOCAL entry must win,
            // because a synced value on an authority is either stale or somebody else's.
            Set(entries, "ClientGlowPaint", true);
            Set(entries, "ServerGlowPaint", true);

            var shapes = new[]
            {
                new { Role = NetworkRole.Server, Name = "dedicated server / listen host (IsServer true)", WantAuthority = true },
                new { Role = NetworkRole.None,   Name = "single-player (IsActive false)",                 WantAuthority = true },
                new { Role = NetworkRole.Client, Name = "remote client",                                  WantAuthority = false },
            };

            foreach (var shape in shapes)
            {
                NetworkManager.NetworkRole = shape.Role;

                bool auth = (bool)isAuthority.GetValue(null);
                Chk($"P3 isAuthority[{shape.Role}]", auth == shape.WantAuthority,
                    $"{shape.Name}: IsAuthority={auth} (want {shape.WantAuthority}) " +
                    $"IsActive={NetworkManager.IsActive} IsServer={NetworkManager.IsServer}");

                // Synced says off, local server entry says on.
                fSyncedGlow.SetValue(null, false);
                bool withSynced = (bool)effGlow.GetValue(null);
                bool wantWithSynced = !shape.WantAuthority ? false : true;
                Chk($"P3 serverHalf-source[{shape.Role}]", withSynced == wantWithSynced,
                    $"{shape.Name}: local server half=on, synced host half=off -> effective={OnOff(withSynced)} " +
                    $"(want {OnOff(wantWithSynced)}: {(shape.WantAuthority ? "LOCAL entry, synced ignored" : "synced host value")})");

                // No synced value at all. Nothing may be silently disabled: an authority
                // reads its own entry, and a remote client that has not received the join
                // payload yet falls back to its own entry rather than to false.
                fSyncedGlow.SetValue(null, null);
                bool noSynced = (bool)effGlow.GetValue(null);
                Chk($"P3 no-synced-not-disabled[{shape.Role}]", noSynced,
                    $"{shape.Name}: synced=null, both local halves on -> effective={OnOff(noSynced)} (want on)");
            }

            // The v1.2.2 shape, stated as its own assertion because it is the one that
            // shipped broken: solo reports NetworkRole.None, so a bare !IsServer guard
            // treats it as a remote client and reads a synced value that does not exist.
            NetworkManager.NetworkRole = NetworkRole.None;
            clearSynced.Invoke(null, null);
            Set(entries, "ClientGlowPaint", true);
            Set(entries, "ServerGlowPaint", false);
            bool soloServerOff = (bool)effGlow.GetValue(null);
            Chk("P3 solo-server-half-applies", !soloServerOff,
                $"single-player with server half off -> effective={OnOff(soloServerOff)} " +
                "(want off: solo behaves as a one-player server, both halves apply)");

            Set(entries, "ClientGlowPaint", false);
            Set(entries, "ServerGlowPaint", true);
            bool soloClientOff = (bool)effGlow.GetValue(null);
            Chk("P3 solo-client-half-applies", !soloClientOff,
                $"single-player with client half off -> effective={OnOff(soloClientOff)} (want off)");

            // Same question for the enum half: an authority must not read a synced mode.
            Set(entries, "ClientColorCycling", Enum.Parse(modeT, "AllColors"));
            Set(entries, "ServerColorCycling", Enum.Parse(modeT, "AllColors"));
            foreach (var shape in shapes)
            {
                NetworkManager.NetworkRole = shape.Role;
                fSyncedCycling.SetValue(null, Enum.Parse(modeT, "CannotChange"));
                object got = effCycling.GetValue(null);
                string want = shape.WantAuthority ? "AllColors" : "CannotChange";
                Chk($"P3 cycling-serverHalf-source[{shape.Role}]", got.ToString() == want,
                    $"{shape.Name}: local halves AllColors, synced CannotChange -> effective={got} (want {want})");

                fSyncedCycling.SetValue(null, null);
                object noSync = effCycling.GetValue(null);
                Chk($"P3 cycling-no-synced[{shape.Role}]", noSync.ToString() == "AllColors",
                    $"{shape.Name}: synced=null, both local halves AllColors -> effective={noSync} (want AllColors)");
            }

            // Nothing silently disabled with a clean slate: every accessor at defaults, in
            // every role, with no synced value present.
            clearSynced.Invoke(null, null);
            foreach (var name in new[] { "ClientColorPicking", "ServerColorPicking", "ClientGlowPaint",
                                         "ServerGlowPaint" })
                Set(entries, name, true);
            Set(entries, "ClientColorCycling", Enum.Parse(modeT, "AllColors"));
            Set(entries, "ServerColorCycling", Enum.Parse(modeT, "AllColors"));

            foreach (var shape in shapes)
            {
                NetworkManager.NetworkRole = shape.Role;
                bool allOn = (bool)effPicking.GetValue(null)
                          && (bool)effGlow.GetValue(null)
                          && effCycling.GetValue(null).ToString() == "AllColors";
                Chk($"P3 defaults-nothing-disabled[{shape.Role}]", allOn,
                    $"{shape.Name}: picking={effPicking.GetValue(null)} glow={effGlow.GetValue(null)} " +
                    $"cycling={effCycling.GetValue(null)} (want all permissive)");
            }

            NetworkManager.NetworkRole = NetworkRole.Server;
        }

        // ================= P4: paint family grouping ===========================

        private static void SppMerge_P4_PaintFamilies(
            MethodInfo buildGate, MethodInfo familyOf, MethodInfo sameFamily, MethodInfo familyName)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P4 paint family grouping");

            buildGate.Invoke(null, null);

            var colors = GameManager.Instance?.CustomColors;
            if (colors == null || colors.Count == 0)
            {
                Chk("P4 swatches-available", false, "GameManager.CustomColors unavailable, phase cannot run");
                return;
            }
            int count = colors.Count;
            Chk("P4 swatch-count", count == 16, $"CustomColors={count} (want 16: 12 base + 4 metallic)");

            var family = new DLCType[count];
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                family[i] = (DLCType)familyOf.Invoke(null, new object[] { i });
                sb.Append(i).Append('=').Append(colors[i]?.Name).Append(':')
                  .Append(family[i]).Append(' ');
            }
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P4 map: {sb.ToString().TrimEnd()}");

            int baseTo = Math.Min(12, count);
            bool baseOk = true;
            for (int i = 0; i < baseTo; i++) if (family[i] != DLCType.None) baseOk = false;
            Chk("P4 base-family-0-to-11", baseOk,
                $"indices 0-{baseTo - 1} all DLCType.None: {baseOk}");

            bool metallicOk = count >= 16;
            for (int i = 12; i < count && i < 16; i++)
                if (family[i] != DLCType.MetallicPaints) metallicOk = false;
            Chk("P4 metallic-family-12-to-15", metallicOk,
                $"indices 12-15 all DLCType.MetallicPaints: {metallicOk}");

            // SameFamily agrees with FamilyOf on every ordered pair, and never reports a
            // cross-family pair as the same family. 16x16 = 256 pairs.
            int within = 0, across = 0, wrong = 0;
            for (int a = 0; a < count; a++)
                for (int b = 0; b < count; b++)
                {
                    bool got = (bool)sameFamily.Invoke(null, new object[] { a, b });
                    bool want = family[a] == family[b];
                    if (want) within++; else across++;
                    if (got != want)
                    {
                        wrong++;
                        if (wrong <= 5)
                            _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | P4 pair FAIL a={a}({family[a]}) " +
                                           $"b={b}({family[b]}) SameFamily={got} want={want}");
                    }
                }
            Chk("P4 sameFamily-matrix", wrong == 0,
                $"pairs={count * count} sameFamily={within} crossFamily={across} mismatches={wrong}");

            // Explicit cross-family negatives, so the matrix passing vacuously would show.
            if (count >= 16)
            {
                Chk("P4 cross-family-negative", !(bool)sameFamily.Invoke(null, new object[] { 0, 12 })
                                              && !(bool)sameFamily.Invoke(null, new object[] { 11, 15 })
                                              && !(bool)sameFamily.Invoke(null, new object[] { 15, 3 }),
                    "SameFamily(0,12), (11,15), (15,3) all false");
                Chk("P4 within-family-positive", (bool)sameFamily.Invoke(null, new object[] { 0, 11 })
                                              && (bool)sameFamily.Invoke(null, new object[] { 12, 15 }),
                    "SameFamily(0,11) and (12,15) both true");
            }

            // Decided policy: a swatch with no dispensing can (another mod's color) joins
            // the base family, so it lands in the largest family instead of a family of
            // one and WithinFamily can never strand it with nowhere to cycle.
            int absent = count + 7;
            var absentFamily = (DLCType)familyOf.Invoke(null, new object[] { absent });
            Chk("P4 unmapped-joins-base-family", absentFamily == DLCType.None
                                               && (bool)sameFamily.Invoke(null, new object[] { absent, 0 }),
                $"FamilyOf({absent})={absentFamily} SameFamily({absent},0)=" +
                $"{sameFamily.Invoke(null, new object[] { absent, 0 })} (want None / true)");
            Chk("P4 unmapped-not-metallic", count < 16
                    || !(bool)sameFamily.Invoke(null, new object[] { absent, 12 }),
                $"SameFamily({absent},12)={(count >= 16 ? sameFamily.Invoke(null, new object[] { absent, 12 }).ToString() : "n/a")} (want false)");

            if (count >= 16)
                Chk("P4 family-names", (string)familyName.Invoke(null, new object[] { 0 }) == "standard"
                                    && (string)familyName.Invoke(null, new object[] { 12 }) == "metallic",
                    $"FamilyName(0)={familyName.Invoke(null, new object[] { 0 })} " +
                    $"FamilyName(12)={familyName.Invoke(null, new object[] { 12 })}");
        }

        // ================= P5: WithinFamily never crosses ======================

        private static void SppMerge_P5_WithinFamilyNeverCrosses(
            MethodInfo nextInCycle, Type modeT, MethodInfo familyOf, MethodInfo sameFamily)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P5 WithinFamily never crosses");

            var colors = GameManager.Instance?.CustomColors;
            if (colors == null || colors.Count == 0)
            {
                Chk("P5 swatches-available", false, "GameManager.CustomColors unavailable, phase cannot run");
                return;
            }
            int count = colors.Count;

            object within = Enum.Parse(modeT, "WithinFamily");
            object allColors = Enum.Parse(modeT, "AllColors");

            // Entitle the session to the metallic family, otherwise the DLC gate (which is
            // checked first and outranks the mode) makes the metallic walk untestable. A
            // batch-mode dedi never seeds the pool, so this starts empty and is restored
            // by the scenario's outer finally.
            SharedDLCManager.SharedDLC = (ushort)DLCType.MetallicPaints;

            foreach (int seed in new[] { 0, 5, 11, 12, 13, 15 })
            {
                var seedFamily = (DLCType)familyOf.Invoke(null, new object[] { seed });

                foreach (bool forward in new[] { true, false })
                {
                    int at = seed;
                    var visited = new List<int>();
                    bool crossed = false;
                    int crossedAt = -1;

                    // Two full laps: long enough to wrap the whole swatch list twice, so a
                    // family escape at a wrap boundary cannot hide.
                    for (int step = 0; step < count * 2; step++)
                    {
                        at = (int)nextInCycle.Invoke(null, new object[] { at, count, forward, within });
                        visited.Add(at);
                        if (!(bool)sameFamily.Invoke(null, new object[] { seed, at }))
                        {
                            crossed = true;
                            crossedAt = at;
                            break;
                        }
                    }

                    Chk($"P5 withinFamily[seed={seed},{(forward ? "fwd" : "back")}]", !crossed,
                        crossed
                            ? $"seed family={seedFamily} escaped to index {crossedAt} " +
                              $"(family {familyOf.Invoke(null, new object[] { crossedAt })})"
                            : $"seed family={seedFamily}, {count * 2} steps stayed inside; " +
                              $"distinct landings={visited.Distinct().Count()} " +
                              $"first8=[{string.Join(",", visited.Take(8).Select(v => v.ToString()).ToArray())}]");
                }
            }

            // The family walk must actually move, or "never crosses" would pass on a
            // wheel that is simply stuck.
            int baseStep = (int)nextInCycle.Invoke(null, new object[] { 0, count, true, within });
            int metallicStep = (int)nextInCycle.Invoke(null, new object[] { 12, count, true, within });
            Chk("P5 family-walk-advances", baseStep == 1 && metallicStep == 13,
                $"WithinFamily forward from 0 -> {baseStep} (want 1), from 12 -> {metallicStep} (want 13)");

            // The family boundary is a wrap, not a wall: the last member of a family
            // wraps to the first member of the same family, skipping the other family.
            int baseWrap = (int)nextInCycle.Invoke(null, new object[] { 11, count, true, within });
            int metallicWrap = (int)nextInCycle.Invoke(null, new object[] { 15, count, true, within });
            int metallicBack = (int)nextInCycle.Invoke(null, new object[] { 12, count, false, within });
            Chk("P5 family-wraps-within-family", baseWrap == 0 && metallicWrap == 12 && metallicBack == 15,
                $"11 fwd -> {baseWrap} (want 0), 15 fwd -> {metallicWrap} (want 12), " +
                $"12 back -> {metallicBack} (want 15)");

            // AllColors under the same entitlement DOES cross, which is the control: it
            // proves the family confinement above comes from the mode and not from the
            // DLC gate quietly hiding the other family.
            int allStep = (int)nextInCycle.Invoke(null, new object[] { 11, count, true, allColors });
            Chk("P5 allColors-control-crosses", allStep == 12,
                $"AllColors forward from 11 -> {allStep} (want 12: the mode, not the gate, is what confines)");

            // Entitlement outranks the mode. With the pool empty a metallic can has no
            // reachable same-family color, and NextColorInCycle returns the seed unchanged
            // rather than escaping into the base family. Recorded as an observation: the
            // can is frozen until entitlement returns or the mode is loosened.
            SharedDLCManager.SharedDLC = 0;
            int strandedFwd = (int)nextInCycle.Invoke(null, new object[] { 12, count, true, within });
            int strandedBack = (int)nextInCycle.Invoke(null, new object[] { 12, count, false, within });
            Chk("P5 entitlement-outranks-mode", strandedFwd == 12 && strandedBack == 12,
                $"pool empty, metallic seed under WithinFamily: fwd -> {strandedFwd}, back -> {strandedBack} " +
                "(want 12 both: gate is checked first, so no escape into the base family)");
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P5 NOTE: an unentitled metallic can under " +
                          "WithinFamily is frozen on its current color (AllColors would let it escape to a " +
                          "base color). By design: the DLC gate is checked before the mode.");

            // A base can in the same unentitled session still walks its own family fine.
            int baseUnentitled = (int)nextInCycle.Invoke(null, new object[] { 11, count, true, within });
            Chk("P5 base-family-unaffected-by-pool", baseUnentitled == 0,
                $"pool empty, base seed 11 forward -> {baseUnentitled} (want 0)");

            SharedDLCManager.SharedDLC = (ushort)DLCType.MetallicPaints;
        }

        // ================= P6: the effective-settings log line =================

        private static void SppMerge_P6_EffectiveLogLine(
            Dictionary<string, ConfigEntryBase> entries, Type modeT, MethodInfo clearSynced,
            MethodInfo logEffective, FieldInfo logField)
        {
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P6 effective-settings log line");

            var src = logField.GetValue(null) as ManualLogSource;
            if (src == null)
            {
                Chk("P6 log-source", false, "SprayPaintPlusPlugin.Log is not a ManualLogSource");
                return;
            }

            // A known configuration so the columns are checkable rather than merely
            // present: one pair off on the client, one off on the server, one mode pair
            // where the server is stricter.
            NetworkManager.NetworkRole = NetworkRole.Server;
            clearSynced.Invoke(null, null);
            foreach (var name in new[] { "ClientColorPicking", "ServerColorPicking",
                                         "ClientUnlimitedSprayPaintUses", "ServerUnlimitedSprayPaintUses",
                                         "ServerGlowPaint", "ClientNetworkPainting",
                                         "ServerNetworkPainting", "ClientNetworkPaintPipes" })
                Set(entries, name, true);
            Set(entries, "ClientGlowPaint", false);          // client off, server on
            Set(entries, "ServerNetworkPaintPipes", false);  // client on, server off
            Set(entries, "ClientColorCycling", Enum.Parse(modeT, "AllColors"));
            Set(entries, "ServerColorCycling", Enum.Parse(modeT, "WithinFamily"));

            _sppCapturedInfo.Clear();
            _sppCapturing = true;
            EventHandler<LogEventArgs> handler = SppMerge_CaptureLog;
            src.LogEvent += handler;
            try
            {
                logEffective.Invoke(null, null);
            }
            finally
            {
                src.LogEvent -= handler;
                _sppCapturing = false;
            }

            Chk("P6 exactly-one-info-line", _sppCapturedInfo.Count == 1,
                $"Info lines emitted by LogEffectiveSettings={_sppCapturedInfo.Count} (want 1)");

            if (_sppCapturedInfo.Count == 0) return;
            string line = _sppCapturedInfo[0];
            _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | P6 captured: {line}");

            Chk("P6 authority-prefix", line.Contains("as authority, both halves local"),
                "line names the resolution mode it used");

            var wanted = new[]
            {
                "Color Cycling=AllColors/WithinFamily -> WithinFamily",
                "Glow Paint=off/on -> off",
                "Network Paint Pipes=on/off -> off",
                "Color Picking=on/on -> on",
                "Unlimited Spray Paint Uses=on/on -> on",
                "Network Painting=on/on -> on",
            };
            foreach (var w in wanted)
                Chk($"P6 column[{w.Split('=')[0]}]", line.Contains(w), $"expected substring: {w}");

            // Every paired function has to be in the line, or the support tool has a hole
            // exactly where somebody's bug report will land.
            var functions = new[]
            {
                "Color Cycling", "Color Picking", "Unlimited Spray Paint Uses", "Glow Paint",
                "Network Painting", "Network Paint Pipes",
                "Network Paint Cables", "Network Paint Chutes", "Network Paint Walls",
                "Network Paint Rails", "Network Paint Large Structures", "Network Paint Elevators",
                "Network Paint Ladders", "Network Paint Stairs", "Network Paint Stairwells",
            };
            var missing = functions.Where(f => !line.Contains(f + "=")).ToArray();
            Chk("P6 all-paired-functions-present", missing.Length == 0,
                missing.Length == 0 ? $"all {functions.Length} paired functions present"
                                    : "missing: " + string.Join(", ", missing));

            // The three unpaired settings are reported too, each marked with its scope.
            Chk("P6 unpaired-settings-present",
                line.Contains("Paint Single Item By Default=") && line.Contains("(client only)")
                && line.Contains("Invert Color Scroll Direction=")
                && line.Contains("Suppress Spray Paint Pollution=") && line.Contains("(server only)"),
                "the three unpaired settings appear with their scope markers");

            // No rich text: the console discards any line containing "<color=" on a
            // dedicated server, and this string is also what a player pastes into a
            // bug report.
            Chk("P6 plain-text-only", !line.Contains("<color=") && !line.Contains("</"),
                "no rich-text markup in the support line");
        }

        private static void SppMerge_CaptureLog(object sender, LogEventArgs e)
        {
            if (!_sppCapturing) return;
            if (e.Level != LogLevel.Info) return;
            _sppCapturedInfo.Add(e.Data?.ToString() ?? "");
        }

        // ================= helpers =============================================

        private static void Set(Dictionary<string, ConfigEntryBase> entries, string field, object value)
        {
            if (!entries.TryGetValue(field, out ConfigEntryBase e) || e == null)
                throw new InvalidOperationException($"config entry field '{field}' not resolved");
            e.BoxedValue = value;
        }

        private static string OnOff(bool v) => v ? "on" : "off";

        private static void Chk(string label, bool ok, string detail)
        {
            if (ok)
            {
                _sppMergePass++;
                _log?.LogInfo($"[ScenarioRunner] {SPP_MERGE_TAG} | {label} PASS | {detail}");
            }
            else
            {
                _sppMergeFail++;
                _log?.LogError($"[ScenarioRunner] {SPP_MERGE_TAG} | {label} FAIL | {detail}");
            }
        }
    }
}
