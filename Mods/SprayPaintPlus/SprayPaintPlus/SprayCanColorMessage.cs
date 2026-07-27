using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using LaunchPadBooster.Networking;

namespace SprayPaintPlus
{
    /// <summary>
    /// Client -> Server message: player changed a spray can's color, either by scrolling
    /// the wheel or by eyedropping a painted object.
    /// </summary>
    public class SprayCanColorMessage : INetworkMessage
    {
        public long SprayCanId;
        public int ColorIndex;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteInt64(SprayCanId);
            writer.WriteInt32(ColorIndex);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            SprayCanId = reader.ReadInt64();
            ColorIndex = reader.ReadInt32();
        }

        /// <summary>
        /// Runs on the server. Every gate below is a re-check of something the sending
        /// client already applied locally, so a well-behaved client never trips any of
        /// them; the point is that a modified one cannot push a color past them.
        ///
        /// Every reject path re-asserts the can's CURRENT server-side color rather than
        /// simply returning. The client applies its color change optimistically before the
        /// message goes out, so a silent reject would leave that client showing a phantom
        /// color the server never accepted, forever, with nothing to correct it.
        /// UpdateSprayCanServer has no same-index guard, so re-asserting an unchanged value
        /// still sets the network flag and still broadcasts. Do not add such a guard: it is
        /// exactly what makes the snap-back work.
        /// </summary>
        public void Process(long hostId)
        {
            // Readiness, not validation: with no swatches loaded there is no color to
            // reject against and nothing sensible to snap the sender back to either.
            int maxColors = GameManager.Instance?.CustomColors?.Count ?? 0;
            if (maxColors == 0)
                return;

            // Resolved before the gates below, not after, because the reject paths need the
            // can in order to snap the sender back.
            var thing = Thing.Find(SprayCanId);
            if (!(thing is SprayCan sprayCan))
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Color change from host {hostId}: could not find SprayCan {SprayCanId}");
                return;
            }

            if (ColorIndex < 0 || ColorIndex >= maxColors)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Invalid ColorIndex {ColorIndex} from host {hostId} (valid: 0-{maxColors - 1}), ignoring");
                Reassert(sprayCan);
                return;
            }

            // Entitlement check at the trust boundary, and it comes first: it is the hard
            // gate and no mod setting may loosen it.
            // IsColorAllowed, never IsColorInCycle: the cycle filter is the sender's own
            // client-local preference and is none of the server's business.
            if (!DlcPaintGate.IsColorAllowed(ColorIndex))
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Rejected ColorIndex {ColorIndex} from host {hostId}: requires a DLC that is not " +
                    "available in this session, ignoring");
                Reassert(sprayCan);
                return;
            }

            // The server's own color cycling half, on top of entitlement. The SERVER half
            // alone, never SettingsMerge.EffectiveColorCycling: the merged value folds in
            // the LOCAL machine's client half, which on a host is the host player's own
            // preference and has nothing to do with what the sender is allowed to do. Same
            // reasoning as IsColorAllowed over IsColorInCycle just above. Reading the entry
            // directly is safe here because Process only ever runs on the authority, where
            // SettingsMerge would resolve the server half to this very value.
            ColorCyclingMode serverMode =
                SprayPaintPlusPlugin.ServerColorCycling?.Value ?? ColorCyclingMode.AllColors;

            int currentIndex = SprayPaintHelpers.GetSprayCanColorIndex(sprayCan);

            if (serverMode == ColorCyclingMode.CannotChange)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Rejected ColorIndex {ColorIndex} from host {hostId}: this server does not allow " +
                    "spray cans to change color, ignoring");
                Reassert(sprayCan);
                return;
            }

            if (serverMode == ColorCyclingMode.WithinFamily
                && !DlcPaintGate.SameFamily(currentIndex, ColorIndex))
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Rejected ColorIndex {ColorIndex} from host {hostId}: this server limits cycling to " +
                    $"one paint family and can {SprayCanId} currently carries color {currentIndex}, ignoring");
                Reassert(sprayCan);
                return;
            }

            SprayPaintHelpers.UpdateSprayCanServer(sprayCan, ColorIndex);
            SprayPaintPlusPlugin.Log.LogDebug(
                $"Color change from host {hostId}: can {SprayCanId} -> color {ColorIndex}");
        }

        /// <summary>
        /// Re-broadcasts the can's current server-side color so the sender's optimistic
        /// apply snaps back instead of leaving a phantom color on that one client.
        /// </summary>
        private static void Reassert(SprayCan sprayCan)
        {
            SprayPaintHelpers.UpdateSprayCanServer(
                sprayCan, SprayPaintHelpers.GetSprayCanColorIndex(sprayCan));
        }
    }
}
