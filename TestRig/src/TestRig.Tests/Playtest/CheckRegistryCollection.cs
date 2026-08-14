using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Serialises every test class that touches the process-global check registry.
/// </summary>
/// <remarks>
///     <para>
///     The registry cannot be reset. Each check file registers itself from its own
///     <c>[ModuleInitializer]</c>, which runs once per assembly load and can never be run
///     again, so a class that clears it does not start from a clean slate: it removes the
///     eight shipped checks from the whole run, and any class reading them afterwards sees an
///     empty collection and fails for a reason that has nothing to do with what it asserts.
///     </para>
///     <para>
///     xUnit parallelises across classes, so the two that touch it were racing. The failure
///     was intermittent and moved with unrelated timing changes elsewhere in the suite, which
///     is the worst shape a test failure can have: it reads as flakiness in whichever class
///     lost the race rather than as pollution from the one that caused it.
///     </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CheckRegistryCollection
{
    public const string Name = "check-registry";
}
