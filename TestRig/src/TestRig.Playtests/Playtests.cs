using TestRig.Playtest.Model;

namespace TestRig.Playtests;

/// <summary>
///     Every check compiled into this build, named one by one.
/// </summary>
/// <remarks>
///     <para>
///     <b>This list is the trimmer root, and that is the whole reason it exists.</b> Checks
///     used to register themselves from a <c>[ModuleInitializer]</c>, so nothing statically
///     referenced a check type. Under <c>PublishAot</c> with <c>TrimMode=full</c> a module
///     initializer is not a root, so ILC removed all eight classes from the shipped binary:
///     <c>testrig playtest --list-checks</c> printed an empty list and exited 0, while
///     <c>dotnet run</c> over the same sources listed all eight. A scan of the 16.7 MB binary
///     found none of the check names.
///     </para>
///     <para>
///     Three guards failed to catch that, and the shape matters more than the bug. The
///     source-hash guard covered <c>TestRig/src/</c> only, while checks live in
///     <c>Mods/*/playtests/</c>; <c>dotnet test</c> runs on CoreCLR where module initializers
///     DO run, so the offline suite asserted all eight were present and stayed green against
///     an artifact with none; and the listing exited 0 on an empty set, so an empty answer read
///     as a clean one. A direct <c>new</c> per check is trimmer-safe by construction: ILC keeps
///     a type a rooted method constructs.
///     </para>
///     <para>
///     The cost is a central list somebody can forget to extend, which self-registration
///     existed to avoid. Two tests hold it instead of the runtime:
///     <c>ShippedChecksTests.EveryCompiledCheckTypeIsInTheStaticList</c> reflects over this
///     assembly and fails on a check type nothing here constructs, and
///     <c>ShippedBinaryChecksTests</c> runs the SHIPPED binary and fails on a set that is not
///     the expected eight. A forgotten line cannot pass both.
///     </para>
/// </remarks>
public static class Playtests
{
    /// <summary>Every check, in the order they run.</summary>
    public static IReadOnlyList<IPlaytestCheck> All { get; } =
    [
        new SprayPaintPlus.Playtests.FirstUseNoticeCap(),
        new SprayPaintPlus.Playtests.JoinSummary(),
        new SprayPaintPlus.Playtests.EyedropperCrossFamilyLine(),
        new SprayPaintPlus.Playtests.EffectiveSettingsLogLine(),
        new SprayPaintPlus.Playtests.ConflictBanner(),
        new SprayPaintPlus.Playtests.HostOwnClientHalfMustNotLeak(),
        new SprayPaintPlus.Playtests.DlcNonOwnerReachesMetallic(),
        new SprayPaintPlus.Playtests.DlcEntitlementOutlivesTheOwner(),
    ];
}
