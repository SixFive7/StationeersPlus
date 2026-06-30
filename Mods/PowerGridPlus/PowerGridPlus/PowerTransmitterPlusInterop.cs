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
                // Prefer the public SourceDrawMultiplier wrapper (forward-compatible). It was added to
                // PowerTransmitterPlus after the currently-published build, so when it is absent fall back
                // to the internal GetMultiplier -- the identical computation (SourceDrawMultiplier =>
                // GetMultiplier). Without this fallback the reflection fails against the shipped
                // PowerTransmitterPlus and the allocator silently bills distance links at m = 1 while
                // PowerTransmitterPlus inflates the _powerProvided debt at the true m, running the source
                // debt away on every long link.
                var mi = type?.GetMethod(
                    "SourceDrawMultiplier",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(PowerTransmitter) },
                    null);
                if (mi == null)
                    mi = type?.GetMethod(
                        "GetMultiplier",
                        BindingFlags.NonPublic | BindingFlags.Static,
                        null,
                        new[] { typeof(PowerTransmitter) },
                        null);
                if (mi != null)
                {
                    _multiplier = (Func<PowerTransmitter, float>)
                        Delegate.CreateDelegate(typeof(Func<PowerTransmitter, float>), mi);
                    Plugin.Log?.LogInfo($"PowerTransmitterPlus detected (via {mi.Name}); modelling transmitter distance draw overhead.");
                }
            }
            catch
            {
                _multiplier = null;   // any resolution failure -> vanilla (m = 1)
            }
            return _multiplier;
        }

        // --- Max Transfer Capacity: the link's static delivery rating (server-authoritative) ---

        private static bool _capResolved;
        private static Func<float> _effCap;

        /// <summary>
        ///     PowerTransmitterPlus's effective Max Transfer Capacity in watts (0 = unlimited), or a
        ///     negative sentinel (-1) when PowerTransmitterPlus is not loaded, so the caller falls back
        ///     to the vanilla transmission rating. Never throws.
        /// </summary>
        internal static float EffectiveMaxCapacityOrAbsent()
        {
            var fn = ResolveCap();
            if (fn == null) return -1f;
            float c;
            try { c = fn(); }
            catch { return -1f; }
            if (float.IsNaN(c) || c < 0f) return 0f;   // garbage -> treat as unlimited (0)
            return c;
        }

        private static Func<float> ResolveCap()
        {
            if (_capResolved) return _effCap;
            _capResolved = true;
            try
            {
                Type type = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType("PowerTransmitterPlus.MaxCapacityConfigSync", false);
                    if (type != null) break;
                }
                var mi = type?.GetMethod(
                    "GetEffectiveMaxCapacity",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);
                if (mi != null && mi.ReturnType == typeof(float))
                    _effCap = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), mi);
            }
            catch
            {
                _effCap = null;   // PTP absent / renamed -> caller uses the vanilla rating
            }
            return _effCap;
        }

        // --- Linked-receiver distance in metres (vanilla rating fallback only) ---

        private static bool _distResolved;
        private static FieldInfo _distField;

        /// <summary>
        ///     The transmitter's cached linked-receiver distance (vanilla private field
        ///     <c>_linkedReceiverDistance</c>), used only for the vanilla rating's distance derate when
        ///     PowerTransmitterPlus is absent. Returns 0 on any failure. Never throws.
        /// </summary>
        internal static float LinkedReceiverDistance(PowerTransmitter pt)
        {
            if (pt == null) return 0f;
            if (!_distResolved)
            {
                _distResolved = true;
                try { _distField = typeof(PowerTransmitter).GetField("_linkedReceiverDistance", BindingFlags.NonPublic | BindingFlags.Instance); }
                catch { _distField = null; }
            }
            if (_distField == null) return 0f;
            try
            {
                var v = _distField.GetValue(pt);
                return v is float f && !float.IsNaN(f) && f >= 0f ? f : 0f;
            }
            catch { return 0f; }
        }
    }
}
