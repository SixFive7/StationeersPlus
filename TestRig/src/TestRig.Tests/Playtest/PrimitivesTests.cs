using System.Text.Json;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;
using TestRig.Playtest.Seams;
using TestRig.Playtest.Values;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The pure functions everything else stands on: path selection, comparison, slugs, bare
///     paths and query building.
/// </summary>
/// <remarks>
///     Nothing here is faked, because there is nothing to fake: these are functions over
///     hand-built inputs, and they are where two of the highest-severity defects lived.
/// </remarks>
public sealed class PrimitivesTests
{
    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    // ---- select path ------------------------------------------------------

    [Fact]
    public void DotAndEmptySelectTheWholeObject()
    {
        var node = Parse("""{"a":1}""");
        Assert.Same(node, SelectPath.Select(node, "."));
        Assert.Same(node, SelectPath.Select(node, string.Empty));
        Assert.Same(node, SelectPath.Select(node, null));
    }

    [Fact]
    public void DottedPathsWalkNestedObjects()
    {
        var node = Parse("""{"state":{"shared":"MetallicPaints","ownedMask":3}}""");
        Assert.Equal("MetallicPaints", ValueText.Render(SelectPath.Select(node, "state.shared")));
        Assert.Equal("3", ValueText.Render(SelectPath.Select(node, "state.ownedMask")));
    }

    [Fact]
    public void ArrayIndexingSelectsOneRow()
    {
        var node = Parse("""{"connectedClients":[{"username":"host"},{"username":"joiner"}]}""");
        Assert.Equal("joiner", ValueText.Render(SelectPath.Select(node, "connectedClients[1].username")));
    }

    [Fact]
    public void CountIsAPseudoMemberOnACollection()
    {
        var node = Parse("""{"connectedClients":[{"a":1},{"a":2},{"a":3}]}""");
        Assert.Equal("3", ValueText.Render(SelectPath.Select(node, "connectedClients.count")));
    }

    [Fact]
    public void CountOnASingleObjectIsOne()
    {
        // A single object standing in for a one-element collection.
        Assert.Equal("1", ValueText.Render(SelectPath.Select(Parse("""{"a":1}"""), "count")));
    }

    [Fact]
    public void ARealCountMemberWinsOverThePseudoMember()
    {
        Assert.Equal("7", ValueText.Render(SelectPath.Select(Parse("""{"count":7}"""), "count")));
    }

    [Fact]
    public void AnUnresolvedPathReadsAbsentRatherThanThrowing()
    {
        Assert.Null(SelectPath.Select(Parse("""{"a":1}"""), "b"));
        Assert.Null(SelectPath.Select(Parse("""{"a":{"b":1}}"""), "a.c.d"));
        Assert.Null(SelectPath.Select(Parse("""{"a":[1]}"""), "a[4]"));
    }

    [Fact]
    public void SelectingThroughAnAbsentParentIsAbsentNotAThrow()
    {
        Assert.Null(SelectPath.Select(Parse("""{"player":null}"""), "player.present"));
    }

    [Fact]
    public void MemberLookupIsCaseInsensitive()
    {
        // The PowerShell original resolved members through PSObject.Properties, which is
        // case-insensitive; a check that wrote HostPort kept working there.
        Assert.Equal("27801", ValueText.Render(SelectPath.Select(Parse("""{"hostPort":27801}"""), "HostPort")));
    }

    [Fact]
    public void IndexingAScalarAtZeroIsTheScalar()
    {
        Assert.Equal("5", ValueText.Render(SelectPath.Select(Parse("""{"a":5}"""), "a[0]")));
        Assert.Null(SelectPath.Select(Parse("""{"a":5}"""), "a[1]"));
    }

    [Fact]
    public void SelectingIntoNullIsAbsent() => Assert.Null(SelectPath.Select(null, "a.b"));

    // ---- comparison -------------------------------------------------------

    [Fact]
    public void BothAbsentIsEqualAndOneAbsentIsNot()
    {
        Assert.True(ValueText.AreEqual(null, null));
        Assert.False(ValueText.AreEqual("x", null));
        Assert.False(ValueText.AreEqual(null, JsonValue.Create("x")));
    }

    [Fact]
    public void BooleansCompareAsBooleansAcrossRenderings()
    {
        Assert.True(ValueText.AreEqual(true, JsonValue.Create(true)));
        Assert.True(ValueText.AreEqual(true, JsonValue.Create("True")));
        Assert.True(ValueText.AreEqual(true, JsonValue.Create("true")));
        Assert.True(ValueText.AreEqual(true, JsonValue.Create("1")));
        Assert.True(ValueText.AreEqual(false, JsonValue.Create(false)));
    }

    [Fact]
    public void TheBooleanCoercionIsDeliberatelyNarrow()
    {
        // A permissive truthiness rule turns "the endpoint answered something unexpected"
        // into "the endpoint agreed". Only true and 1 coerce true.
        Assert.False(ValueText.AreEqual(true, JsonValue.Create("yes")));
        Assert.False(ValueText.AreEqual(true, JsonValue.Create(2)));
        Assert.True(ValueText.AreEqual(false, JsonValue.Create("yes")));
    }

    [Fact]
    public void NumbersCompareAsNumbers()
    {
        Assert.True(ValueText.AreEqual(4, JsonValue.Create("4")));
        Assert.True(ValueText.AreEqual("4.0", JsonValue.Create(4)));
        Assert.False(ValueText.AreEqual(4, JsonValue.Create(5)));
    }

    [Fact]
    public void EverythingElseComparesCaseInsensitively()
    {
        Assert.True(ValueText.AreEqual("listenHost", JsonValue.Create("LISTENHOST")));
        Assert.False(ValueText.AreEqual("listenHost", JsonValue.Create("joinedClient")));
    }

    [Fact]
    public void ABooleanIsNotANumber()
    {
        // PowerShell coerced true to 1 through [double], which made "at least 1" pass against
        // hosting and say nothing at all.
        Assert.False(ValueText.TryAsNumber(JsonValue.Create(true), out _));
        Assert.False(ValueText.TryAsNumber(true, out _));
    }

    [Fact]
    public void RenderingMatchesWhatAMessagePrints()
    {
        Assert.Equal(string.Empty, ValueText.Render(null));
        Assert.Equal("True", ValueText.Render(JsonValue.Create(true)));
        Assert.Equal("False", ValueText.Render(JsonValue.Create(false)));
        Assert.Equal("4", ValueText.Render(JsonValue.Create(4)));
        Assert.Equal("listenHost", ValueText.Render(JsonValue.Create("listenHost")));
        Assert.Equal("a b", ValueText.Render(Parse("""["a","b"]""")));
    }

    // ---- matchers ---------------------------------------------------------

    [Fact]
    public void IsAndIsNotAreOpposites()
    {
        Assert.True(ValueMatcher.Is(3).Evaluate(JsonValue.Create(3)).Satisfied);
        Assert.False(ValueMatcher.IsNot(3).Evaluate(JsonValue.Create(3)).Satisfied);
        Assert.True(ValueMatcher.IsNot(4).Evaluate(JsonValue.Create(3)).Satisfied);
    }

    [Fact]
    public void MatchesIsACaseInsensitiveRegex()
    {
        Assert.True(ValueMatcher.Matches("^listen").Evaluate(JsonValue.Create("listenHost")).Satisfied);
        Assert.True(ValueMatcher.Matches("METALLIC").Evaluate(JsonValue.Create("MetallicPaints")).Satisfied);
        Assert.False(ValueMatcher.Matches("^joined").Evaluate(JsonValue.Create("listenHost")).Satisfied);
    }

    [Fact]
    public void MatchesRendersItsOwnPatternAndNotAnythingElse()
    {
        // Defect P-09: in PowerShell the -match operator wrote the automatic $Matches
        // variable into the same typed local, so a SATISFIED match logged
        // "matches /System.Collections.Hashtable/". Verified empirically at the time.
        // In C# the pattern is a field on the matcher and nothing writes to it.
        var matcher = ValueMatcher.Matches("^listen");
        Assert.True(matcher.Evaluate(JsonValue.Create("listenHost")).Satisfied);
        Assert.Equal("matches /^listen/", matcher.Wants);
        Assert.True(matcher.Evaluate(JsonValue.Create("listenHost")).Satisfied);
        Assert.Equal("matches /^listen/", matcher.Wants);
    }

    [Fact]
    public void BoundsCompareNumerically()
    {
        Assert.True(ValueMatcher.AtLeast(12).Evaluate(JsonValue.Create(12)).Satisfied);
        Assert.True(ValueMatcher.AtLeast(12).Evaluate(JsonValue.Create(15)).Satisfied);
        Assert.False(ValueMatcher.AtLeast(12).Evaluate(JsonValue.Create(4)).Satisfied);
        Assert.True(ValueMatcher.AtMost(3).Evaluate(JsonValue.Create(3)).Satisfied);
        Assert.False(ValueMatcher.AtMost(3).Evaluate(JsonValue.Create(4)).Satisfied);
    }

    [Fact]
    public void AtMostAgainstAnAbsentValueDoesNotPass()
    {
        // Defect P-10, the highest-severity vacuity in the harness. An unresolved select path
        // rendered as the empty string, [double]'' is 0 in PowerShell, and 0 <= n held for
        // every non-negative bound, so a typo turned the assertion into a guaranteed pass.
        var verdict = ValueMatcher.AtMost(200).Evaluate(null);
        Assert.False(verdict.Satisfied);
        Assert.Contains("ABSENT", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AtLeastAgainstAnAbsentValueDoesNotPassEither()
    {
        var verdict = ValueMatcher.AtLeast(0).Evaluate(null);
        Assert.False(verdict.Satisfied);
        Assert.Contains("ABSENT", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ABoundAgainstANonNumericValueIsNotSatisfiedAndDoesNotThrow()
    {
        // Defect P-11: this threw "Cannot convert value listenHost to type System.Double",
        // which was unmarked and therefore landed as inconclusive/unclassified-error rather
        // than as a failure. The value WAS read and it is not a number.
        var verdict = ValueMatcher.AtLeast(1).Evaluate(JsonValue.Create("listenHost"));
        Assert.False(verdict.Satisfied);
        Assert.Contains("not numeric", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonNumericBoundIsACheckMistakeAndThrows()
    {
        // A bound the check supplied is not a reading, so it may never accuse the mod.
        Assert.Throws<ArgumentException>(() => ValueMatcher.AtLeast("banana"));
    }

    [Fact]
    public void ContainsRunsThroughWildcardSemantics()
    {
        Assert.True(ValueMatcher.Contains("Metallic").Evaluate(JsonValue.Create("MetallicPaints")).Satisfied);
        Assert.True(ValueMatcher.Contains("metallic").Evaluate(JsonValue.Create("MetallicPaints")).Satisfied);
        Assert.False(ValueMatcher.Contains("Chrome").Evaluate(JsonValue.Create("MetallicPaints")).Satisfied);
    }

    [Fact]
    public void ContainsSearchesEveryElementOfACollection()
    {
        Assert.True(ValueMatcher.Contains("joiner").Evaluate(Parse("""["host","joiner"]""")).Satisfied);
    }

    [Fact]
    public void EveryMatcherRendersTheClauseAMessageUses()
    {
        Assert.Equal("is [3]", ValueMatcher.Is(3).Wants);
        Assert.Equal("is [True]", ValueMatcher.Is(true).Wants);
        Assert.Equal("is not [3]", ValueMatcher.IsNot(3).Wants);
        Assert.Equal("is at least [12]", ValueMatcher.AtLeast(12).Wants);
        Assert.Equal("is at most [0]", ValueMatcher.AtMost(0).Wants);
        Assert.Equal("contains [x]", ValueMatcher.Contains("x").Wants);
    }

    // ---- slugs ------------------------------------------------------------

    [Theory]
    [InlineData("the first-use notice cap", "the-first-use-notice-cap")]
    [InlineData("GET /status", "get-status")]
    [InlineData("Section/Key", "section-key")]
    [InlineData("---", Slug.Empty)]
    [InlineData("", Slug.Empty)]
    [InlineData(null, Slug.Empty)]
    public void SlugsAreFileNameSafeAndStable(string? input, string expected) =>
        Assert.Equal(expected, Slug.Of(input));

    [Fact]
    public void SlugsAreTruncatedAndReTrimmed()
    {
        var slug = Slug.Of(new string('a', 80));
        Assert.Equal(Slug.MaxLength, slug.Length);

        // The truncation must not leave a trailing separator behind.
        var trailing = Slug.Of(new string('a', Slug.MaxLength) + " tail");
        Assert.NotEqual('-', trailing[^1]);
    }

    // ---- bare paths -------------------------------------------------------

    [Theory]
    [InlineData("/status", "/status")]
    [InlineData("/status/", "/status")]
    [InlineData("/STATUS", "/status")]
    [InlineData("/thing?refIds=442&fields=CustomColor", "/thing")]
    [InlineData("/console/log?limit=1", "/console/log")]
    [InlineData("", "")]
    public void BarePathsDropQueryStringsAndCase(string input, string expected) =>
        Assert.Equal(expected, Paths.Bare(input));

    [Fact]
    public void BarePathMattersBecauseAQueryIsHowAWindowsPathIsSent()
    {
        // Matching on the raw path would miss every request that carried one, which is every
        // thing, config and console read a check makes.
        Assert.Equal(Endpoints.Connect, Paths.Bare("/connect?address=127.0.0.1&port=27801"));
    }

    // ---- query building ---------------------------------------------------

    [Fact]
    public void AQueryComesFromAContractsRequestRecord()
    {
        var query = RigWire.Query(new ConsoleLogRequest { Since = 12, Source = "console", Contains = "hello", Limit = 200 });
        Assert.Equal("?contains=hello&limit=200&since=12&source=console", query);
    }

    [Fact]
    public void UnsetMembersAreOmittedFromTheQuery()
    {
        Assert.Equal("?guid=net.example", RigWire.Query(new ConfigRequest { Guid = "net.example" }));
        Assert.Equal(string.Empty, RigWire.Query(new ConfigRequest()));
        Assert.Equal(string.Empty, RigWire.Query(null));
    }

    [Fact]
    public void QueryValuesArePercentEncodedSoAPathSurvives()
    {
        var query = RigWire.Query(new ThingRequest { RefIds = "442", Fields = "CustomColor.Index" });
        Assert.Contains("fields=CustomColor.Index", query, StringComparison.Ordinal);
        Assert.Contains("refIds=442", query, StringComparison.Ordinal);
    }

    [Fact]
    public void ABodyThatIsNotAWireTypeCannotBeSent()
    {
        // Hand-built payloads are what this port exists to remove. The fake transport cannot
        // drift from the plugin because a body has to be a Contracts record to serialize at
        // all.
        var thrown = Assert.Throws<PlaytestUsageException>(() => RigWire.Serialize(new { guid = "net.example" }));
        Assert.Contains("TestRig.Contracts", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWireTypeSerializesToTheShapeThePluginParses()
    {
        var json = RigWire.Serialize(new ConfigSetRequest { Guid = "g", Section = "s", Key = "k", Value = "true", Save = false });
        Assert.Equal("""{"guid":"g","section":"s","key":"k","value":"true","save":false}""", json);
    }

    // ---- a body that does not fit the contract is loud, and names the field ----

    /// <summary>
    ///     Null means the plugin sent nothing, and it means only that.
    /// </summary>
    [Fact]
    public void AnEmptyBodyIsTheOnlyThingThatDeserializesToNull()
    {
        Assert.Null(RigWire.Deserialize<StatusResponse>(string.Empty));
        Assert.Null(RigWire.Deserialize<StatusResponse>("   "));
    }

    /// <summary>
    ///     The original defect, from the reading end. A <c>long</c> connection id against the
    ///     old <c>int?</c> made the deserializer throw, and the catch turned one bad field
    ///     into a null for the WHOLE response, so the host's roster read as empty and the
    ///     harness said the joiner had never arrived.
    /// </summary>
    [Fact]
    public void ABodyThatDoesNotFitTheContractThrowsAndNamesTheFieldAndTheValue()
    {
        // referenceId is a long on a Thing row, so a string there is the same class of
        // disagreement in the other direction, and it is one the current contract still
        // rejects.
        var thrown = Assert.Throws<RigWireFormatException>(() => RigWire.Deserialize<ThingResponse>(
            """{"ok":true,"things":[{"requestedRefId":"442","found":true,"referenceId":"442"}]}"""));

        Assert.Contains("things[0].referenceId", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("\"442\"", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("ThingResponse", thrown.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(thrown.InnerException);
    }

    /// <summary>A number where the contract wants a string is named the same way.</summary>
    [Fact]
    public void ANumberWhereTheContractWantsAStringIsNamedWithItsValue()
    {
        var thrown = Assert.Throws<RigWireFormatException>(() => RigWire.Deserialize<StatusResponse>(
            """{"ok":true,"connectedClients":[{"clientId":"1","connectionId":189151461494586169}]}"""));

        Assert.Contains("connectedClients[0].connectionId", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("189151461494586169", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("number", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     A body that is not JSON at all still throws rather than vanishing, and says where
    ///     the parse gave up. There is no token to quote, so it does not invent one.
    /// </summary>
    [Fact]
    public void ABodyThatIsNotJsonAtAllStillThrowsRatherThanVanishing()
    {
        var thrown = Assert.Throws<RigWireFormatException>(
            () => RigWire.Deserialize<StatusResponse>("<html>404 not found</html>"));

        Assert.Contains("StatusResponse", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("The value there is", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The throw is classified as <c>inconclusive/wire-format</c>, so it accuses the wire
    ///     rather than the mod, and does so under a name a report reader can act on. It used
    ///     to be indistinguishable from "the plugin sent nothing".
    /// </summary>
    [Fact]
    public void AWireFormatFailureIsClassifiedInconclusiveUnderItsOwnDetector()
    {
        var thrown = Assert.Throws<RigWireFormatException>(() => RigWire.Deserialize<StatusResponse>(
            """{"ok":true,"connectedClients":[{"clientId":"1","connectionId":189151461494586169}]}"""));

        var classified = SignalClassifier.Classify(thrown);

        Assert.Equal(CheckOutcome.Inconclusive, classified.Outcome);
        Assert.Equal(Detectors.WireFormat, classified.Detector);
        Assert.Contains("connectedClients[0].connectionId", classified.Message, StringComparison.Ordinal);
        Assert.NotEqual(Detectors.UnclassifiedError, classified.Detector);
    }

    /// <summary>A wire-format failure nested inside another throw is still found.</summary>
    [Fact]
    public void AWireFormatFailureIsFoundThroughAnInnerChain()
    {
        var inner = new RigWireFormatException("the plugin's answer does not fit 'StatusResponse'");
        var classified = SignalClassifier.Classify(new InvalidOperationException("rethrown", inner));

        Assert.Equal(Detectors.WireFormat, classified.Detector);
        Assert.Equal(CheckOutcome.Inconclusive, classified.Outcome);
    }

    /// <summary>
    ///     A real signal still wins. A check that declared itself inconclusive must not be
    ///     relabelled just because something wire-shaped is in its inner chain.
    /// </summary>
    [Fact]
    public void ASignalStillOutranksAWireFormatFailureInTheSameChain()
    {
        var signal = PlaytestSignal.Inconclusive("the check declined", Detectors.CheckDeclined);
        var wrapped = new InvalidOperationException("outer", new AggregateException(
            signal, new RigWireFormatException("does not fit")));

        Assert.Equal(Detectors.CheckDeclined, SignalClassifier.Classify(wrapped).Detector);
    }
}
