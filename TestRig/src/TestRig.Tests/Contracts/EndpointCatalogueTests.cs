using System.Reflection;
using TestRig.Contracts;
using Xunit;

namespace TestRig.Tests.Contracts;

/// <summary>
///     The catalogue exists because a path typed as a string literal is only checked when a
///     request actually goes out, against a rig somebody had to take the lock for. The
///     PowerShell refusal matrix advised callers to drive <c>/console/run</c>, which has
///     never existed, and nothing anywhere caught it.
/// </summary>
public sealed class EndpointCatalogueTests
{
    /// <summary>
    ///     69 path strings covering 68 handlers, because <c>/</c> and <c>/help</c> share
    ///     one. The number is asserted so that adding a constant without adding it to
    ///     <c>All</c>, or the reverse, fails here.
    /// </summary>
    [Fact]
    public void AllCarriesSixtyNinePaths()
    {
        Assert.Equal(69, Endpoints.All.Count);
    }

    /// <summary>
    ///     The four scenario paths are in the catalogue rather than string literals.
    /// </summary>
    /// <remarks>
    ///     They were the last paths in the merged plugin's dispatch table still written as
    ///     literals, which is the exact shape of the <c>/console/run</c> mistake: a path only
    ///     checked when a request goes out, against a rig somebody had to take the lock for.
    /// </remarks>
    [Fact]
    public void TheScenarioPathsAreInTheCatalogue()
    {
        foreach (var path in new[]
                 {
                     Endpoints.Scenarios, Endpoints.ScenarioRun,
                     Endpoints.ScenarioArm, Endpoints.ScenarioDisarm,
                 })
        {
            Assert.True(Endpoints.Exists(path), path + " is not in the catalogue");
        }

        Assert.Equal("/scenarios", Endpoints.Scenarios);
        Assert.Equal("/scenario/run", Endpoints.ScenarioRun);
        Assert.Contains(Endpoints.ScenarioRun, Endpoints.Suggest("/scenario/go"));
    }

    [Fact]
    public void AllPathsAreDistinct()
    {
        Assert.Equal(Endpoints.All.Count, new HashSet<string>(Endpoints.All, StringComparer.Ordinal).Count);
    }

    [Fact]
    public void EveryDeclaredConstantIsInAll()
    {
        var declared = typeof(Endpoints)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(declared);
        foreach (string path in declared)
            Assert.Contains(path, Endpoints.All);
    }

    [Fact]
    public void EveryPathInAllExists()
    {
        foreach (string path in Endpoints.All)
            Assert.True(Endpoints.Exists(path), path + " is in All but Exists says no");
    }

    /// <summary>The measured failure: the refusal matrix named a path the router does not switch on.</summary>
    [Fact]
    public void ConsoleRunDoesNotExistAndConsoleExecDoes()
    {
        Assert.False(Endpoints.Exists("/console/run"));
        Assert.True(Endpoints.Exists(Endpoints.ConsoleExec));
        Assert.Equal("/console/exec", Endpoints.ConsoleExec);
    }

    [Fact]
    public void SuggestPointsConsoleRunAtConsoleExec()
    {
        IReadOnlyList<string> hits = Endpoints.Suggest("/console/run");

        Assert.Contains(Endpoints.ConsoleExec, hits);
        Assert.Contains(Endpoints.ConsoleLog, hits);
        Assert.DoesNotContain(Endpoints.Status, hits);
    }

    [Fact]
    public void SuggestReturnsTheExactPathWhenItIsAlreadyKnown()
    {
        Assert.Equal(new[] { Endpoints.Status }, Endpoints.Suggest("/STATUS/"));
    }

    [Fact]
    public void SuggestIsEmptyWhenTheFirstSegmentIsUnknown()
    {
        Assert.Empty(Endpoints.Suggest("/nonsense/route"));
    }

    /// <summary>
    ///     Matches the router: <c>TrimEnd('/')</c>, empty becomes <c>"/"</c>, then
    ///     lower-cased.
    /// </summary>
    [Theory]
    [InlineData("/STATUS", "/status")]
    [InlineData("/status/", "/status")]
    [InlineData("/Status", "/status")]
    [InlineData("/status", "/status")]
    [InlineData("///", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/Console/Exec/", "/console/exec")]
    public void NormalizeMatchesTheRouter(string? input, string expected)
    {
        Assert.Equal(expected, Endpoints.Normalize(input));
    }

    [Fact]
    public void TryResolveNormalizesAndValidatesTogether()
    {
        Assert.True(Endpoints.TryResolve("/Status/", out string? resolved));
        Assert.Equal(Endpoints.Status, resolved);

        Assert.False(Endpoints.TryResolve("/console/run", out string? missing));
        Assert.Null(missing);
    }

    /// <summary>
    ///     The router's own alias. Both strings hit one handler, so both must resolve and
    ///     both must be in the catalogue.
    /// </summary>
    [Fact]
    public void RootAndHelpAreBothPresent()
    {
        Assert.True(Endpoints.Exists(Endpoints.Root));
        Assert.True(Endpoints.Exists(Endpoints.Help));
        Assert.Equal("/", Endpoints.Root);
        Assert.Equal("/help", Endpoints.Help);
    }
}
