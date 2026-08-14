using System.Text.Json;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// The structured output mode.
/// </summary>
/// <remarks>
/// The rig had no machine-readable output at all. Its one machine-readable contract was a
/// single printed line, which never printed, and everything else was prose that callers
/// scraped: the playtest harness recovered the lock owner id with two regexes over a
/// human-readable block, so any wording change would have broken every check at once. Nothing
/// may need to scrape prose again.
/// </remarks>
[Collection("cli")]
public sealed class JsonOutputTests(CliFixture rig)
{
    [Theory]
    [InlineData("status")]
    [InlineData("list")]
    [InlineData("logs")]
    [InlineData("snapshot")]
    [InlineData("wait")]
    public void EveryReadOnlyVerbAnswersInJson(string verb)
    {
        var home = rig.NewHome("readonly");
        CliFixture.Provision(home, "hostie");

        var target = verb is "snapshot" or "wait" ? "clients" : "all";
        string[] args = verb == "wait"
            ? [verb, "--target", target, "--wait-seconds", "1", "--json"]
            : [verb, "--target", target, "--json"];

        var result = rig.RunIn(home, args);

        using var doc = result.Json();
        var root = doc.RootElement;
        Assert.Equal(verb, root.GetProperty("verb").GetString());
        Assert.Equal(result.ExitCode, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(result.ExitCode == 0, root.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("values").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("lines").ValueKind);
    }

    [Fact]
    public void TheEnvelopeCarriesEverySection()
    {
        var result = rig.Run("status", "--json");
        using var doc = result.Json();
        foreach (var field in new[] { "ok", "verb", "exitCode", "error", "values", "refusal", "lines" })
            Assert.True(doc.RootElement.TryGetProperty(field, out _), $"the envelope has no '{field}'");
    }

    [Fact]
    public void NothingLandsOnStandardErrorInJsonMode()
    {
        // A caller redirecting stdout gets the whole answer; stderr carries only the fatal
        // message, and there is not one here.
        var result = rig.Run("status", "--json");
        Assert.Equal(string.Empty, result.StdErr.Trim());
    }

    [Fact]
    public void ProseBecomesLinesRatherThanDisappearing()
    {
        var (home, _) = rig.LockedHome("proselines");
        var result = rig.RunIn(home, "status", "--json");
        using var doc = result.Json();

        var lines = doc.RootElement.GetProperty("lines");
        Assert.True(lines.GetArrayLength() > 0);
        foreach (var line in lines.EnumerateArray())
        {
            Assert.Contains(
                line.GetProperty("level").GetString(),
                new[] { "detail", "info", "warning", "error" });
            Assert.NotNull(line.GetProperty("text").GetString());
        }
    }

    [Fact]
    public void ARecordedValueIsNeverEmittedTwice()
    {
        // The lock service records the owner and the CLI may record it again. An object with
        // the key twice means something different depending on which duplicate a parser keeps.
        var home = rig.NewHome("dupes");
        var result = rig.RunIn(home, "lock", "--purpose", "duplicate keys", "--keep-state", "--json");

        var values = ReadRawValueKeys(result.StdOut);
        Assert.Equal(values.Count, values.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("owner", values, StringComparer.Ordinal);
    }

    [Fact]
    public void AUsageErrorIsStructuredToo()
    {
        var result = rig.Run("status", "server", "--json");
        using var doc = result.Json();
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("--target", doc.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVerbIsStructuredToo()
    {
        var result = rig.Run("bogusverb", "--json");
        using var doc = result.Json();
        Assert.Equal(2, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("is not a testrig verb", doc.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonMayBeSpelledTheWayEveryOtherOptionMayBe()
    {
        foreach (var spelling in new[] { "--json", "-json", "-Json", "--json=true" })
        {
            var result = rig.Run("status", spelling);
            using var doc = result.Json();
            Assert.Equal("status", doc.RootElement.GetProperty("verb").GetString());
        }
    }

    /// <summary>
    /// A half's prose reaches the JSON envelope as lines rather than being written past it.
    /// </summary>
    /// <remarks>
    /// The two sinks are two renderings of the same events, so a half must never write to the
    /// console itself. If one did, its output would land on the terminal beside the JSON
    /// document and the envelope would be missing exactly the lines that explain what happened.
    /// </remarks>
    [Fact]
    public void AHalfsOwnOutputLandsInsideTheEnvelope()
    {
        var home = rig.NewHome("halflines");
        var log = Path.Combine(home, "DedicatedServer", "data", "server.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, Enumerable.Range(1, 40).Select(i => $"line {i}"));

        var result = rig.RunIn(home, "logs", "--target", "server", "--tail", "7", "--json");
        using var doc = result.Json();

        var lines = doc.RootElement.GetProperty("lines")
            .EnumerateArray().Select(l => l.GetProperty("text").GetString()!).ToArray();

        // Exactly the tail it was asked for, inside the envelope, and nothing outside it.
        Assert.Contains("line 40", lines);
        Assert.DoesNotContain("line 33", lines);
        Assert.Equal(string.Empty, result.StdErr.Trim());
    }

    /// <summary>Property names inside <c>values</c>, in document order, duplicates included.</summary>
    private static List<string> ReadRawValueKeys(string json)
    {
        var keys = new List<string>();
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        var depth = -1;
        var inValues = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && !inValues
                && reader.GetString() == "values")
            {
                inValues = true;
                continue;
            }

            if (!inValues) continue;

            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    depth++;
                    break;
                case JsonTokenType.EndObject:
                    if (depth == 0) return keys;
                    depth--;
                    break;
                case JsonTokenType.PropertyName when depth == 0:
                    keys.Add(reader.GetString()!);
                    break;
            }
        }

        return keys;
    }
}
