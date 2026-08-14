using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Runner;

/// <summary>What to run.</summary>
public sealed class SuiteRequest
{
    /// <summary>Names the evidence folder and the run report.</summary>
    public required string SuiteName { get; init; }

    /// <summary>The checks, in the order they will run.</summary>
    public required IReadOnlyList<IPlaytestCheck> Checks { get; init; }

    /// <summary>Where the bundle goes.</summary>
    public required string EvidenceRoot { get; init; }

    /// <summary>A wildcard over check names. Applied once.</summary>
    public string Only { get; init; } = "*";

    /// <summary>
    ///     Seconds to queue for the rig lock, per check. Zero means fail at once if another
    ///     session holds it. It is a queue, not a reservation, and it promises no fairness.
    /// </summary>
    public int LockWaitSeconds { get; init; }
}

/// <summary>What a whole run produced.</summary>
public sealed record SuiteResult(
    string Suite,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    int Passed,
    int Failed,
    int Inconclusive,
    int ExitCode,
    SaveInventoryComparison Tier1,
    IReadOnlyList<CheckResult> Results);

/// <summary>
///     Runs a suite: one bundle, one tier-1 snapshot on either side, one lock per check.
/// </summary>
public sealed class SuiteRunner
{
    private readonly PlaytestDependencies _deps;

    public SuiteRunner(PlaytestDependencies dependencies) =>
        _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

    /// <summary>Exit code when at least one check failed. The mod is the suspect.</summary>
    public const int ExitFailed = 1;

    /// <summary>
    ///     Exit code when nothing failed but something was inconclusive.
    /// </summary>
    /// <remarks>
    ///     Distinct from <see cref="ExitFailed"/> on purpose: a caller that cannot tell them
    ///     apart will eventually treat one as the other, and the whole three-outcome model
    ///     exists because those two mean opposite things about the mod.
    /// </remarks>
    public const int ExitInconclusive = 2;

    public SuiteResult Run(SuiteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var selected = Select(request.Checks, request.Only);
        if (selected.Count == 0)
        {
            throw new PlaytestUsageException(
                $"No check matched '{request.Only}'. Registered: {string.Join(", ", request.Checks.Select(c => c.Spec.Name))}");
        }

        var startedUtc = _deps.Clock.UtcNow;
        var bundle = new EvidenceBundle(_deps.Files, request.EvidenceRoot, request.SuiteName, startedUtc);

        var before = SaveInventoryScanner.Capture(_deps.Files, _deps.Tier1SaveRoot);
        bundle.Write("save-inventory-before.txt", SaveInventoryScanner.Render(before, Stamps.Format(startedUtc)));

        var runner = new CheckRunner(_deps);
        var results = new List<CheckResult>(selected.Count);

        for (var i = 0; i < selected.Count; i++)
        {
            var check = selected[i];
            _deps.Log?.Invoke($"[Playtest] {check.Spec.Name}");

            var evidence = bundle.NewCheck(i + 1, check.Spec.Name);
            var folder = Path.GetFileName(evidence.Root);
            var result = runner.Run(check, evidence, folder, request.LockWaitSeconds);

            _deps.Log?.Invoke($"[Playtest] {check.Spec.Name}: {result.Text}");
            results.Add(result);
        }

        var endedUtc = _deps.Clock.UtcNow;
        var after = SaveInventoryScanner.Capture(_deps.Files, _deps.Tier1SaveRoot);
        bundle.Write("save-inventory-after.txt", SaveInventoryScanner.Render(after, Stamps.Format(endedUtc)));

        var tier1 = SaveInventoryScanner.Compare(before, after);
        bundle.Write("save-inventory.verdict.txt", SaveInventoryScanner.RenderVerdict(tier1));

        if (tier1.Verdict == Tier1Verdict.Changed)
        {
            _deps.Log?.Invoke(
                $"[Playtest] The developer's save folder listing CHANGED across this run. Nothing in the rig may write there. See {Path.Combine(bundle.Root, "save-inventory.verdict.txt")}.");
        }
        else if (tier1.Verdict == Tier1Verdict.RootMissing)
        {
            _deps.Log?.Invoke(
                $"[Playtest] The tier-1 save folder '{_deps.Tier1SaveRoot}' did not exist at either end of this run, so the safety check watched NOTHING. This is a wrong path, not a clean result.");
        }

        var failed = results.Count(r => r.Outcome == CheckOutcome.Fail);
        var inconclusive = results.Count(r => r.Outcome == CheckOutcome.Inconclusive);
        var passed = results.Count(r => r.Outcome == CheckOutcome.Pass);
        var exitCode = failed > 0 ? ExitFailed : inconclusive > 0 ? ExitInconclusive : 0;

        var suite = new SuiteResult(request.SuiteName, startedUtc, endedUtc, passed, failed, inconclusive, exitCode, tier1, results);
        bundle.Write("run.json", RenderRunJson(suite));
        bundle.Write("run.md", RenderRunMarkdown(suite));
        return suite;
    }

    /// <summary>
    ///     Applies the name filter, once.
    /// </summary>
    /// <remarks>
    ///     The PowerShell composition root filtered, then the suite function filtered again
    ///     from its own registry. Two filters over two sources is two places for a selection
    ///     to disagree with the report that names it.
    /// </remarks>
    public static IReadOnlyList<IPlaytestCheck> Select(IReadOnlyList<IPlaytestCheck> checks, string only)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (string.IsNullOrEmpty(only) || only == "*") return checks;

        var pattern = "^" + Regex.Escape(only).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return [.. checks.Where(c => regex.IsMatch(c.Spec.Name))];
    }

    internal static string RenderRunJson(SuiteResult suite)
    {
        var checks = new JsonArray();
        foreach (var result in suite.Results)
        {
            checks.Add((JsonNode)new JsonObject
            {
                ["name"] = result.Name,
                ["outcome"] = CheckResult.OutcomeText(result.Outcome),
                ["text"] = result.Text,
                ["degraded"] = result.Degraded,
                ["retries"] = result.Retries,
                ["worstAttempts"] = result.WorstAttempts,
                ["assertions"] = result.AssertionCount,
                ["detector"] = result.Detector,
                ["detectors"] = PlaytestJson.Array(result.Detectors),
                ["message"] = result.Message,
                ["durationMs"] = result.DurationMs,
                ["evidence"] = result.EvidenceFolder,
                ["lockOwner"] = result.LockOwner,
                ["teardownNotes"] = PlaytestJson.Array(result.TeardownNotes),
            });
        }

        var obj = new JsonObject
        {
            ["suite"] = suite.Suite,
            ["startedUtc"] = Stamps.Format(suite.StartedUtc),
            ["endedUtc"] = Stamps.Format(suite.EndedUtc),
            ["passed"] = suite.Passed,
            ["failed"] = suite.Failed,
            ["inconclusive"] = suite.Inconclusive,
            ["exitCode"] = suite.ExitCode,
            ["tier1SaveFolder"] = new JsonObject
            {
                ["root"] = suite.Tier1.Before.Root,
                ["verdict"] = SaveInventoryScanner.VerdictText(suite.Tier1.Verdict),
                ["identical"] = suite.Tier1.Identical,
                ["before"] = suite.Tier1.Before.Sha256,
                ["after"] = suite.Tier1.After.Sha256,
            },
            ["checks"] = checks,
        };

        return PlaytestJson.Write(obj);
    }

    internal static string RenderRunMarkdown(SuiteResult suite)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"# Playtest: {suite.Suite}\n\n");
        builder.Append(CultureInfo.InvariantCulture, $"Started {Stamps.Format(suite.StartedUtc)}, ended {Stamps.Format(suite.EndedUtc)}.\n\n");
        builder.Append("| Check | Outcome | Retries | Assertions | Detectors | Evidence |\n");
        builder.Append("|---|---|---|---|---|---|\n");

        foreach (var result in suite.Results)
        {
            builder.Append(CultureInfo.InvariantCulture,
                $"| {result.Name} | {result.Text} | {result.Retries} | {result.AssertionCount} | {string.Join(", ", result.Detectors)} | {result.EvidenceFolder} |\n");
        }

        builder.Append(CultureInfo.InvariantCulture,
            $"\nPassed {suite.Passed}, failed {suite.Failed}, inconclusive {suite.Inconclusive}. Exit code {suite.ExitCode}.\n");

        builder.Append(suite.Tier1.Verdict switch
        {
            Tier1Verdict.Identical => $"\nThe developer's save folder ({suite.Tier1.Before.Root}) is unchanged across this run.\n",
            Tier1Verdict.Changed => $"\nThe developer's save folder ({suite.Tier1.Before.Root}) CHANGED across this run. Nothing in the rig may write there.\n",
            _ => $"\nThe developer's save folder ({suite.Tier1.Before.Root}) did not exist at either end of this run, so nothing was watched. The tier-1 path is wrong.\n",
        });

        return builder.ToString();
    }
}
