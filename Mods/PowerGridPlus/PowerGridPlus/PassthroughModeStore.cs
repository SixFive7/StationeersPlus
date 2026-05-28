using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    // Per-Thing LogicPassthroughMode state. Keyed by Thing.ReferenceId, value is
    // 0 or 1. Used for Transformer, Battery, PowerTransmitter, and PowerReceiver.
    //
    // Per-PrefabName defaults:
    //   - Small Transformer + Reversed: 1 (logic-transparent out of the box).
    //   - Other Transformer variants:    0 (vanilla-opaque).
    // Per-type defaults (anything without a specific PrefabName override):
    //   - Battery:          1 (logic-transparent across input / output cable ports).
    //   - PowerTransmitter: 1 (logic-transparent across the wireless link to its receiver).
    //   - PowerReceiver:    1 (logic-transparent across the wireless link to its transmitter).
    //   - Everything else:  0.
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
                    return 1;
            }
            if (thing is Battery) return 1;
            if (thing is PowerTransmitter) return 1;
            if (thing is PowerReceiver) return 1;
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
