// Shared helpers for the Spray Paint Plus checks.
//
// Mod-local on purpose. The engine is mod-agnostic and nothing in it may name a mod, a
// prefab, a setting or a guid; this file is where those names are allowed to live, because
// it sits next to the mod it is about and is compiled only when that mod's playtests are.

using System.Globalization;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;

namespace SprayPaintPlus.Playtests;

internal static class Spp
{
    /// <summary>The plugin guid, as the running process knows it.</summary>
    /// <remarks>
    ///     Attestation does not read this: it derives the guid from About.xml through the
    ///     check's own location. This constant is only for the config reader and writer,
    ///     where the guid is a query parameter rather than a claim about identity.
    /// </remarks>
    internal const string ModGuid = "net.spraypaintplus";

    /// <summary>The first Metallic Paints swatch. 12 to 15 are the band.</summary>
    internal const int FirstMetallic = 12;

    /// <summary>A reference id, as the thing reader and the console want it.</summary>
    internal static string Id(long referenceId) => referenceId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The console sequence an observation carried, as the endpoint wants it back.</summary>
    internal static long Seq(Observation observation) =>
        ValueText.TryAsNumber(observation.Value, out var value) ? (long)value : 0;

    /// <summary>
    ///     Runs a cleanup step and swallows whatever it throws.
    /// </summary>
    /// <remarks>
    ///     Deliberately catches everything, which is what the PowerShell <c>try { } catch { }</c>
    ///     around every cleanup action did. A cleanup step that throws must never replace the
    ///     check's real outcome, and must never stop the steps after it: the last one is
    ///     usually the one that matters most.
    /// </remarks>
    internal static void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception)
        {
        }
    }
}
