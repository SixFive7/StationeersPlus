using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;

namespace PowerTransmitterPlus
{
    // React to a dish on/off switch flip on every peer (server, single-player
    // host, and remote clients) by re-evaluating beam visibility.
    //
    // Hook choice: Thing.OnInteractableUpdated is the convergence point for
    // every interactable state change. The Interactable.State setter (and
    // therefore this callback) fires unconditionally on every State write,
    // but per-tick churn is prevented by caller-side gating in the game's
    // own code (the power tick only calls OnServer.Interact(InteractPowered,
    // ...) inside if (Powered) / if (!Powered) guards), so this postfix fires
    // only on actual transitions, never per tick. See
    // Research/GameClasses/Interactable.md for the full propagation map.
    //
    // The callback fires on remote clients too via the interactable-state
    // replication path (Thing.ProcessInteractableUpdate -> Interact(state,
    // skipAnimation: false) -> State setter -> OnInteractableUpdated), so a
    // remote client's beam updates promptly when the host or another player
    // toggles a dish.
    //
    // Patching the base Thing virtual catches every subclass because every
    // override in the chain (Device, PowerReceiver, PowerTransmitter) calls
    // base.OnInteractableUpdated(interactable) first. The Action filter is
    // mandatory: Powered, Mode, every button, every slot all enter the same
    // callback on their own changes.
    //
    // Background: this is the missing piece behind the v1.5.1 beam-stays-lit-
    // when-off bug. Before v1.5.1 the beam followed VisualizerIntensity,
    // which vanilla zeroes on OnOff = false, so an off dish naturally hid
    // its beam. v1.5.1 moved show / hide to LinkedReceiver, which vanilla
    // does NOT null on OnOff = false, so the beam needs an explicit OnOff
    // gate. This patch provides it.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnInteractableUpdated))]
    public static class ThingOnInteractableUpdatedPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, Interactable interactable)
        {
            if (interactable == null) return;
            if (interactable.Action != InteractableType.OnOff) return;

            // Most Things in the world are not dishes; bail before any cast
            // work for the common case (lights, fabricators, switches, doors,
            // etc. all hit this filter constantly).
            if (!(__instance is WirelessPower)) return;

            PowerTransmitter tx;
            if (__instance is PowerTransmitter t) tx = t;
            else if (__instance is PowerReceiver r) tx = r.LinkedPowerTransmitter;
            else return;

            // An unlinked receiver toggled: no transmitter to update.
            if (tx == null) return;

            BeamManager.ReevaluateVisibility(tx, "OnOff");
        }
    }
}
