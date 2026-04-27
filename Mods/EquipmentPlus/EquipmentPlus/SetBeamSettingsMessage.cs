using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using LaunchPadBooster.Networking;

namespace EquipmentPlus
{
    // Beam-settings sync message. Three flow paths:
    //
    //   1. Local input on a client (HelmetBeamPatches.HandleScroll):
    //      send the local human's new settings to the host with .SendToHost().
    //
    //   2. Host receives (Process runs server-side, GameManager.RunSimulation
    //      true): update the host's PerCharacter dict so the side-car snapshot
    //      includes this character's preference, then rebroadcast to all
    //      other peers via .SendAll(clientId) so every client renders the
    //      remote player's beam visual at the chosen angle.
    //
    //   3. Other peers receive the rebroadcast (Process runs client-side):
    //      update local PerCharacter dict so HelmetBeamApplyPatch picks up
    //      the new settings on the remote player's helmet next LateUpdate.
    //
    // Self-update guard: if the message's HumanReferenceId equals
    // LocalHuman.ReferenceId, skip. Local input always wins; a late
    // rebroadcast must not clobber a fresh local adjustment between send and
    // receive.
    //
    // Authority: server validates that the referenced Human exists; per-Human
    // ownership is not enforced (worst-case payload is a cosmetic preference
    // for someone else's character; vanilla SetLogicFromClient takes the same
    // posture, no per-id ownership check).
    public class SetBeamSettingsMessage : INetworkMessage
    {
        public long HumanReferenceId;
        public float SpotAngle;
        public float Intensity;
        public float Range;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(HumanReferenceId);
            writer.WriteSingle(SpotAngle);
            writer.WriteSingle(Intensity);
            writer.WriteSingle(Range);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            HumanReferenceId = reader.ReadInt64();
            SpotAngle = reader.ReadSingle();
            Intensity = reader.ReadSingle();
            Range = reader.ReadSingle();
        }

        public void Process(long clientId)
        {
            if (HumanReferenceId == 0L) return;

            // Self-update from a rebroadcast: HandleScroll already applied
            // this (or a newer) value locally. Skipping prevents a late
            // rebroadcast from clobbering a fresh local adjustment.
            var localHuman = Human.LocalHuman;
            if (localHuman != null && localHuman.ReferenceId == HumanReferenceId)
                return;

            if (!(Thing.Find(HumanReferenceId) is Human)) return;

            HelmetBeamState.PerCharacter[HumanReferenceId] = new BeamSettings
            {
                SpotAngle = SpotAngle,
                Intensity = Intensity,
                Range     = Range,
            };

            // Host: rebroadcast to all other peers so every client renders
            // this character's beam visual at the new angle. Excludes the
            // sender (clientId) so they don't receive their own message back.
            if (GameManager.RunSimulation)
                this.SendAll(clientId);
        }
    }
}
