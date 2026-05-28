using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Assets.Scripts.Networks;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // A linked PowerTransmitter / PowerReceiver dish pair bridges logic only while linked, but vanilla
    // never dirties the cable data device lists when a link forms or breaks (the link is power state, not
    // data). So a motherboard or IC housing on either dish's cable network never refreshes its dropdown
    // when auto-aim (or manual aim) links / unlinks / retargets the pair. We re-fire the refresh.
    //
    // Where the link changes, per the decompile (Assembly-CSharp 0.2.6228.27061):
    //   - Host: PowerTransmitter.TryContactReceiver sets LinkedReceiver (gated on GameManager.RunSimulation,
    //     so host-only) and mirrors LinkedReceiver.LinkedPowerTransmitter = this.
    //   - Client: PowerTransmitter.ProcessUpdate applies the replicated LinkedReceiver ReferenceId
    //     (NetworkUpdateType.Thing.WirelessPower.Receiver). The RX back-reference does NOT replicate, so
    //     on a client only the TX-cable-side merge sees across the link (documented limitation).
    //
    // PowerTransmitterPlus compatibility: its TryContactReceiverPatch is a PREFIX that returns false to
    // replace the link probe; our POSTFIX still runs and reads the final LinkedReceiver. Its auto-aim
    // ProcessUpdate postfix is on WirelessPower.ProcessUpdate (the base) and appends bytes between the
    // base read and the PowerTransmitter override read; our PowerTransmitter.ProcessUpdate postfix reads
    // nothing from the stream (it only inspects LinkedReceiver), so wire order is undisturbed either way.
    [HarmonyPatch]
    public static class DishLinkPatches
    {
        private sealed class LinkHolder { public PowerReceiver Value; }

        // Last LinkedReceiver we acted on, per transmitter. ConditionalWeakTable so entries vanish when a
        // dish is GC'd. TryContactReceiver re-runs every animation frame on the host even on a stable
        // link, so the change check keeps this a cheap no-op except on an actual link transition.
        private static readonly ConditionalWeakTable<PowerTransmitter, LinkHolder> _lastLink =
            new ConditionalWeakTable<PowerTransmitter, LinkHolder>();

        [HarmonyPostfix, HarmonyPatch(typeof(PowerTransmitter), "TryContactReceiver")]
        public static void TryContactReceiverPostfix(PowerTransmitter __instance) => RefreshLink(__instance);

        [HarmonyPostfix, HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.ProcessUpdate))]
        public static void ProcessUpdatePostfix(PowerTransmitter __instance) => RefreshLink(__instance);

        private static void RefreshLink(PowerTransmitter tx)
        {
            if (tx == null) return;
            var holder = _lastLink.GetOrCreateValue(tx);
            var current = tx.LinkedReceiver;
            if (holder.Value == current) return;
            var previous = holder.Value;
            holder.Value = current;

            // Client-side back-reference mirror. Vanilla replicates tx.LinkedReceiver to clients but NOT
            // rx.LinkedPowerTransmitter (the RX setter sets no NetworkUpdateFlag; it is assigned only
            // host-side in TryContactReceiver, which is RunSimulation-gated). Without the back-reference
            // the passthrough merge is one-directional on a client: the RX-cable side cannot reach the
            // TX-cable side because GetOtherSide(RX) reads rx.LinkedPowerTransmitter. Mirror it so the
            // client matches the host. PowerTransmitterPlus does not set or rely on this on clients, and
            // the write is the same value either mod would write, so the two compose idempotently (the
            // != tx guard skips when already set). Ownership note + how both mods compose: see the
            // PowerTransmitterPlus TODO ("client-side LinkedPowerTransmitter mirror").
            //
            // MP-state-mutation audit (Research/Patterns/MultiplayerStateMutation.md): (1) targets the RX
            // LinkedPowerTransmitter setter; (2) vanilla invokes it host-only, so this runs only on the
            // client; (3) mutates rx.LinkedPowerTransmitter + rx.InputNetwork (the setter assigns
            // InputNetwork = tx.OutputNetwork); (4) gated to the client; (5) the value is deterministic --
            // exactly the host state derived from the already-replicated tx.LinkedReceiver, so it cannot
            // diverge; (6) propagation: none needed, it is a client-local reconstruction of host state.
            // The setter's own OnServer.Interact is RunSimulation-gated and skipped on the client.
            if (!NetworkManager.IsServer)
            {
                if (previous != null && previous != current && previous.LinkedPowerTransmitter == tx)
                    previous.LinkedPowerTransmitter = null;
                if (current != null && current.LinkedPowerTransmitter != tx)
                    current.LinkedPowerTransmitter = tx;
            }

            // Refresh the TX's cable side and both the old and new receivers' cable sides, so a link, an
            // unlink, and a retarget-to-a-different-receiver all refresh the correct partner networks.
            var nets = new List<CableNetwork>(3);
            AddNet(nets, tx.InputNetwork);
            if (previous != null) AddNet(nets, previous.OutputNetwork);
            if (current != null) AddNet(nets, current.OutputNetwork);
            if (nets.Count == 0) return;

            // Dirty marks the merge stale (each dirty also fires the propagation postfix, which cascades
            // the transitive reachable set); ScheduleCascade refreshes the immediate networks' consumers.
            for (int i = 0; i < nets.Count; i++)
                nets[i].DirtyPowerAndDataDeviceLists();
            CableNetworkPatches.ScheduleCascade(nets);
        }

        private static void AddNet(List<CableNetwork> list, CableNetwork net)
        {
            if (net != null && !list.Contains(net)) list.Add(net);
        }
    }
}
