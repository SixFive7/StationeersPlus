using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;   // Device base lives here (Device : SmallGrid)
using Assets.Scripts.Networks;
// WindTurbineGenerator + LargeWindTurbineGenerator live in the bare Objects namespace
// (decompile: namespace Objects), not Assets.Scripts.Objects. Alias to avoid a using-collision.
using WindTurbineGenerator = global::Objects.WindTurbineGenerator;

namespace PowerGridPlus
{
    /// <summary>
    ///     Classifies devices for the producer-isolation rule (POWER.md §8.5 / POWERTODO 1.6.5): a power
    ///     producer may only connect to a transformer or to other producers. If a producer shares a cable
    ///     network with a rigid consumer and no transformer, it is faulted (VARIABLE_VOLTAGE_FAULT).
    ///
    ///     <para>"Producer" = the game's generator classes. "Flashable producer" = a producer with an OnOff
    ///     button that can host a red flash; the rest (SolarPanel, both wind turbines, RTG) are hover-only.
    ///     Base-class checks cover the variants: WindTurbineGenerator covers LargeWindTurbineGenerator,
    ///     PowerGeneratorPipe covers GasFuelGenerator, PowerGeneratorSlot covers SolidFuelGenerator.</para>
    ///
    ///     <para>Only a Transformer satisfies isolation (POWERTODO Q1): Battery / APC / PT / PR / rocket
    ///     umbilical do NOT. So SolarPanel + Battery + Light still faults the solar; the battery is
    ///     transparent to the rule.</para>
    /// </summary>
    internal static class ProducerClassifier
    {
        internal static bool IsProducer(Device d)
        {
            return d is SolarPanel
                || d is WindTurbineGenerator               // covers LargeWindTurbineGenerator
                || d is RadioscopicThermalGenerator
                || d is PowerGeneratorPipe                 // covers GasFuelGenerator
                || d is PowerGeneratorSlot                 // covers SolidFuelGenerator
                || d is StirlingEngine
                || d is TurbineGenerator                   // small wall wind turbine (hover-only)
                || d is PowerConnector;                    // dynamic-generator dock (hover-only)
        }

        // The PowerConnector is a buttonless dock that forwards a docked portable generator's power
        // (Research/GameSystems/PowerProducerOnOffState.md). It is a source only while a generator is
        // docked AND producing. Read the generator's RAW PowerGenerated, not the connector's
        // GetGeneratedPower (the VARIABLE_VOLTAGE_FAULT enforcement postfix zeroes that, so gating on
        // it would oscillate). PowerGenerated is already gated on the generator's OnOff && Powered, so
        // an off / unfuelled / absent generator reports 0. Worker-thread safe: managed reference
        // check (is null), never the Unity (bool)/==null native operator.
        internal static bool ConnectorIsDelivering(PowerConnector pc)
        {
            var g = pc.ConnectedDynamicGenerator;
            return !(g is null) && g.PowerGenerated > 0f;
        }

        // The form of "producer" the isolation rule actually faults (POWER.md §8.5): a producer feeds
        // unregulated voltage only while it is an ACTIVE source right now. This is the VVF analog of
        // the elastic-overload "ON cohort" (§8.4.1).
        //   - PowerConnector: a docked generator is delivering (the dock is a transparent proxy).
        //   - a producer with an on/off button (gas / solid / stirling): it is switched ON.
        //   - a buttonless producer (solar / wind / wall turbine / RTG): always -- it cannot be
        //     switched off and produces whenever the environment allows. Gating these on
        //     GetGeneratedPower would oscillate against the enforcement zero, so they stay class-based.
        internal static bool IsActiveProducer(Device d)
        {
            if (!IsProducer(d)) return false;
            if (d is PowerConnector pc) return ConnectorIsDelivering(pc);
            if (d.HasOnOffState) return d.OnOff;
            return true;
        }

        // A device that SUPPLIES power on this network but is in neither the known producer list nor
        // the segmenter list: a new vanilla class or an unclassified modded producer. The
        // producer-isolation rule catches it via the cable-burn fallback (POWER.md §0.5 decision:
        // fault+zero for known producers, cable burn for unknown ones so nothing slips through).
        internal static bool IsUnknownProducerLike(Device d, CableNetwork net)
        {
            if (d == null) return false;
            if (IsProducer(d)) return false;
            if (d is ElectricalInputOutput) return false;   // segmenters (incl. WirelessPower) are not producers
            try { return d.GetGeneratedPower(net) > 0f; }
            catch { return false; }
        }

        // A producer with an InteractableType.OnOff button (can host a red flash). The remaining producers
        // (SolarPanel, WindTurbineGenerator, LargeWindTurbineGenerator, RadioscopicThermalGenerator) are
        // hover-only.
        internal static bool IsFlashableProducer(Device d)
        {
            return d is PowerGeneratorPipe                 // covers GasFuelGenerator
                || d is PowerGeneratorSlot                 // covers SolidFuelGenerator
                || d is StirlingEngine;
        }

        // A rigid consumer is any power-drawing device that is NEITHER a producer NOR a segmenting device.
        // Segmenting devices (Transformer / Battery / APC / PT / PR / rocket umbilical) are transparent to
        // the producer-isolation rule and do not count as rigid consumers.
        internal static bool IsRigidConsumer(Device d, CableNetwork net)
        {
            if (d == null) return false;
            if (IsProducer(d)) return false;
            if (d is ElectricalInputOutput eio && SegmentingDeviceRegistry.IsSegmenter(eio)) return false;
            try { return d.GetUsedPower(net) > 0f; }
            catch { return false; }
        }
    }
}
