using System.Threading;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using UnityEngine;
using VanillaAdvancedComposter = global::Objects.Electrical.AdvancedComposter;
using VanillaDroidSleeper = global::Objects.Electrical.DroidSleeper;

namespace PowerGridPlus.Core
{
    /// <summary>
    ///     Per-class demand descriptors for the boundary read (POWER.md §0 decision 26). Most consumer
    ///     classes are read with ONE live <c>GetUsedPower</c> call per tick; the accumulator-pattern
    ///     classes below get special handling so the <c>_powerUsedDuringTick</c> drain is exact:
    ///
    ///     <para><b>Main-queue accumulate classes</b> (SimpleFabricatorBase, Fabricator, ArcFurnace,
    ///     IceCrusher): their accumulator is written on the MAIN thread (frame coroutines / the 100 ms
    ///     server ticks), so the drain is marshaled there via <see cref="MainThreadDebitQueue"/>.
    ///     Demand is RECONSTRUCTED from one <c>Volatile.Read</c> of the field plus the class's
    ///     decompile-verified formula (0.2.6403.27689: SimpleFabricatorBase 420203, Fabricator 396283,
    ///     ArcFurnace 365548, IceCrusher 380296), with pending-but-unapplied debits subtracted, so the
    ///     billed amount and the debited amount are the same number by construction.</para>
    ///
    ///     <para><b>Worker-direct accumulate classes</b> (DroidSleeper: OnPowerTick-written;
    ///     WallCooler, AdvancedComposter: atmosphere-phase-written): their writers are sequenced
    ///     against the power solve by the tick structure, so the write-back drains them synchronously
    ///     on the worker. Demand stays a plain single <c>GetUsedPower</c> read (tick-stable).</para>
    ///
    ///     <para><b>Assert-semantics classes</b> (Fermenter and the atmosphere re-assigners): their
    ///     writers overwrite the field absolutely every cycle; there is nothing to drain and no debit
    ///     is taken. One plain read.</para>
    /// </summary>
    internal static class DemandModel
    {
        internal enum DebitHome : byte { None = 0, MainQueue = 1, WorkerDirect = 2 }

        internal enum DebitClass : byte
        {
            None = 0,
            SimpleFabricator = 1,
            Fabricator = 2,
            ArcFurnace = 3,
            IceCrusher = 4,
            DroidSleeper = 5,
            WallCooler = 6,
            AdvancedComposter = 7,
        }

        // FieldRefs to each class's private float _powerUsedDuringTick (declarations verified in the
        // 0.2.6403.27689 decompile; the field is declared per class, never on a shared base).
        private static readonly AccessTools.FieldRef<SimpleFabricatorBase, float> SfbAcc =
            AccessTools.FieldRefAccess<SimpleFabricatorBase, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<Fabricator, float> FabAcc =
            AccessTools.FieldRefAccess<Fabricator, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<ArcFurnace, float> ArcAcc =
            AccessTools.FieldRefAccess<ArcFurnace, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<IceCrusher, float> IceAcc =
            AccessTools.FieldRefAccess<IceCrusher, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<VanillaDroidSleeper, float> SleeperAcc =
            AccessTools.FieldRefAccess<VanillaDroidSleeper, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<WallCooler, float> CoolerAcc =
            AccessTools.FieldRefAccess<WallCooler, float>("_powerUsedDuringTick");
        private static readonly AccessTools.FieldRef<VanillaAdvancedComposter, float> ComposterAcc =
            AccessTools.FieldRefAccess<VanillaAdvancedComposter, float>("_powerUsedDuringTick");

        internal struct Reading
        {
            public float Demand;        // the billed own-network demand for this tick
            public float DebitAmount;   // the accumulator component that must be drained when funded
            public DebitHome Home;
            public DebitClass Class;
        }

        /// <summary>
        ///     Boundary-read demand for one device on its OWN power network. Returns true when the
        ///     class needed special handling; false means the caller should take one plain
        ///     <c>GetUsedPower(net)</c> sample.
        /// </summary>
        internal static bool TryReadSpecial(Device device, out Reading reading)
        {
            switch (device)
            {
                // ---- main-queue accumulate classes (reconstructed formulas) ----
                case SimpleFabricatorBase sfb:
                {
                    float acc = EffectiveAcc(Volatile.Read(ref SfbAcc(sfb)), sfb.ReferenceId);
                    reading = new Reading
                    {
                        // 420203: foreign -> -1 (caller guarantees own net); !OnOff -> acc; else UsedPower + acc.
                        Demand = sfb.OnOff ? sfb.UsedPower + acc : acc,
                        DebitAmount = acc,
                        Home = DebitHome.MainQueue,
                        Class = DebitClass.SimpleFabricator,
                    };
                    return true;
                }
                case Fabricator fab:
                {
                    float acc = EffectiveAcc(Volatile.Read(ref FabAcc(fab)), fab.ReferenceId);
                    reading = new Reading
                    {
                        // 396283: !OnOff -> 0 (the accumulator is NOT billed while off); else UsedPower + acc.
                        Demand = fab.OnOff ? fab.UsedPower + acc : 0f,
                        DebitAmount = fab.OnOff ? acc : 0f,
                        Home = DebitHome.MainQueue,
                        Class = DebitClass.Fabricator,
                    };
                    return true;
                }
                case ArcFurnace arc:
                {
                    float acc = EffectiveAcc(Volatile.Read(ref ArcAcc(arc)), arc.ReferenceId);
                    reading = new Reading
                    {
                        // 365548: !OnOff -> acc; else UsedPower + acc.
                        Demand = arc.OnOff ? arc.UsedPower + acc : acc,
                        DebitAmount = acc,
                        Home = DebitHome.MainQueue,
                        Class = DebitClass.ArcFurnace,
                    };
                    return true;
                }
                case IceCrusher ice:
                {
                    float acc = EffectiveAcc(Volatile.Read(ref IceAcc(ice)), ice.ReferenceId);
                    reading = new Reading
                    {
                        // 380296: !OnOff -> acc; else UsedPower + acc.
                        Demand = ice.OnOff ? ice.UsedPower + acc : acc,
                        DebitAmount = acc,
                        Home = DebitHome.MainQueue,
                        Class = DebitClass.IceCrusher,
                    };
                    return true;
                }

                // ---- worker-direct accumulate classes (plain read; synchronous drain) ----
                case VanillaDroidSleeper sleeper:
                {
                    reading = PlainWithWorkerDebit(device, Volatile.Read(ref SleeperAcc(sleeper)), DebitClass.DroidSleeper);
                    return true;
                }
                case WallCooler cooler:
                {
                    reading = PlainWithWorkerDebit(device, Volatile.Read(ref CoolerAcc(cooler)), DebitClass.WallCooler);
                    return true;
                }
                case VanillaAdvancedComposter composter:
                {
                    reading = PlainWithWorkerDebit(device, Volatile.Read(ref ComposterAcc(composter)), DebitClass.AdvancedComposter);
                    return true;
                }

                default:
                    reading = default;
                    return false;
            }
        }

        private static Reading PlainWithWorkerDebit(Device device, float acc, DebitClass cls)
        {
            // These overrides bill UsedPower + accumulator unconditionally on the own network
            // (AdvancedComposter 179748, DroidSleeper 181260, WallCooler 426618), so the billed
            // accumulator component is the whole accumulator. One plain sample carries the demand.
            float raw = device.GetUsedPower(device.PowerCableNetwork);
            return new Reading
            {
                Demand = raw > 0f ? raw : 0f,
                DebitAmount = acc > 0f ? acc : 0f,
                Home = DebitHome.WorkerDirect,
                Class = cls,
            };
        }

        private static float EffectiveAcc(float raw, long refId)
        {
            float pending = MainThreadDebitQueue.PendingFor(refId);
            float eff = raw - pending;
            return eff > 0f ? eff : 0f;
        }

        /// <summary>
        ///     Subtract a drained amount from a class's accumulator, clamped at zero. MUST run on the
        ///     class's home thread (main for the queue classes, the power worker for the direct ones):
        ///     that single-writer discipline is what makes the drain race-free (decision 25/26).
        /// </summary>
        internal static void ApplyDebit(Device device, DebitClass cls, float amount)
        {
            if (device == null || amount <= 0f) return;
            switch (cls)
            {
                case DebitClass.SimpleFabricator:
                    if (device is SimpleFabricatorBase sfb) { ref float f = ref SfbAcc(sfb); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.Fabricator:
                    if (device is Fabricator fab) { ref float f = ref FabAcc(fab); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.ArcFurnace:
                    if (device is ArcFurnace arc) { ref float f = ref ArcAcc(arc); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.IceCrusher:
                    if (device is IceCrusher ice) { ref float f = ref IceAcc(ice); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.DroidSleeper:
                    if (device is VanillaDroidSleeper sleeper) { ref float f = ref SleeperAcc(sleeper); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.WallCooler:
                    if (device is WallCooler cooler) { ref float f = ref CoolerAcc(cooler); f = Mathf.Max(0f, f - amount); }
                    break;
                case DebitClass.AdvancedComposter:
                    if (device is VanillaAdvancedComposter composter) { ref float f = ref ComposterAcc(composter); f = Mathf.Max(0f, f - amount); }
                    break;
            }
        }
    }
}
