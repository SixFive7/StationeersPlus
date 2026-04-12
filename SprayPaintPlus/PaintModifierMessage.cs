using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace SprayPaintPlus
{
    /// <summary>
    /// Client → Server message: player's modifier key state changed while holding a spray can.
    /// Bit 0 = Shift (single item paint), Bit 1 = Ctrl (checkered pattern).
    /// </summary>
    public class PaintModifierMessage : INetworkMessage
    {
        public byte Modifiers;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteByte(Modifiers);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Modifiers = reader.ReadByte();
        }

        /// <summary>
        /// Runs on the server when a client's modifier state changes.
        /// Stores the state per player for the NetworkPainter to read.
        /// </summary>
        public void Process(long hostId)
        {
            SprayPaintHelpers.PlayerModifiers[hostId] = Modifiers;
        }
    }
}
