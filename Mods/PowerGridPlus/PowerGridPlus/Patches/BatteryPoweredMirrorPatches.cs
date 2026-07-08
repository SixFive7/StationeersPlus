using Assets.Scripts;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Client-side mirror for <c>Battery.Powered</c>. Vanilla <see cref="Battery"/> overrides
    ///     <c>Powered</c> to read its private <c>_batteryState</c> field, which is recomputed only
    ///     inside the host-only power tick (<c>Battery.OnPowerTick</c>, reachable only while
    ///     <c>GameManager.RunSimulation</c> is true, i.e. <c>!NetworkManager.IsClient</c>). On a
    ///     connected client <c>_batteryState</c> therefore stays at its default <c>Empty</c> and
    ///     every battery reads <c>Powered == false</c> even while the host reads <c>true</c>
    ///     (confirmed live 2026-07-08: 23 operating batteries read false on the client, true on the
    ///     host). Transformers, APCs, and transmitters do not override <c>Powered</c>; they inherit
    ///     the interactable-backed base property, which replicates, so they are unaffected. This is
    ///     vanilla behavior, not introduced by PowerGridPlus, which never sets battery Powered.
    ///
    ///     <para>The host keeps the base <c>InteractPowered</c> interactable synced to its own
    ///     Powered value (<c>OnServer.Interact(InteractPowered, Powered ? 1 : 0)</c> from
    ///     <c>Battery.CheckPower</c>), and that interactable replicates like any other device state,
    ///     so the correct value is already present on the client, just not exposed through the
    ///     overridden getter. On the client only, this postfix reads Powered back off the replicated
    ///     <c>InteractPowered.State</c> so a player and any client-side observation (tooltips, the
    ///     battery's own screen, InspectorPlus, a client-held IC10 read) see batteries powered
    ///     consistently with the host. Verified live: operating batteries read
    ///     <c>InteractPowered.State == 1</c> on the client while the vanilla getter returned false;
    ///     a genuinely empty battery read State 0.</para>
    ///
    ///     <para>Host and single-player keep the exact vanilla value: the <c>RunSimulation</c> guard
    ///     returns before touching <c>__result</c>, so the allocator, the Stage 3 healthy-set
    ///     reconcile, and every host-side power decision are untouched. Gameplay is unaffected either
    ///     way, IC10 chips execute host-side under <c>RunSimulation</c> and so always read the
    ///     authoritative host value; this closes only the client-side display gap, consistent with
    ///     the Stage 3 principle that a healthy powered device reads the same on both peers. Zero
    ///     added network traffic (the source is already-replicated interactable state).</para>
    ///
    ///     <para>Covers <see cref="Battery"/> and every subclass that does not itself override
    ///     <c>Powered</c> (the inherited getter dispatches to this patched method), including the
    ///     station, medium, and third-party nuclear batteries observed in the test.</para>
    /// </summary>
    [HarmonyPatch(typeof(Battery), nameof(Battery.Powered), MethodType.Getter)]
    public static class BatteryPoweredClientMirrorPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Battery __instance, ref bool __result)
        {
            if (GameManager.RunSimulation) return;   // host / single-player: vanilla value is authoritative
            var powered = __instance.InteractPowered;
            if (powered != null) __result = powered.State >= 1;
        }
    }
}
