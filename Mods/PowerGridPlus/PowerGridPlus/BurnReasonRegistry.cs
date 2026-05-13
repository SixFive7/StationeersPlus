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
    ///        cleans up automatically when the wreckage is destroyed or unloaded).
    ///     3. <see cref="Patches.BurnReasonPatches.Thing_GetPassiveTooltip_Postfix"/> appends the reason
    ///        to the wreckage's hover tooltip (<see cref="PassiveTooltip.Extended"/>).
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
        }

        internal static string GetAttached(object wreckage)
        {
            if (wreckage == null)
                return null;
            return _attached.TryGetValue(wreckage, out var holder) ? holder.Reason : null;
        }
    }
}
