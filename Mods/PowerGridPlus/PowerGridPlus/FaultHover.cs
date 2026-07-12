using System.Globalization;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Objects.Rockets;

namespace PowerGridPlus
{
    /// <summary>
    ///     Single source of truth for the fault hover lines and the flash-colour decision
    ///     (POWER.md §11.1 / §11.5). Precedence when more than one fault is active on the same
    ///     device: CYCLE_FAULT &gt; VARIABLE_VOLTAGE_FAULT &gt; OVERLOAD &gt; SHED. Exactly ONE line
    ///     is emitted per hover (the highest-precedence active fault); the flash uses the same
    ///     resolution (every non-shed fault is red, shed is orange).
    ///
    ///     <para>The countdown is the live remaining lockout in seconds with two-decimal precision,
    ///     locale-formatted ("4.32s" / "4,32s"), recomputed on every poll: tick-derived and
    ///     wall-clock-smoothed on the host, MonotonicClock-derived on a client (§11.2).</para>
    ///
    ///     <para>For a <see cref="PowerReceiver"/> the lockouts are keyed on its linked
    ///     transmitter's ReferenceId (the pair anchor, §6.2), so fault reads alias to the PT.</para>
    /// </summary>
    internal static class FaultHover
    {
        internal enum Kind { None, Shed, Overload, CycleFault, VariableVoltage, DeadInput }

        // Resolve the device whose ReferenceId keys the registries (PR -> linked PT).
        internal static long ResolveFaultRefId(Thing thing)
        {
            if (thing is PowerReceiver receiver && receiver.LinkedPowerTransmitter != null)
                return receiver.LinkedPowerTransmitter.ReferenceId;
            return thing.ReferenceId;
        }

        internal static Kind ActiveFault(long refId, int tick)
        {
            if (CycleFaultRegistry.IsCycleFaulted(refId, tick)) return Kind.CycleFault;
            if (VariableVoltageFaultRegistry.IsVariableVoltageFaulted(refId, tick)) return Kind.VariableVoltage;
            if (OverloadRegistry.IsOverloaded(refId, tick)) return Kind.Overload;
            if (BrownoutRegistry.IsShedding(refId, tick)) return Kind.Shed;
            return Kind.None;
        }

        // The fully-formatted hover line for the highest-precedence active fault, or false.
        internal static bool TryGetLine(long refId, int tick, Thing thing, out string line, out Kind kind)
        {
            kind = ActiveFault(refId, tick);
            switch (kind)
            {
                case Kind.CycleFault:
                {
                    CycleFaultRegistry.TryGetSecondsLeft(refId, tick, out var seconds);
                    line = $"<color=#ff2626>(Cycle Fault: This device is part of a loop! {Fmt(seconds)}s)</color>";
                    return true;
                }
                case Kind.VariableVoltage:
                {
                    VariableVoltageFaultRegistry.TryGetFault(refId, tick, out var seconds, out var violators);
                    string who = string.IsNullOrEmpty(violators) ? "another device" : violators;
                    line = $"<color=#ff2626>(Variable Voltage Fault: connected to {who}. A producer connects only to producers and transformers. {Fmt(seconds)}s)</color>";
                    return true;
                }
                case Kind.Overload:
                {
                    OverloadRegistry.TryGetSecondsLeft(refId, tick, out var seconds);
                    line = $"<color=#ff2626>(Overloaded: {OverloadClause(thing)} {Fmt(seconds)}s)</color>";
                    return true;
                }
                case Kind.Shed:
                {
                    BrownoutRegistry.TryGetSecondsLeft(refId, tick, out var seconds);
                    line = $"<color=#ffa500>(Shedding: Insufficient upstream supply! {Fmt(seconds)}s)</color>";
                    return true;
                }
                default:
                    // No active fault. Lowest-precedence INFO cue (not a fault): a contributor whose
                    // input network has no upstream supply idles (POWER.md §8.3 dead-input carveout).
                    // Steady, no countdown, neutral grey, and deliberately NOT routed through
                    // ActiveFault, so it never drives the flash (BrownoutFlashBehaviour). Keyed on the
                    // resolved fault refId (a PR aliases to its PT). Host reads the live set; clients the
                    // synced mirror.
                    if (DeadInputRegistry.IsDeadInput(refId))
                    {
                        kind = Kind.DeadInput;
                        line = "<color=#9aa0a6>(No upstream supply)</color>";
                        return true;
                    }
                    line = null;
                    return false;
            }
        }

        // Per-device-type OVERLOAD wording (POWER.md §11.1): the hovered device names what hit its
        // limit. A transformer reports its throughput limit (its Setting), a storage device its
        // discharge rate, a wireless transmitter / receiver pair its link capacity. The instance is
        // the one the player is hovering, so a PowerReceiver shows the same link wording as its
        // PowerTransmitter. Unknown overload-capable devices fall back to the generic clause.
        private static string OverloadClause(Thing thing)
        {
            switch (thing)
            {
                case Transformer _:
                    return "Downstream demand exceeds this transformer's limit!";
                case AreaPowerControl _:
                    return "Downstream demand exceeds this APC's output!";
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    return "Downstream demand exceeds this umbilical's discharge rate!";
                case PowerTransmitter _:
                case PowerReceiver _:
                    return "This power link cannot carry the downstream demand!";
                case Battery _:
                    return "Downstream demand exceeds this battery's discharge rate!";
                default:
                    return "Downstream demand exceeds this device's limit!";
            }
        }

        // Locale-driven two-decimal format ("4.32" in en-US, "4,32" in nl-NL), POWER.md §11.2.
        private static string Fmt(float seconds)
        {
            return seconds.ToString("0.00", CultureInfo.CurrentCulture);
        }
    }
}
