using System.Globalization;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Objects.Rockets;

namespace PowerGridPlus
{
    /// <summary>
    ///     Single source of truth for the fault / info hover blocks and the flash-colour decision
    ///     (POWER.md §11.1 / §11.5). Precedence when more than one state is active on the same
    ///     device: CYCLE_FAULT &gt; CURRENT_MISMATCH_FAULT &gt; CABLE_OVERLOADED &gt;
    ///     DEVICE_OVERLOADED &gt; DEPRIORITIZED, then the two non-fault info states DEAD_INPUT and
    ///     THROTTLED. Exactly ONE block is emitted per hover: the highest-precedence active fault,
    ///     else the dead-input cue, else the transformer throttle note. The flash uses the same
    ///     fault resolution (every fault except DEPRIORITIZED is red, DEPRIORITIZED is orange);
    ///     the info states never flash.
    ///
    ///     <para>Block template (locked 2026-07-14, both hover surfaces render the same block):
    ///     line 1 merges the switch state and the title into one sentence-case line,
    ///     "On - Cable overloaded fault: 35.23s". The switch word is StateGreen when on, CalmGrey
    ///     when off, the " - " separator CalmGrey; a fault title plus its countdown is FaultRed;
    ///     an info title is CalmGrey (dead input) or ThrottleAmber (throttled) and carries no
    ///     countdown. Devices without an on/off switch (and a null thing, which is how the
    ///     ScenarioRunner fixture drives this renderer without a live Thing) omit the "On - "
    ///     prefix entirely. The following line(s) are the diagnostics: CalmGrey prose,
    ///     sentence-capitalized, the offending value FaultRed, the capacity value CapBlue.</para>
    ///
    ///     <para>The countdown is the live remaining lockout in seconds with two-decimal precision,
    ///     locale-formatted ("4.32s" / "4,32s"), recomputed on every poll: tick-derived and
    ///     wall-clock-smoothed on the host, MonotonicClock-derived on a client (§11.2). The watt
    ///     diagnostics follow the same locale rule: below 1000 W an integer watt count, at or
    ///     above 1000 W kilowatts with one decimal.</para>
    ///
    ///     <para>For a <see cref="PowerReceiver"/> the lockouts are keyed on its linked
    ///     transmitter's ReferenceId (the pair anchor, §6.2), so fault reads alias to the PT.</para>
    /// </summary>
    internal static class FaultHover
    {
        internal enum Kind
        {
            None,
            Deprioritized,
            DeviceOverload,
            CableOverload,
            CycleFault,
            CurrentMismatch,
            DeadInput,
            Throttled,
        }

        // Fault colours: red for every fault except DEPRIORITIZED (matches
        // FaultFlashBehaviour.RedFlashColor), orange for DEPRIORITIZED (flash only; the
        // deprioritized hover title uses the same red as the other faults), neutral grey for info
        // prose. The diagnostics line uses the grey as the calm base so the red offending value
        // stands out against the calmer capacity figure. The capacity value itself is blue:
        // #008AE6 is the informational blue vanilla already uses in HUD text (suit min-coolant
        // readouts, Stationpedia link colour), so the pair reads as "red = what you did, blue =
        // what the hardware allows" in the game's own palette.
        private const string FaultRed = "#ff2626";
        private const string CalmGrey = "#9aa0a6";
        private const string CapBlue = "#008AE6";

        // The switch-state "On" in the merged line: #00FF00 is UnityEngine.Color.green, the exact
        // green vanilla wraps the whole button-tooltip action text in for an instant allowed click
        // (Tooltip.SetValuesForInteractable picks Color.green, SetUpToolTip's ColorToHex wrap applies
        // it; decompile 254426 / 254319), so "On" reads in the game's own go-ahead green. "Off" uses
        // the neutral CalmGrey instead: only the powered state earns the green.
        private const string StateGreen = "#00FF00";

        // Muted amber for the throttled info title: reads as "heads up / advanced" without
        // mimicking the deprioritized flash orange (#ffa500) or a red fault.
        private const string ThrottleAmber = "#d9a441";

        // Vanilla tooltip yellow (UnityEngine.Color.yellow) for the literal IC10 variable name
        // "Setting" inside the throttled diagnostics, so the word reads as the game's own
        // interaction highlight rather than mod prose.
        private const string SettingYellow = "#FFFF00";

        // True for the five lockout faults (the registries with a 60 s timer and a flash). The
        // two info states share the hover pipeline but must never trigger the fault-only
        // presentation extras (no-ALT body-tooltip visibility).
        internal static bool IsLockoutFault(Kind kind)
            => kind != Kind.None && kind != Kind.DeadInput && kind != Kind.Throttled;

        // Resolve the device whose ReferenceId keys the registries (PR -> linked PT).
        internal static long ResolveFaultRefId(Thing thing)
        {
            if (thing is PowerReceiver receiver && receiver.LinkedPowerTransmitter != null)
                return receiver.LinkedPowerTransmitter.ReferenceId;
            return thing.ReferenceId;
        }

        // The highest-precedence active LOCKOUT fault (never an info state): the flash colour,
        // the enforcement patches, and the hover renderer below all resolve through this.
        internal static Kind ActiveFault(long refId, int tick)
        {
            if (CycleFaultRegistry.IsCycleFaulted(refId, tick)) return Kind.CycleFault;
            if (CurrentMismatchFaultRegistry.IsCurrentMismatchFaulted(refId, tick)) return Kind.CurrentMismatch;
            if (CableOverloadRegistry.IsCableOverloaded(refId, tick)) return Kind.CableOverload;
            if (OverloadRegistry.IsOverloaded(refId, tick)) return Kind.DeviceOverload;
            if (DeprioritizedRegistry.IsDeprioritized(refId, tick)) return Kind.Deprioritized;
            return Kind.None;
        }

        // The full hover block for the highest-precedence active fault, else the dead-input cue,
        // else the throttle note; false when the device is idle (hover stays pure vanilla). Both
        // hover surfaces (the casing tooltip's Extended row and the on/off button tooltip's Title
        // box) render exactly this block; the callers wrap it in their own alignment / size tags.
        internal static bool TryGetMergedBlock(long refId, int tick, Thing thing, out string block, out Kind kind)
        {
            kind = ActiveFault(refId, tick);
            switch (kind)
            {
                case Kind.CableOverload:
                {
                    CableOverloadRegistry.TryGetFault(refId, tick, out var seconds, out var flowW, out var capW);
                    block = FaultTitleLine(thing, "Cable overloaded fault", seconds);
                    if (flowW > 0f || capW > 0f)
                        block += $"\n<color={CalmGrey}>Pushing <color={FaultRed}>{FmtWatts(flowW)}</color> onto a <color={CapBlue}>{FmtWatts(capW)}</color> wire</color>";
                    return true;
                }
                case Kind.DeviceOverload:
                {
                    OverloadRegistry.TryGetFault(refId, tick, out var seconds, out var valueW, out var capW);
                    // The generic "Device" in the IC10 name DeviceOverloadedFault is substituted
                    // by the hovered device's own label in the title (locked matrix): Transformer /
                    // Link / Battery / APC / Umbilical overloaded fault.
                    block = FaultTitleLine(thing, DeviceOverloadLabel(thing) + " overloaded fault", seconds);
                    if (valueW > 0f || capW > 0f)
                        block += $"\n<color={CalmGrey}>Drawing <color={FaultRed}>{FmtWatts(valueW)}</color> of <color={CapBlue}>{FmtWatts(capW)}</color></color>";
                    return true;
                }
                case Kind.Deprioritized:
                {
                    DeprioritizedRegistry.TryGetFault(refId, tick, out var seconds,
                        out var needsW, out var upstreamDemandW, out var upstreamSupplyW);
                    block = FaultTitleLine(thing, "Deprioritized fault", seconds);
                    if (needsW > 0f || upstreamDemandW > 0f || upstreamSupplyW > 0f)
                        block += $"\n<color={CalmGrey}>Needs {FmtWatts(needsW)} while <color={FaultRed}>{FmtWatts(upstreamDemandW)}</color> competes for <color={CapBlue}>{FmtWatts(upstreamSupplyW)}</color> upstream</color>";
                    return true;
                }
                case Kind.CycleFault:
                {
                    CycleFaultRegistry.TryGetSecondsLeft(refId, tick, out var seconds);
                    block = FaultTitleLine(thing, "Cycle fault", seconds);
                    block += $"\n<color={CalmGrey}>This device is part of a power loop</color>";
                    return true;
                }
                case Kind.CurrentMismatch:
                {
                    CurrentMismatchFaultRegistry.TryGetFault(refId, tick, out var seconds, out var violators);
                    block = FaultTitleLine(thing, "Current mismatch fault", seconds);
                    block += ViolatorLines(violators);
                    block += $"\n<color={CalmGrey}>Generator DC cannot feed the AC grid without a transformer</color>";
                    return true;
                }
                default:
                {
                    // No lockout fault. The two INFO states (no timer, no flash, never the
                    // fault-only presentation extras), in fixed order: the dead-input cue first
                    // (POWER.md §8.3 carveout; keyed on the resolved fault refId so a PR aliases
                    // to its PT; host reads the live set, clients the synced mirror), then the
                    // transformer throttle note (§5.3 / P13, live Setting-vs-rated read).
                    if (DeadInputRegistry.IsDeadInput(refId))
                    {
                        kind = Kind.DeadInput;
                        block = InfoTitleLine(thing, "No upstream supply", CalmGrey);
                        block += $"\n<color={CalmGrey}>The input network carries no power</color>";
                        return true;
                    }
                    if (TransformerThrottleHover.TryGetThrottle(thing, out var settingW, out var maximumW))
                    {
                        kind = Kind.Throttled;
                        block = InfoTitleLine(thing, "Throttled", ThrottleAmber);
                        block += $"\n<color={CalmGrey}>Limited to <color={FaultRed}>{FmtWatts(settingW)}</color> of <color={CapBlue}>{FmtWatts(maximumW)}</color> by the IC10 <color={SettingYellow}>Setting</color> value</color>";
                        block += $"\n<color={CalmGrey}>The dial sets priority instead of power</color>";
                        return true;
                    }
                    block = null;
                    return false;
                }
            }
        }

        // Line 1 for a lockout fault: "{On|Off} - {title}: {countdown}s", title + countdown red.
        private static string FaultTitleLine(Thing thing, string title, float seconds)
            => StatePrefix(thing) + $"<color={FaultRed}>{title}: {Fmt(seconds)}s</color>";

        // Line 1 for an info state: "{On|Off} - {title}", no countdown, caller-picked title colour.
        private static string InfoTitleLine(Thing thing, string title, string colour)
            => StatePrefix(thing) + $"<color={colour}>{title}</color>";

        // The merged switch-state prefix: green "On" / grey "Off" plus the grey separator.
        // Devices without an on/off switch (and the fixture's null thing) get no prefix, so the
        // line starts at the title. ActionStrings keeps the localisation.
        private static string StatePrefix(Thing thing)
        {
            if (thing == null || !thing.HasOnOffState) return string.Empty;
            string word = thing.OnOff
                ? $"<color={StateGreen}>{ActionStrings.On}</color>"
                : $"<color={CalmGrey}>{ActionStrings.Off}</color>";
            return word + $"<color={CalmGrey}> - </color>";
        }

        // The current-mismatch violator block: one red name per line, capped at three lines by
        // the detector, plus a grey "and N more" line when the detector appended its "+N"
        // overflow marker (CurrentMismatchFaultDetector.BuildViolatorNames writes
        // "Name\nName\nName\n+4"). An empty payload (mid-sync race) renders no violator lines;
        // the explanation line below the block still names the rule.
        private static string ViolatorLines(string violators)
        {
            if (string.IsNullOrEmpty(violators)) return string.Empty;
            var lines = violators.Split('\n');
            string block = string.Empty;
            for (int i = 0; i < lines.Length; i++)
            {
                string name = lines[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (name[0] == '+' && int.TryParse(name.Substring(1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int more))
                    block += $"\n<color={CalmGrey}>and {more} more</color>";
                else
                    block += $"\n<color={FaultRed}>{name}</color>";
            }
            return block;
        }

        // The device label substituted into the DeviceOverloadedFault title (locked matrix):
        // the throughput-rated segmenters read "Transformer" / "Link" (the wireless
        // transmitter / receiver pair; they derive from WirelessPower, not Transformer), the
        // storage elastics "Battery" / "APC" / "Umbilical". Unknown overload-capable devices
        // (and the fixture's null thing) fall back to the IC10 name's own generic "Device".
        private static string DeviceOverloadLabel(Thing thing)
        {
            switch (thing)
            {
                case Transformer _:
                    return "Transformer";
                case PowerTransmitter _:
                case PowerReceiver _:
                    return "Link";
                case AreaPowerControl _:
                    return "APC";
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    return "Umbilical";
                case Battery _:
                    return "Battery";
                default:
                    return "Device";
            }
        }

        // Locale-driven two-decimal format ("4.32" in en-US, "4,32" in nl-NL), POWER.md §11.2.
        private static string Fmt(float seconds)
        {
            return seconds.ToString("0.00", CultureInfo.CurrentCulture);
        }

        // Watt formatting for the diagnostics: integer watts below 1000, kilowatts with one
        // decimal at or above. The decimal separator follows the system locale, matching the
        // countdown format above.
        private static string FmtWatts(float watts)
        {
            if (watts < 1000f)
                return watts.ToString("0", CultureInfo.CurrentCulture) + " W";
            return (watts / 1000f).ToString("0.0", CultureInfo.CurrentCulture) + " kW";
        }
    }
}
