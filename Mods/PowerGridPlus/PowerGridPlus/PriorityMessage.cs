using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server -> client replication of a per-Transformer Priority change.
    //
    // The Priority lives in the server-only PriorityStore and the SetLogicValue
    // write is server-gated, so a client never updates its own store. Without
    // this, a client keeps reading the DEFAULT priority via LogicType.Priority
    // and the strict-priority allocation on the client side diverges from the
    // host.
    //
    // On receive, the client also refreshes the needle visual (SetKnob) because
    // the Transformer's _outputSetting backing field is no longer the source of
    // truth for the dial; SetKnob has been patched to lerp on Priority instead,
    // and the vanilla Setting-setter -> SetKnob call chain no longer fires when
    // Priority alone changes. Without this refresh, the client's needle stays
    // put after every host-side or client-originated priority change.
    public class PriorityMessage : INetworkMessage
    {
        public long DeviceId;
        public int Priority;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(DeviceId);
            writer.WriteInt32(Priority);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            DeviceId = reader.ReadInt64();
            Priority = reader.ReadInt32();
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            PriorityStore.SetPriorityByReference(DeviceId, Priority);

            // Refresh the needle on the client. Use Thing.Find to locate the transformer; safe to
            // call on the client immediately because by the time this message fires the Thing has
            // been registered (PriorityMessage is broadcast from the server-side write path which
            // necessarily runs against an existing Thing).
            if (Thing.Find<Thing>(DeviceId) is Transformer t)
            {
                AccessTools.Method(typeof(Transformer), "SetKnob")?.Invoke(t, null);
            }
        }
    }
}
