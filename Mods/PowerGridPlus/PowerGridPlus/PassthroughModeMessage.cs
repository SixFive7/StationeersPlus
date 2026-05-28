using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using LaunchPadBooster.Networking;
using PowerGridPlus.Patches;

namespace PowerGridPlus
{
    // Server -> client replication of a per-device LogicPassthroughMode change.
    //
    // The mode lives in the server-only PassthroughModeStore and the SetLogicValue write is
    // server-gated, so a client never updates its own store. Without this, a client keeps computing the
    // data-device-list merge with the DEFAULT mode, so its motherboard dropdowns / IC-housing caches
    // diverge from the host (the bug this fixes for connected clients). The host broadcasts this on
    // every actual mode change; the full current state for a fresh joiner is shipped separately by the
    // plugin's IJoinSuffixSerializer. Registered in Plugin.OnPrefabsLoaded via MOD.Networking.RegisterMessage.
    // See Research/Protocols/LaunchPadBoosterNetworking.md.
    public class PassthroughModeMessage : INetworkMessage
    {
        public long DeviceId;
        public int Mode;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(DeviceId);
            writer.WriteInt32(Mode);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            DeviceId = reader.ReadInt64();
            Mode = reader.ReadInt32();
        }

        public void Process(long hostId)
        {
            // The server is authoritative; ignore any echo received as the host.
            if (NetworkManager.IsServer) return;

            PassthroughModeStore.SetModeByReference(DeviceId, Mode);

            // Re-run the merge + consumer refresh locally on this client so its motherboard dropdowns /
            // IC-housing caches pick up the change. DirtyBridgeNetworks marks the merge stale;
            // ScheduleCascadeForDevice fires the consumer cascade for this device's component.
            if (Thing.Find<Thing>(DeviceId) is Device device)
            {
                PassthroughTopology.DirtyBridgeNetworks(device);
                CableNetworkPatches.ScheduleCascadeForDevice(device);
            }
        }
    }
}
