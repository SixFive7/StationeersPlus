using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace SprayPaintPlus
{
    /// <summary>
    /// Client -> Server message: the sender's client-half preference mask changed while
    /// holding a spray can or spray gun.
    ///
    /// Modifiers carries the sender's WHOLE client half, not only the two live modifier
    /// keys it carried before v1.11.0: every client-side setting the server has to merge
    /// before it acts on that player's paint (the eleven network-painting toggles, glow
    /// paint, unlimited uses) rides along with them. Bit positions live in
    /// SettingsMerge.PlayerPrefs, which is the single source of truth for the layout.
    /// Appending a bit there is safe; renumbering one is not, because both ends of this
    /// message must agree and MOD.Networking.Required only guarantees a matching mod
    /// version, not a matching interpretation of an old one.
    ///
    /// PlayerHumanId is the sender's own controlled Human ReferenceId; the server keys
    /// PlayerModifiers by that id because vanilla paint messages identify the actor
    /// by AttackParentId (a Human ReferenceId), not by the LaunchPadBooster connection id.
    /// </summary>
    public class PaintModifierMessage : INetworkMessage
    {
        public ushort Modifiers;
        public long PlayerHumanId;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteUInt16(Modifiers);
            writer.WriteInt64(PlayerHumanId);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Modifiers = reader.ReadUInt16();
            PlayerHumanId = reader.ReadInt64();
        }

        /// <summary>
        /// Runs on the server when a client's preference mask changes.
        /// </summary>
        public void Process(long hostId)
        {
            if (PlayerHumanId == 0)
                return;
            SprayPaintHelpers.PlayerModifiers[PlayerHumanId] = Modifiers;
        }
    }
}
