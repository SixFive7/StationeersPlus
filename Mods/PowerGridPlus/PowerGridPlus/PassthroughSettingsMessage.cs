using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Host -> client replication of the four Enable*LogicPassthrough server settings, so a client's merge
    // (PassthroughTopology) uses the host's authoritative values rather than its own local BepInEx config.
    // The toggles take effect live (not RequireRestart), so the host broadcasts on every change; the full
    // current state for a fresh joiner rides the plugin's IJoinSuffixSerializer. Registered in
    // Plugin.OnPrefabsLoaded. See Research/Protocols/LaunchPadBoosterNetworking.md.
    public class PassthroughSettingsMessage : INetworkMessage
    {
        public bool Transformer;
        public bool Battery;
        public bool Apc;
        public bool PowerTransmitter;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteBoolean(Transformer);
            writer.WriteBoolean(Battery);
            writer.WriteBoolean(Apc);
            writer.WriteBoolean(PowerTransmitter);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Transformer = reader.ReadBoolean();
            Battery = reader.ReadBoolean();
            Apc = reader.ReadBoolean();
            PowerTransmitter = reader.ReadBoolean();
        }

        public void Process(long hostId)
        {
            if (NetworkManager.IsServer) return;
            PassthroughSettingsSync.ApplyFromHost(Transformer, Battery, Apc, PowerTransmitter);
        }
    }
}
