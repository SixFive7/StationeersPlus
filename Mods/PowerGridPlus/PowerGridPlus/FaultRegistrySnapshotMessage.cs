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
    //   KindDeviceOverload / KindCableOverload: int64 refId, int32 remainingTicks,
    //                                          single valueW, single capW
    //   KindDeprioritized:                     int64 refId, int32 remainingTicks,
    //                                          single needsW, single upstreamDemandW,
    //                                          single upstreamSupplyW
    //   KindCurrentMismatch:                   int64 refId, int32 remainingTicks, string violators
    // The float payloads are the hover diagnostics: for KindDeviceOverload the rigid draw that
    // tripped the capacity rule and the combined deliverable cap; for KindCableOverload the flow
    // and the network's weakest-cable cap; for KindDeprioritized the locked "Needs D while U
    // competes for S upstream" triple.
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

        public byte Kind;
        public List<KeyValuePair<long, int>> Entries = new List<KeyValuePair<long, int>>();
        // Parallel to Entries for KindCurrentMismatch only (violator names per entry).
        public List<string> Violators = new List<string>();
        // Parallel to Entries for KindDeviceOverload and KindCableOverload only (per-entry hover payload).
        public List<float> PayloadValuesW = new List<float>();
        public List<float> PayloadCapsW = new List<float>();
        // Parallel to Entries for KindDeprioritized only (per-entry hover triple).
        public List<float> PayloadNeedsW = new List<float>();
        public List<float> PayloadUpstreamDemandW = new List<float>();
        public List<float> PayloadUpstreamSupplyW = new List<float>();

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
                }
                if (Kind == KindDeprioritized)
                {
                    writer.WriteSingle(i < PayloadNeedsW.Count ? PayloadNeedsW[i] : 0f);
                    writer.WriteSingle(i < PayloadUpstreamDemandW.Count ? PayloadUpstreamDemandW[i] : 0f);
                    writer.WriteSingle(i < PayloadUpstreamSupplyW.Count ? PayloadUpstreamSupplyW[i] : 0f);
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
            bool triplePayload = Kind == KindDeprioritized;
            PayloadValuesW = new List<float>(wattPayload ? count : 0);
            PayloadCapsW = new List<float>(wattPayload ? count : 0);
            PayloadNeedsW = new List<float>(triplePayload ? count : 0);
            PayloadUpstreamDemandW = new List<float>(triplePayload ? count : 0);
            PayloadUpstreamSupplyW = new List<float>(triplePayload ? count : 0);
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
                }
                if (triplePayload)
                {
                    PayloadNeedsW.Add(reader.ReadSingle());
                    PayloadUpstreamDemandW.Add(reader.ReadSingle());
                    PayloadUpstreamSupplyW.Add(reader.ReadSingle());
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
                    var combined = new List<(long, int, float, float, float)>(Entries.Count);
                    for (int i = 0; i < Entries.Count; i++)
                        combined.Add((Entries[i].Key, Entries[i].Value,
                            i < PayloadNeedsW.Count ? PayloadNeedsW[i] : 0f,
                            i < PayloadUpstreamDemandW.Count ? PayloadUpstreamDemandW[i] : 0f,
                            i < PayloadUpstreamSupplyW.Count ? PayloadUpstreamSupplyW[i] : 0f));
                    DeprioritizedRegistry.ReplaceClientSnapshot(combined);
                    break;
                }
                case KindDeviceOverload:
                    OverloadRegistry.ReplaceClientSnapshot(CombineWattPayload());
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
            }
        }

        private List<(long, int, float, float)> CombineWattPayload()
        {
            var combined = new List<(long, int, float, float)>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
                combined.Add((Entries[i].Key, Entries[i].Value,
                    i < PayloadValuesW.Count ? PayloadValuesW[i] : 0f,
                    i < PayloadCapsW.Count ? PayloadCapsW[i] : 0f));
            return combined;
        }
    }
}
