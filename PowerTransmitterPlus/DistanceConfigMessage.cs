using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server -> client message: pushes the host's authoritative DistanceCostFactor (k)
    // value so client tablets/IC10 readouts compute the same MicrowaveSourceDraw and
    // MicrowaveTransmissionLoss values that the server is simulating.
    //
    // Process() runs on the receiving side. On a client receiving from the host,
    // hostId == NetworkManager._hostId (the host's connection ID).
    public class DistanceConfigMessage : INetworkMessage
    {
        public float K;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteSingle(K);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            K = reader.ReadSingle();
        }

        public void Process(long hostId)
        {
            // We only adopt this on the client side. If we receive it as the
            // server (e.g. a malformed echo) we ignore it — the server's own
            // config is the authoritative source.
            if (NetworkManager.IsServer) return;
            DistanceConfigSync.OnHostConfigReceived(K);
        }
    }
}
