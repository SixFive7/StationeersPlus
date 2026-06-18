using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Targeted fixes on the VANILLA <see cref="PowerTick"/>, which runs unmodified in Phases 1
    ///     and 3 of the atomic tick (single-architecture decision, POWER.md §0.1; the former
    ///     PowerGridTick subclass and its routing prefixes are deleted). Three patches:
    ///
    ///     <para>1. <b>Per-anchor traversal-record clear</b> (POWER.md §4.2 bug 2). Vanilla
    ///     CheckForRecursiveProviders clears _networkTraversalRecord once before the anchor loop, so
    ///     an anchor visited during an earlier anchor's walk gets pruned on its own first call and
    ///     its cycle is missed. The full-body prefix clears the record per anchor. This is the
    ///     belt-and-braces detector only; PowerGridPlus's own directed-SCC walk (Phase 1.5b) is the
    ///     primary cycle handler and normally dissolves every loop before this can fire.</para>
    ///
    ///     <para>2. <b>NaN/Infinity guard</b>. A device reporting NaN watts poisons the network sums
    ///     (vanilla has no guard). PowerGridPlus sanitizes at two levels: the GetGeneratedPower /
    ///     GetUsedPower postfixes on the device classes it patches clamp a non-finite return to 0 at the
    ///     source (<see cref="DeviceOutputSanitizer"/>, so the network never darkens and every reader
    ///     sees a clean value), and this CalculateState postfix is the backstop -- it zeroes a poisoned
    ///     sum (one dark tick) and scans the network to NAME the offending device, which catches unknown
    ///     / modded classes the source clamps do not cover. Each broken device is named once per session
    ///     in the in-game console and logged every time to the BepInEx file.</para>
    ///
    ///     <para>3. <b>Cable-burn rule</b> (POWER.md §5.7 + non-mutating caps decision §0.2). Vanilla
    ///     GetBreakableCables reads the per-instance serialized Cable.MaxVoltage and burns on ANY
    ///     overflow. The prefix replaces it with a DETERMINISTIC rule (no randomness, no settings):
    ///     caps come from <see cref="CableMax"/> (runtime, per-tier, never written to the save), and a
    ///     cable burns when the 20-tick running average of the direct generator power flowing on a
    ///     network exceeds the weakest cable's cap (<see cref="CableBurnWindow"/>; 20 ticks = 10 s of
    ///     grace). Overflow caused by transformer / battery / APC contributions does NOT burn the
    ///     cable; the allocator trips those suppliers into OVERLOAD instead (PowerAllocator, Phase 2).
    ///     The burn victim is the cable at the output of the generator that produced the most over the
    ///     window. Entries added by CheckForRecursiveProviders remain in BreakableCables untouched, so
    ///     the vanilla recursive backstop still breaks its cable in ApplyState.</para>
    /// </summary>
    [HarmonyPatch(typeof(PowerTick))]
    public static class PowerTickPatches
    {
        private const float Eps = 0.01f;

        private static readonly AccessTools.FieldRef<PowerTick, List<long>> TraversalRecordRef =
            AccessTools.FieldRefAccess<PowerTick, List<long>>("_networkTraversalRecord");
        private static readonly AccessTools.FieldRef<PowerTick, float> ActualRef =
            AccessTools.FieldRefAccess<PowerTick, float>("_actual");
        private static readonly AccessTools.FieldRef<PowerTick, bool> IsPowerMetRef =
            AccessTools.FieldRefAccess<PowerTick, bool>("_isPowerMet");
        private static readonly AccessTools.FieldRef<PowerTick, float> PowerRatioRef =
            AccessTools.FieldRefAccess<PowerTick, float>("_powerRatio");

        // Reusable per-tick generator-production map for the §5.7 check. GetBreakableCables runs only
        // in Phase 3, one network at a time on the worker thread, so a shared instance is safe.
        private static readonly Dictionary<long, float> _producersThisTick = new Dictionary<long, float>();

        // ------------------------------------------------------------------
        // 1. CheckForRecursiveProviders: clear the visited record per anchor.
        // ------------------------------------------------------------------
        [HarmonyPrefix, HarmonyPatch("CheckForRecursiveProviders")]
        public static bool CheckForRecursiveProviders_Prefix(PowerTick __instance)
        {
            ref var record = ref TraversalRecordRef(__instance);
            if (record == null) return true;   // unexpected shape: fall back to vanilla

            var inputOutputDevices = __instance.InputOutputDevices;
            if (inputOutputDevices == null) return false;
            foreach (var provider in inputOutputDevices)
            {
                if (provider?.Device == null) continue;
                record.Clear();   // vanilla clears ONCE outside the loop; that is the bug
                if (!provider.Device.IsProviderToDevice(provider.Device, ref record)) continue;

                var providerNet = provider.Device.PowerCableNetwork;
                if (providerNet != null && providerNet.FuseList.Count > 0)
                    __instance.BreakableFuses.Add(providerNet.FuseList[0]);
                else
                    __instance.BreakableCables.Add(provider.Device.PowerCable);
                break;   // vanilla single-anchor bail kept: this is belt-and-braces, not the primary detector
            }
            return false;
        }

        // ------------------------------------------------------------------
        // 2. NaN/Infinity guard on the freshly-computed sums.
        // ------------------------------------------------------------------
        [HarmonyPostfix, HarmonyPatch(nameof(PowerTick.CalculateState))]
        public static void CalculateState_NanGuard(PowerTick __instance)
        {
            bool poisoned = false;
            if (float.IsNaN(__instance.Required) || float.IsInfinity(__instance.Required))
            {
                __instance.Required = 0f;
                poisoned = true;
            }
            if (float.IsNaN(__instance.Potential) || float.IsInfinity(__instance.Potential))
            {
                __instance.Potential = 0f;
                poisoned = true;
            }
            if (!poisoned) return;

            // The sums were zeroed (one dark tick) to contain the poison. A device PowerGridPlus patches
            // would have been clamped at its source postfix (DeviceOutputSanitizer), so a poisoned sum
            // means an UNKNOWN / modded class reported the non-finite value -- exactly what a player needs
            // to be told. Scan the network to name the culprit(s); the sanitizer dedups the in-game
            // console per device, so this is not spammy even when it fires every tick.
            var net = __instance.CableNetwork;
            var devices = __instance.Devices;
            if (net == null || devices == null) return;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null) continue;
                float used = device.GetUsedPower(net);
                if (float.IsNaN(used) || float.IsInfinity(used))
                    DeviceOutputSanitizer.Report(device, false, used);
                float gen = device.GetGeneratedPower(net);
                if (float.IsNaN(gen) || float.IsInfinity(gen))
                    DeviceOutputSanitizer.Report(device, true, gen);
            }
        }

        // ------------------------------------------------------------------
        // 2b. Power-met boundary relaxation.
        // ------------------------------------------------------------------
        // Vanilla CacheState sets _isPowerMet = (Potential - Required) > 0f (STRICT), so a network
        // whose supply exactly equals its demand reads as NOT powered and its rigid loads go dark. The
        // allocator reports each transformer's EXACT throughput (no headroom), so a fully served network
        // lands at Potential == Required and would be darkened by the strict test. This postfix relaxes
        // the test to >=: meeting demand counts as powered. It only nudges the exact-balance boundary
        // (Required positive, supply within Eps of covering it, and vanilla had set not-met); a genuinely
        // short network (Potential < Required - Eps) is untouched. BreakSingleFuse / BreakSingleCable call
        // CacheState again after lowering Required to a cable / fuse cap; this postfix re-runs against that
        // lowered Required, which is still correct (the network is still meeting the reduced figure).
        [HarmonyPostfix, HarmonyPatch("CacheState")]
        public static void CacheState_PowerMetBoundary(PowerTick __instance)
        {
            float required = __instance.Required;
            if (required <= 0f) return;                 // nothing demanded: leave vanilla _powerRatio = 1
            if (IsPowerMetRef(__instance)) return;      // already met under the strict test
            if (__instance.Potential >= required - Eps)
            {
                IsPowerMetRef(__instance) = true;
                PowerRatioRef(__instance) = 1f;
            }
        }

        // ------------------------------------------------------------------
        // 3. GetBreakableCables: deterministic generator-overflow burn against
        //    the runtime per-tier caps, via a 20-tick running average.
        // ------------------------------------------------------------------
        [HarmonyPrefix, HarmonyPatch("GetBreakableCables")]
        public static bool GetBreakableCables_Prefix(PowerTick __instance)
        {
            var net = __instance.CableNetwork;
            if (net == null || __instance.Cables.Count == 0) return false;

            // A burn is already in flight on this network (a tier burn queued this tick, or a prior
            // §5.7 burn whose split has not landed). Skip until the topology re-partitions; the window
            // was reset on the burn and re-accumulates on the post-split network.
            if (SplitPendingRegistry.IsPending(net.ReferenceId)) return false;

            float cap = CableMax.WeakestCapOnNetwork(net);
            if (cap >= float.MaxValue) return false;   // unlimited tier (super-heavy default): never burns

            // Sum this tick's direct generator supply (everything generating that is not an
            // ElectricalInputOutput; WirelessPower derives from EIO so PT/PR are excluded). Batteries,
            // transformers, APCs and umbilicals are segmenters -- their overflow trips OVERLOAD in the
            // allocator (Phase 2), not a cable burn. Also capture each generator's production so the
            // burn victim can be ranked by 20-tick output.
            float generatorSupply = 0f;
            _producersThisTick.Clear();
            var devices = __instance.Devices;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null || device is ElectricalInputOutput) continue;
                float generated = device.GetGeneratedPower(net);
                if (generated > 0f)
                {
                    generatorSupply += generated;
                    _producersThisTick[device.ReferenceId] = generated;
                }
            }

            // Generator power actually flowing on the cable this tick = min(generator supply, real
            // throughput): an idle network (no draw) or a transformer-fed overflow does not count.
            // min(gen, actual) > cap reproduces the old two-gate rule exactly; here it feeds the 20-tick
            // running average instead of a one-tick probability roll.
            float actual = ActualRef(__instance);
            float flow = generatorSupply < actual ? generatorSupply : actual;
            CableBurnWindow.Observe(net.ReferenceId, _producersThisTick, flow);

            // Deterministic burn: the 20-tick running average of generator flow exceeds the cap. A full
            // window is required first (a 10 s settling period, re-armed after each burn).
            if (!CableBurnWindow.IsFull(net.ReferenceId)) return false;
            float avg = CableBurnWindow.AverageFlow(net.ReferenceId);
            if (avg <= cap) return false;

            // Burn the cable at the output of the generator that produced the most over the window --
            // where the most energy enters the grid, and fully deterministic. Fall back to the weakest
            // cable if that producer's output cable cannot be resolved.
            Cable victim = ResolveProducerOutputCable(CableBurnWindow.TopProducer(net.ReferenceId), __instance, net)
                           ?? WeakestCable(__instance);
            if (victim != null)
            {
                BurnReasonRegistry.RegisterPending(victim,
                    $"Overloaded -- sustained generator supply (~{avg:0} W over 10 s) exceeded this cable's rating ({cap:0} W)");
                __instance.BreakableCables.Add(victim);
                // Vanilla ApplyState.Pick() burns one cable on THIS network's BreakableCables this tick,
                // splitting it. Mark it pending (Option C: the allocator defers durable lockouts until
                // the split lands) and reset the window so a single sustained overload cannot burn a
                // second cable before the (now smaller) network re-accumulates a fresh 20-tick window.
                int count;
                lock (net.CableList) count = net.CableList.Count;
                SplitPendingRegistry.MarkBurned(net.ReferenceId, count);
                CableBurnWindow.Reset(net.ReferenceId);
            }
            return false;
        }

        // The cable at the top-producing generator's output (its PowerCable on this network), found by
        // scanning the network's own device list so no namespace-specific Referencable lookup is needed.
        private static Cable ResolveProducerOutputCable(long producerRefId, PowerTick pt, CableNetwork net)
        {
            if (producerRefId == 0L) return null;
            var devices = pt.Devices;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null || device.ReferenceId != producerRefId) continue;
                if (device.PowerCable != null && device.PowerCable.CableNetwork == net)
                    return device.PowerCable;
                if (device.PowerCables != null)
                    foreach (var c in device.PowerCables)
                        if (c != null && c.CableNetwork == net) return c;
                return null;
            }
            return null;
        }

        private static Cable WeakestCable(PowerTick pt)
        {
            Cable victim = null;
            float victimCap = float.MaxValue;
            for (int i = 0; i < pt.Cables.Count; i++)
            {
                var cable = pt.Cables[i];
                if (cable == null) continue;
                float cableCap = CableMax.For(cable);
                if (cableCap < victimCap)
                {
                    victimCap = cableCap;
                    victim = cable;
                }
            }
            return victim;
        }
    }
}
