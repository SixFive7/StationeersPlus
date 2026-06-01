using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server -> client message: pushes the host's authoritative Max Transfer
    // Capacity (watts) so clients know the per-transmitter delivery cap. The
    // delivery clamp itself runs server-side (GetGeneratedPower); clients need
    // this only for planned client-side beam visuals that react when a link runs
    // at or above the cap. 0 = unlimited. Sent on connect and on every change,
    // mirroring DistanceConfigMessage.
    //
    // Process() runs on the receiving side. On a client receiving from the host,
    // hostId == NetworkManager._hostId (the host's connection ID).
    public class MaxCapacityConfigMessage : INetworkMessage
    {
        public float MaxCapacity;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteSingle(MaxCapacity);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            MaxCapacity = reader.ReadSingle();
        }

        public void Process(long hostId)
        {
            // Adopt on the client side only. If we receive it as the server
            // (e.g. a malformed echo) we ignore it; the server's own config is
            // the authoritative source.
            if (NetworkManager.IsServer) return;
            MaxCapacityConfigSync.OnHostConfigReceived(MaxCapacity);
        }
    }
}
