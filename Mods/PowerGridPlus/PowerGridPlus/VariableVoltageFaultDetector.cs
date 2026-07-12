using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;   // Device base lives here (Device : SmallGrid)

namespace PowerGridPlus
{
    /// <summary>
    ///     PROTECT (producer-isolation) walk (POWER.md §8.5, strict-literal). A power producer may only
    ///     connect to a transformer or to other producers; a producer that shares a cable network with
    ///     ANY other device (a consumer, a battery, an APC, a dish, an umbilical, anything that is
    ///     neither a producer nor a Transformer) enters VARIABLE_VOLTAGE_FAULT and stops generating
    ///     (ProducerFaultEnforcementPatches). A Transformer on the net is allowed but exempts nothing:
    ///     the foreign devices themselves are the violation (full-strict, user decision 2026-07-12).
    ///
    ///     <para>The fault is a NETWORK property handled with the same network-level commit as the
    ///     elastic-overload retry (§8.4.1): <c>VariableVoltageFaultRegistry</c> stays per-device (each
    ///     producer flashes / hovers / snapshots independently), but the commit is network-level with a
    ///     RETRY before any reset.</para>
    ///
    ///     <list type="bullet">
    ///       <item>The cohort is the ACTIVE producers on the net (<see cref="ProducerClassifier.IsActiveProducer"/>):
    ///       a buttoned producer that is on, a connector whose docked generator is delivering, a
    ///       buttonless producer always. An off / not-delivering producer is inert and never faults.</item>
    ///       <item>A net is a commit candidate when an active producer NEWLY violates this tick, or a
    ///       toggle requested a retry (<see cref="RequestRetry"/>, raised by OffAsResetSweep when it
    ///       clears a producer's lock). A stable all-locked violating net is NOT a candidate, so its
    ///       synced timer counts down instead of being re-stamped (no frozen countdown).</item>
    ///       <item>RECOVER: if the candidate net no longer violates (the foreign devices are gone, or
    ///       no active producer is left) the whole producer cohort's locks are CLEARED.</item>
    ///       <item>RESET: if it still violates, every active producer is stamped to ONE shared fresh
    ///       expiry (<c>NoteVariableVoltageFault</c> re-stamps currentTick + LockoutDurationTicks, so
    ///       the cohort is phase-synced and a buttonless producer re-arms together with the buttoned
    ///       one that retried it).</item>
    ///     </list>
    ///
    ///     <para>Like overload, there is no free auto-recovery mid-lockout: a fix with no interaction
    ///     clears at the 60 s expiry; toggling any buttoned producer on the net is the instant retry.</para>
    ///
    ///     <para>Deviation (POWER_DEVIATIONS.md D6): non-flashable producers (solar / wind / RTG) enter
    ///     the registry and have their output zeroed, same as flashable producers, rather than burning
    ///     a cable. The flash vs hover-only distinction is purely visual. Unclassified producer-like
    ///     devices outside the known class list still fall back to the destructive cable burn.</para>
    /// </summary>
    internal static class VariableVoltageFaultDetector
    {
        // One-shot diagnostic: on the first tick that produces any fault, log a tally of the producer
        // classes faulted and the rigid-consumer classes that triggered it.
        private static bool _diagLogged;

        // Networks where OffAsResetSweep cleared a producer's VVF lock this tick (the toggle edge).
        // Populated by the sweep, drained at the start of Run in PROTECT -- both on the same
        // power-tick worker thread, sequentially within the tick. The lock guards against any
        // future re-ordering. A flagged net gets a cohort-wide retry even when nothing is newly
        // violating, which is how toggling a buttoned producer clears the buttonless ones on its net.
        private static readonly HashSet<long> _retryRequested = new HashSet<long>();
        private static readonly object _retryLock = new object();

        internal static void RequestRetry(long networkReferenceId)
        {
            lock (_retryLock) _retryRequested.Add(networkReferenceId);
        }

        private static HashSet<long> DrainRetryRequests()
        {
            lock (_retryLock)
            {
                if (_retryRequested.Count == 0) return null;
                var copy = new HashSet<long>(_retryRequested);
                _retryRequested.Clear();
                return copy;
            }
        }

        // Returns the number of producers whose VVF lock state CHANGED this tick (faulted or
        // recovered), so the caller can decide whether a re-observe is needed before the allocator.
        internal static int Run(int currentTick, Core.GridSnapshot snap)
        {
            var retryNets = DrainRetryRequests();   // null when empty
            int changed = 0;
            bool diag = !_diagLogged;
            var producerTally = diag ? new Dictionary<string, int>() : null;
            var violatorTally = diag ? new Dictionary<string, int>() : null;
            int faultingNets = 0;
            if (snap == null) return 0;

            for (int ni = 0; ni < snap.Nets.Count; ni++)
            {
                try
                {
                var nr = snap.Nets[ni];

                bool hasActiveProducer = false, hasForeignDevice = false;
                var activeProducers = new List<Device>();                     // IsActiveProducer -> the cohort to lock
                var allProducers = new List<Core.GridSnapshot.DeviceRow>();   // IsProducer by class -> the cohort to clear
                var foreign = new List<Device>();
                List<Device> unknownProducers = null;
                Cable foreignCable = null;                   // burn adjacency for the unknown fallback

                for (int i = 0; i < nr.Rows.Count; i++)
                {
                    var row = nr.Rows[i];
                    if (row.IsProducerClass)
                    {
                        allProducers.Add(row);
                        if (row.IsActiveProducer)
                        {
                            hasActiveProducer = true;
                            activeProducers.Add(row.Device);
                        }
                    }
                    else if (row.IsSegmenter && row.IsTransformer)
                    {
                        // The only allowed non-producer (POWER.md §8.5 strict-literal). Allowed,
                        // never exempting: a Transformer's presence does not legalize anything
                        // else on the net.
                    }
                    else if (row.UnknownProducerLike)
                    {
                        // Producer-LIKE but not in the known class list (new game version or an
                        // unclassified modded producer). The flag already gates on a positive
                        // boundary-read output, so it is delivery-checked like an active producer.
                        // The cable-burn fallback below handles it (POWER.md §0.5).
                        hasActiveProducer = true;
                        (unknownProducers ?? (unknownProducers = new List<Device>())).Add(row.Device);
                    }
                    else
                    {
                        // Foreign: neither a producer nor a Transformer. Rigid consumers,
                        // batteries, APCs, dishes, umbilicals, idle devices, everything.
                        // Presence-based, not draw-based: topology legality, not instantaneous
                        // load, is what the rule protects.
                        hasForeignDevice = true;
                        foreign.Add(row.Device);
                        if (foreignCable == null) foreignCable = row.PowerCable;
                    }
                }

                bool violating = hasActiveProducer && hasForeignDevice;
                bool retryRequested = retryNets != null && retryNets.Contains(nr.Id);

                // Is any active producer not yet locked (a NEW violator), and is any producer (active
                // or stale) currently locked (something to recover / re-sync)?
                bool hasNewlyViolating = false;
                if (violating)
                {
                    for (int i = 0; i < activeProducers.Count; i++)
                        if (!VariableVoltageFaultRegistry.IsLockedOut(activeProducers[i].ReferenceId, currentTick))
                        { hasNewlyViolating = true; break; }
                }
                bool hasLockedProducer = false;
                for (int i = 0; i < allProducers.Count; i++)
                    if (VariableVoltageFaultRegistry.IsLockedOut(allProducers[i].RefId, currentTick))
                    { hasLockedProducer = true; break; }

                // Commit candidate (mirrors §8.4.1). A stable all-locked violating net is excluded
                // (its synced timer counts down); a NEW violator or a retry-requested net commits.
                bool candidate = hasNewlyViolating || (retryRequested && hasLockedProducer);

                if (candidate)
                {
                    if (!violating)
                    {
                        // RECOVER: net no longer violates (foreign devices gone, or no active
                        // producer left) -> clear every producer's lock, rejoin.
                        for (int i = 0; i < allProducers.Count; i++)
                        {
                            long id = allProducers[i].RefId;
                            if (VariableVoltageFaultRegistry.IsLockedOut(id, currentTick)) changed++;
                            VariableVoltageFaultRegistry.ClearLockout(id);
                        }
                    }
                    else
                    {
                        // RESET: still violating -> stamp every ACTIVE producer to one shared fresh
                        // expiry (phase-synced cohort). Clear any stale lock left on a now-inactive
                        // producer so it does not linger.
                        string violatorNames = BuildViolatorNames(foreign);
                        for (int i = 0; i < activeProducers.Count; i++)
                        {
                            long id = activeProducers[i].ReferenceId;
                            if (!VariableVoltageFaultRegistry.IsLockedOut(id, currentTick)) changed++;
                            VariableVoltageFaultRegistry.NoteVariableVoltageFault(id, currentTick, violatorNames);
                        }
                        for (int i = 0; i < allProducers.Count; i++)
                        {
                            var p = allProducers[i];
                            // Row-captured activity: the same sample the cohort was built from,
                            // never a second live read.
                            if (!p.IsActiveProducer
                                && VariableVoltageFaultRegistry.IsLockedOut(p.RefId, currentTick))
                            {
                                VariableVoltageFaultRegistry.ClearLockout(p.RefId);
                                changed++;
                            }
                        }
                    }
                }

                // Diagnostics + the unknown-producer cable-burn fallback run whenever the net is
                // violating, independent of the VVF commit (unknown producers are handled by burning a
                // cable, not by a lock).
                if (!violating) continue;

                if (diag)
                {
                    faultingNets++;
                    foreach (var p in activeProducers) Tally(producerTally, CleanTypeName(p.GetType().Name));
                    foreach (var r in foreign) Tally(violatorTally, CleanTypeName(r.GetType().Name));
                }

                if (unknownProducers != null && foreignCable != null)
                {
                    string label = CleanTypeName(unknownProducers[0].GetType().Name);
                    Plugin.Log?.LogInfo(
                        $"Producer isolation: unclassified producer {label} on network {nr.Id}; burning the cable adjacent to a foreign device (fallback handling).");
                    BurnReasonRegistry.RegisterPending(foreignCable,
                        $"Power producing devices can only connect to a transformer (adjacent {label})");
                    foreignCable.Break();   // self-marshals to the main thread
                }
                }
                catch (System.Exception ex)
                {
                    // One malformed net costs its isolation check this tick, never the whole sweep.
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Producer-isolation walk failed on network {snap.Nets[ni].Id}: {ex.Message}");
                }
            }

            if (diag && faultingNets > 0)
            {
                _diagLogged = true;
                Plugin.Log?.LogInfo($"[PGP-VVF-DIAG] producer-isolation faulted across {faultingNets} network(s). " +
                                    $"Producers faulted by class: {Format(producerTally)}. " +
                                    $"Foreign devices that triggered it (the 'also on the network' devices): {Format(violatorTally)}.");
            }
            return changed;
        }

        // Distinct foreign-device class names for the hover line, comma-separated, capped at 3 + "...".
        private static string BuildViolatorNames(List<Device> foreign)
        {
            var violatorList = foreign
                .Select(r => CleanTypeName(r.GetType().Name))
                .Distinct()
                .Take(4)
                .ToList();
            return violatorList.Count > 3
                ? string.Join(", ", violatorList.Take(3)) + ", ..."
                : string.Join(", ", violatorList);
        }

        private static void Tally(Dictionary<string, int> d, string key)
        {
            if (d == null || string.IsNullOrEmpty(key)) return;
            d.TryGetValue(key, out var c);
            d[key] = c + 1;
        }

        private static string Format(Dictionary<string, int> d)
        {
            if (d == null || d.Count == 0) return "(none)";
            return string.Join(", ", d.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + "x" + kv.Value));
        }

        // Unqualified C# class name with a leading "Structure" stripped (class names rarely carry it, but
        // be defensive); this is what the hover line shows the player.
        private static string CleanTypeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            const string prefix = "Structure";
            return name.StartsWith(prefix, System.StringComparison.Ordinal) && name.Length > prefix.Length
                ? name.Substring(prefix.Length)
                : name;
        }
    }
}
