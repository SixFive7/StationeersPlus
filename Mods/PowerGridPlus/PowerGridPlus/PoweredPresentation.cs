using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Powered presentation policy for routed segmenters (Stage 3). Vanilla
    ///     <c>PowerTick.ApplyState</c> derives a device's Powered flag from what it consumed this
    ///     tick, so a HEALTHY segmenter that idles at zero throughput (a 50 kW charger transformer
    ///     whose batteries are full, an idle wireless dish pair) bills 0 on its input network and
    ///     vanilla flips it to Powered=False. Players read that as "device broken" even though the
    ///     allocator is routing it normally: the exact diagnostic trap the Gate 2/4 regression
    ///     surfaced. Policy: a routed segmenter the allocator considers healthy presents
    ///     Powered=True even when idle; a faulted / locked-out / dead-input segmenter keeps vanilla
    ///     behavior (bills 0, vanilla un-powers it, the hover explains why).
    ///
    ///     <para>Mechanics, two halves around vanilla's own writer:</para>
    ///     <list type="bullet">
    ///       <item>Block the False edge: vanilla ApplyState's only un-power path is gated on
    ///       <c>Device.AllowSetPower(net)</c> (PowerTick.ApplyState, the sole caller in the game).
    ///       The <see cref="Patches.PoweredPresentationPatches"/> postfixes return false for a
    ///       device in the healthy set, so ApplyState can never un-power a healthy segmenter and
    ///       no False/True double transition (with its OnServer.Interact network churn) ever
    ///       happens. Unhealthy segmenters pass through untouched: vanilla un-powers them exactly
    ///       as it always did.</item>
    ///       <item>Assert the True edge: vanilla only powers a device that consumed this tick, so
    ///       an idle healthy segmenter would stay False forever. <see cref="ReconcileEnforceTail"/>
    ///       runs at the ENFORCE tail (AtomicElectricityTickPatch, after every network's
    ///       ApplyState) and calls the vanilla <c>Device.SetPowerFromThread(net, true)</c> (which
    ///       self-marshals to the main thread, same as ApplyState itself uses) for each healthy
    ///       segmenter still reading Powered=False. SetPower no-ops when the state already
    ///       matches, so this fires exactly once per transition: zero steady-state churn.</item>
    ///     </list>
    ///
    ///     <para>Healthy (published by PowerAllocator at the end of ALLOCATE): enrolled in this
    ///     tick's roster, carrying no fault (not cycle-faulted / shed-locked / overload-locked /
    ///     shed / overloaded this tick; segmenters are never VVF candidates, that registry only
    ///     holds producers), and either conducting flow (TotalThrough &gt; 0) or idle on an input
    ///     network that has effective supply and no unmet rigid demand. An idle segmenter on a
    ///     DARK input network (night-time solar feed) is deliberately NOT healthy: vanilla
    ///     un-powers it, matching the dead-input hover cue.</para>
    ///
    ///     <para>Multiplayer: Powered is networked state and this policy is server-authoritative
    ///     by construction. ApplyState (and therefore AllowSetPower) and the ENFORCE tail only run
    ///     inside the host's atomic tick (GameManager.RunSimulation gate); SetPower routes through
    ///     OnServer.Interact, so clients receive the flag via the normal interactable replication.
    ///     On a client peer the published set stays empty and the postfixes no-op.</para>
    ///
    ///     <para>Threading: the allocator worker publishes both snapshots by a single reference
    ///     swap (volatile fields, immutable after publish, like the share caches). Readers are the
    ///     AllowSetPower postfixes and the ENFORCE tail, which run later in the same tick on the
    ///     same worker; volatile keeps any cross-thread reader coherent too.</para>
    /// </summary>
    internal static class PoweredPresentation
    {
        /// <summary>
        ///     One enrolled routed segmenter, as published by ALLOCATE for the ENFORCE tail. The
        ///     device references are captured during GATHER and consumed within the same tick.
        /// </summary>
        internal sealed class EnrolledSeg
        {
            public long RefId;                       // anchor ReferenceId (transformer / transmitter / APC)
            public ElectricalInputOutput Anchor;     // the enumerated bridge device
            public ElectricalInputOutput Partner;    // linked receiver half for a wireless pair, else null
            public CableNetwork InNet;
            public CableNetwork OutNet;
            public bool Healthy;                     // presentation policy verdict for this tick
            public float TotalThrough;               // rigid + soft output-side delivery (ledger settle standing value)
            public float TotalPull;                  // rigid + soft input-side draw (the one-tick bill; high-water threshold basis)
            public bool SettleLedger;                // false for the APC (its ledger is load-bearing, see PowerAllocator publish tail)
        }

        private static volatile HashSet<long> _healthy = new HashSet<long>();
        private static volatile List<EnrolledSeg> _roster = new List<EnrolledSeg>();

        /// <summary>Swap in this tick's snapshots (end of ALLOCATE, allocator worker).</summary>
        internal static void Publish(HashSet<long> healthy, List<EnrolledSeg> roster)
        {
            _healthy = healthy;
            _roster = roster;
        }

        /// <summary>
        ///     True when the device was published healthy by this tick's ALLOCATE. Between world
        ///     load and the first atomic tick the set is empty, so everything reads unhealthy and
        ///     vanilla behavior applies (the safe default).
        /// </summary>
        internal static bool IsHealthy(long refId) => _healthy.Contains(refId);

        /// <summary>This tick's enrolled-segmenter roster (read by the ENFORCE tail).</summary>
        internal static List<EnrolledSeg> Roster => _roster;

        /// <summary>World-load reset: drop the previous world's snapshots and device references.</summary>
        internal static void Clear()
        {
            _healthy = new HashSet<long>();
            _roster = new List<EnrolledSeg>();
        }

        /// <summary>
        ///     ENFORCE tail: re-assert Powered=True on every healthy segmenter vanilla left dark.
        ///     Runs on the power worker after all ApplyState passes; SetPowerFromThread marshals
        ///     the actual interactable write to the main thread, exactly as vanilla ApplyState
        ///     does. Only fires on an actual False -&gt; True transition (the Powered check here
        ///     plus SetPower's own no-op on a matching state), so a steadily healthy segmenter
        ///     causes no per-tick traffic.
        /// </summary>
        internal static void ReconcileEnforceTail()
        {
            var roster = _roster;
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                if (!e.Healthy) continue;
                // The CableNetwork argument is decorative for these classes (Device.SetPower
                // ignores it; none of the four segmenter classes override SetPower), so pass the
                // terminal the device draws from / delivers to for readability.
                var anchor = e.Anchor;
                if (anchor != null && !anchor.Powered)
                    anchor.SetPowerFromThread(e.InNet, true).Forget();
                var partner = e.Partner;
                if (partner != null && !partner.Powered)
                    partner.SetPowerFromThread(e.OutNet, true).Forget();
            }
        }
    }
}
