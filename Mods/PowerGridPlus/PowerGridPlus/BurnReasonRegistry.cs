using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Carries a "why did this cable burn" string from each call site that initiates a
    ///     <see cref="Cable.Break"/> through to the resulting <see cref="CableRuptured"/> wreckage, so the
    ///     wreckage's hover tooltip can tell the player what happened.
    ///
    ///     Flow:
    ///     1. Just before calling <c>cable.Break()</c>, the caller calls <see cref="RegisterPending"/>
    ///        keyed by <c>cable.LocalGrid</c> (the cell the cable occupies; the wreckage spawns there).
    ///     2. <see cref="Patches.BurnReasonPatches.CableRuptured_OnRegistered_Postfix"/> consumes the
    ///        pending entry by cell and stores the reason on the wreckage instance via
    ///        <see cref="Attach"/> (a <see cref="ConditionalWeakTable{TKey,TValue}"/> sidecar, so GC
    ///        cleans up automatically when the wreckage is destroyed or unloaded), then replicates it
    ///        to clients via <see cref="BurnReasonSyncMessage"/> (live) and the join suffix (bulk);
    ///        a client stores received reasons in <see cref="_clientByReference"/>.
    ///     3. <see cref="Patches.BurnReasonPatches.Structure_GetPassiveTooltip_Postfix"/> appends the reason
    ///        to the wreckage's hover tooltip (<see cref="PassiveTooltip.Extended"/>) via
    ///        <see cref="TryGetReason"/>, which consults all three stores.
    ///
    ///     Threading: the power tick runs on UniTask worker threads, so multiple worker threads can
    ///     register pending reasons concurrently. <see cref="_pendingByCell"/> is a
    ///     <see cref="ConcurrentDictionary{TKey,TValue}"/>; <see cref="ConditionalWeakTable{TKey,TValue}"/>
    ///     is thread-safe per its MSDN contract.
    /// </summary>
    internal static class BurnReasonRegistry
    {
        // Reason waiting to be picked up by the about-to-register wreckage at this cell. Cleared on consume.
        private static readonly ConcurrentDictionary<Grid3, string> _pendingByCell = new ConcurrentDictionary<Grid3, string>();
        // Reason permanently attached to a specific wreckage Thing.
        private static readonly ConditionalWeakTable<object, ReasonHolder> _attached = new ConditionalWeakTable<object, ReasonHolder>();
        // ReferenceId-keyed mirror of _attached for save persistence: ConditionalWeakTable has no
        // enumeration on .NET Framework 4.7.2, so BurnReasonSideCar snapshots from here. Entries for
        // wreckage that no longer exists are purged at snapshot time.
        private static readonly ConcurrentDictionary<long, string> _attachedByReference = new ConcurrentDictionary<long, string>();
        // Client-side lane: reasons received over the wire (BurnReasonSyncMessage live, the join
        // suffix in bulk). Keyed by ReferenceId because the message can land before the client has
        // any use for the Thing itself. Entries for wreckage that later despawns (the decision-32
        // sweep, deconstruction) just sit unread; they are a few bytes each and the dictionary is
        // cleared on every world load and at the start of every join, so no pruning pass is needed.
        private static readonly ConcurrentDictionary<long, string> _clientByReference = new ConcurrentDictionary<long, string>();

        private class ReasonHolder { public string Reason; }

        internal static void RegisterPending(Cable cable, string reason)
        {
            if (cable == null || string.IsNullOrEmpty(reason))
                return;
            _pendingByCell[cable.LocalGrid] = reason;
        }

        internal static bool TryConsumePending(Grid3 cell, out string reason)
        {
            return _pendingByCell.TryRemove(cell, out reason);
        }

        internal static void Attach(object wreckage, string reason)
        {
            if (wreckage == null || string.IsNullOrEmpty(reason))
                return;
            _attached.Remove(wreckage);
            _attached.Add(wreckage, new ReasonHolder { Reason = reason });
            if (wreckage is Thing thing)
                _attachedByReference[thing.ReferenceId] = reason;
        }

        // Consolidated read path for the tooltip: the instance weak table (live host attach), the
        // ReferenceId mirror (host durable copy; also catches a restore racing the weak table), then
        // the client cache filled over the wire. First non-empty hit wins.
        internal static bool TryGetReason(Thing wreckage, out string reason)
        {
            reason = null;
            if (wreckage == null)
                return false;
            if (_attached.TryGetValue(wreckage, out var holder) && !string.IsNullOrEmpty(holder.Reason))
            {
                reason = holder.Reason;
                return true;
            }
            if (_attachedByReference.TryGetValue(wreckage.ReferenceId, out var mirrored) && !string.IsNullOrEmpty(mirrored))
            {
                reason = mirrored;
                return true;
            }
            if (_clientByReference.TryGetValue(wreckage.ReferenceId, out var synced) && !string.IsNullOrEmpty(synced))
            {
                reason = synced;
                return true;
            }
            return false;
        }

        // Client-side store (BurnReasonSyncMessage.Process and the join-suffix deserializer).
        internal static void StoreClientReason(long referenceId, string reason)
        {
            if (referenceId == 0L || string.IsNullOrEmpty(reason))
                return;
            _clientByReference[referenceId] = reason;
        }

        // Save-side restore (BurnReasonSideCar): re-attach a persisted reason to reloaded wreckage.
        // NOT RegisterPending (that lane is for live burns racing the wreckage spawn).
        internal static void RestoreFromSideCar(Thing wreckage, string reason)
        {
            Attach(wreckage, reason);
        }

        // Snapshot for the save side-car, purging entries whose wreckage no longer exists (wreckage
        // gets deconstructed / decayed; the reference dictionary would otherwise grow across cable
        // churn for the whole session).
        internal static System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, string>> SnapshotAttached()
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<long, string>>();
            foreach (var pair in _attachedByReference)
            {
                if (Thing.Find(pair.Key) is CableRuptured)
                    result.Add(pair);
                else
                    _attachedByReference.TryRemove(pair.Key, out _);
            }
            return result;
        }

        // Reset for both world entry paths. Local load (FaultRegistryLoadPatches): reasons
        // re-attach from the side-car per Thing, and stale entries from the previous session must
        // not leak into the next world. Join (Plugin.DeserializeJoinSuffix): nothing local
        // survives; the join suffix and live sync messages repopulate the client lane. Either way,
        // an entry surviving into a world it was not recorded in surfaces a wrong reason on
        // ReferenceId collision.
        internal static void ClearAll()
        {
            _pendingByCell.Clear();
            _attachedByReference.Clear();
            _clientByReference.Clear();
        }
    }
}
