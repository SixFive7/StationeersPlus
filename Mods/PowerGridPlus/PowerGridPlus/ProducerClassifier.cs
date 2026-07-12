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
    ///     Classifies devices for the producer-isolation rule (POWER.md §8.5, strict-literal): a power
    ///     producer may only connect to a transformer or to other producers. If a producer shares a cable
    ///     network with any device that is neither a producer nor a Transformer, it is faulted
    ///     (VARIABLE_VOLTAGE_FAULT).
    ///
    ///     <para>"Producer" = the game's generator classes. "Flashable producer" = a producer with an OnOff
    ///     button that can host a red flash; the rest (SolarPanel, both wind turbines, RTG) are hover-only.
    ///     Base-class checks cover the variants: WindTurbineGenerator covers LargeWindTurbineGenerator,
    ///     PowerGeneratorPipe covers GasFuelGenerator, PowerGeneratorSlot covers SolidFuelGenerator.</para>
    ///
    ///     <para>Only a Transformer is allowed next to producers, and its presence exempts nothing else
    ///     (full-strict, user decision 2026-07-12): Battery / AreaPowerControl / PowerTransmitter /
    ///     PowerReceiver / rocket umbilical are foreign devices that fault the producers themselves. So
    ///     SolarPanel + Battery faults the solar even with a Transformer on the same net; the battery
    ///     belongs behind the transformer.</para>
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

        // The unknown-producer-like classification (supplies power, not in the known class list, not
        // a segmenter) lives on the snapshot rows now: GridSnapshot.Build computes it from the
        // boundary-read output (row.UnknownProducerLike), and the producer-isolation walk consumes
        // the row. The cable-burn fallback semantics are unchanged (POWER.md §0.5).
    }
}
