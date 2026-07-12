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
    ///     <para>Mechanics (B + D1 edition): vanilla ApplyState is retired, so nothing else writes
    ///     a segmenter's Powered flag any more. <see cref="ReconcileEnforceTail"/> owns BOTH edges
    ///     from the health verdict: healthy asserts true, unhealthy asserts false, via the vanilla
    ///     self-marshaling <c>Device.SetPowerFromThread</c>. Edges fire only on an actual
    ///     transition, so steady state causes zero per-tick traffic.</para>
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
    ///     write-back tail, which runs later in the same tick on the same worker; volatile keeps
    ///     any cross-thread reader coherent too.</para>
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
            public float TotalPull;                  // rigid + soft input-side draw (the one-tick bill)
            public bool SettleLedger;                // false for the APC (its ledger is load-bearing, see PowerAllocator publish tail)
        }

        private static volatile List<EnrolledSeg> _roster = new List<EnrolledSeg>();

        /// <summary>Swap in this tick's roster (end of ALLOCATE, allocator worker). Between world
        /// load and the first atomic tick the roster is empty, so nothing is asserted (the safe
        /// default).</summary>
        internal static void Publish(List<EnrolledSeg> roster)
        {
            _roster = roster;
        }

        /// <summary>This tick's enrolled-segmenter roster (read by the write-back tail).</summary>
        internal static List<EnrolledSeg> Roster => _roster;

        /// <summary>World-load reset: drop the previous world's roster and device references.</summary>
        internal static void Clear()
        {
            _roster = new List<EnrolledSeg>();
        }

        /// <summary>
        ///     Write-back tail: assert BOTH Powered edges on every rostered segmenter from its
        ///     health verdict. With vanilla ApplyState retired (POWER.md §0 decision 24 stage 3)
        ///     nothing else writes a segmenter's Powered any more, so this owns the false edge too:
        ///     healthy presents powered, unhealthy (dark input, shed, overloaded, cycle-locked)
        ///     presents dark, matching the §10.6 presentation policy exactly. SetPowerFromThread
        ///     marshals the interactable write to the main thread as vanilla did; edges only fire
        ///     on an actual transition, so steady state causes no per-tick traffic.
        /// </summary>
        internal static void ReconcileEnforceTail()
        {
            var roster = _roster;
            for (int i = 0; i < roster.Count; i++)
            {
                var e = roster[i];
                // The CableNetwork argument is decorative for these classes (Device.SetPower
                // ignores it; none of the four segmenter classes override SetPower), so pass the
                // terminal the device draws from / delivers to for readability.
                var anchor = e.Anchor;
                if (anchor != null && anchor.Powered != e.Healthy)
                    anchor.SetPowerFromThread(e.InNet, e.Healthy).Forget();
                var partner = e.Partner;
                if (partner != null && partner.Powered != e.Healthy)
                    partner.SetPowerFromThread(e.OutNet, e.Healthy).Forget();
            }
        }
    }
}
