using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server -> client replication of a per-Transformer shedding-state transition.
    //
    // BrownoutRegistry (the host-side shed-state machine) is a host-only data
    // structure. Without this message, a client never knows when a transformer
    // enters or leaves lockout; the BrownoutFlashBehaviour's per-frame check
    // sees only the server-side state and shows nothing on the client.
    //
    // The host broadcasts on every transition (entry into lockout, exit from
    // lockout) detected at the start of each electricity tick by
    // BrownoutRegistry.SyncTransitionsToClients. The client maintains a parallel
    // ClientShedState dictionary keyed by ReferenceId that the registry's
    // IsShedding query falls back to when running on a non-server peer.
    //
    // Full current state for a fresh joiner rides the plugin's
    // IJoinSuffixSerializer; live changes ride this message.
    public class ShedStateMessage : INetworkMessage
    {
        public long DeviceId;
        public bool Shedding;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(DeviceId);
            writer.WriteBoolean(Shedding);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            DeviceId = reader.ReadInt64();
            Shedding = reader.ReadBoolean();
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            BrownoutRegistry.SetClientShedding(DeviceId, Shedding);
        }
    }
}
