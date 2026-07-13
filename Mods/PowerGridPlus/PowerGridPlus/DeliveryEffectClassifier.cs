using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;

namespace PowerGridPlus
{
    /// <summary>
    ///     Classifies the delivery-effect consumers: plain devices whose real gameplay effect runs
    ///     INSIDE <c>ReceivePower</c> rather than in their own tick. The atomic write-back retired
    ///     vanilla ConsumePower (which called ReceivePower per provider drained), so these classes
    ///     get their granted power re-delivered by the Core/WriteBack shim. The built-in set is
    ///     exactly the five vanilla classes with a gameplay-bearing override:
    ///
    ///     <list type="bullet">
    ///       <item>PowerTransmitterOmni: forwards received power to wireless consumers.</item>
    ///       <item>SuitStorage: recharges the stored suit / helmet / backpack cells.</item>
    ///       <item>BatteryCellCharger: charges the docked battery cells.</item>
    ///       <item>Bench: forwards received power to its powered appliances.</item>
    ///       <item>WallLightBattery: the grid-fed latch (WasPoweredByCableLastTick) plus the
    ///       internal cell top-up; the emergency-light toggle reads that latch.</item>
    ///     </list>
    ///
    ///     Type checks (<c>is X</c>) so third-party subclasses of the five inherit the behavior.
    ///     The Extra Delivery Devices setting extends the set by PrefabName for modded classes the
    ///     ReceivePower override census names (ReceivePowerOverrideCensus). Deliberately NOT here:
    ///     the fabricator family (their accumulator reset belongs to the debit queue, Core/
    ///     DemandModel) and stores / segmenters (Core/WriteBack owns their ledgers).
    ///
    ///     <para>Threading: the classifier runs on the power worker (GridSnapshot.Build); the
    ///     config set is parsed once per config change (the VoltageTier.RefreshConfig pattern:
    ///     the set is replaced wholesale, never mutated after publish).</para>
    /// </summary>
    internal static class DeliveryEffectClassifier
    {
        private static HashSet<string> _extraDeliveryDevices;

        /// <summary>Drop and re-parse the configured extra prefab set (wired to SettingChanged).</summary>
        internal static void RefreshConfig()
        {
            var raw = Settings.ExtraDeliveryDevices?.Value ?? string.Empty;
            _extraDeliveryDevices = new HashSet<string>(
                raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static HashSet<string> ExtraDeliveryDevices
        {
            get
            {
                if (_extraDeliveryDevices == null)
                    RefreshConfig();
                return _extraDeliveryDevices;
            }
        }

        /// <summary>True when the write-back shim should deliver this device's granted power.</summary>
        internal static bool IsDeliveryEffect(Device device)
        {
            if (device == null)
                return false;

            if (device is PowerTransmitterOmni
                || device is SuitStorage
                || device is BatteryCellCharger
                || device is Bench
                || device is WallLightBattery)
                return true;

            var prefabName = device.PrefabName;
            return !string.IsNullOrEmpty(prefabName) && ExtraDeliveryDevices.Contains(prefabName);
        }

        /// <summary>
        ///     True when <paramref name="type"/> is one of the built-in five (or a subclass), i.e.
        ///     already covered by the shim's type checks. Used by the ReceivePower override census
        ///     so a subclass of a built-in is not misreported as outside the shim.
        /// </summary>
        internal static bool IsBuiltInDeliveryType(Type type)
        {
            return type != null
                   && (typeof(PowerTransmitterOmni).IsAssignableFrom(type)
                       || typeof(SuitStorage).IsAssignableFrom(type)
                       || typeof(BatteryCellCharger).IsAssignableFrom(type)
                       || typeof(Bench).IsAssignableFrom(type)
                       || typeof(WallLightBattery).IsAssignableFrom(type));
        }
    }
}
