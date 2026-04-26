using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server-authoritative auto-aim cache sync.
    //
    // Vanilla persists RotatableBehaviour.TargetHorizontal / TargetVertical so
    // a dish reloads pointing at its target, but the per-dish auto-aim target
    // ReferenceId lives only in AutoAimState's in-process cache. On a save
    // load (host) and on a remote join (client), that cache is empty even
    // though the dish is still effectively auto-aimed and the link forms.
    //
    // - Save load on the host: AutoAimSideCar restores the cache from a
    //   side-car XML file written into the save ZIP.
    // - Remote join on a client: AutoAimSnapshotMessage is broadcast from
    //   the host on every PlayerConnected event, carrying every live
    //   (dish, target) pair. Receiving clients call RestoreCache for each.
    //
    // Re-broadcasts to ALL clients (not just the new joiner) on every connect,
    // mirroring DistanceConfigSync / BeamVisualConfigSync. The cost is one
    // int + 16 bytes per active auto-aim per existing client per join,
    // negligible.
    internal static class AutoAimSnapshotSync
    {
        internal static void BroadcastIfHost()
        {
            if (!NetworkManager.IsServer) return;
            if (!PowerTransmitterPlusPlugin.AutoAimPatched) return;

            var msg = new AutoAimSnapshotMessage();
            foreach (var pair in AutoAimState.SnapshotEntries())
            {
                msg.DishIds.Add(pair.Key);
                msg.TargetIds.Add(pair.Value);
            }
            msg.SendAll(0L);
            PowerTransmitterPlusPlugin.Log?.LogDebug(
                $"Broadcast auto-aim cache to clients: {msg.DishIds.Count} entries");
        }

        internal static void OnSnapshotReceived(AutoAimSnapshotMessage msg)
        {
            if (msg == null || msg.DishIds == null || msg.TargetIds == null) return;
            int applied = 0, missing = 0;
            int n = msg.DishIds.Count;
            for (int i = 0; i < n; i++)
            {
                var dish = Thing.Find(msg.DishIds[i]) as WirelessPower;
                if (dish == null) { missing++; continue; }
                AutoAimState.RestoreCache(dish, msg.TargetIds[i]);
                applied++;
            }
            PowerTransmitterPlusPlugin.Log?.LogInfo(
                $"Received auto-aim cache from host: {applied} applied, {missing} dishes not found locally");
        }
    }
}
