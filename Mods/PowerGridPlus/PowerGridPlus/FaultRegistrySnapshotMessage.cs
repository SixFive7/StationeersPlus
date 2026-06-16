using System.Collections.Generic;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server -> client per-tick FULL snapshot of one fault registry (POWER.md §13 heartbeat model;
    // replaces the former per-transition ShedState / OverloadState / CycleFaultState /
    // VariableVoltageFaultState messages). Each entry carries the device's REMAINING lockout ticks;
    // the client converts to a MonotonicClock expiry on receive, so no tick-domain alignment between
    // peers is needed and a lost packet self-heals on the next snapshot.
    //
    // Send policy (PowerAllocator.SyncFaultSnapshots): a registry's snapshot is sent every tick while
    // it is non-empty, plus exactly one EMPTY snapshot on the non-empty -> empty transition so an
    // OFF-as-reset clears the client mirror immediately instead of waiting out the local expiry.
    public class FaultRegistrySnapshotMessage : INetworkMessage
    {
        public const byte KindShed = 0;
        public const byte KindOverload = 1;
        public const byte KindCycleFault = 2;
        public const byte KindVariableVoltage = 3;
        public const byte KindDeadInput = 4;

        public byte Kind;
        public List<KeyValuePair<long, int>> Entries = new List<KeyValuePair<long, int>>();
        // Parallel to Entries for KindVariableVoltage only (violator names per entry).
        public List<string> Violators = new List<string>();

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteByte(Kind);
            writer.WriteInt32(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.WriteInt64(Entries[i].Key);
                writer.WriteInt32(Entries[i].Value);
                if (Kind == KindVariableVoltage)
                    writer.WriteString(i < Violators.Count ? Violators[i] ?? string.Empty : string.Empty);
            }
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Kind = reader.ReadByte();
            int count = reader.ReadInt32();
            Entries = new List<KeyValuePair<long, int>>(count);
            Violators = new List<string>(Kind == KindVariableVoltage ? count : 0);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                Entries.Add(new KeyValuePair<long, int>(refId, remaining));
                if (Kind == KindVariableVoltage)
                    Violators.Add(reader.ReadString());
            }
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            switch (Kind)
            {
                case KindShed:
                    BrownoutRegistry.ReplaceClientSnapshot(Entries);
                    break;
                case KindOverload:
                    OverloadRegistry.ReplaceClientSnapshot(Entries);
                    break;
                case KindCycleFault:
                    CycleFaultRegistry.ReplaceClientSnapshot(Entries);
                    break;
                case KindVariableVoltage:
                {
                    var combined = new List<(long, int, string)>(Entries.Count);
                    for (int i = 0; i < Entries.Count; i++)
                        combined.Add((Entries[i].Key, Entries[i].Value, i < Violators.Count ? Violators[i] : string.Empty));
                    VariableVoltageFaultRegistry.ReplaceClientSnapshot(combined);
                    break;
                }
                case KindDeadInput:
                    DeadInputRegistry.ReplaceClientSnapshot(Entries);
                    break;
            }
        }
    }
}
