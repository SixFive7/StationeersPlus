using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Objects.Rockets;

namespace PowerGridPlus
{
    // Per-Thing LogicPassthroughMode state. Keyed by Thing.ReferenceId, value is
    // 0 or 1. Used for Transformer, Battery, AreaPowerControl, PowerTransmitter,
    // PowerReceiver, and both halves of the rocket power umbilical.
    //
    // Defaults for a device with no explicit entry come from the six server-authoritative
    // per-kind Passthrough Default settings (host values, synced to clients via
    // PassthroughDefaultsSync):
    //   - Small Transformer + Reversed + Rocket Small: Small Transformer Passthrough Default.
    //   - Other Transformer variants:                  Other Transformer Passthrough Default.
    //   - Battery:                                     Battery Passthrough Default.
    //   - AreaPowerControl:                            APC Passthrough Default.
    //   - PowerTransmitter / PowerReceiver:            Power Transmitter Passthrough Default.
    //   - Rocket power umbilical (Male / Female):      Umbilical Passthrough Default.
    //   - Everything else:                             0.
    // Once a device's mode is written (IC10, logic writer, or restore from the side-car),
    // the stored per-device value wins over the default.
    //
    // Persistence: PassthroughSideCar reads / writes a sidecar XML inside the save
    // ZIP. PassthroughSaveLoadPatches restores state in Thing.OnFinishedLoad for
    // every supported type.
    internal static class PassthroughModeStore
    {
        private static readonly ConcurrentDictionary<long, int> _byReference =
            new ConcurrentDictionary<long, int>();

        internal static int GetMode(Thing thing)
        {
            if (thing == null) return 0;
            if (_byReference.TryGetValue(thing.ReferenceId, out var mode))
                return mode;
            return GetDefaultMode(thing);
        }

        internal static void SetMode(Thing thing, int mode)
        {
            if (thing == null) return;
            _byReference[thing.ReferenceId] = mode != 0 ? 1 : 0;
        }

        internal static int GetDefaultMode(Thing thing)
        {
            if (thing == null) return 0;
            switch (thing.PrefabName)
            {
                case "StructureTransformerSmall":
                case "StructureTransformerSmallReversed":
                case "StructureRocketTransformerSmall":
                    return PassthroughDefaultsSync.EffectiveSmallTransformer ? 1 : 0;
            }
            if (thing is Transformer) return PassthroughDefaultsSync.EffectiveOtherTransformer ? 1 : 0;
            if (thing is Battery) return PassthroughDefaultsSync.EffectiveBattery ? 1 : 0;
            if (thing is AreaPowerControl) return PassthroughDefaultsSync.EffectiveApc ? 1 : 0;
            if (thing is PowerTransmitter) return PassthroughDefaultsSync.EffectivePowerTransmitter ? 1 : 0;
            if (thing is PowerReceiver) return PassthroughDefaultsSync.EffectivePowerTransmitter ? 1 : 0;
            if (thing is RocketPowerUmbilicalMale) return PassthroughDefaultsSync.EffectiveUmbilical ? 1 : 0;
            if (thing is RocketPowerUmbilicalFemale) return PassthroughDefaultsSync.EffectiveUmbilical ? 1 : 0;
            return 0;
        }

        internal static IEnumerable<KeyValuePair<long, int>> SnapshotEntries()
        {
            foreach (var pair in _byReference) yield return pair;
        }

        // Set the mode by ReferenceId. Used by the save-load side-car restore, the
        // join-time snapshot, and the live PassthroughModeMessage from the host.
        internal static void SetModeByReference(long referenceId, int mode)
        {
            _byReference[referenceId] = mode != 0 ? 1 : 0;
        }

        internal static void RestoreFromSideCar(long referenceId, int mode)
        {
            SetModeByReference(referenceId, mode);
        }
    }
}
