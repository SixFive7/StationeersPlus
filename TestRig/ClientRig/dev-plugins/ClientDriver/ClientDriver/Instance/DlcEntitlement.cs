using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using DLC;

namespace ClientDriver
{
    /// <summary>
    ///     A per-process DLC entitlement override that can only ever TAKE ENTITLEMENT AWAY.
    ///
    ///     Why it exists: three documents in this repository record "a test needing one DLC owner
    ///     and one non-owner is out of reach here", because every rig instance shares the
    ///     developer's one Steam session. That is true of Steam and false of the game. Entitlement
    ///     lives in two plain static holders, both per process, and neither is persisted anywhere:
    ///
    ///     <list type="bullet">
    ///       <item><c>DLCManager._ownedDLC</c>, a private static set ONCE from Steam inside
    ///       <c>DLCManager.Initialize()</c> during boot and never re-read afterwards.</item>
    ///       <item><c>SharedDLCManager.SharedDLC</c>, a public static <c>ushort</c> holding the
    ///       session-wide union, fed by <c>AvailableDLCMessage</c> whose <c>Process</c> discards the
    ///       sender id and never validates the claim.</item>
    ///     </list>
    ///
    ///     So one Steam account can stage an owner and a non-owner: strip the non-owner's copy in
    ///     its own process before it enters a world. Details and every write site are on
    ///     <c>Research/GameSystems/DLCGating.md</c>. The dedicated server half already writes the
    ///     shared pool directly in <c>Dispatcher.SppDlcGateVerify.cs</c>; this is the same mechanism
    ///     with the direction nailed shut.
    ///
    ///     <b>REMOVAL ONLY, and that is enforced by construction rather than by documentation.</b>
    ///     The user asked for exactly this shape, for a stated reason: the risk worth designing
    ///     against is accidentally giving somebody easy access to a DLC they did not pay for. A
    ///     capability that can only subtract is self-documenting about its intent, and cannot be
    ///     repurposed. Five things make it so:
    ///
    ///     <list type="number">
    ///       <item>There is exactly ONE expression in this file that produces a value to write, and
    ///       it is <see cref="Clear"/>: <c>current &amp; ~bits</c>. No request field is ever used AS
    ///       a value; a request only ever names bits to CLEAR. An arbitrary mask arriving from a
    ///       caller can therefore only ever remove, including a mask naming a DLC the process never
    ///       had, which is a no-op.</item>
    ///       <item>Every write is verified by reading the value back and asserting that no bit
    ///       appeared. A gained bit is impossible from this code and would mean something else wrote
    ///       concurrently; the write is rolled back and the request refused, because "a bit
    ///       appeared" is the one outcome this file must never leave behind.</item>
    ///       <item><see cref="Restore"/> is the only write that is not a clear, and it writes a
    ///       BASELINE captured from this process's own live state before the first removal. It is
    ///       not assignable from a request, so a restore can hand back at most what the process
    ///       already had, and nothing at all if nothing was ever removed.</item>
    ///       <item>The route surface carries no name that could add: <c>GET /dlc</c>,
    ///       <c>POST /dlc/remove</c>, <c>POST /dlc/restore</c>. There is no <c>/dlc/grant</c> to
    ///       call by mistake and none to write by mistake.</item>
    ///       <item>A request carrying an add-shaped field is REFUSED rather than ignored, so a
    ///       caller who assumed the endpoint was symmetric is told, not silently obeyed.</item>
    ///     </list>
    ///
    ///     It is in memory, per process, and never persisted: nothing in either manager is
    ///     serialised, and neither is re-read from Steam after boot. It lives in a dev-plugin whose
    ///     <c>WorkshopHandle</c> is 0 and which never ships.
    /// </summary>
    internal static class DlcEntitlement
    {
        /// <summary>
        ///     THE ONLY EXPRESSION IN THIS FILE THAT PRODUCES A VALUE TO WRITE.
        ///
        ///     A bit set in <paramref name="bits"/> is cleared; every other bit is left exactly as
        ///     it was. There is no code path that ORs, assigns a caller's number, or otherwise
        ///     produces a value that is not a subset of <paramref name="current"/>, which is what
        ///     makes "this cannot grant entitlement" a property of the code rather than a claim
        ///     about it.
        /// </summary>
        private static int Clear(int current, int bits) => current & ~bits;

        private static bool _baselineCaptured;
        private static int _baselineOwned;
        private static int _baselineShared;
        private static long _baselineSession;
        private static int _removedOwned;
        private static int _removedShared;
        private static int _removeCalls;

        // ---- reading the two holders -----------------------------------------

        /// <summary>
        ///     <c>DLCManager._ownedDLC</c>. Private static, so writing it needs reflection; reading
        ///     it does not, because <c>GetOwnedDLC()</c> is public.
        /// </summary>
        private static FieldInfo OwnedField()
        {
            try
            {
                return typeof(DLCManager).GetField("_ownedDLC",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            }
            catch { return null; }
        }

        private static int CurrentOwned()
        {
            try { return (int)DLCManager.GetOwnedDLC(); } catch { return 0; }
        }

        private static int CurrentShared()
        {
            try { return SharedDLCManager.SharedDLC; } catch { return 0; }
        }

        /// <summary>
        ///     Whether <c>DLCManager.Initialize()</c> has certainly run. See the guard at the top of
        ///     <see cref="Remove"/> for why this particular flag is the exact answer.
        /// </summary>
        private static bool GameInitialized()
        {
            try { return Assets.Scripts.GameManager.IsInitialized; } catch { return false; }
        }

        /// <summary>
        ///     Captured once, from live state, before this process removes anything. It is the only
        ///     value <see cref="Restore"/> can write, which is what bounds a restore to "put back
        ///     what was here" rather than "hand out whatever was asked for".
        /// </summary>
        private static void CaptureBaseline()
        {
            if (_baselineCaptured) return;
            _baselineOwned = CurrentOwned();
            _baselineShared = CurrentShared();
            _baselineSession = Epoch.Session;
            _baselineCaptured = true;
        }

        // ---- the mask vocabulary ---------------------------------------------

        /// <summary>
        ///     Parses what to remove: one or more <c>DLCType</c> names, <c>all</c>, or a numeric
        ///     mask. Values are enumerated off the live enum rather than hardcoded, so a DLC added
        ///     in a future game version is nameable here the day it ships.
        ///
        ///     A numeric mask is safe to accept precisely because of <see cref="Clear"/>: whatever
        ///     number arrives, it can only name bits to take away.
        /// </summary>
        internal static bool TryParseMask(string spec, out int mask, out string error)
        {
            mask = 0;
            error = null;
            if (string.IsNullOrEmpty(spec)) { error = "missing 'dlc'"; return false; }

            var unknown = new List<string>();
            foreach (string raw in spec.Split(',', '|', '+'))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;

                if (string.Equals(part, "all", StringComparison.OrdinalIgnoreCase))
                {
                    try { mask |= (int)DLCManager.AllDLC; } catch { }
                    continue;
                }

                int numeric;
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric) ||
                    (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(part.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out numeric)))
                {
                    mask |= numeric;
                    continue;
                }

                bool matched = false;
                foreach (var known in KnownValues())
                {
                    if (!string.Equals(known.Key, part, StringComparison.OrdinalIgnoreCase)) continue;
                    mask |= known.Value;
                    matched = true;
                    break;
                }
                if (!matched) unknown.Add(part);
            }

            if (unknown.Count > 0)
            {
                error = "unknown DLC name(s): " + string.Join(", ", unknown.ToArray()) +
                        ". Known: " + string.Join(", ", KnownNames().ToArray()) +
                        ", plus 'all' and a numeric mask.";
                return false;
            }
            if (mask == 0) { error = "'dlc' named nothing to remove"; return false; }
            return true;
        }

        private static List<KeyValuePair<string, int>> KnownValues()
        {
            var result = new List<KeyValuePair<string, int>>();
            try
            {
                foreach (var value in Enum.GetValues(typeof(DLCType)))
                {
                    int numeric = (int)value;
                    if (numeric == 0) continue;   // None
                    result.Add(new KeyValuePair<string, int>(value.ToString(), numeric));
                }
            }
            catch { }
            return result;
        }

        private static List<string> KnownNames()
        {
            var names = new List<string>();
            foreach (var kv in KnownValues()) names.Add(kv.Key);
            return names;
        }

        private static string MaskNames(int mask)
        {
            var names = new List<string>();
            foreach (var kv in KnownValues()) if ((mask & kv.Value) != 0) names.Add(kv.Key);
            return names.Count == 0 ? "None" : string.Join(", ", names.ToArray());
        }

        /// <summary>
        ///     Writes a mask as both its numeric value and its names. The names are what a reader
        ///     checks; the number is what a caller compares.
        /// </summary>
        private static void MaskJson(string key, int mask, Json.Obj o)
        {
            o.Int(key + "Mask", mask);
            o.Str(key, MaskNames(mask));
        }

        // ---- removal ----------------------------------------------------------

        /// <summary>
        ///     Clears the named bits from one or both holders and reports exactly what moved.
        ///     Removing a DLC the process never had is a no-op and is reported as such, never as a
        ///     failure: "make sure this process does not own X" is the request, and it succeeding
        ///     because X was already absent is the request being satisfied.
        /// </summary>
        internal static string Remove(int mask, bool doOwned, bool doShared, out bool ok)
        {
            // Refused before the game has initialised, with no override, because a removal issued
            // then is SILENTLY UNDONE and looks exactly like one that worked: _ownedDLC is still 0
            // at that point, so the clear is a no-op that reports success, and DLCManager.Initialize
            // then fills it from Steam afterwards.
            //
            // GameManager.IsInitialized is an exact guard rather than a heuristic. Both statements
            // live in GameManager.Start(): DLCManager.Initialize() runs there, then several more
            // initialisers including an awaited WorldManager.Initialize(), and IsInitialized = true
            // is the second-to-last statement of the same method. So IsInitialized being true
            // strictly implies DLCManager.Initialize() has already run.
            if (!GameInitialized())
            {
                ok = false;
                return new Json.Obj()
                    .Bit("ok", false)
                    .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                    .Bit("gameInitialized", false)
                    .Str("error", "refusing to remove entitlement before the game has initialised. " +
                                  "DLCManager.Initialize() has not run yet, so DLCManager._ownedDLC is " +
                                  "still 0: the removal would be a no-op that reports success and would " +
                                  "then be overwritten from Steam. Wait for the menu first " +
                                  "(client-rig.ps1 -Wait -Stage menu, or POST /waitfor), then remove. " +
                                  "Nothing was changed.")
                    .Raw("epoch", Epoch.Json())
                    .Raw("state", DescribeState())
                    .ToString();
            }

            CaptureBaseline();
            _removeCalls++;

            int ownedBefore = CurrentOwned();
            int sharedBefore = CurrentShared();
            var problems = new List<string>();
            string ownedBlock = null;
            string sharedBlock = null;

            // Both blocks are built first and the response is assembled afterwards, because Json.Obj
            // is append-only: writing ok:true up front and correcting it later would leave a caller
            // reading the first 'ok' with a stale answer.

            // ---- owned ----
            if (doOwned)
            {
                var field = OwnedField();
                if (field == null)
                {
                    problems.Add("DLCManager._ownedDLC could not be resolved by reflection, so local " +
                                 "ownership was NOT changed. The field has been renamed in this game " +
                                 "version and this endpoint needs updating.");
                }
                else
                {
                    string writeError = WriteOwned(field, Clear(ownedBefore, mask));
                    int ownedAfter = CurrentOwned();

                    // The verification. A bit that is set now and was not before cannot come out of
                    // Clear(), so its presence means something else wrote between the two reads.
                    int gained = ownedAfter & ~ownedBefore;
                    if (gained != 0)
                    {
                        WriteOwned(field, ownedBefore);
                        ownedAfter = CurrentOwned();
                        problems.Add("refusing the owned-DLC write: reading it back showed bit(s) " +
                                     MaskNames(gained) + " that were not there before, which this " +
                                     "endpoint cannot produce. The previous value has been put back.");
                    }
                    else if (writeError != null)
                    {
                        problems.Add("writing DLCManager._ownedDLC failed: " + writeError);
                    }
                    else
                    {
                        _removedOwned |= ownedBefore & ~ownedAfter;
                    }

                    var block = new Json.Obj();
                    MaskJson("before", ownedBefore, block);
                    MaskJson("after", ownedAfter, block);
                    MaskJson("cleared", ownedBefore & ~ownedAfter, block);
                    MaskJson("alreadyAbsent", mask & ~ownedBefore, block);
                    ownedBlock = block.ToString();
                }
            }

            // ---- shared ----
            if (doShared)
            {
                string writeError = null;
                try { SharedDLCManager.SharedDLC = (ushort)(Clear(sharedBefore, mask) & 0xFFFF); }
                catch (Exception ex) { writeError = ex.Message; }

                int sharedAfter = CurrentShared();
                int gained = sharedAfter & ~sharedBefore;
                if (gained != 0)
                {
                    try { SharedDLCManager.SharedDLC = (ushort)(sharedBefore & 0xFFFF); } catch { }
                    sharedAfter = CurrentShared();
                    problems.Add("refusing the shared-pool write: reading it back showed bit(s) " +
                                 MaskNames(gained) + " that were not there before. The previous value " +
                                 "has been put back.");
                }
                else if (writeError != null)
                {
                    problems.Add("writing SharedDLCManager.SharedDLC failed: " + writeError);
                }
                else
                {
                    _removedShared |= sharedBefore & ~sharedAfter;
                }

                var block = new Json.Obj();
                MaskJson("before", sharedBefore, block);
                MaskJson("after", sharedAfter, block);
                MaskJson("cleared", sharedBefore & ~sharedAfter, block);
                MaskJson("alreadyAbsent", mask & ~sharedBefore, block);
                sharedBlock = block.ToString();
            }

            ok = problems.Count == 0;

            var o = new Json.Obj().Bit("ok", ok);
            o.Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name);
            MaskJson("requested", mask, o);
            o.Str("scope", doOwned && doShared ? "both" : (doOwned ? "owned" : "shared"));
            if (ownedBlock != null) o.Raw("owned", ownedBlock);
            if (sharedBlock != null) o.Raw("shared", sharedBlock);
            if (!ok) o.Str("error", string.Join(" | ", problems.ToArray()));
            o.Raw("epoch", Epoch.Json());
            o.Raw("state", DescribeState());
            AppendSequencing(o, doOwned, doShared);
            return o.ToString();
        }

        private static string WriteOwned(FieldInfo field, int value)
        {
            try
            {
                field.SetValue(null, Enum.ToObject(typeof(DLCType), value));
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---- restore ----------------------------------------------------------

        /// <summary>
        ///     Puts back the baseline captured before the first removal, and nothing else. When this
        ///     process has never removed anything there is no baseline and this is a no-op, which is
        ///     the correct answer: there is no state to return to and no value a caller could name
        ///     that this endpoint would honour.
        /// </summary>
        internal static string Restore(out bool ok)
        {
            ok = true;
            var o = new Json.Obj();

            if (!_baselineCaptured)
            {
                o.Bit("ok", true).Bit("restored", false)
                 .Str("note", "this process has not removed any entitlement, so there is no baseline to " +
                              "restore and nothing was changed.");
                o.Raw("epoch", Epoch.Json());
                o.Raw("state", DescribeState());
                return o.ToString();
            }

            int ownedBefore = CurrentOwned();
            int sharedBefore = CurrentShared();
            var problems = new List<string>();

            var field = OwnedField();
            if (field == null) problems.Add("DLCManager._ownedDLC could not be resolved; local ownership was not restored.");
            else
            {
                string err = WriteOwned(field, _baselineOwned);
                if (err != null) problems.Add("restoring DLCManager._ownedDLC failed: " + err);
            }

            try { SharedDLCManager.SharedDLC = (ushort)(_baselineShared & 0xFFFF); }
            catch (Exception ex) { problems.Add("restoring SharedDLCManager.SharedDLC failed: " + ex.Message); }

            if (problems.Count > 0) ok = false;
            _removedOwned = 0;
            _removedShared = 0;

            o.Bit("ok", ok).Bit("restored", true);
            MaskJson("ownedBefore", ownedBefore, o);
            MaskJson("ownedAfter", CurrentOwned(), o);
            MaskJson("sharedBefore", sharedBefore, o);
            MaskJson("sharedAfter", CurrentShared(), o);
            o.Int("baselineSession", _baselineSession);
            if (problems.Count > 0) o.Str("error", string.Join(" | ", problems.ToArray()));
            o.Str("note", "the baseline is what this process held before its first removal, captured " +
                          "from live state. Nothing a caller can name is writable here, so a restore " +
                          "never hands back more than the process already had.");
            o.Raw("epoch", Epoch.Json());
            o.Raw("state", DescribeState());
            return o.ToString();
        }

        // ---- reporting --------------------------------------------------------

        internal static string DescribeState()
        {
            var o = new Json.Obj();
            MaskJson("owned", CurrentOwned(), o);
            MaskJson("shared", CurrentShared(), o);
            o.Bit("overridden", _baselineCaptured);
            if (_baselineCaptured)
            {
                MaskJson("baselineOwned", _baselineOwned, o);
                MaskJson("baselineShared", _baselineShared, o);
                MaskJson("removedOwned", _removedOwned, o);
                MaskJson("removedShared", _removedShared, o);
                o.Int("baselineSession", _baselineSession);
                o.Int("removeCalls", _removeCalls);
            }
            o.Bit("ownedFieldReachable", OwnedField() != null);
            // False means DLCManager.Initialize() has not run, so 'owned' below is 0 for that
            // reason and not because anything was removed. POST /dlc/remove refuses in that state.
            o.Bit("gameInitialized", GameInitialized());
            return o.ToString();
        }

        internal static string Describe()
        {
            var o = new Json.Obj().Bit("ok", true);
            o.Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name);
            o.Raw("epoch", Epoch.Json());
            o.Raw("state", DescribeState());

            var known = new List<string>();
            foreach (var kv in KnownValues())
                known.Add(new Json.Obj().Str("name", kv.Key).Int("value", kv.Value).ToString());
            o.Raw("known", "[" + string.Join(",", known.ToArray()) + "]");

            o.Str("direction", "REMOVAL ONLY. POST /dlc/remove clears bits and cannot set one; there is " +
                               "no route that grants entitlement and no request field that names a value " +
                               "to write. POST /dlc/restore puts back the baseline this process held " +
                               "before its first removal, and nothing else.");
            AppendSequencing(o, true, true);
            return o.ToString();
        }

        /// <summary>
        ///     What a caller has to sequence for a removal to take effect. This rides the response
        ///     rather than living only in a document, because the ordering is the whole difference
        ///     between the override working and the override being silently undone, and the two look
        ///     identical from outside.
        /// </summary>
        private static void AppendSequencing(Json.Obj o, bool doOwned, bool doShared)
        {
            var steps = new List<string>
            {
                "Remove AFTER the instance reaches the menu and BEFORE it enters a world. That window " +
                "is the whole of the sequencing. DLCManager.Initialize() runs inside GameManager.Start() " +
                "and would overwrite an earlier removal from Steam; GameManager.IsInitialized is set at " +
                "the end of the same method, so waiting for it (client-rig.ps1 -Wait -Stage menu) is an " +
                "exact guarantee that Initialize() is done. POST /dlc/remove refuses before that.",

                "Remove BEFORE world entry, on every instance that must not have the DLC. " +
                "DLCManager._ownedDLC is read at world entry by both paths that fill the session pool.",

                "A joiner: remove scope=owned at the MENU, then POST /connect. " +
                "SharedDLCManager.ClientFinishedLoad reads DLCManager.GetOwnedDLC() at the very end " +
                "of the join and sends it to the server, so a joiner stripped at the menu truthfully " +
                "contributes nothing to the pool.",

                "A listen host: remove scope=owned at the MENU, then POST /host. " +
                "SharedDLCManager.HostFinishedLoad re-seeds the pool from DLCManager.GetOwnedDLC() at " +
                "the end of the LOAD path, so a host stripped after its world is up would have been " +
                "seeded already. (The new-world path never seeds the pool at all, so a created world " +
                "starts empty either way.)",

                "scope=shared on a JOINER is local and temporary: the server broadcasts the pool back " +
                "under delta bit 256 and SharedDLCManager.DeserializeDeltaState overwrites it. Strip " +
                "the pool on the instance whose /status.role is listenHost or dedicated.",

                "The pool only ever GROWS during a session: nothing subtracts on disconnect. A DLC " +
                "owner who joins and leaves leaves the bit set until the world is torn down, so run " +
                "the whole test with the non-owner stripped from the start rather than stripping " +
                "mid-session.",

                "SharedDLCManager.ClearAll() zeroes the pool on world teardown, so a scope=shared " +
                "removal does not survive leaving the world. A scope=owned removal does: nothing " +
                "re-reads Steam after DLCManager.Initialize().",

                "Verify from the game rather than from this endpoint: POST /console/exec " +
                "{\"command\":\"dlc shared\"} prints the pool, and the vanilla gates are the console " +
                "spawn path and the fabricator.",
            };
            o.StrArray("sequence", steps);
            if (doShared && !doOwned)
                o.Str("scopeWarning", "scope=shared alone leaves DLCManager._ownedDLC intact, so the " +
                                      "next world entry re-seeds or re-announces the entitlement you " +
                                      "just removed. Remove scope=owned as well, or instead.");
        }
    }
}
