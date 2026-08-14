using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TestRig
{
    /// <summary>
    ///     Enter and exit counters on every link of the game's per-frame input chain.
    ///
    ///     This exists because a previous session could not tell the difference between "the game
    ///     never ran the consumer" and "the game ran it and ignored the value", guessed, and guessed
    ///     wrong. A prefix and a postfix on each link answers it directly and cheaply:
    ///
    ///       <list type="bullet">
    ///         <item>a link whose ENTER count stops advancing is not being reached;</item>
    ///         <item>a link whose ENTER count outruns its EXIT count is throwing;</item>
    ///         <item>a link that keeps pace with the one above it is fine.</item>
    ///       </list>
    ///
    ///     That is what turned the cursor-gate diagnosis from a hypothesis into a measurement. At
    ///     the main menu the chain read balanced all the way down to
    ///     <c>InventoryManager.ManagerUpdate</c> (1685 enters, 1685 exits) and then stopped dead,
    ///     with <c>CheckDisplaySlotInput</c> and <c>NormalMode</c> absent entirely. Balanced-then-
    ///     absent is the shape of an early return, not of an exception, which ruled out the
    ///     competing explanation that a throwing Harmony patch was aborting the update loop.
    ///
    ///     One dictionary lookup per link per frame. Six links, so the cost is noise beside the lock
    ///     this plugin already takes on every <c>Input.GetKey</c> call.
    ///
    ///     Patched manually rather than by attribute because one prefix/postfix pair serves every
    ///     link and tells them apart through <c>__originalMethod</c>.
    /// </summary>
    internal static class ChainProbe
    {
        private sealed class Counter
        {
            public long Enter;
            public long Exit;
            public int LastEnterFrame = -1;
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<string, Counter> _counts = new Dictionary<string, Counter>();
        private static readonly List<string> _installed = new List<string>();
        internal static string LastError;

        // (type name candidates, method name). Candidates inside a cell are in preference order,
        // separated by '|', so a namespace move in a game update degrades to "this link is not
        // reported" rather than to a failed plugin load.
        private static readonly string[][] Targets =
        {
            new[] { "Assets.Scripts.GameManager|GameManager",     "Update" },
            new[] { "KeyManager|Assets.Scripts.KeyManager",       "ManagerUpdate" },
            new[] { "KeyMap|Assets.Scripts.KeyMap",               "PollInputs" },
            new[] { "Assets.Scripts.Inventory.InventoryManager",  "ManagerUpdate" },
            new[] { "Assets.Scripts.Inventory.InventoryManager",  "CheckDisplaySlotInput" },
            new[] { "Assets.Scripts.Inventory.InventoryManager",  "NormalMode" },
        };

        internal static void Install(Harmony harmony)
        {
            var pre = new HarmonyMethod(AccessTools.Method(typeof(ChainProbe), nameof(ProbeEnter)));
            var post = new HarmonyMethod(AccessTools.Method(typeof(ChainProbe), nameof(ProbeExit)));

            foreach (var spec in Targets)
            {
                try
                {
                    Type type = null;
                    foreach (var candidate in spec[0].Split('|'))
                    {
                        type = AccessTools.TypeByName(candidate);
                        if (type != null) break;
                    }
                    if (type == null) continue;

                    var method = AccessTools.Method(type, spec[1]);
                    if (method == null || method.IsAbstract) continue;

                    harmony.Patch(method, pre, post);
                    lock (_gate) _installed.Add(Key(method));
                }
                catch (Exception ex)
                {
                    LastError = spec[0] + "." + spec[1] + ": " + ex.Message;
                }
            }
        }

        private static string Key(MethodBase m)
        {
            if (m == null) return "?";
            return (m.DeclaringType == null ? "?" : m.DeclaringType.Name) + "." + m.Name;
        }

        internal static void ProbeEnter(MethodBase __originalMethod)
        {
            string k = Key(__originalMethod);
            lock (_gate)
            {
                Counter c;
                if (!_counts.TryGetValue(k, out c)) { c = new Counter(); _counts[k] = c; }
                c.Enter++;
                c.LastEnterFrame = Time.frameCount;
            }
        }

        internal static void ProbeExit(MethodBase __originalMethod)
        {
            string k = Key(__originalMethod);
            lock (_gate)
            {
                Counter c;
                if (!_counts.TryGetValue(k, out c)) { c = new Counter(); _counts[k] = c; }
                c.Exit++;
            }
        }

        /// <summary>Enter count for one link, for before/after deltas around an input window.</summary>
        internal static long Enters(string key)
        {
            lock (_gate)
            {
                Counter c;
                return _counts.TryGetValue(key, out c) ? c.Enter : 0;
            }
        }

        internal static string DescribeJson()
        {
            var o = new Json.Obj();
            lock (_gate)
            {
                foreach (var kv in _counts)
                {
                    o.Raw(kv.Key, new Json.Obj()
                        .Int("enter", kv.Value.Enter)
                        .Int("exit", kv.Value.Exit)
                        .Int("unbalanced", kv.Value.Enter - kv.Value.Exit)
                        .Int("lastEnterFrame", kv.Value.LastEnterFrame)
                        .ToString());
                }
                o.StrArray("installed", _installed.ToArray());
            }
            o.Str("lastError", LastError);
            return o.ToString();
        }

        // Link names as they appear in the report, so callers do not have to guess the spelling.
        internal const string GameManagerUpdate = "GameManager.Update";
        internal const string KeyManagerUpdate = "KeyManager.ManagerUpdate";
        internal const string KeyMapPoll = "KeyMap.PollInputs";
        internal const string InventoryUpdate = "InventoryManager.ManagerUpdate";
        internal const string CheckDisplaySlotInput = "InventoryManager.CheckDisplaySlotInput";
        internal const string NormalMode = "InventoryManager.NormalMode";
    }
}
