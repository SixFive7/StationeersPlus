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
    ///     <para>Line 1 merges the switch state and the title into one sentence-case line,
    ///     "On - Cable overloaded fault: 35.23s". The switch word is StateGreen when on, CalmGrey
    ///     when off, the " - " separator CalmGrey; a fault title plus its countdown is FaultRed; an
    ///     info title is CalmGrey (dead input) or ThrottleAmber (throttled) and carries no
    ///     countdown. Devices without an on/off switch (and a null thing, the ScenarioRunner
    ///     fixture path) omit the "On - " prefix. The diagnostic line(s) below differ per fault:
    ///     the overload family shows a demand-vs-available breakdown plus a shortfall, current
    ///     mismatch lists the incompatible devices, deprioritized names the upstream contention and
    ///     the reason the allocator recorded when it shed the device.</para>
    ///
    ///     <para>Colour system (one rule across every block): FaultRed = power wanted (downstream
    ///     demand, cable flow, the shortfall / overage) and the offending device names; CapBlue =
    ///     power available (upstream supply, device capacity, wire rating, throttle maximum);
    ///     TealStorage = the internal-storage (battery) contribution split out from the available
    ///     total; LogicOrange = the IC10 "Setting" field, the exact colour Stationpedia paints a
    ///     logic field (&lt;color=orange&gt; = #FF8000); CalmGrey = prose. A diagnostic line is a
    ///     CalmGrey base with the coloured values nested inside.</para>
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
            Undersupplied,
        }

        // FaultRed = power wanted (downstream demand, cable flow, the shortfall / overage) and the
        // offending device names; also the fault title plus countdown. CalmGrey = calm prose base.
        // CapBlue = power available (#008AE6 is the informational blue vanilla uses in HUD text and
        // Stationpedia links), so demand-vs-supply reads as "red = wanted, blue = available".
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

        // Internal-storage (battery / APC / umbilical) contribution, split out from the available
        // total in the overload breakdown. A teal / cyan distinct from the On-green and the CapBlue
        // supply figure, so the player reads the battery's help as its own thing at a glance.
        private const string TealStorage = "#2DD4BF";

        // The IC10 "Setting" field word: #FF8000 is the exact colour Stationpedia paints a logic
        // field name (the game emits <color=orange>, which TextMeshPro resolves to #FF8000), so the
        // word reads as a logic field just like the Stationpedia entry rather than mod prose.
        private const string LogicOrange = "#FF8000";

        // True for the five lockout faults (the registries with a 60 s timer and a flash). The
        // three info states share the hover pipeline but must never trigger the fault-only
        // presentation extras (no-ALT body-tooltip visibility).
        internal static bool IsLockoutFault(Kind kind)
            => kind != Kind.None && kind != Kind.DeadInput && kind != Kind.Throttled
               && kind != Kind.Undersupplied;

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
                    {
                        block += $"\n<color={CalmGrey}>Pushing <color={FaultRed}>{FmtWatts(flowW)}</color> onto a wire rated for <color={CapBlue}>{FmtWatts(capW)}</color></color>";
                        float over = flowW - capW;
                        if (over > 0f)
                            block += $"\n<color={CalmGrey}>Overloaded by <color={FaultRed}>{FmtWatts(over)}</color></color>";
                    }
                    return true;
                }
                case Kind.DeviceOverload:
                {
                    OverloadRegistry.TryGetFault(refId, tick, out var seconds, out var valueW, out var capW, out var storageW);
                    // The generic "Device" in the IC10 name DeviceOverloadedFault is substituted
                    // by the hovered device's own label in the title: Transformer / Link / Battery /
                    // APC / Umbilical overloaded fault.
                    block = FaultTitleLine(thing, DeviceOverloadLabel(thing) + " overloaded fault", seconds);
                    if (valueW > 0f || capW > 0f)
                    {
                        block += $"\n<color={CalmGrey}>Downstream demand of <color={FaultRed}>{FmtWatts(valueW)}</color> exceeds the available <color={CapBlue}>{FmtWatts(capW)}</color></color>";
                        // The source-breakdown line is driven by what the device CAN do, not by
                        // which number happens to be zero right now, so the hover teaches the
                        // device's mechanics (user decision 2026-07-18). A pass-through-only
                        // device (transformer, wireless link) never shows a storage component; a
                        // storage-only device (battery, umbilical) shows only its storage
                        // component; a device that can do both (the APC) always shows both
                        // parts, zero-valued parts included.
                        float upstreamW = capW - storageW;
                        if (upstreamW < 0f) upstreamW = 0f;
                        switch (OverloadSourceMode(thing))
                        {
                            case SourceMode.Both:
                                block += $"\n<color={CalmGrey}><color={CapBlue}>{FmtWatts(upstreamW)}</color> upstream + <color={TealStorage}>{FmtWatts(storageW)}</color> internal storage = <color={CapBlue}>{FmtWatts(capW)}</color> available</color>";
                                break;
                            case SourceMode.StorageOnly:
                                block += $"\n<color={CalmGrey}><color={TealStorage}>{FmtWatts(storageW)}</color> of the available comes from internal storage</color>";
                                break;
                            case SourceMode.PassthroughOnly:
                                break;   // no storage component exists on the device; the total says it all
                        }
                        float shortW = valueW - capW;
                        if (shortW > 0f)
                            block += $"\n<color={CalmGrey}>Short by <color={FaultRed}>{FmtWatts(shortW)}</color></color>";
                    }
                    return true;
                }
                case Kind.Deprioritized:
                {
                    DeprioritizedRegistry.TryGetFault(refId, tick, out var seconds,
                        out var needsW, out var upstreamDemandW, out var upstreamSupplyW,
                        out var shortfallW, out var reason, out var victimPriority);
                    block = FaultTitleLine(thing, "Deprioritized fault", seconds);
                    if (upstreamDemandW > 0f || upstreamSupplyW > 0f)
                        block += $"\n<color={CalmGrey}>Upstream demand of <color={FaultRed}>{FmtWatts(upstreamDemandW)}</color> exceeded the upstream supply of <color={CapBlue}>{FmtWatts(upstreamSupplyW)}</color></color>";
                    if (shortfallW > 0f || needsW > 0f)
                        block += $"\n<color={CalmGrey}>Due to the shortfall of <color={FaultRed}>{FmtWatts(shortfallW)}</color> this device's {FmtWatts(needsW)} share was cut</color>";
                    block += ReasonLines(reason, victimPriority);
                    return true;
                }
                case Kind.CycleFault:
                {
                    CycleFaultRegistry.TryGetSecondsLeft(refId, tick, out var seconds);
                    block = FaultTitleLine(thing, "Cycle fault", seconds);
                    block += $"\n<color={CalmGrey}>This device is part of a power loop that feeds back into itself</color>";
                    return true;
                }
                case Kind.CurrentMismatch:
                {
                    CurrentMismatchFaultRegistry.TryGetFault(refId, tick, out var seconds, out var violators);
                    block = FaultTitleLine(thing, "Current mismatch fault", seconds);
                    block += $"\n<color={CalmGrey}>Put a transformer between the generator DC and the AC grid</color>";
                    block += IncompatibleDeviceLines(violators);
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
                        block += $"\n<color={CalmGrey}>No power is reaching this device from upstream</color>";
                        return true;
                    }
                    if (TransformerThrottleHover.TryGetThrottle(thing, out var settingW, out var maximumW))
                    {
                        kind = Kind.Throttled;
                        block = InfoTitleLine(thing, "Throttled", ThrottleAmber);
                        block += $"\n<color={CalmGrey}>Limited to <color={FaultRed}>{FmtWatts(settingW)}</color> of the <color={CapBlue}>{FmtWatts(maximumW)}</color> maximum by the <color={LogicOrange}>Setting</color> value</color>";
                        block += $"\n<color={CalmGrey}>The dial sets priority instead of power</color>";
                        block += $"\n<color={CalmGrey}>Use IC10 to set the <color={LogicOrange}>Setting</color> value</color>";
                        return true;
                    }
                    // Decision-33 Undersupplied (the DEAD_UNMET face; the most generic info state,
                    // so it renders only when nothing more specific applies): any device on a
                    // network whose rigid demand outran the supply that reaches it, with shedding
                    // exhausted, shows the amber needs-vs-delivers block plus a pointer at the
                    // strongest feeder so the dark room diagnoses itself.
                    if (TryGetUndersuppliedNet(thing, out float usNeedsW, out float usAvailW, out long usFeederRef))
                    {
                        kind = Kind.Undersupplied;
                        block = InfoTitleLine(thing, "Undersupplied", ThrottleAmber);
                        block += $"\n<color={CalmGrey}>Needs <color={FaultRed}>{FmtWatts(usNeedsW)}</color> while upstream delivers <color={CapBlue}>{FmtWatts(usAvailW)}</color></color>";
                        string feederName = UndersuppliedFeederName(usFeederRef);
                        if (feederName != null)
                            block += $"\n<color={CalmGrey}>Check {feederName}</color>";
                        return true;
                    }
                    block = null;
                    return false;
                }
            }
        }

        // Decision-33 Undersupplied lookup: keyed by the device's power NETWORK (the state is
        // per-net). Host reads the live set, clients the synced mirror.
        private static bool TryGetUndersuppliedNet(Thing thing, out float needsW, out float availW, out long feederRefId)
        {
            needsW = 0f;
            availW = 0f;
            feederRefId = 0L;
            var net = (thing as Assets.Scripts.Objects.Pipes.Device)?.PowerCableNetwork;
            if (net == null) return false;
            if (!UndersuppliedRegistry.TryGet(net.ReferenceId, out var info)) return false;
            needsW = info.NeedsW;
            availW = info.AvailW;
            feederRefId = info.FeederRefId;
            return true;
        }

        // Resolve the feeder pointer to a display name on the main-thread hover path; null when
        // there is no feeder (a generator-only net) or the device is gone.
        private static string UndersuppliedFeederName(long refId)
        {
            if (refId == 0L) return null;
            var feeder = Thing.Find(refId);
            return feeder != null ? feeder.DisplayName : null;
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
            bool on;
            try { on = thing.OnOff; }
            catch { return string.Empty; }   // a Thing whose OnOff backing is absent (a prefab
                                             // template, or a broken modded getter) renders
                                             // switchless; a tooltip must never throw
            string word = on
                ? $"<color={StateGreen}>{ActionStrings.On}</color>"
                : $"<color={CalmGrey}>{ActionStrings.Off}</color>";
            return word + $"<color={CalmGrey}> - </color>";
        }

        // The current-mismatch incompatible-device block: a grey header, then one indented device
        // per line (the pretty prefab name in FaultRed with a grey " - " bullet), capped at four by
        // the detector. When the detector appended its "+N" overflow marker
        // (CurrentMismatchFaultDetector.BuildViolatorNames writes "Name\nName\nName\n+N" past four
        // distinct names) the marker renders as a grey " - And N more devices..." row. An empty
        // payload (mid-sync race) renders no device lines; the explanation line above still names
        // the rule.
        private static string IncompatibleDeviceLines(string violators)
        {
            if (string.IsNullOrEmpty(violators)) return string.Empty;
            var lines = violators.Split('\n');
            string block = string.Empty;
            bool headerWritten = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string name = lines[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (!headerWritten)
                {
                    block += $"\n<color={CalmGrey}>Incompatible devices on this network:</color>";
                    headerWritten = true;
                }
                if (name[0] == '+' && int.TryParse(name.Substring(1), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int more))
                    block += $"\n<color={CalmGrey}> - And {more} more devices...</color>";
                else
                    block += $"\n<color={CalmGrey}> - </color><color={FaultRed}>{name}</color>";
            }
            return block;
        }

        // The deprioritized "Reason:" block: grey lines chosen by the cause the allocator recorded
        // at shed time (DeprioritizeReason). LowerPriority names the losing priority value;
        // EqualWattCover explains the decision-33 within-tier choice: the victim was part of the
        // smallest combined cut, in watts, that covered the remaining shortfall.
        private static string ReasonLines(DeprioritizeReason reason, int victimPriority)
        {
            switch (reason)
            {
                case DeprioritizeReason.EqualWattCover:
                    return $"\n<color={CalmGrey}>Reason: Its priority was equal to other consumers</color>"
                         + $"\n<color={CalmGrey}>Reason: It was part of the smallest combined cut covering the shortfall</color>";
                default:   // LowerPriority
                    return $"\n<color={CalmGrey}>Reason: Its priority of {victimPriority} was below other consumers</color>";
            }
        }

        // The device label substituted into the DeviceOverloadedFault title (locked matrix):
        // the throughput-rated segmenters read "Transformer" / "Link" (the wireless
        // transmitter / receiver pair; they derive from WirelessPower, not Transformer), the
        // storage elastics "Battery" / "APC" / "Umbilical". Unknown overload-capable devices
        // (and the fixture's null thing) fall back to the IC10 name's own generic "Device".
        // What power sources the device class can physically route to its output: through
        // upstream passthrough, from internal storage, or both. Drives the overload hover's
        // source-breakdown line (the class capability decides what renders, never the current
        // values, so a zero-valued part still teaches).
        private enum SourceMode { PassthroughOnly, StorageOnly, Both }

        private static SourceMode OverloadSourceMode(Thing thing)
        {
            switch (thing)
            {
                case AreaPowerControl _:
                    return SourceMode.Both;
                case Battery _:
                case RocketPowerUmbilicalMale _:
                case RocketPowerUmbilicalFemale _:
                    return SourceMode.StorageOnly;
                default:
                    return SourceMode.PassthroughOnly;   // Transformer, wireless link, unknown
            }
        }

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
