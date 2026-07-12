using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Assets.Scripts.Objects.Pipes;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     The race-free accumulator drain for the main-clock consumer classes (POWER.md §0 decision
    ///     26). Their <c>_powerUsedDuringTick</c> is written by main-thread clocks (frame coroutines,
    ///     the 100 ms server ticks); a worker-side subtract would be a read-modify-write racing a
    ///     blind store (the CAS clobber, rejected). Instead the write-back posts ONE batch per tick,
    ///     the batch is applied on the main thread (single-writer discipline: every mutation of the
    ///     field now executes on the thread that owns the class's accruals), and a worker-owned
    ///     pending ledger keeps the boundary read exact until the batch lands:
    ///
    ///     <para><c>effective accumulator = max(0, Volatile.Read(field) - pending)</c>. Every
    ///     interleaving conserves: applied-before-read serves fresh accruals only; not-yet-applied
    ///     subtracts via the ledger; the mid-drain overlap (entries applied, sequence not yet stored)
    ///     under-serves for one tick and bills the remainder next tick. Nothing is ever lost or
    ///     double-billed, with no scheduling assumption (see the OPTIONS.md section 4 review item on
    ///     this bookkeeping).</para>
    ///
    ///     <para>Threading: <see cref="Post"/> / <see cref="Reconcile"/> / <see cref="PendingFor"/> /
    ///     <see cref="Clear"/> are power-worker-only. <see cref="DrainOnMain"/> is main-thread-only.
    ///     The crossing is a <see cref="ConcurrentQueue{T}"/> plus one volatile applied-sequence int.
    ///     When no dispatcher exists (early boot), the batch stays queued and the pending ledger keeps
    ///     demand exact; the accumulators drain as soon as the pump appears.</para>
    /// </summary>
    internal static class MainThreadDebitQueue
    {
        internal struct Entry
        {
            public Device Device;
            public long RefId;
            public DemandModel.DebitClass Class;
            public float Amount;
        }

        private sealed class Batch
        {
            public int Seq;
            public List<Entry> Entries;
        }

        private static readonly ConcurrentQueue<Batch> _queue = new ConcurrentQueue<Batch>();
        private static int _appliedSeq;                 // Volatile-crossed: main writes, worker reads

        // Worker-owned state (never touched by the main thread).
        private static int _postedSeq;
        private static readonly List<Batch> _inFlight = new List<Batch>();
        private static readonly Dictionary<long, float> _pending = new Dictionary<long, float>();

        private static bool _pumpMissingLogged;

        /// <summary>Worker: pending (posted, not yet confirmed applied) debit total for a device.</summary>
        internal static float PendingFor(long refId)
        {
            return _pending.TryGetValue(refId, out float amount) ? amount : 0f;
        }

        /// <summary>
        ///     Worker, at tick start: retire in-flight batches the main thread has confirmed applied.
        /// </summary>
        internal static void Reconcile()
        {
            if (_inFlight.Count == 0) return;
            int applied = Volatile.Read(ref _appliedSeq);
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                var batch = _inFlight[i];
                if (batch.Seq > applied) continue;
                var entries = batch.Entries;
                for (int j = 0; j < entries.Count; j++)
                {
                    long refId = entries[j].RefId;
                    if (!_pending.TryGetValue(refId, out float amount)) continue;
                    amount -= entries[j].Amount;
                    if (amount <= 0.0001f) _pending.Remove(refId);
                    else _pending[refId] = amount;
                }
                _inFlight.RemoveAt(i);
            }
        }

        /// <summary>Worker, at write-back: post this tick's debit batch and schedule the drain.</summary>
        internal static void Post(List<Entry> entries)
        {
            if (entries == null || entries.Count == 0) return;
            var batch = new Batch { Seq = ++_postedSeq, Entries = entries };
            for (int i = 0; i < entries.Count; i++)
            {
                _pending.TryGetValue(entries[i].RefId, out float amount);
                _pending[entries[i].RefId] = amount + entries[i].Amount;
            }
            _inFlight.Add(batch);
            _queue.Enqueue(batch);

            if (!MainThread.TryEnqueue(DrainOnMain) && !_pumpMissingLogged)
            {
                _pumpMissingLogged = true;
                Plugin.Log?.LogWarning(
                    "[PowerGridPlus] Main-thread pump unavailable; accumulator debits queue until it appears " +
                    "(demand stays exact via the pending ledger).");
            }
        }

        /// <summary>Main thread: apply every queued batch, newest last, then publish the sequence.</summary>
        private static void DrainOnMain()
        {
            int last = 0;
            while (_queue.TryDequeue(out var batch))
            {
                var entries = batch.Entries;
                for (int i = 0; i < entries.Count; i++)
                {
                    DemandModel.ApplyDebit(entries[i].Device, entries[i].Class, entries[i].Amount);
                }
                last = batch.Seq;
            }
            if (last != 0) Volatile.Write(ref _appliedSeq, last);
        }

        /// <summary>World-load reset (worker, via the load boundary): the old world's devices are gone.</summary>
        internal static void Clear()
        {
            _pending.Clear();
            _inFlight.Clear();
            // Anything still in the crossing queue applies harmlessly to dead managed objects, or is
            // dropped here if nothing drains it; the applied-sequence only ever moves forward.
            while (_queue.TryDequeue(out _)) { }
            _pumpMissingLogged = false;
        }
    }
}
