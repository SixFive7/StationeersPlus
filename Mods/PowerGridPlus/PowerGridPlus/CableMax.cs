using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Non-mutating per-tier cable Watts caps (POWER.md decision 2). <c>Cable.MaxVoltage</c> is a
    ///     per-instance serialized field; rewriting it bakes the configured caps into the save and
    ///     survives mod removal. Instead, every cap reader in PowerGridPlus (battery / APC headroom,
    ///     the generator-overflow burn check, the allocator's effective-cap formula) consults this
    ///     helper, and vanilla's own cable-burn check is patched to do the same
    ///     (PowerTickPatches.GetBreakableCables_Prefix). The cable instances are never written;
    ///     removing the mod reverts cables to vanilla ratings with no save contamination.
    ///
    ///     <para>A configured value of 0 means "unlimited" and is normalised to
    ///     <see cref="float.MaxValue"/> so a <c>flow &gt; cap</c> comparison is never satisfied.
    ///     Settings are immutable mid-session (POWER.md §17.42), so the values are read live with no
    ///     caching or change handling.</para>
    /// </summary>
    internal static class CableMax
    {
        internal static float ForType(Cable.Type type)
        {
            int configured;
            switch (type)
            {
                case Cable.Type.heavy: configured = Settings.CableHeavyMaxWatts.Value; break;
                case Cable.Type.superHeavy: configured = Settings.CableSuperHeavyMaxWatts.Value; break;
                default: configured = Settings.CableNormalMaxWatts.Value; break;
            }
            return configured <= 0 ? float.MaxValue : configured;
        }

        // Cap for a specific cable; null-safe (no cable = nothing to burn = unlimited).
        internal static float For(Cable cable)
        {
            return cable == null ? float.MaxValue : ForType(cable.CableType);
        }

        /// <summary>
        ///     The weakest (lowest-cap) tier present on a network, as a Watts cap. Networks are
        ///     single-tier once the Phase 1.5a backstop has run, so this is normally just the
        ///     network tier's cap; during the brief mixed-tier window it is the lowest tier seen.
        ///     Reads the tier scan cached by <see cref="VoltageTierEnforcer"/> (rebuilt when the
        ///     network's cable count changes), so the per-tick cost is O(1) for a stable network.
        ///     A cableless network (wireless relay) returns <see cref="float.MaxValue"/>.
        /// </summary>
        internal static float WeakestCapOnNetwork(CableNetwork network)
        {
            var info = VoltageTierEnforcer.GetTierInfo(network);
            if (!info.LowestTier.HasValue) return float.MaxValue;
            return ForType(info.LowestTier.Value);
        }
    }
}
