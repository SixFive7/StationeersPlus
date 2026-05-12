using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace NetworkPuristPlus
{
    // Server -> client message carrying the host's authoritative Network Purist Plus settings.
    //
    // The JoinValidator (Plugin.cs) already guarantees a connected client's settings match the host's,
    // so this message is informational on the join path (a client adopts values it already had). Its
    // real job is the live-change case: the host changes a toggle in the in-game settings panel while a
    // client is connected, and the client's SettingsConfigSync mirror picks up the new value for its
    // own logging / diagnostics. (The prefab-time effects -- the build-kit strip, the Stationpedia hide,
    // the build-cursor AutomaticSetup -- happen at Prefab.OnPrefabsLoaded, before any join, so a live
    // change does not retroactively reshape an already-loaded session on either side; a restart is
    // needed for that. The world-rebuild on load is host-authoritative regardless.)
    //
    // Process() runs on the receiving side; on a client receiving from the host, hostId is the host's
    // connection ID. Field order MUST match between Serialize and Deserialize. Wire-format additions go
    // at the END to keep the read order stable -- RemoveLongInsulatedLiquidPipes (the 8th bool) was
    // appended last in v1.1, after AlignStraightCables, even though it groups logically with the other
    // pipe toggles. (The v1.1-polish rename of RemoveLongPipes -> RemoveLongGasPipes and
    // RemoveLongInsulatedPipes -> RemoveLongInsulatedGasPipes did NOT move any position -- the renamed
    // entries keep their slots in the read order; only the names changed.)
    public class SettingsConfigMessage : INetworkMessage
    {
        public bool Enabled;
        public bool RemoveLongGasPipes;              // the basic pipe ("Gas Pipe") -- StructurePipeStraight*
        public bool RemoveLongLiquidPipes;
        public bool RemoveLongInsulatedGasPipes;     // insulated gas pipe only ("Insulated Gas Pipe") -- StructureInsulatedPipeStraight*
        public bool RemoveLongChutes;
        public bool RemoveLongSuperHeavyCables;
        public bool AlignStraightCables;
        public bool RemoveLongInsulatedLiquidPipes;  // StructureInsulatedPipeLiquidStraight* -- appended at the END (8th bool)

        // Build a message from the local config (host side).
        internal static SettingsConfigMessage FromLocalConfig() => new SettingsConfigMessage
        {
            Enabled = Settings.Enabled?.Value ?? true,
            RemoveLongGasPipes = Settings.RemoveLongGasPipes?.Value ?? true,
            RemoveLongLiquidPipes = Settings.RemoveLongLiquidPipes?.Value ?? true,
            RemoveLongInsulatedGasPipes = Settings.RemoveLongInsulatedGasPipes?.Value ?? true,
            RemoveLongChutes = Settings.RemoveLongChutes?.Value ?? true,
            RemoveLongSuperHeavyCables = Settings.RemoveLongSuperHeavyCables?.Value ?? true,
            AlignStraightCables = Settings.AlignStraightCables?.Value ?? true,
            RemoveLongInsulatedLiquidPipes = Settings.RemoveLongInsulatedLiquidPipes?.Value ?? true,
        };

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteBoolean(Enabled);
            writer.WriteBoolean(RemoveLongGasPipes);
            writer.WriteBoolean(RemoveLongLiquidPipes);
            writer.WriteBoolean(RemoveLongInsulatedGasPipes);
            writer.WriteBoolean(RemoveLongChutes);
            writer.WriteBoolean(RemoveLongSuperHeavyCables);
            writer.WriteBoolean(AlignStraightCables);
            writer.WriteBoolean(RemoveLongInsulatedLiquidPipes);   // 8th bool -- appended at the END
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Enabled = reader.ReadBoolean();
            RemoveLongGasPipes = reader.ReadBoolean();
            RemoveLongLiquidPipes = reader.ReadBoolean();
            RemoveLongInsulatedGasPipes = reader.ReadBoolean();
            RemoveLongChutes = reader.ReadBoolean();
            RemoveLongSuperHeavyCables = reader.ReadBoolean();
            AlignStraightCables = reader.ReadBoolean();
            RemoveLongInsulatedLiquidPipes = reader.ReadBoolean();   // 8th bool -- appended at the END
        }

        public void Process(long hostId)
        {
            // Only the client adopts this. A malformed echo received as the server is ignored; the
            // server's own config is the authoritative source.
            if (NetworkManager.IsServer) return;
            SettingsConfigSync.OnHostConfigReceived(this);
        }
    }
}
