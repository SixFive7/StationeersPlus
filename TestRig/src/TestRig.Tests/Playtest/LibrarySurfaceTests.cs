using System.Reflection;
using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Readers;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Seams;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The surface everything else stands on, and the drift guards over it.
/// </summary>
/// <remarks>
///     The most valuable test in the PowerShell suite was reflection over the live parameter
///     attribute of three verbs against the live reader catalogue, because it caught the exact
///     mistake no amount of fake transport would: a reader added to one and not the others.
///     Here the reader is an enum, so that mistake is a compile error, and what is left to
///     guard is that every reader still has a description, an endpoint decision and a name.
/// </remarks>
public sealed class LibrarySurfaceTests
{
    [Fact]
    public void ThereAreThirteenReaders() => Assert.Equal(13, Enum.GetValues<Reader>().Length);

    [Fact]
    public void EveryReaderDocumentsItself()
    {
        foreach (var reader in Enum.GetValues<Reader>())
        {
            Assert.True(ReaderCatalogue.Descriptions.ContainsKey(reader), $"{reader} has no description");
            Assert.False(string.IsNullOrWhiteSpace(ReaderCatalogue.Descriptions[reader]), $"{reader} has an empty description");
        }
    }

    [Fact]
    public void EveryReaderHasAName()
    {
        var names = Enum.GetValues<Reader>().Select(ReaderCatalogue.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void EveryReaderExceptTheLogFileResolvesToARealEndpoint()
    {
        foreach (var reader in Enum.GetValues<Reader>())
        {
            var endpoint = ReaderCatalogue.Endpoint(reader);
            if (reader == Reader.BepInExLog)
            {
                Assert.Null(endpoint);
                continue;
            }

            Assert.NotNull(endpoint);
            Assert.True(Endpoints.Exists(endpoint), $"{reader} points at '{endpoint}', which the router does not answer");
        }
    }

    [Fact]
    public void OnlyTheReadersThatTakeAQueryAppendOne()
    {
        Reader[] withQuery = [Reader.Thing, Reader.Inventory, Reader.Config, Reader.Reflect, Reader.Nearby, Reader.Console];
        foreach (var reader in Enum.GetValues<Reader>())
        {
            Assert.Equal(withQuery.Contains(reader), ReaderCatalogue.TakesQuery(reader));
        }
    }

    [Fact]
    public void TheRosterReaderSharesTheStatusEndpointBecauseItIsANarrowing()
    {
        Assert.Equal(Endpoints.Status, ReaderCatalogue.Endpoint(Reader.Status));
        Assert.Equal(Endpoints.Status, ReaderCatalogue.Endpoint(Reader.Roster));
    }

    [Fact]
    public void TheContextExposesTheVerbsACheckIsWrittenAgainstAndNothingElse()
    {
        var verbs = typeof(IPlaytestContext).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        string[] expected =
        [
            "Act", "AssertAgreement", "AssertBinaryUnderTest", "AssertChange", "AssertValue",
            "ConnectJoiner", "Read", "RestartInstance", "SaveConsoleTail", "SetInconclusive",
            "Stamp", "Wait", "WaitStage", "WriteEvidence",
        ];

        foreach (var name in expected) Assert.Contains(name, verbs);
    }

    [Fact]
    public void DecliningIsMarkedAsNotReturningSoAGuardEndsTheCheck()
    {
        var method = typeof(IPlaytestContext).GetMethod(nameof(IPlaytestContext.SetInconclusive))!;
        Assert.NotNull(method.GetCustomAttribute<System.Diagnostics.CodeAnalysis.DoesNotReturnAttribute>());
    }

    [Fact]
    public void TheLockRefreshCadenceIsOnceAMinuteAtMost() =>
        Assert.Equal(TimeSpan.FromSeconds(60), PlaytestContext.LockRefreshInterval);

    [Fact]
    public void EveryDependencyIsRequiredSoThereIsNoUnwiredSeamState()
    {
        // The PowerShell library carried three paragraph-long errors explaining that the
        // composition root had forgotten to wire a seam. A required property cannot be absent.
        var required = typeof(PlaytestDependencies).GetProperties()
            .Where(p => p.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute"))
            .Select(p => p.Name)
            .ToList();

        foreach (var name in new[] { "Transport", "Launcher", "Registry", "Files", "LogFiles", "Clock", "Sleeper", "RigHome", "Tier1SaveRoot" })
        {
            Assert.Contains(name, required);
        }
    }

    [Fact]
    public void TheLoggerIsTheOnlyOptionalDependency()
    {
        var log = typeof(PlaytestDependencies).GetProperty(nameof(PlaytestDependencies.Log))!;
        Assert.DoesNotContain(log.GetCustomAttributes(), a => a.GetType().Name == "RequiredMemberAttribute");
    }

    [Fact]
    public void ARunnerCannotBeBuiltWithoutItsDependencies()
    {
        Assert.Throws<ArgumentNullException>(() => new SuiteRunner(null!));
        Assert.Throws<ArgumentNullException>(() => new CheckRunner(null!));
    }

    [Fact]
    public void EveryLauncherVerbTheHarnessUsesIsOnTheSeamAndNothingElseIs()
    {
        // Five verbs and no more: never a rig-wide target, never reset, create or deploy.
        var verbs = typeof(IRigLauncher).GetMethods().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(["AcquireLock", "RefreshLock", "ReleaseLock", "StartInstance", "StopInstance"], verbs);
    }

    [Fact]
    public void TheLauncherSeamNamesOneInstanceWhereverAnInstanceIsInvolved()
    {
        foreach (var name in new[] { nameof(IRigLauncher.StartInstance), nameof(IRigLauncher.StopInstance) })
        {
            var method = typeof(IRigLauncher).GetMethod(name)!;
            Assert.Equal("name", method.GetParameters()[0].Name);
            Assert.Equal(typeof(string), method.GetParameters()[0].ParameterType);
        }
    }

    [Fact]
    public void TheLockGrantCarriesTheOwnerAsAFieldRatherThanAsProse()
    {
        // The one stdout-scraping site in the PowerShell harness recovered the owner id with a
        // regex over launcher output, and the line it matched has never once been printed.
        var owner = typeof(LockGrant).GetProperty(nameof(LockGrant.Owner))!;
        Assert.Equal(typeof(string), owner.PropertyType);
    }

    [Fact]
    public void TheSleepGoesThroughTheInjectedSleeper()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        ctx.Wait(5);
        Assert.Contains(TimeSpan.FromSeconds(5), fixture.Sleeper.Delays);
    }

    [Fact]
    public void AZeroOrNegativeWaitCostsNothing()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        ctx.Wait(0);
        ctx.Wait(-1);
        Assert.Empty(fixture.Sleeper.Delays);
    }

    [Fact]
    public void TheStampComesFromTheInjectedClock()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        fixture.Clock.UtcNow = new DateTimeOffset(2026, 8, 14, 12, 34, 56, TimeSpan.Zero);
        Assert.Equal("2026-08-14T12:34:56Z", ctx.Stamp());
    }

    [Fact]
    public void TheCheckListingSaysWhatEachCheckNeeds()
    {
        IPlaytestCheck[] checks =
        [
            new TestCheck(new CheckSpec("alpha check", "does a thing",
            [
                new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar"),
                new InstanceSpec("joiner"),
            ]), _ => { }),
        ];

        var listing = PlaytestListing.Checks(checks);
        Assert.Contains("alpha check", listing, StringComparison.Ordinal);
        Assert.Contains("does a thing", listing, StringComparison.Ordinal);
        Assert.Contains("hostie (host) world Lunar", listing, StringComparison.Ordinal);
        Assert.Contains("joiner (client)", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingMarksWhatTheFilterWouldSkip()
    {
        IPlaytestCheck[] checks =
        [
            new TestCheck(new CheckSpec("alpha check", "s", [new InstanceSpec("hostie")]), _ => { }),
            new TestCheck(new CheckSpec("beta check", "s", [new InstanceSpec("hostie")]), _ => { }),
        ];

        var listing = PlaytestListing.Checks(checks, "alpha*");
        Assert.Contains("  - beta check", listing, StringComparison.Ordinal);
        Assert.Contains("    alpha check", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingInTheEngineNamesAModAPrefabASettingOrAGuid()
    {
        // The mod-agnostic invariant, held by the type system rather than by a document: the
        // engine assembly must not reference any type from the checks assembly, and its own
        // public surface must not mention one.
        var engine = typeof(SuiteRunner).Assembly;
        var referenced = engine.GetReferencedAssemblies().Select(a => a.Name).ToList();

        Assert.DoesNotContain("TestRig.Playtests", referenced);
        Assert.DoesNotContain(engine.GetTypes(), t => t.FullName!.Contains("SprayPaint", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheEngineCarriesNoModNamesInItsStringsEither()
    {
        var forbidden = new[] { "spraypaintplus", "StructureCableStraight", "ItemSprayCan", "MetallicPaints" };
        var engineTypes = typeof(SuiteRunner).Assembly.GetTypes();

        foreach (var constant in engineTypes.SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                     .Where(f => f is { IsLiteral: true, FieldType: { } type } && type == typeof(string)))
        {
            var value = (string?)constant.GetRawConstantValue() ?? string.Empty;
            foreach (var name in forbidden)
            {
                Assert.False(value.Contains(name, StringComparison.OrdinalIgnoreCase),
                    $"{constant.DeclaringType!.Name}.{constant.Name} names '{name}', and the engine is mod-agnostic");
            }
        }
    }
}
