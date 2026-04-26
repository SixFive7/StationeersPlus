using System.Collections.Generic;
using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server -> client message: pushes the host's per-dish auto-aim cache so a
    // joining client's MicrowaveAutoAimTarget reads return the same value the
    // host's dish is aimed at. Without this, a freshly-connected client sees
    // 0 from every dish until someone re-writes the LogicType.
    //
    // Sent from PlayerConnectedSyncPatch on every connect; same channel as
    // DistanceConfigMessage and BeamVisualConfigMessage. Payload is one int
    // (count) plus 16 bytes per active auto-aim entry; bounded by the number
    // of dishes a player has auto-aimed in the world (a few dozen at most in
    // normal play).
    public class AutoAimSnapshotMessage : INetworkMessage
    {
        public List<long> DishIds = new List<long>();
        public List<long> TargetIds = new List<long>();

        public void Serialize(RocketBinaryWriter writer)
        {
            int count = DishIds?.Count ?? 0;
            if (TargetIds == null || TargetIds.Count != count) count = 0;
            writer.WriteInt32(count);
            for (int i = 0; i < count; i++)
            {
                writer.WriteInt64(DishIds[i]);
                writer.WriteInt64(TargetIds[i]);
            }
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            DishIds = new List<long>(count);
            TargetIds = new List<long>(count);
            for (int i = 0; i < count; i++)
            {
                DishIds.Add(reader.ReadInt64());
                TargetIds.Add(reader.ReadInt64());
            }
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            AutoAimSnapshotSync.OnSnapshotReceived(this);
        }
    }
}
