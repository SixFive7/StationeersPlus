using System.Collections.Generic;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server -> client per-tick FULL snapshot of one fault registry (POWER.md §13 heartbeat model;
    // replaces the former per-transition state messages). Each entry carries the device's REMAINING
    // lockout ticks; the client converts to a MonotonicClock expiry on receive, so no tick-domain
    // alignment between peers is needed and a lost packet self-heals on the next snapshot.
    //
    // Per-entry wire layout by kind (host and client ship in the same DLL, so the layout can change
    // freely as long as both sides agree):
    //   KindCycleFault / KindDeadInput:        int64 refId, int32 remainingTicks
    //   KindDeviceOverload:                    int64 refId, int32 remainingTicks,
    //                                          single valueW, single capW, single storageW
    //   KindCableOverload:                     int64 refId, int32 remainingTicks,
    //                                          single valueW, single capW
    //   KindDeprioritized:                     int64 refId, int32 remainingTicks,
    //                                          single needsW, single upstreamDemandW,
    //                                          single upstreamSupplyW, single shortfallW,
    //                                          byte reason, int32 victimPriority
    //   KindCurrentMismatch:                   int64 refId, int32 remainingTicks, string violators
    //   KindUndersupplied:                     int64 NETWORK id, int32 keepAliveTtl,
    //                                          single needsW, single availW, int64 feederRefId
    // The float payloads are the hover diagnostics: for KindDeviceOverload the rigid draw that
    // tripped the capacity rule, the combined deliverable cap, and the internal-storage component
    // of that cap; for KindCableOverload the flow and the network's weakest-cable cap; for
    // KindDeprioritized the (needsW, upstreamDemandW, upstreamSupplyW) triple plus the
    // decision-time shortfall, the DeprioritizeReason (as a byte), and the victim's priority.
    //
    // Send policy (PowerAllocator.SyncFaultSnapshots): a registry's snapshot is sent every tick while
    // it is non-empty, plus exactly one EMPTY snapshot on the non-empty -> empty transition so an
    // OFF-as-reset clears the client mirror immediately instead of waiting out the local expiry.
    public class FaultRegistrySnapshotMessage : INetworkMessage
    {
        public const byte KindDeprioritized = 0;
        public const byte KindDeviceOverload = 1;
        public const byte KindCycleFault = 2;
        public const byte KindCurrentMismatch = 3;
        public const byte KindDeadInput = 4;
        public const byte KindCableOverload = 5;
        public const byte KindUndersupplied = 6;

        public byte Kind;
        public List<KeyValuePair<long, int>> Entries = new List<KeyValuePair<long, int>>();
        // Parallel to Entries for KindCurrentMismatch only (violator names per entry).
        public List<string> Violators = new List<string>();
        // Parallel to Entries for KindDeviceOverload and KindCableOverload only (per-entry hover payload).
        public List<float> PayloadValuesW = new List<float>();
        public List<float> PayloadCapsW = new List<float>();
        // Parallel to Entries for KindDeviceOverload only (the internal-storage component of CapW).
        public List<float> PayloadStoragesW = new List<float>();
        // Parallel to Entries for KindDeprioritized only (per-entry hover triple + decision fields).
        public List<float> PayloadNeedsW = new List<float>();
        public List<float> PayloadUpstreamDemandW = new List<float>();
        public List<float> PayloadUpstreamSupplyW = new List<float>();
        public List<float> PayloadShortfallW = new List<float>();
        public List<byte> PayloadReason = new List<byte>();
        public List<int> PayloadVictimPriority = new List<int>();
        // Parallel to Entries for KindUndersupplied only (the feeder pointer; Entries carry
        // NETWORK ids for that kind, and PayloadValuesW/PayloadCapsW carry needsW/availW).
        public List<long> PayloadFeederRefId = new List<long>();

        private static bool KindHasWattPayload(byte kind)
            => kind == KindDeviceOverload || kind == KindCableOverload;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteByte(Kind);
            writer.WriteInt32(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.WriteInt64(Entries[i].Key);
                writer.WriteInt32(Entries[i].Value);
                if (Kind == KindCurrentMismatch)
                    writer.WriteString(i < Violators.Count ? Violators[i] ?? string.Empty : string.Empty);
                if (KindHasWattPayload(Kind))
                {
                    writer.WriteSingle(i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f);
                    writer.WriteSingle(i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f);
                    // Device overload carries the storage component of the cap; cable overload does not.
                    if (Kind == KindDeviceOverload)
                        writer.WriteSingle(i < PayloadStoragesW.Count ? PayloadStoragesW[i] : 0f);
                }
                if (Kind == KindDeprioritized)
                {
                    writer.WriteSingle(i < PayloadNeedsW.Count ? PayloadNeedsW[i] : 0f);
                    writer.WriteSingle(i < PayloadUpstreamDemandW.Count ? PayloadUpstreamDemandW[i] : 0f);
                    writer.WriteSingle(i < PayloadUpstreamSupplyW.Count ? PayloadUpstreamSupplyW[i] : 0f);
                    writer.WriteSingle(i < PayloadShortfallW.Count ? PayloadShortfallW[i] : 0f);
                    writer.WriteByte(i < PayloadReason.Count ? PayloadReason[i] : (byte)0);
                    writer.WriteInt32(i < PayloadVictimPriority.Count ? PayloadVictimPriority[i] : 0);
                }
                if (Kind == KindUndersupplied)
                {
                    writer.WriteSingle(i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f);
                    writer.WriteSingle(i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f);
                    writer.WriteInt64(i < PayloadFeederRefId.Count ? PayloadFeederRefId[i] : 0L);
                }
            }
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Kind = reader.ReadByte();
            int count = reader.ReadInt32();
            Entries = new List<KeyValuePair<long, int>>(count);
            Violators = new List<string>(Kind == KindCurrentMismatch ? count : 0);
            bool wattPayload = KindHasWattPayload(Kind);
            bool deviceOverload = Kind == KindDeviceOverload;
            bool triplePayload = Kind == KindDeprioritized;
            bool undersupplied = Kind == KindUndersupplied;
            PayloadValuesW = new List<float>((wattPayload || undersupplied) ? count : 0);
            PayloadCapsW = new List<float>((wattPayload || undersupplied) ? count : 0);
            PayloadFeederRefId = new List<long>(undersupplied ? count : 0);
            PayloadStoragesW = new List<float>(deviceOverload ? count : 0);
            PayloadNeedsW = new List<float>(triplePayload ? count : 0);
            PayloadUpstreamDemandW = new List<float>(triplePayload ? count : 0);
            PayloadUpstreamSupplyW = new List<float>(triplePayload ? count : 0);
            PayloadShortfallW = new List<float>(triplePayload ? count : 0);
            PayloadReason = new List<byte>(triplePayload ? count : 0);
            PayloadVictimPriority = new List<int>(triplePayload ? count : 0);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                Entries.Add(new KeyValuePair<long, int>(refId, remaining));
                if (Kind == KindCurrentMismatch)
                    Violators.Add(reader.ReadString());
                if (wattPayload)
                {
                    PayloadValuesW.Add(reader.ReadSingle());
                    PayloadCapsW.Add(reader.ReadSingle());
                    if (deviceOverload)
                        PayloadStoragesW.Add(reader.ReadSingle());
                }
                if (triplePayload)
                {
                    PayloadNeedsW.Add(reader.ReadSingle());
                    PayloadUpstreamDemandW.Add(reader.ReadSingle());
                    PayloadUpstreamSupplyW.Add(reader.ReadSingle());
                    PayloadShortfallW.Add(reader.ReadSingle());
                    PayloadReason.Add(reader.ReadByte());
                    PayloadVictimPriority.Add(reader.ReadInt32());
                }
                if (undersupplied)
                {
                    PayloadValuesW.Add(reader.ReadSingle());
                    PayloadCapsW.Add(reader.ReadSingle());
                    PayloadFeederRefId.Add(reader.ReadInt64());
                }
            }
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            switch (Kind)
            {
                case KindDeprioritized:
                {
                    var combined = new List<(long, int, float, float, float, float, DeprioritizeReason, int)>(Entries.Count);
                    for (int i = 0; i < Entries.Count; i++)
                        combined.Add((Entries[i].Key, Entries[i].Value,
                            i < PayloadNeedsW.Count ? PayloadNeedsW[i] : 0f,
                            i < PayloadUpstreamDemandW.Count ? PayloadUpstreamDemandW[i] : 0f,
                            i < PayloadUpstreamSupplyW.Count ? PayloadUpstreamSupplyW[i] : 0f,
                            i < PayloadShortfallW.Count ? PayloadShortfallW[i] : 0f,
                            (DeprioritizeReason)(i < PayloadReason.Count ? PayloadReason[i] : (byte)0),
                            i < PayloadVictimPriority.Count ? PayloadVictimPriority[i] : 0));
                    DeprioritizedRegistry.ReplaceClientSnapshot(combined);
                    break;
                }
                case KindDeviceOverload:
                    OverloadRegistry.ReplaceClientSnapshot(CombineDeviceOverloadPayload());
                    break;
                case KindCycleFault:
                    CycleFaultRegistry.ReplaceClientSnapshot(Entries);
                    break;
                case KindCurrentMismatch:
                {
                    var combined = new List<(long, int, string)>(Entries.Count);
                    for (int i = 0; i < Entries.Count; i++)
                        combined.Add((Entries[i].Key, Entries[i].Value, i < Violators.Count ? Violators[i] : string.Empty));
                    CurrentMismatchFaultRegistry.ReplaceClientSnapshot(combined);
                    break;
                }
                case KindDeadInput:
                    DeadInputRegistry.ReplaceClientSnapshot(Entries);
                    break;
                case KindCableOverload:
                    CableOverloadRegistry.ReplaceClientSnapshot(CombineWattPayload());
                    break;
                case KindUndersupplied:
                {
                    var combined = new List<(long, int, float, float, long)>(Entries.Count);
                    for (int i = 0; i < Entries.Count; i++)
                        combined.Add((Entries[i].Key, Entries[i].Value,
                            i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f,
                            i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f,
                            i < PayloadFeederRefId.Count ? PayloadFeederRefId[i] : 0L));
                    UndersuppliedRegistry.ReplaceClientSnapshot(combined);
                    break;
                }
            }
        }

        // Cable overload: (refId, remainingTicks, flowW, capW). No storage component.
        private List<(long, int, float, float)> CombineWattPayload()
        {
            var combined = new List<(long, int, float, float)>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
                combined.Add((Entries[i].Key, Entries[i].Value,
                    i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f,
                    i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f));
            return combined;
        }

        // Device overload: (refId, remainingTicks, valueW, capW, storageW).
        private List<(long, int, float, float, float)> CombineDeviceOverloadPayload()
        {
            var combined = new List<(long, int, float, float, float)>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
                combined.Add((Entries[i].Key, Entries[i].Value,
                    i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f,
                    i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f,
                    i < PayloadStoragesW.Count ? PayloadStoragesW[i] : 0f));
            return combined;
        }
    }
}
