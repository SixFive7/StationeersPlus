using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace PowerGridPlus
{
    /// <summary>
    ///     Server-side OFF-as-reset (POWER.md §10.3): every tick, any device currently in a fault
    ///     lockout that the player has switched OFF gets every lockout cleared, so toggling a faulted
    ///     device OFF clears its fault within one tick regardless of peer topology. "Switched off"
    ///     means the device actually HAS an on/off control and it is currently off
    ///     (<c>HasOnOffState &amp;&amp; !OnOff</c>). A buttonless device reports <c>OnOff == false</c>
    ///     permanently (the absence of an on/off concept, not an OFF gesture), so it is NOT swept; this
    ///     keeps a buttonless producer's VARIABLE_VOLTAGE_FAULT counting down on its own timer instead
    ///     of being cleared-and-re-noted every tick (which would freeze the hover countdown).
    ///
    ///     <para>The Power Connector is the one special case: it is a buttonless dock that forwards a
    ///     docked portable generator's power, so the real on/off gesture lives on the docked generator.
    ///     It is reset-eligible exactly when it is NOT delivering (no generator docked, or the docked
    ///     generator is off / out of fuel), via <see cref="ProducerClassifier.ConnectorIsDelivering"/>.</para>
    ///
    ///     <para>Network-level retry (POWER.md §8.5, mirrors the elastic-overload commit §8.4.1): when
    ///     this sweep clears a producer that was in VARIABLE_VOLTAGE_FAULT (the toggle edge -- it was
    ///     locked, now switched off), it flags that producer's cable network via
    ///     <see cref="VariableVoltageFaultDetector.RequestRetry"/>. The detector then re-evaluates the
    ///     WHOLE producer cohort on that net this tick, so the buttonless producers on it (which have
    ///     no button of their own) clear too and either resolve (if the wiring is now fixed) or
    ///     re-fault on one shared synced timer. Toggling a buttoned producer off is therefore a
    ///     network-level retry for every producer sharing its network.</para>
    ///
    ///     <para>The client-side SwitchOnOffShedPatches clear is visual-only (it runs in the rendering
    ///     path, which a headless dedicated server never executes); this sweep is the authoritative
    ///     clear. When the player toggles back ON, the next allocator / detector pass re-decides; a
    ///     persisting condition re-fires the lockout instantly.</para>
    ///
    ///     <para>Threading: runs on the power-tick worker (AtomicElectricityTickPatch Phase 1), so this
    ///     stays managed-memory only -- the connector presence check is a plain reference test, never
    ///     the Unity <c>(bool)</c>/<c>==null</c> operator. Cost: bounded by the number of currently-
    ///     locked devices (usually zero); Thing.Find is a dictionary lookup.</para>
    /// </summary>
    internal static class OffAsResetSweep
    {
        private static readonly List<long> _scratch = new List<long>();

        internal static void Run(int currentTick)
        {
            _scratch.Clear();
            foreach (var id in BrownoutRegistry.CurrentlyLockedOut(currentTick)) _scratch.Add(id);
            foreach (var id in OverloadRegistry.CurrentlyLockedOut(currentTick)) _scratch.Add(id);
            foreach (var id in CycleFaultRegistry.CurrentlyLockedOut(currentTick)) _scratch.Add(id);
            foreach (var id in VariableVoltageFaultRegistry.CurrentlyLockedOut(currentTick)) _scratch.Add(id);
            if (_scratch.Count == 0) return;

            for (int i = 0; i < _scratch.Count; i++)
            {
                long refId = _scratch[i];
                if (!(Thing.Find(refId) is Device device)) continue;
                if (!ResetEligible(device)) continue;

                // Clearing a VVF lock here is the toggle edge (the device was locked, now switched
                // off / not delivering); flag its network so the detector runs a cohort-wide retry.
                bool wasVvfLocked = VariableVoltageFaultRegistry.IsLockedOut(refId, currentTick);

                BrownoutRegistry.ClearLockout(refId);
                OverloadRegistry.ClearLockout(refId);
                CycleFaultRegistry.ClearLockout(refId);
                VariableVoltageFaultRegistry.ClearLockout(refId);

                if (wasVvfLocked)
                {
                    var net = device.PowerCable?.CableNetwork;
                    if (net != null) VariableVoltageFaultDetector.RequestRetry(net.ReferenceId);
                }
            }
        }

        // OFF-as-reset eligibility: a device is reset only when the player has actually switched it
        // off. For most devices that means it has an on/off control and that control is off. The
        // Power Connector has no control of its own; the docked portable generator carries it, so the
        // connector is reset when it is not delivering power (nothing docked, or the generator off).
        private static bool ResetEligible(Device device)
        {
            if (device is PowerConnector connector)
                return !ProducerClassifier.ConnectorIsDelivering(connector);

            return device.HasOnOffState && !device.OnOff;
        }
    }
}
