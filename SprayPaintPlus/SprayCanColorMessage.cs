using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using LaunchPadBooster.Networking;

namespace SprayPaintPlus
{
    /// <summary>
    /// Client -> Server message: player scrolled to change a spray can's color.
    /// </summary>
    public class SprayCanColorMessage : ModNetworkMessage<SprayCanColorMessage>
    {
        public long SprayCanId;
        public int ColorIndex;

        public override void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(SprayCanId);
            writer.WriteInt32(ColorIndex);
        }

        public override void Deserialize(RocketBinaryReader reader)
        {
            SprayCanId = reader.ReadInt64();
            ColorIndex = reader.ReadInt32();
        }

        public override void Process(long hostId)
        {
            // Validate ColorIndex at the trust boundary
            int maxColors = GameManager.Instance?.CustomColors?.Count ?? 0;
            if (maxColors == 0)
                return;
            if (ColorIndex < 0 || ColorIndex >= maxColors)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Invalid ColorIndex {ColorIndex} from host {hostId} (valid: 0-{maxColors - 1}), ignoring");
                return;
            }

            var thing = Thing.Find(SprayCanId);
            if (thing is SprayCan sprayCan)
            {
                SprayPaintHelpers.UpdateSprayCanServer(sprayCan, ColorIndex);
                SprayPaintPlusPlugin.Log.LogDebug(
                    $"Color change from host {hostId}: can {SprayCanId} -> color {ColorIndex}");
            }
            else
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Color change from host {hostId}: could not find SprayCan {SprayCanId}");
            }
        }
    }
}
