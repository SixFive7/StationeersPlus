using System;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Soft (reflection) bridge to PowerTransmitterPlus's distance model. PowerTransmitterPlus
    ///     does NOT derate a microwave link's delivered power for distance; instead it inflates the
    ///     transmitter's INPUT-side draw by a factor m = 1 + k * distance_km (POWER.md §6.3). The
    ///     allocator must know m to present the real input demand (delivered * m) on the source
    ///     network and to bound the input cable, or a long link on a constrained source network
    ///     silently brown-outs that network (POWER.md §8.4.2).
    ///
    ///     <para>When PowerTransmitterPlus is not loaded, a microwave link is the vanilla curve where
    ///     delivered == drawn, so the factor is 1 and this bridge is a no-op. The target type/method is
    ///     resolved once across loaded assemblies; any failure (mod absent, method renamed, throw)
    ///     degrades to 1f. PowerGridPlus never hard-references PowerTransmitterPlus, so this stays an
    ///     optional dependency.</para>
    /// </summary>
    internal static class PowerTransmitterPlusInterop
    {
        private static bool _resolved;
        private static Func<PowerTransmitter, float> _multiplier;

        /// <summary>
        ///     The factor by which a transmitter's input-network draw exceeds its delivered output
        ///     (>= 1). Returns 1 when PowerTransmitterPlus is absent or the link has no distance
        ///     overhead. Never throws.
        /// </summary>
        internal static float SourceDrawMultiplier(PowerTransmitter pt)
        {
            if (pt == null) return 1f;
            var fn = Resolve();
            if (fn == null) return 1f;
            float m;
            try { m = fn(pt); }
            catch { return 1f; }
            if (float.IsNaN(m) || float.IsInfinity(m) || m < 1f) return 1f;
            return m;
        }

        private static Func<PowerTransmitter, float> Resolve()
        {
            if (_resolved) return _multiplier;
            _resolved = true;   // resolve once; plugin load completes before any tick runs
            try
            {
                Type type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType("PowerTransmitterPlus.DistanceCostShared", false);
                    if (type != null) break;
                }
                var mi = type?.GetMethod(
                    "SourceDrawMultiplier",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(PowerTransmitter) },
                    null);
                if (mi != null)
                {
                    _multiplier = (Func<PowerTransmitter, float>)
                        Delegate.CreateDelegate(typeof(Func<PowerTransmitter, float>), mi);
                    Plugin.Log?.LogInfo("PowerTransmitterPlus detected; modelling transmitter distance draw overhead.");
                }
            }
            catch
            {
                _multiplier = null;   // any resolution failure -> vanilla (m = 1)
            }
            return _multiplier;
        }
    }
}
