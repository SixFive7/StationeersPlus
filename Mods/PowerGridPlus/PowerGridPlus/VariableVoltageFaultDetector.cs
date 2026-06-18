using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;   // Device base lives here (Device : SmallGrid)

namespace PowerGridPlus
{
    /// <summary>
    ///     PROTECT (producer-isolation) walk (POWER.md §8.5). A power producer may only connect to a
    ///     transformer or to other producers; a producer that shares a cable network with a rigid
    ///     consumer and no transformer enters VARIABLE_VOLTAGE_FAULT and stops generating
    ///     (ProducerFaultEnforcementPatches).
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
    ///       <item>RECOVER: if the candidate net no longer violates (transformer added, no active
    ///       producer, no rigid consumer) the whole producer cohort's locks are CLEARED.</item>
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
        // Populated in OBSERVE (the sweep), drained at the start of Run in PROTECT (producer-isolation) -- both on the
        // same power-tick worker thread, sequentially within the tick. The lock guards against any
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
        internal static int Run(int currentTick)
        {
            var retryNets = DrainRetryRequests();   // null when empty
            int changed = 0;
            bool diag = !_diagLogged;
            var producerTally = diag ? new Dictionary<string, int>() : null;
            var violatorTally = diag ? new Dictionary<string, int>() : null;
            int faultingNets = 0;

            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;

                bool hasActiveProducer = false, hasRigid = false, hasTransformer = false;
                var activeProducers = new List<Device>();   // IsActiveProducer -> the cohort to lock
                var allProducers = new List<Device>();       // IsProducer by class -> the cohort to clear
                var rigid = new List<Device>();
                List<Device> unknownProducers = null;

                lock (net.PowerDeviceList)
                {
                    var list = net.PowerDeviceList;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!(list[i] is Device dev)) continue;
                        if (ProducerClassifier.IsProducer(dev))
                        {
                            allProducers.Add(dev);
                            if (ProducerClassifier.IsActiveProducer(dev))
                            {
                                hasActiveProducer = true;
                                activeProducers.Add(dev);
                            }
                        }
                        else if (dev is Transformer) { hasTransformer = true; }   // ONLY Transformer isolates (Q1)
                        else if (ProducerClassifier.IsUnknownProducerLike(dev, net))
                        {
                            // Producer-LIKE but not in the known class list (new game version or an
                            // unclassified modded producer). IsUnknownProducerLike already gates on
                            // GetGeneratedPower > 0, so it is delivery-checked like an active producer.
                            // The cable-burn fallback below handles it (POWER.md §0.5).
                            hasActiveProducer = true;
                            (unknownProducers ?? (unknownProducers = new List<Device>())).Add(dev);
                        }
                        else if (ProducerClassifier.IsRigidConsumer(dev, net)) { hasRigid = true; rigid.Add(dev); }
                    }
                }

                bool violating = hasActiveProducer && hasRigid && !hasTransformer;
                bool retryRequested = retryNets != null && retryNets.Contains(net.ReferenceId);

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
                    if (VariableVoltageFaultRegistry.IsLockedOut(allProducers[i].ReferenceId, currentTick))
                    { hasLockedProducer = true; break; }

                // Commit candidate (mirrors §8.4.1). A stable all-locked violating net is excluded
                // (its synced timer counts down); a NEW violator or a retry-requested net commits.
                bool candidate = hasNewlyViolating || (retryRequested && hasLockedProducer);

                if (candidate)
                {
                    if (!violating)
                    {
                        // RECOVER: net no longer violates -> clear every producer's lock, rejoin.
                        for (int i = 0; i < allProducers.Count; i++)
                        {
                            long id = allProducers[i].ReferenceId;
                            if (VariableVoltageFaultRegistry.IsLockedOut(id, currentTick)) changed++;
                            VariableVoltageFaultRegistry.ClearLockout(id);
                        }
                    }
                    else
                    {
                        // RESET: still violating -> stamp every ACTIVE producer to one shared fresh
                        // expiry (phase-synced cohort). Clear any stale lock left on a now-inactive
                        // producer so it does not linger.
                        string violatorNames = BuildViolatorNames(rigid);
                        for (int i = 0; i < activeProducers.Count; i++)
                        {
                            long id = activeProducers[i].ReferenceId;
                            if (!VariableVoltageFaultRegistry.IsLockedOut(id, currentTick)) changed++;
                            VariableVoltageFaultRegistry.NoteVariableVoltageFault(id, currentTick, violatorNames);
                        }
                        for (int i = 0; i < allProducers.Count; i++)
                        {
                            var p = allProducers[i];
                            if (!ProducerClassifier.IsActiveProducer(p)
                                && VariableVoltageFaultRegistry.IsLockedOut(p.ReferenceId, currentTick))
                            {
                                VariableVoltageFaultRegistry.ClearLockout(p.ReferenceId);
                                changed++;
                            }
                        }
                    }
                }

                // Diagnostics + the unknown-producer cable-burn fallback run whenever the net is
                // violating, independent of the VVF commit (unknown producers are handled by burning a
                // cable, not by a lock).
                if (!violating) return;

                if (diag)
                {
                    faultingNets++;
                    foreach (var p in activeProducers) Tally(producerTally, CleanTypeName(p.GetType().Name));
                    foreach (var r in rigid) Tally(violatorTally, CleanTypeName(r.GetType().Name));
                }

                if (unknownProducers != null)
                {
                    string label = CleanTypeName(unknownProducers[0].GetType().Name);
                    foreach (var violator in rigid)
                    {
                        var cable = violator.PowerCable;
                        if (cable == null) continue;
                        Plugin.Log?.LogInfo(
                            $"Producer isolation: unclassified producer {label} on network {net.ReferenceId}; burning the cable adjacent to {CleanTypeName(violator.GetType().Name)} (fallback handling).");
                        BurnReasonRegistry.RegisterPending(cable,
                            $"Power producing devices can only connect to a transformer (adjacent {label})");
                        cable.Break();
                        break;
                    }
                }
            });

            if (diag && faultingNets > 0)
            {
                _diagLogged = true;
                Plugin.Log?.LogInfo($"[PGP-VVF-DIAG] producer-isolation faulted across {faultingNets} network(s). " +
                                    $"Producers faulted by class: {Format(producerTally)}. " +
                                    $"Rigid consumers that triggered it (the 'also on the network' devices): {Format(violatorTally)}.");
            }
            return changed;
        }

        // Distinct rigid-consumer class names for the hover line, comma-separated, capped at 3 + "...".
        private static string BuildViolatorNames(List<Device> rigid)
        {
            var violatorList = rigid
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
