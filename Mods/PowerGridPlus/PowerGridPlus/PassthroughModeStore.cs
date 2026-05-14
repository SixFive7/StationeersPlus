using System.Collections.Concurrent;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    // Per-Transformer LogicPassthroughMode state. Keyed by Transformer.ReferenceId,
    // value is 0 or 1. Defaults by PrefabName: 1 for the small transformer + its
    // reversed variant (so they ship as logic-transparent out of the box), 0 for
    // every other transformer prefab.
    //
    // Persistence: PassthroughSideCar reads / writes a sidecar XML inside the save
    // ZIP. PassthroughSaveLoadPatches restores state in Thing.OnFinishedLoad.
    internal static class PassthroughModeStore
    {
        private static readonly ConcurrentDictionary<long, int> _byReference =
            new ConcurrentDictionary<long, int>();

        internal static int GetMode(Transformer transformer)
        {
            if (transformer == null) return 0;
            if (_byReference.TryGetValue(transformer.ReferenceId, out var mode))
                return mode;
            return GetDefaultMode(transformer.PrefabName);
        }

        internal static void SetMode(Transformer transformer, int mode)
        {
            if (transformer == null) return;
            _byReference[transformer.ReferenceId] = mode != 0 ? 1 : 0;
        }

        internal static int GetDefaultMode(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return 0;
            switch (prefabName)
            {
                case "StructureTransformerSmall":
                case "StructureTransformerSmallReversed":
                    return 1;
                default:
                    return 0;
            }
        }

        internal static IEnumerable<KeyValuePair<long, int>> SnapshotEntries()
        {
            foreach (var pair in _byReference) yield return pair;
        }

        internal static void RestoreFromSideCar(long referenceId, int mode)
        {
            _byReference[referenceId] = mode != 0 ? 1 : 0;
        }
    }
}
