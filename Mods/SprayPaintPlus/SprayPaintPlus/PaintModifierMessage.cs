using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace SprayPaintPlus
{
    /// <summary>
    /// Client -> Server message: player's modifier key state changed while holding a spray can.
    /// Bit 0 = Shift (single item paint, invert already applied), Bit 1 = Ctrl (checkered).
    /// PlayerHumanId is the sender's own controlled Human ReferenceId; the server keys
    /// PlayerModifiers by that id because vanilla paint messages identify the actor
    /// by AttackParentId (a Human ReferenceId), not by the LaunchPadBooster connection id.
    /// </summary>
    public class PaintModifierMessage : INetworkMessage
    {
        public byte Modifiers;
        public long PlayerHumanId;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteByte(Modifiers);
            writer.WriteInt64(PlayerHumanId);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Modifiers = reader.ReadByte();
            PlayerHumanId = reader.ReadInt64();
        }

        /// <summary>
        /// Runs on the server when a client's modifier state changes.
        /// </summary>
        public void Process(long hostId)
        {
            if (PlayerHumanId == 0)
                return;
            SprayPaintHelpers.PlayerModifiers[PlayerHumanId] = Modifiers;
        }
    }
}
