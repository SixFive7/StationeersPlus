using System.Collections.Generic;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     The mod-owned write-back (POWER.md §0 decision 24 stage 3): everything vanilla
    ///     <c>PowerTick.ApplyState</c> used to do, done once, from the allocator's converged results,
    ///     with exact conservation. Vanilla Initialise / CalculateState / ApplyState are never called;
    ///     this replaces, per tick:
    ///
    ///     <list type="bullet">
    ///       <item>The net HUD/logic fields (RequiredLoad / CurrentLoad / PotentialLoad /
    ///       ShortfallLoad / DuringTickLoad reset): MP-serialized to clients and read by the network
    ///       analyser cartridge, CableAnalyser, IC10 PowerRequired / PowerActual / PowerPotential,
    ///       and the main thread's AssessPower headroom check.</item>
    ///       <item>Fuse protection: the lowest-rated fuse on a net blows when the delivered flow
    ///       exceeds its rating (vanilla picked a RANDOM breakable fuse; lowest-rated is the
    ///       deterministic, multiplayer-stable choice). <c>CableFuse.Break</c> self-marshals.</item>
    ///       <item>The deterministic §5.7 generator-overflow cable burn (the 20-tick window,
    ///       ported verbatim from the retired PowerTick prefix; <c>Cable.Break</c> self-marshals).</item>
    ///       <item>Energy settlement: storage charge credits (battery efficiency + sub-500 W trickle
    ///       floor preserved), storage discharge debits, umbilical Last* mirrors, the delivery audits
    ///       fed at the settlement site (credit == grant by construction), and the consumer
    ///       accumulator drains per decision 26 (main-queue post + worker-direct debits; a DEAD net
    ///       drains nothing, so debts freeze exactly and are billed on revival).</item>
    ///     </list>
    /// </summary>
    internal static class WriteBack
    {
        internal sealed class NetResult
        {
            public CableNetwork Network;
            public long Id;
            public float Required;
            public float Current;
            public float Potential;
        }

        internal struct StoreCredit
        {
            public long RefId;
            public byte Kind;                    // ChargeDeliveryAudit.Kind*
            public float Amount;
            public ElectricalInputOutput Owner;  // Battery / AreaPowerControl / RocketPowerUmbilical
        }

        internal struct StoreDebit
        {
            public long RefId;
            public byte Kind;
            public float Amount;
            public ElectricalInputOutput Owner;
        }

        /// <summary>Built by the allocator's publish tail each tick; consumed here. Worker-only.</summary>
        internal sealed class Plan
        {
            public readonly List<NetResult> Nets = new List<NetResult>();
            public readonly List<StoreCredit> Credits = new List<StoreCredit>();
            public readonly List<StoreDebit> Debits = new List<StoreDebit>();
        }

        internal static Plan Current;

        internal static void Clear()
        {
            Current = null;
        }

        // Umbilical Last* mirrors (vanilla ReceivePower/UsePower kept these; public get, protected set).
        private static readonly System.Action<object, object> SetLastPowerAdded =
            BuildSetter("LastPowerAdded");
        private static readonly System.Action<object, object> SetLastPowerRemoved =
            BuildSetter("LastPowerRemoved");

        private static System.Action<object, object> BuildSetter(string property)
        {
            try
            {
                var t = AccessTools.TypeByName("Assets.Scripts.Objects.Electrical.RocketPowerUmbilical")
                        ?? AccessTools.TypeByName("RocketPowerUmbilical");
                var setter = t == null ? null : AccessTools.PropertySetter(t, property);
                if (setter == null) return null;
                return (instance, value) => setter.Invoke(instance, new[] { value });
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<long, float> _producersScratch = new Dictionary<long, float>();

        internal static void Run(int currentTick, GridSnapshot snap)
        {
            var plan = Current;
            if (plan == null || snap == null) return;

            // ---- 1. Net fields (the presentation / MP / AssessPower surface) ----
            var currentById = new Dictionary<long, float>(plan.Nets.Count);
            for (int i = 0; i < plan.Nets.Count; i++)
            {
                var r = plan.Nets[i];
                var net = r.Network;
                if (net == null) continue;
                net.DuringTickLoad = 0f;
                net.RequiredLoad = r.Required;
                net.CurrentLoad = r.Current;
                net.PotentialLoad = r.Potential;
                net.ShortfallLoad = r.Required > r.Potential ? r.Required - r.Potential : 0f;
                currentById[r.Id] = r.Current;
            }

            // ---- 2. Fuses + the deterministic generator-overflow burn ----
            for (int i = 0; i < snap.Nets.Count; i++)
            {
                try
                {
                    var nr = snap.Nets[i];
                    currentById.TryGetValue(nr.Id, out float flow);

                    if (nr.MinFuse != null && nr.MinFuseBreak < flow)
                    {
                        // Deterministic protective blow (vanilla: random Pick among breakables).
                        nr.MinFuse.Break();
                    }

                    if (nr.GenSupply <= 0f) continue;
                    if (SplitPendingRegistry.IsPending(nr.Id)) continue;
                    float cap = nr.WeakestCap;
                    if (cap >= float.MaxValue) continue;    // unlimited tier: never burns

                    _producersScratch.Clear();
                    for (int d = 0; d < nr.Rows.Count; d++)
                    {
                        var row = nr.Rows[d];
                        if (row.IsSegmenter || row.Generated <= 0f) continue;
                        _producersScratch[row.RefId] = row.Generated;
                    }
                    float genFlow = nr.GenSupply < flow ? nr.GenSupply : flow;
                    CableBurnWindow.Observe(nr.Id, _producersScratch, genFlow);
                    if (!CableBurnWindow.IsFull(nr.Id)) continue;
                    float avg = CableBurnWindow.AverageFlow(nr.Id);
                    if (avg <= cap) continue;

                    Cable victim = ResolveProducerOutputCable(CableBurnWindow.TopProducer(nr.Id), nr)
                                   ?? WeakestCable(nr.Network);
                    if (victim == null) continue;
                    BurnReasonRegistry.RegisterPending(victim,
                        $"Overloaded -- sustained generator supply (~{avg:0} W over 10 s) exceeded this cable's rating ({cap:0} W)");
                    int count;
                    lock (nr.Network.CableList) count = nr.Network.CableList.Count;
                    SplitPendingRegistry.MarkBurned(nr.Id, count);
                    CableBurnWindow.Reset(nr.Id);
                    victim.Break();   // self-marshals to the main thread
                }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Write-back protection pass failed on network {snap.Nets[i].Id}: {ex.Message}");
                }
            }

            // ---- 3. Storage settlement (credit == grant by construction) ----
            float efficiency = Mathf.Clamp01(Settings.BatteryChargeEfficiency.Value);
            for (int i = 0; i < plan.Credits.Count; i++)
            {
                var credit = plan.Credits[i];
                if (credit.Amount <= 0f) continue;
                try { ApplyCredit(credit, efficiency); }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Charge settlement failed for store {credit.RefId}: {ex.Message}");
                }
            }
            for (int i = 0; i < plan.Debits.Count; i++)
            {
                var debit = plan.Debits[i];
                if (debit.Amount <= 0f) continue;
                try { ApplyDebit(debit); }
                catch (System.Exception ex)
                {
                    Plugin.Log?.LogWarning(
                        $"[PowerGridPlus] Discharge settlement failed for store {debit.RefId}: {ex.Message}");
                }
            }

            // ---- 4. Consumer accumulator drains (decision 26) ----
            List<MainThreadDebitQueue.Entry> mainBatch = null;
            for (int i = 0; i < snap.Nets.Count; i++)
            {
                var nr = snap.Nets[i];
                // A net that is not verdict-LIVE drains nothing: unfunded work freezes in the
                // accumulator and is billed in full on revival (the explicit form of the old
                // zero-Potential-corollary freeze).
                if (!NetLiveness.TryGetVerdict(nr.Id, out byte verdict) || verdict != NetLiveness.Live)
                    continue;
                for (int d = 0; d < nr.Rows.Count; d++)
                {
                    var row = nr.Rows[d];
                    if (row.DebitHome == DemandModel.DebitHome.None || row.DebitAmount <= 0f) continue;
                    if (row.DebitHome == DemandModel.DebitHome.WorkerDirect)
                    {
                        DemandModel.ApplyDebit(row.Device, row.DebitClass, row.DebitAmount);
                    }
                    else
                    {
                        (mainBatch ?? (mainBatch = new List<MainThreadDebitQueue.Entry>())).Add(
                            new MainThreadDebitQueue.Entry
                            {
                                Device = row.Device,
                                RefId = row.RefId,
                                Class = row.DebitClass,
                                Amount = row.DebitAmount,
                            });
                    }
                }
            }
            if (mainBatch != null) MainThreadDebitQueue.Post(mainBatch);
        }

        private static void ApplyCredit(in StoreCredit credit, float efficiency)
        {
                switch (credit.Kind)
                {
                    case ChargeDeliveryAudit.KindBattery:
                    {
                        if (!(credit.Owner is Battery battery)) break;
                        // The retired ReceivePower prefix's exact rule: efficiency applies first,
                        // and a post-efficiency credit below 500 W stores the full delivery instead
                        // (the trickle floor, so a battery can always top off).
                        float stored = credit.Amount * efficiency;
                        if (stored < 500f) stored = credit.Amount;
                        battery.PowerStored = Mathf.Clamp(battery.PowerStored + stored, 0f, battery.PowerMaximum);
                        ChargeDeliveryAudit.RecordCredit(credit.RefId, ChargeDeliveryAudit.KindBattery, stored);
                        break;
                    }
                    case ChargeDeliveryAudit.KindApcCell:
                    {
                        // The cell is re-fetched at settlement (a main-thread slot pull can land
                        // mid-tick); the grant was capped by grant-time headroom and the clamp
                        // guards the rest.
                        var cell = (credit.Owner as AreaPowerControl)?.Battery;
                        if (cell == null) break;
                        cell.PowerStored = Mathf.Clamp(cell.PowerStored + credit.Amount, 0f, cell.PowerMaximum);
                        ChargeDeliveryAudit.RecordCredit(credit.RefId, ChargeDeliveryAudit.KindApcCell, credit.Amount);
                        break;
                    }
                    case ChargeDeliveryAudit.KindUmbilical:
                    {
                        if (!(credit.Owner is RocketPowerUmbilical half)) break;
                        half.PowerStored = Mathf.Clamp(half.PowerStored + credit.Amount, 0f, half.PowerMaximum);
                        SetLastPowerAdded?.Invoke(half, credit.Amount);
                        ChargeDeliveryAudit.RecordCredit(credit.RefId, ChargeDeliveryAudit.KindUmbilical, credit.Amount);
                        break;
                    }
                }
        }

        private static void ApplyDebit(in StoreDebit debit)
        {
                switch (debit.Kind)
                {
                    case ChargeDeliveryAudit.KindBattery:
                    {
                        if (!(debit.Owner is Battery battery)) break;
                        battery.PowerStored = Mathf.Clamp(battery.PowerStored - debit.Amount, 0f, battery.PowerMaximum);
                        DischargeDeliveryAudit.RecordDrain(debit.RefId, debit.Kind, debit.Amount);
                        break;
                    }
                    case ChargeDeliveryAudit.KindApcCell:
                    {
                        // The APC cell's discharge grant drains directly (the vanilla deferred
                        // UsePower ledger drain is retired with ApplyState). Deliberately not fed to
                        // the discharge audit: the allocator never publishes APC-cell grants there.
                        var cell = (debit.Owner as AreaPowerControl)?.Battery;
                        if (cell == null) break;
                        cell.PowerStored = Mathf.Clamp(cell.PowerStored - debit.Amount, 0f, cell.PowerMaximum);
                        break;
                    }
                    case ChargeDeliveryAudit.KindUmbilical:
                    {
                        if (!(debit.Owner is RocketPowerUmbilical half)) break;
                        half.PowerStored = Mathf.Clamp(half.PowerStored - debit.Amount, 0f, half.PowerMaximum);
                        SetLastPowerRemoved?.Invoke(half, debit.Amount);
                        DischargeDeliveryAudit.RecordDrain(debit.RefId, debit.Kind, debit.Amount);
                        break;
                    }
                }
        }

        private static Cable ResolveProducerOutputCable(long producerRefId, GridSnapshot.NetRow nr)
        {
            if (producerRefId == 0L) return null;
            for (int i = 0; i < nr.Rows.Count; i++)
            {
                var row = nr.Rows[i];
                if (row.RefId != producerRefId) continue;
                return row.PowerCable;
            }
            return null;
        }

        private static Cable WeakestCable(CableNetwork net)
        {
            Cable victim = null;
            float victimCap = float.MaxValue;
            lock (net.CableList)
            {
                for (int i = 0; i < net.CableList.Count; i++)
                {
                    var cable = net.CableList[i];
                    if (cable == null) continue;
                    float cableCap = CableMax.For(cable);
                    if (cableCap < victimCap)
                    {
                        victimCap = cableCap;
                        victim = cable;
                    }
                }
            }
            return victim;
        }
    }
}
