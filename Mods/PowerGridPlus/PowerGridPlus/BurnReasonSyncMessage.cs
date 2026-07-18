using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server -> client replication of a single burn reason at the moment it attaches to freshly
    // spawned CableRuptured wreckage (BurnReasonPatches.CableRuptured_OnRegistered_Postfix).
    //
    // Burns only happen on the simulating peer (every writer sits behind the RunSimulation gate),
    // so without this a connected client has no reason for any wreckage that burns mid-session and
    // its hover falls back to the legacy line. The full current set for a fresh joiner ships
    // separately in the plugin's IJoinSuffixSerializer; this message covers live burns after the
    // join. Registered in Plugin.OnPrefabsLoaded via MOD.Networking.RegisterMessage.
    // See Research/Protocols/LaunchPadBoosterNetworking.md.
    public class BurnReasonSyncMessage : INetworkMessage
    {
        public long WreckageRefId;
        public string Reason;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(WreckageRefId);
            writer.WriteString(Reason ?? string.Empty);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            WreckageRefId = reader.ReadInt64();
            Reason = reader.ReadString();
        }

        public void Process(long hostId)
        {
            // The server is authoritative; ignore any echo received as the host.
            if (NetworkManager.IsServer) return;
            BurnReasonRegistry.StoreClientReason(WreckageRefId, Reason);
        }
    }
}
