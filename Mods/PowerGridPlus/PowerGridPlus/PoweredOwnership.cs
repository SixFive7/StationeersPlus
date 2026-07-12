using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using VanillaLandingPadTankConnector = global::Objects.LandingPads.LandingPadTankConnector;
using VanillaLandingPadPump = global::Objects.Electrical.LandingPadPump;
using VanillaLandingPadTaxiThreshold = global::Objects.Electrical.LandingPadTaxiThreshold;

namespace PowerGridPlus
{
    /// <summary>
    ///     Consumer Powered ownership (the net-liveness model): the mod, not vanilla, decides every
    ///     plain device's Powered flag, from its network's LIVE / DEAD verdict (NetLiveness), never
    ///     from whether the device's own demand was met this tick. A device on a LIVE net is
    ///     powered; a device on a DEAD net is dark; a device's own draw, however spiky or random,
    ///     cannot flicker it. This kills the whole per-device depower class (the fabricator
    ///     print-start reboot) structurally: either the subnet can carry the load (stays LIVE,
    ///     everyone powered) or its segmenter sheds / overloads and the subnet goes dark AS A UNIT,
    ///     which is the mod's stated contract.
    ///
    ///     <para><b>How it owns the flag (B + D1 edition).</b> Vanilla ApplyState is retired, so no
    ///     vanilla tick path writes Powered at all; this sweep, at the write-back tail, is the only
    ///     tick-driven writer and asserts both edges per device from the verdict. A DEAD net
    ///     delivers nothing by construction (the write-back settles no energy there and the
    ///     accumulator drains skip it, so debts freeze exactly and are billed on revival). The only
    ///     other vanilla Powered writer left is the main-thread AssessPower event path, suppressed
    ///     under orthogonality by PoweredOwnershipPatches.</para>
    ///
    ///     <para><b>Expected state.</b> LIVE net AND structure complete AND (orthogonality ON, or
    ///     the device's OnOff switch is on) => Powered true; otherwise false. With "Decouple
    ///     Powered From On Off" enabled (the default), Powered means "my outlet is energized": a
    ///     switched-off device on a live net reads Power=1 (powered but off), which is the
    ///     physically-correct decoupling the mod adopts; the draw stays OnOff-gated so nothing is
    ///     consumed. With the toggle off, expected also requires OnOff (vanilla-compatible
    ///     semantics minus the per-device depower).</para>
    ///
    ///     <para><b>Classification is total.</b> The verdict map is computed FROM the same snapshot
    ///     this sweep iterates, so every swept net has a same-tick verdict by construction; the
    ///     old unclassified-streak machinery is gone. A verdict miss (impossible in practice) is a
    ///     pure fail-safe: no write.</para>
    ///
    ///     <para><b>Exemptions.</b> Segmenters keep the existing healthy-set policy
    ///     (PoweredPresentation). Classes that legitimately self-own Powered are never driven:
    ///     battery wall lights (the emergency-light feature depends on their per-device-tick
    ///     CheckPowerState re-assert), the gas / Stirling generator family (AllowSetPower=false in
    ///     vanilla; their atmospheric tick writes both edges from combustion state), landing pad
    ///     devices (AssessPower from their own OnPowerTick), doors forced always-powered, the
    ///     elevator family (shaft-network aggregate + per-physics-frame carriage sync), and, via
    ///     reflection, ANY class overriding the Powered getter below Thing (a SetPower write would
    ///     be invisible to its reads; vanilla Battery is the archetype). Emergency-light prefabs
    ///     from the config list are exempt by name.</para>
    ///
    ///     <para><b>Quarantine.</b> A device whose actual Powered keeps contradicting the sweep's
    ///     stable expectation for 10 consecutive tails (a third-party self-asserter fighting the
    ///     write) is quarantined: the mod stops writing it and restores full vanilla behavior for
    ///     it, one log line. The conformance counters double as the auditor the design demands:
    ///     any device found in a state the mod did not expect, past the one-tick marshal grace,
    ///     is counted and reported (aggregated, once per 600 ticks), and unclassified streaks
    ///     surface devices the mod failed to classify.</para>
    ///
    ///     <para>Threading: the sweep runs on the power worker; AssessPower suppression runs on
    ///     the main thread, so the shared type-exemption cache and quarantine set are concurrent
    ///     containers. Cleared at the load boundary (Core/LoadBoundary, on the worker).</para>
    /// </summary>
    internal static class PoweredOwnership
    {
        private const int QuarantineStreak = 10;           // contested tails before giving the device back to vanilla
        private const int WarnCooldownTicks = 600;         // one aggregated warning per ~5 minutes at 2 Hz
        private const int PruneEveryTicks = 600;

        private struct DevState
        {
            public byte LastExpected;        // 0 false, 1 true, 2 none (fresh / quarantine-reset)
            public byte PrevExpected;
            public byte MismatchStreak;
            public int LastTouchedTick;
        }

        private static readonly Dictionary<long, DevState> _state = new Dictionary<long, DevState>();
        private static readonly List<long> _pruneScratch = new List<long>();
        // Read from the main thread by the AssessPower prefix and (rarely) third-party
        // SetPowerFromThread callers, hence concurrent.
        private static readonly ConcurrentDictionary<long, byte> _quarantined = new ConcurrentDictionary<long, byte>();
        private static readonly ConcurrentDictionary<Type, bool> _exemptTypeCache = new ConcurrentDictionary<Type, bool>();

        // Exact counters since load; reflection surface for the rearch suite.
        internal static long MismatchDeviceTicks { get; private set; }
        internal static int MismatchDistinctDevices => _mismatchDevices.Count;
        internal static int QuarantinedCount => _quarantined.Count;
        internal static long LastMismatchRefId { get; private set; }
        internal static int WarningsEmitted { get; private set; }

        private static readonly HashSet<long> _mismatchDevices = new HashSet<long>();
        private static long _deviceTicksAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        /// <summary>
        ///     Gate: this tick's liveness verdict is published. The ownership layer itself is always
        ///     on (no setting); only verdict freshness can hold it back.
        /// </summary>
        internal static bool OwnershipActiveNow()
        {
            return NetLiveness.PublishedTick == ElectricityTickCounter.CurrentTick;
        }

        internal static bool IsQuarantined(long refId) => _quarantined.ContainsKey(refId);

        /// <summary>Class-level exemption (see the class doc), cached per concrete type.</summary>
        internal static bool IsExemptDevice(Device device)
        {
            var t = device.GetType();
            if (_exemptTypeCache.TryGetValue(t, out bool cached)) return cached;
            bool exempt = ComputeExempt(t);
            _exemptTypeCache[t] = exempt;
            return exempt;
        }

        private static bool ComputeExempt(Type t)
        {
            if (typeof(WallLightBattery).IsAssignableFrom(t)) return true;
            if (typeof(PowerGeneratorPipe).IsAssignableFrom(t)) return true;   // + GasFuelGenerator
            if (typeof(StirlingEngine).IsAssignableFrom(t)) return true;
            if (typeof(UnPoweredDoor).IsAssignableFrom(t)) return true;
            if (typeof(ElevatorShaft).IsAssignableFrom(t)) return true;        // + ElevatorLevel
            if (typeof(VanillaLandingPadTankConnector).IsAssignableFrom(t)) return true;
            if (typeof(VanillaLandingPadPump).IsAssignableFrom(t)) return true;
            if (typeof(VanillaLandingPadTaxiThreshold).IsAssignableFrom(t)) return true;
            try
            {
                // Any Powered-getter override below Thing is unownable: our SetPower writes the
                // interactable, but the class's reads come from somewhere else (vanilla Battery's
                // charge state, a modded equivalent). Leave those classes fully vanilla.
                var getter = t.GetProperty("Powered",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy)?.GetGetMethod();
                if (getter != null && getter.DeclaringType != typeof(Thing)) return true;
            }
            catch
            {
                return true;   // ambiguous / hidden property: fail safe, leave it vanilla
            }
            return false;
        }

        /// <summary>
        ///     ENFORCE tail: assert both Powered edges per device from this tick's net verdict,
        ///     audit last tick's expectation against the actual flag, and quarantine contested
        ///     devices. Runs on the power worker after every network's ApplyState.
        /// </summary>
        internal static void SweepEnforceTail(int currentTick, Core.GridSnapshot gridSnap)
        {
            if (NetLiveness.PublishedTick != currentTick) return;
            if (gridSnap == null) return;
            bool orthogonal = Settings.DecouplePoweredFromOnOff.Value;
            var emergencyPrefabs = Patches.EmergencyLightSupport.PrefabNames;

            for (int ni = 0; ni < gridSnap.Nets.Count; ni++)
            {
                try
                {
                    var nr = gridSnap.Nets[ni];
                    var net = nr.Network;
                    // Snapshot nets always receive a same-tick verdict (the allocator builds its
                    // model FROM this snapshot), so the old unclassified-streak machinery is gone
                    // by construction; the guard below is a pure fail-safe (no write on a miss).
                    if (!NetLiveness.TryGetVerdict(nr.Id, out byte verdict)) continue;
                    for (int i = 0; i < nr.Rows.Count; i++)
                    {
                        var row = nr.Rows[i];
                        var device = row.Device;
                        if (device == null) continue;
                        // Single-owner rule: a multi-net device is swept only by its primary
                        // network, mirroring the base AllowSetPower semantics.
                        if (device.PowerCableNetwork != net) continue;
                        if (!device.HasPowerState) continue;   // PoweredValue write would silently no-op
                        if (row.IsSegmenter) continue;         // the presentation roster owns segmenters
                        if (IsExemptDevice(device)) continue;
                        if (emergencyPrefabs != null && emergencyPrefabs.Contains(device.PrefabName)) continue;
                        long refId = device.ReferenceId;
                        if (_quarantined.ContainsKey(refId)) continue;

                        _state.TryGetValue(refId, out var st);

                        // OnOff comes from the boundary read (row.OnOff), so the expectation is
                        // computed from the same sample the solve billed under.
                        byte expected = (verdict == NetLiveness.Live
                                         && device.IsStructureCompleted
                                         && (orthogonal || row.OnOff)) ? (byte)1 : (byte)0;

                        bool actual = device.Powered;

                        // Conformance audit: judged against LAST tail's expectation, and only when
                        // that expectation was stable across two tails (the marshal grace: a fresh
                        // transition legitimately lands on the next main-thread frame).
                        if (st.LastExpected != 2 && st.LastExpected == st.PrevExpected
                            && actual != (st.LastExpected == 1))
                        {
                            st.MismatchStreak = (byte)Math.Min(byte.MaxValue, st.MismatchStreak + 1);
                            if (st.MismatchStreak >= 2)
                            {
                                MismatchDeviceTicks++;
                                _mismatchDevices.Add(refId);
                                LastMismatchRefId = refId;
                            }
                            if (st.MismatchStreak >= QuarantineStreak && _quarantined.TryAdd(refId, 1))
                            {
                                Plugin.Log?.LogWarning(
                                    "[PowerGridPlus] Powered ownership: device " + refId.ToString(CultureInfo.InvariantCulture)
                                    + " ('" + (device.DisplayName ?? device.PrefabName) + "', " + device.GetType().Name
                                    + ") kept contradicting the expected Powered state for "
                                    + QuarantineStreak.ToString(CultureInfo.InvariantCulture)
                                    + " consecutive ticks (another writer owns it); quarantined back to vanilla behavior.");
                                st.MismatchStreak = 0;
                                st.PrevExpected = st.LastExpected = 2;
                                st.LastTouchedTick = currentTick;
                                _state[refId] = st;
                                continue;
                            }
                        }
                        else
                        {
                            st.MismatchStreak = 0;
                        }

                        // Enforce both edges. SetPower no-ops on a matching state, so steady state
                        // costs nothing; the write marshals to the main thread like vanilla's own.
                        if (expected == 1 && !actual) device.SetPowerFromThread(net, hasPower: true).Forget();
                        else if (expected == 0 && actual) device.SetPowerFromThread(net, hasPower: false).Forget();

                        st.PrevExpected = st.LastExpected;
                        st.LastExpected = expected;
                        st.LastTouchedTick = currentTick;
                        _state[refId] = st;
                    }
                }
                catch (Exception ex)
                {
                    // One malformed net (a throwing third-party override) costs that net's sweep,
                    // never the tick.
                    Plugin.Log?.LogWarning(
                        "[PowerGridPlus] Powered ownership sweep failed on network "
                        + gridSnap.Nets[ni].Id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
                }
            }

            if (currentTick % PruneEveryTicks == 0) PruneStale(currentTick);
            EmitWarningIfDue(currentTick);
        }

        private static void PruneStale(int currentTick)
        {
            _pruneScratch.Clear();
            foreach (var kv in _state)
                if (currentTick - kv.Value.LastTouchedTick > PruneEveryTicks) _pruneScratch.Add(kv.Key);
            for (int i = 0; i < _pruneScratch.Count; i++)
            {
                _state.Remove(_pruneScratch[i]);
            }
        }

        private static void EmitWarningIfDue(int currentTick)
        {
            if (MismatchDeviceTicks == _deviceTicksAtLastWarn) return;
            if (currentTick - _lastWarnTick < WarnCooldownTicks) return;
            _lastWarnTick = currentTick;
            _deviceTicksAtLastWarn = MismatchDeviceTicks;
            WarningsEmitted++;
            Plugin.Log?.LogWarning(
                "[PowerGridPlus] Powered ownership conformance: a device read a Powered state the sweep did not expect past the marshal grace on "
                + MismatchDeviceTicks.ToString(CultureInfo.InvariantCulture)
                + " device-tick(s) across " + MismatchDistinctDevices.ToString(CultureInfo.InvariantCulture)
                + " device(s) since load (latest: device " + LastMismatchRefId.ToString(CultureInfo.InvariantCulture)
                + "). Persistent offenders are quarantined automatically; "
                + QuarantinedCount.ToString(CultureInfo.InvariantCulture) + " quarantined so far.");
        }

        /// <summary>World-load reset: drop the previous world's state, quarantine, and counters.</summary>
        internal static void Clear()
        {
            _state.Clear();
            _quarantined.Clear();
            _mismatchDevices.Clear();
            MismatchDeviceTicks = 0;
            LastMismatchRefId = 0L;
            WarningsEmitted = 0;
            _deviceTicksAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
            // The type-exemption cache is world-independent (pure type facts); keep it.
        }
    }
}
