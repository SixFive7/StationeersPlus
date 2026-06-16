using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-APC discharge-rate cap (Watts), keyed by <c>AreaPowerControl.ReferenceId</c>. Vanilla has
    ///     no discharge-rate field on <see cref="Assets.Scripts.Objects.Electrical.AreaPowerControl"/>, so
    ///     PowerGridPlus owns this. The elastic-supply allocator (POWER.md §7.3) reads the per-APC cap
    ///     here; an APC with no explicit override returns the server default
    ///     (<see cref="Settings.ApcBatteryDischargeRate"/>).
    ///
    ///     <para>Session-only (POWERTODO open-question 10): no save persistence, the server setting
    ///     governs. The dictionary exists so a future per-APC IC10 override could be added without
    ///     reworking callers.</para>
    /// </summary>
    internal static class ApcDischargeRateRegistry
    {
        private static readonly ConcurrentDictionary<long, float> _overrides = new ConcurrentDictionary<long, float>();

        internal static float GetDischargeRate(long referenceId)
        {
            return _overrides.TryGetValue(referenceId, out var v) ? v : Settings.ApcBatteryDischargeRate.Value;
        }

        internal static void SetDischargeRate(long referenceId, float watts)
        {
            _overrides[referenceId] = watts < 0f ? 0f : watts;
        }

        internal static void ClearAll() => _overrides.Clear();
    }
}
