using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Attestation;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;
using TestRig.Playtest.Readers;
using TestRig.Playtest.Seams;
using TestRig.Playtest.Values;

namespace TestRig.Playtest.Runner;

/// <summary>
///     The verbs a check body runs against, and all the bookkeeping around them.
/// </summary>
/// <remarks>
///     One of these per check. Nothing here is static, so two checks cannot share a
///     sequence counter, a detector set or a flake catalogue.
/// </remarks>
public sealed class PlaytestContext : IPlaytestContext
{
    /// <summary>How often the rig lock is refreshed, at most.</summary>
    /// <remarks>
    ///     Refreshing happens only as a SIDE EFFECT of the harness actually driving
    ///     something. There is no background refresher and there must never be one: that
    ///     would hold the rig after the agent is gone. The stamp is updated BEFORE the
    ///     launcher call, so a failing refresh does not retry immediately.
    /// </remarks>
    public static readonly TimeSpan LockRefreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>Endpoints that freeze an instance's whole control plane while they run.</summary>
    public static readonly IReadOnlyList<string> BlockingPaths =
    [
        Endpoints.Host, Endpoints.Connect, Endpoints.Save, Endpoints.Load, Endpoints.NewWorld, Endpoints.WaitFor,
    ];

    /// <summary>Request timeout for a blocking endpoint.</summary>
    public const int BlockingTimeoutSeconds = 330;

    /// <summary>Request timeout for everything else.</summary>
    public const int DefaultTimeoutSeconds = 120;

    /// <summary>Request timeout for a reader.</summary>
    public const int ReaderTimeoutSeconds = 60;

    private readonly PlaytestDependencies _deps;
    private readonly List<string> _detectors = [];
    private int _sequence;
    private DateTimeOffset _lastRefreshUtc;

    public PlaytestContext(PlaytestDependencies dependencies, CheckSpec check, FlakeCatalogue flakes, CheckEvidence? evidence, string owner)
    {
        _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        Check = check ?? throw new ArgumentNullException(nameof(check));
        Flakes = flakes ?? throw new ArgumentNullException(nameof(flakes));
        Evidence = evidence;
        Owner = owner;
        _lastRefreshUtc = dependencies.Clock.UtcNow;
    }

    public CheckSpec Check { get; }

    public FlakeCatalogue Flakes { get; }

    public CheckEvidence? Evidence { get; }

    public string Owner { get; internal set; }

    public string RigHome => _deps.RigHome;

    public IFileSystem Files => _deps.Files;

    public IList<string> TeardownNotes { get; } = new List<string>();

    /// <summary>Instances this check has started, in start order. Teardown walks it.</summary>
    public IList<string> Started { get; } = new List<string>();

    public int AssertionCount { get; private set; }

    /// <summary>True once attestation has completed. A pass is gated on it.</summary>
    public bool BinaryAttested { get; private set; }

    /// <summary>The worst single operation's attempt count.</summary>
    public int WorstAttempts { get; private set; }

    /// <summary>Total retries across the check.</summary>
    public int Retries { get; private set; }

    /// <summary>True once anything needed retrying. A degraded pass is still a pass, never a clean one.</summary>
    public bool Degraded { get; private set; }

    /// <summary>Every detector recorded during the check, in first-seen order.</summary>
    public IReadOnlyList<string> RecordedDetectors => _detectors;

    /// <summary>The attestation report, once it exists.</summary>
    public AttestationReport? Attestation { get; private set; }

    // ---- bookkeeping ------------------------------------------------------

    internal void RecordAttempts(int attempts)
    {
        if (attempts > WorstAttempts) WorstAttempts = attempts;
        if (attempts > 1)
        {
            Retries += attempts - 1;
            Degraded = true;
        }
    }

    internal void RecordDetector(string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        if (!_detectors.Contains(name, StringComparer.Ordinal)) _detectors.Add(name);
    }

    internal int NextSequence() => ++_sequence;

    private void Say(string line) => _deps.Log?.Invoke(line);

    public string Stamp() => Stamps.Format(_deps.Clock.UtcNow);

    public void Wait(double seconds)
    {
        if (seconds <= 0) return;
        _deps.Sleeper.DelayAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
    }

    // ---- instance resolution ----------------------------------------------

    /// <summary>
    ///     The guard that enforces "assert on the authority".
    /// </summary>
    /// <remarks>
    ///     In PowerShell this also had to reject an object being passed where a name belonged,
    ///     because handing an action result to an assert verb string-coerced to
    ///     <c>@{...}</c>. The type signature does that here, so what is left is the half that
    ///     types cannot express: the name has to be one of THIS check's instances.
    /// </remarks>
    internal void RequireOwnInstance(string name, string parameter)
    {
        if (Check.InstanceNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return;

        throw new PlaytestUsageException(
            $"{parameter} '{name}' is not one of this check's instances ({string.Join(", ", Check.InstanceNames)}). " +
            "Name the instance that is the AUTHORITY for the value you are asserting: on a listen host that is the host for anything the server owns, and the joiner only for what its own client half decides.");
    }

    internal RigInstanceRow ResolveInstance(string name)
    {
        var rows = _deps.Registry.Rows();
        var row = rows.FirstOrDefault(r => string.Equals(r.InstanceName, name, StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row;

        var known = rows.Count == 0 ? "(none)" : string.Join(", ", rows.Select(r => r.InstanceName));
        throw PlaytestSignal.Inconclusive(
            $"'{name}' is not in the rig registry, so there is nothing to drive and nothing was measured about the mod. Known instances: {known}. " +
            $"Create it: testrig create -Target {name} -As <id> [-Role host]",
            Detectors.InstanceNotProvisioned,
            PlaytestJson.Detail(new Dictionary<string, object?> { ["instance"] = name, ["known"] = known }));
    }

    internal int ResolvePort(string name) => ResolveInstance(name).Port;

    /// <summary>The instance's BepInEx log, which lives under the game TREE, not the data folder.</summary>
    internal string ResolveLogPath(string name)
    {
        var row = ResolveInstance(name);
        var root = row.InstancesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            // The same fallback order the launcher uses. This step used to be missing, so an
            // entry written before the root was recorded resolved to a path a rig built on
            // the install's volume has never had, and the read came back "absent" rather than
            // wrong, which is the hardest kind of wrong to notice.
            root = Environment.GetEnvironmentVariable("STATIONEERS_CLIENTRIG_ROOT");
            if (string.IsNullOrWhiteSpace(root)) root = Path.Combine(_deps.RigHome, "ClientRig", "instances");
        }

        return Path.Combine(root, name, "BepInEx", "LogOutput.log");
    }

    // ---- the lock ---------------------------------------------------------

    internal void RefreshLockIfDue()
    {
        if (string.IsNullOrEmpty(Owner)) return;
        if (_deps.Clock.UtcNow - _lastRefreshUtc < LockRefreshInterval) return;

        _lastRefreshUtc = _deps.Clock.UtcNow;
        var result = _deps.Launcher.RefreshLock(Owner);
        RecordLauncher("refresh-lock", Owner, result);
        if (result.Success) return;

        var probe = new FlakeProbe(ProbeKind.Lock, Error: result.Message);
        var flake = Flakes.Resolve(probe);
        if (flake is not null) RecordDetector(flake.Name);

        throw PlaytestSignal.Inconclusive(
            $"The rig session lock could not be refreshed, so this check no longer owns the rig and anything it reads next could be another session's. {result.Message}",
            flake?.Name ?? "lock-lost",
            PlaytestJson.Detail(new Dictionary<string, object?> { ["owner"] = Owner, ["exit"] = result.ExitCode }));
    }

    internal void RecordLauncher(string verb, string arguments, LauncherResult result)
    {
        if (Evidence is null) return;

        var name = string.Create(CultureInfo.InvariantCulture, $"{NextSequence():d4}-{Slug.Of(verb + " " + arguments)}.txt");
        Evidence.Write(EvidenceKind.Launcher, name,
            EvidenceRecords.Launcher(verb, arguments, Stamp(), result.ExitCode, result.Message, result.Output));
    }

    // ---- driving ----------------------------------------------------------

    public ActionResult Act(string on, string path, object? body = null, bool blocking = false, bool noRetry = false, int? timeoutSeconds = null)
    {
        RequireOwnInstance(on, "on");
        RefreshLockIfDue();

        if (!Endpoints.Exists(path))
        {
            var suggestions = Endpoints.Suggest(path);
            throw new PlaytestUsageException(
                $"'{path}' is not an endpoint the plugin answers. " +
                (suggestions.Count > 0
                    ? $"Did you mean one of: {string.Join(", ", suggestions)}?"
                    : "GET /help lists them all.") +
                " Use the TestRig.Contracts.Endpoints constants; the PowerShell refusal matrix told callers to drive /console/run, which has never existed.");
        }

        var bodyJson = body is null ? string.Empty : RigWire.Serialize(body);
        var method = bodyJson.Length > 0 ? "POST" : "GET";
        var port = ResolvePort(on);
        var bare = Paths.Bare(path);
        var timeout = timeoutSeconds is > 0
            ? timeoutSeconds.Value
            : blocking || BlockingPaths.Contains(bare, StringComparer.Ordinal) ? BlockingTimeoutSeconds : DefaultTimeoutSeconds;

        var attempt = 1;
        while (true)
        {
            var started = _deps.Clock.UtcNow;
            var status = 0;
            var responseBody = string.Empty;
            var error = string.Empty;

            try
            {
                var response = _deps.Transport.Send(port, path, bodyJson.Length > 0 ? bodyJson : null, TimeSpan.FromSeconds(timeout));
                status = response.HttpStatus;
                responseBody = response.Body;
            }
            catch (RigTransportException ex)
            {
                error = ex.Message;
            }

            var elapsed = (long)(_deps.Clock.UtcNow - started).TotalMilliseconds;
            var parsed = PlaytestJson.TryParse(responseBody);
            var reference = WriteRequestRecord(on, method, path, attempt, elapsed, bodyJson, status, parsed, error);

            if (IsSuccess(error, status, parsed))
            {
                RecordAttempts(attempt);
                return new ActionResult(on, path, attempt, attempt > 1, status, responseBody, elapsed, reference);
            }

            // A non-2xx is a REFUSAL, not a transport fault. PowerShell threw on any non-2xx
            // with the body inside the exception message, so a 409 arrived wearing a
            // transport fault's clothes: it was retried three times as a rig flake and then
            // reported under a detector that misdiagnosed it. Classifying with the body in
            // hand is what lets a duplicate-identity refusal on /connect reach the detector
            // that understands it.
            var probe = new FlakeProbe(
                error.Length > 0 ? ProbeKind.Transport : ProbeKind.Action,
                on, path, attempt, parsed, Error: error, Blocking: blocking);

            var flake = Flakes.Resolve(probe);
            if (flake is null)
            {
                RecordAttempts(attempt);
                throw PlaytestSignal.Inconclusive(
                    $"{on} refused {path} and nothing in the flake taxonomy explains it, so this check is inconclusive rather than failed. " +
                    "An endpoint refusing is not the mod misbehaving; only a value read back through a reader can say that. " +
                    $"Status: {status} Response: {responseBody} Error: {error}",
                    Detectors.ActionRefused,
                    PlaytestJson.Detail(new Dictionary<string, object?> { ["instance"] = on, ["path"] = path, ["httpStatus"] = status, ["evidence"] = reference }));
            }

            RecordDetector(flake.Name);

            var maxAttempts = noRetry ? 1 : flake.MaxAttempts;
            if (flake.Remedy == FlakeRemedy.Abort || attempt >= maxAttempts)
            {
                RecordAttempts(attempt);
                throw PlaytestSignal.Inconclusive(
                    $"{on} could not complete {path} after {attempt} attempt(s): {flake.Summary} " +
                    $"This is a rig condition, so the check is inconclusive and never failed. Error: {error} Response: {responseBody}",
                    flake.Name,
                    PlaytestJson.Detail(new Dictionary<string, object?>
                    {
                        ["instance"] = on, ["path"] = path, ["detector"] = flake.Name, ["attempts"] = attempt, ["evidence"] = reference,
                    }));
            }

            if (flake.Remedy == FlakeRemedy.RestartInstance) RestartInstance(on, flake.Name);
            Wait(flake.GapSeconds);
            attempt++;
        }
    }

    /// <summary>
    ///     Success is: the call completed, the status was 2xx, and if the body carries
    ///     <c>ok</c> then <c>ok</c> is true. A body with no <c>ok</c> at 200 is a success.
    /// </summary>
    internal static bool IsSuccess(string error, int httpStatus, JsonNode? parsed)
    {
        if (error.Length > 0) return false;
        if (httpStatus is < 200 or > 299) return false;
        if (parsed is not JsonObject obj) return true;
        if (!obj.TryGetPropertyValue("ok", out var ok)) return true;
        return ValueText.AsBoolean(ok);
    }

    private string WriteRequestRecord(string instance, string method, string path, int attempt, long elapsedMs, string bodyJson, int httpStatus, JsonNode? response, string error)
    {
        if (Evidence is null) return string.Empty;

        var sequence = NextSequence();
        var name = string.Create(CultureInfo.InvariantCulture,
            $"{sequence:d4}-{Slug.Of(instance)}-{Slug.Of(method)}-{Slug.Of(Paths.Bare(path))}.json");

        return Evidence.Write(EvidenceKind.Requests, name,
            EvidenceRecords.Request(sequence, Stamp(), instance, method, path, attempt, elapsedMs, bodyJson, httpStatus, response, error));
    }

    // ---- reading ----------------------------------------------------------

    public Observation Read(string from, Reader reader, string select = ".", string of = "", object? readerArgs = null)
    {
        RequireOwnInstance(from, "from");
        RefreshLockIfDue();

        return reader == Reader.BepInExLog
            ? ReadLogFile(from, select, of, readerArgs)
            : ReadEndpoint(from, reader, select, of, readerArgs);
    }

    private Observation ReadLogFile(string from, string select, string of, object? readerArgs)
    {
        var request = readerArgs as BepInExLogRequest ?? new BepInExLogRequest();
        if (readerArgs is not null && readerArgs is not BepInExLogRequest)
        {
            throw new PlaytestUsageException(
                $"The bepinexlog reader takes a {nameof(BepInExLogRequest)} and was given {readerArgs.GetType().Name}. It reads a FILE, so there is no endpoint and no wire type for it.");
        }

        var path = ResolveLogPath(from);
        var started = _deps.Clock.UtcNow;
        var reading = BepInExLogReader.Read(_deps.LogFiles, from, path, request.Contains, request.Limit);
        var elapsed = (long)(_deps.Clock.UtcNow - started).TotalMilliseconds;

        var node = reading.ToNode();
        var reference = WriteRequestRecord(from, "FILE", path, 1, elapsed, string.Empty, 200, node, string.Empty);

        return BuildObservation(from, Reader.BepInExLog, select, of, request, node, "FILE " + path, reference);
    }

    private Observation ReadEndpoint(string from, Reader reader, string select, string of, object? readerArgs)
    {
        var endpoint = ReaderCatalogue.Endpoint(reader)
            ?? throw new PlaytestUsageException($"The {ReaderCatalogue.Name(reader)} reader has no endpoint.");

        var query = ReaderCatalogue.TakesQuery(reader) ? RigWire.Query(readerArgs) : string.Empty;
        var path = endpoint + query;
        var port = ResolvePort(from);

        var started = _deps.Clock.UtcNow;
        var status = 0;
        var body = string.Empty;
        var error = string.Empty;

        try
        {
            var response = _deps.Transport.Send(port, path, null, TimeSpan.FromSeconds(ReaderTimeoutSeconds));
            status = response.HttpStatus;
            body = response.Body;

            // A reader treats a non-2xx as unreachable, deliberately. A 409 from /thing means
            // some row or field did not resolve, and narrowing it anyway would hand the check
            // an absent value that an assertion would then blame on the mod. An inconclusive
            // costs a re-run; a false fail costs a day.
            if (status is < 200 or > 299) error = $"HTTP {status}: {body}";
        }
        catch (RigTransportException ex)
        {
            error = ex.Message;
        }

        var elapsed = (long)(_deps.Clock.UtcNow - started).TotalMilliseconds;
        var parsed = PlaytestJson.TryParse(body);
        var reference = WriteRequestRecord(from, "GET", path, 1, elapsed, string.Empty, status, parsed, error);

        if (error.Length > 0)
        {
            var probe = new FlakeProbe(ProbeKind.Transport, from, path, Error: error);
            var flake = Flakes.Resolve(probe);
            if (flake is not null) RecordDetector(flake.Name);

            throw PlaytestSignal.Inconclusive(
                $"Could not read '{ReaderCatalogue.Name(reader)}' from '{from}', so nothing can be concluded and the check is inconclusive: {error}",
                flake?.Name ?? Detectors.ReaderUnreachable,
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["instance"] = from, ["reader"] = ReaderCatalogue.Name(reader), ["path"] = path, ["evidence"] = reference,
                }));
        }

        var narrowed = ReaderCatalogue.Narrow(reader, body, of);
        return BuildObservation(from, reader, select, of, readerArgs, narrowed, "GET " + path, reference);
    }

    private Observation BuildObservation(string from, Reader reader, string select, string of, object? readerArgs, JsonNode? narrowed, string source, string reference)
    {
        var value = SelectPath.Select(narrowed, select);
        var observation = new Observation(from, reader, select, of, readerArgs, value, source, Stamp(), reference);

        if (Evidence is not null)
        {
            var sequence = NextSequence();
            var name = string.Create(CultureInfo.InvariantCulture,
                $"{sequence:d4}-{Slug.Of(from)}-{Slug.Of(ReaderCatalogue.Name(reader))}-{Slug.Of(select)}.json");

            var argsNode = readerArgs switch
            {
                null => null,
                BepInExLogRequest log => new JsonObject { ["contains"] = log.Contains, ["limit"] = log.Limit },
                _ => RigWire.ToNode(readerArgs),
            };

            Evidence.Write(EvidenceKind.Observations, name,
                EvidenceRecords.Observation(from, ReaderCatalogue.Name(reader), select, of, argsNode, value, source, observation.CapturedUtc, reference));
        }

        return observation;
    }

    // ---- asserting --------------------------------------------------------

    public Observation AssertValue(string from, Reader reader, ValueMatcher matcher, string because, string select = ".", string of = "", object? readerArgs = null)
    {
        ArgumentNullException.ThrowIfNull(matcher);
        RequireBecause(because);

        var observation = Read(from, reader, select, of, readerArgs);
        AssertionCount++;

        var verdict = matcher.Evaluate(observation.Value);
        if (verdict.Satisfied)
        {
            Say($"[Playtest]   ok   {from}.{ReaderCatalogue.Name(reader)}.{select} {matcher.Wants}");
            return observation;
        }

        var note = verdict.Note.Length > 0 ? " " + verdict.Note : string.Empty;
        throw PlaytestSignal.Failure(
            $"{from}.{ReaderCatalogue.Name(reader)}.{select} {matcher.Wants}, but it was [{observation.Text}].{note} {because}",
            PlaytestJson.Detail(new Dictionary<string, object?>
            {
                ["instance"] = from,
                ["reader"] = ReaderCatalogue.Name(reader),
                ["select"] = select,
                ["of"] = of,
                ["expected"] = matcher.Wants,
                ["actual"] = observation.Text,
                ["because"] = because,
                ["evidence"] = observation.EvidenceRef,
            }));
    }

    public IReadOnlyList<Observation> AssertAgreement(IReadOnlyList<string> across, Reader reader, string because, string select = ".", string of = "", object? readerArgs = null, object? isValue = null, bool pinValue = false)
    {
        ArgumentNullException.ThrowIfNull(across);
        RequireBecause(because);

        if (across.Count < 2)
        {
            throw new PlaytestUsageException(
                "AssertAgreement needs at least two instances; agreement with itself is not an observation.");
        }

        var observations = new List<Observation>(across.Count);
        foreach (var name in across) observations.Add(Read(name, reader, select, of, readerArgs));

        AssertionCount++;

        var first = observations[0];
        for (var i = 1; i < observations.Count; i++)
        {
            var other = observations[i];
            if (ValueText.AreEqual(first.Text, other.Value) || ValueText.Render(first.Value) == other.Text) continue;

            throw PlaytestSignal.Failure(
                $"{first.Instance} and {other.Instance} disagree about {ReaderCatalogue.Name(reader)}.{select} : " +
                $"{first.Instance}=[{first.Text}] {other.Instance}=[{other.Text}]. {because}",
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["reader"] = ReaderCatalogue.Name(reader), ["select"] = select,
                    ["a"] = first.Instance, ["aValue"] = first.Text,
                    ["b"] = other.Instance, ["bValue"] = other.Text,
                    ["because"] = because,
                }));
        }

        if (pinValue && !ValueText.AreEqual(isValue, first.Value))
        {
            throw PlaytestSignal.Failure(
                $"{string.Join(", ", across)} agree about {ReaderCatalogue.Name(reader)}.{select} but on the wrong value: " +
                $"expected [{ValueText.RenderExpected(isValue)}], all reported [{first.Text}]. {because}",
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["reader"] = ReaderCatalogue.Name(reader), ["select"] = select,
                    ["expected"] = ValueText.RenderExpected(isValue), ["actual"] = first.Text, ["because"] = because,
                }));
        }

        Say($"[Playtest]   ok   {string.Join("/", across)} agree on {ReaderCatalogue.Name(reader)}.{select} = [{first.Text}]");
        return observations;
    }

    public Observation AssertChange(Observation baseline, string because, object? to = null, bool unchanged = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        RequireBecause(because);

        if (unchanged && to is not null)
            throw new PlaytestUsageException("AssertChange takes either a target value or unchanged, not both.");
        if (!unchanged && to is null)
            throw new PlaytestUsageException("AssertChange needs a target value, or unchanged.");

        // The re-read reproduces the baseline's request EXACTLY, reader args included. This
        // was a shipped defect: without the args the re-read went out as a bare /thing or
        // /config, the endpoint answered 400, and every before-and-after check on a per-Thing
        // field ended inconclusive with no comparison made.
        var after = Read(baseline.Instance, baseline.Reader, baseline.Select, baseline.Of, baseline.ReaderArgs);
        AssertionCount++;

        var readerName = ReaderCatalogue.Name(baseline.Reader);

        if (unchanged)
        {
            if (!ValueText.AreEqual(baseline.Text, after.Value))
            {
                throw PlaytestSignal.Failure(
                    $"{baseline.Instance}.{readerName}.{baseline.Select} was expected to stay at [{baseline.Text}] and is now [{after.Text}]. {because}",
                    PlaytestJson.Detail(new Dictionary<string, object?>
                    {
                        ["instance"] = baseline.Instance, ["reader"] = readerName, ["select"] = baseline.Select,
                        ["before"] = baseline.Text, ["after"] = after.Text, ["because"] = because,
                    }));
            }

            Say($"[Playtest]   ok   {baseline.Instance}.{readerName}.{baseline.Select} unchanged at [{after.Text}]");
            return after;
        }

        if (!ValueText.AreEqual(to, after.Value))
        {
            throw PlaytestSignal.Failure(
                $"{baseline.Instance}.{readerName}.{baseline.Select} was expected to become [{ValueText.RenderExpected(to)}] and reads [{after.Text}] (baseline was [{baseline.Text}]). {because}",
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["instance"] = baseline.Instance, ["reader"] = readerName, ["select"] = baseline.Select,
                    ["before"] = baseline.Text, ["after"] = after.Text,
                    ["expected"] = ValueText.RenderExpected(to), ["because"] = because,
                }));
        }

        Say($"[Playtest]   ok   {baseline.Instance}.{readerName}.{baseline.Select} moved [{baseline.Text}] -> [{after.Text}]");
        return after;
    }

    private static void RequireBecause(string because)
    {
        if (string.IsNullOrWhiteSpace(because))
        {
            throw new PlaytestUsageException(
                "Every assertion needs a reason. A report saying \"hosting was False\" is a puzzle; one saying why it matters is a finding.");
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public void SetInconclusive(string because, string detector = Detectors.CheckDeclined, IReadOnlyDictionary<string, object?>? detail = null)
    {
        throw PlaytestSignal.Inconclusive(because, detector, detail is null ? "null" : PlaytestJson.Detail(detail));
    }

    // ---- attestation ------------------------------------------------------

    public void AssertBinaryUnderTest()
    {
        var mod = ModIdentityResolver.Resolve(Check.SourceFile, _deps.Files);
        var report = BinaryAttestation.Attest(_deps.Files, _deps.RigHome, mod, Check.InstanceNames, ReadConfigEntryCount);

        Attestation = report;
        BinaryAttested = true;
        Evidence?.Write(EvidenceKind.Root, "binary.json", report.ToJson());
        Say($"[Playtest]   attested {mod.ModName} ({mod.Guid}) on {string.Join(", ", Check.InstanceNames)}");
    }

    private int ReadConfigEntryCount(string instance)
    {
        var mod = ModIdentityResolver.Resolve(Check.SourceFile, _deps.Files);
        var observation = Read(instance, Reader.Config, "entries.count", string.Empty, new ConfigRequest { Guid = mod.Guid });
        return ValueText.TryAsNumber(observation.Value, out var count) ? (int)count : 0;
    }

    // ---- rig operations ---------------------------------------------------

    public StatusResponse WaitStage(string name, Stage stage, int waitSeconds = 300, int pollSeconds = 5)
    {
        RequireOwnInstance(name, "name");
        var port = ResolvePort(name);
        if (pollSeconds <= 0) pollSeconds = 1;

        // Two brakes on the inner loop, not one. The wall-clock deadline is the real budget;
        // the poll cap is what stops a frozen or injected clock from turning a barrier into an
        // infinite loop, which is the difference between a harness that reports a boot timeout
        // and one that hangs holding the rig.
        var maxPolls = (int)Math.Ceiling((double)waitSeconds / pollSeconds) + 2;
        var attempt = 1;

        while (true)
        {
            var deadline = _deps.Clock.UtcNow.AddSeconds(waitSeconds);
            StatusResponse? last = null;
            var polls = 0;

            while (_deps.Clock.UtcNow < deadline && polls < maxPolls)
            {
                polls++;
                StatusResponse? status = null;
                try
                {
                    var response = _deps.Transport.Send(port, Endpoints.Status, null, TimeSpan.FromSeconds(15));
                    if (response.HttpStatus is >= 200 and <= 299) status = RigWire.Deserialize<StatusResponse>(response.Body);
                }
                catch (RigTransportException)
                {
                    status = null;
                }

                if (status is not null)
                {
                    last = status;
                    if (Reached(status, stage))
                    {
                        RecordAttempts(attempt);
                        Say($"[Playtest]   {name} reached '{stage}'");
                        return status;
                    }
                }

                Wait(pollSeconds);
            }

            var probe = new FlakeProbe(ProbeKind.Barrier, name, Attempt: attempt, Status: last, Stage: stage.ToString());
            var flake = Flakes.Resolve(probe);
            if (flake is not null) RecordDetector(flake.Name);

            var maxAttempts = flake?.MaxAttempts ?? 1;
            if (flake is null || flake.Remedy == FlakeRemedy.Abort || attempt >= maxAttempts)
            {
                RecordAttempts(attempt);
                var why = flake?.Summary ?? "nothing in the taxonomy explains it";
                throw PlaytestSignal.Inconclusive(
                    $"'{name}' did not reach '{stage}' within {waitSeconds}s after {attempt} attempt(s). {why} " +
                    $"Last status: phase={last?.Phase} gameInitialized={last?.GameInitialized} plugins={last?.LoadedPluginCount}",
                    flake?.Name ?? "boot-timeout",
                    PlaytestJson.Detail(new Dictionary<string, object?> { ["instance"] = name, ["stage"] = stage.ToString(), ["attempts"] = attempt }));
            }

            if (flake.Remedy == FlakeRemedy.RestartInstance) RestartInstance(name, flake.Name);
            Wait(flake.GapSeconds);
            attempt++;
        }
    }

    internal static bool Reached(StatusResponse status, Stage stage) => stage switch
    {
        Stage.Ping => true,
        Stage.ModsLoaded => status.LoadedPluginCount > 10,
        Stage.Menu => status.GameInitialized == true && string.Equals(status.Phase, "menu", StringComparison.Ordinal),
        Stage.InWorld => string.Equals(status.Phase, "inWorld", StringComparison.Ordinal),
        _ => false,
    };

    public void RestartInstance(string name, string reason = "")
    {
        RequireOwnInstance(name, "name");
        if (string.IsNullOrEmpty(Owner))
        {
            throw new PlaytestUsageException(
                "An instance cannot be restarted without the rig session lock: every mutating launcher command carries the owner id.");
        }

        Say($"[Playtest]   restarting {name}{(reason.Length > 0 ? " (" + reason + ")" : string.Empty)}");

        // The stop's exit code is deliberately ignored: an instance that was already gone is
        // exactly the condition a restart is for.
        var stop = _deps.Launcher.StopInstance(name, Owner, 60, force: false);
        RecordLauncher("stop", $"-Target {name}", stop);

        var start = _deps.Launcher.StartInstance(name, Owner);
        RecordLauncher("start", $"-Target {name}", start);
        if (!Started.Contains(name, StringComparer.OrdinalIgnoreCase)) Started.Add(name);

        if (!start.Success)
        {
            throw PlaytestSignal.Inconclusive(
                $"Could not restart '{name}', so the check cannot continue and is inconclusive. Launcher exit {start.ExitCode}: {start.Message}",
                Detectors.InstanceRestartFailed,
                PlaytestJson.Detail(new Dictionary<string, object?> { ["instance"] = name, ["exit"] = start.ExitCode }));
        }
    }

    /// <summary>Starts an instance and registers it for teardown BEFORE the result is checked.</summary>
    /// <remarks>The process may exist even when the launcher reported a failure.</remarks>
    internal void StartInstanceProcess(string name)
    {
        var result = _deps.Launcher.StartInstance(name, Owner);
        RecordLauncher("start", $"-Target {name}", result);
        if (!Started.Contains(name, StringComparer.OrdinalIgnoreCase)) Started.Add(name);

        if (!result.Success)
        {
            throw PlaytestSignal.Inconclusive(
                $"Could not start '{name}', so the check did not run and is inconclusive. Launcher exit {result.ExitCode}: {result.Message}",
                Detectors.InstanceStartFailed,
                PlaytestJson.Detail(new Dictionary<string, object?> { ["instance"] = name, ["exit"] = result.ExitCode }));
        }
    }

    public JoinResult ConnectJoiner(string name, string to, string address = "127.0.0.1", int port = 0, int attempts = 3, double gapSeconds = 10, int rosterPollSeconds = 30)
    {
        RequireOwnInstance(name, "name");
        RequireOwnInstance(to, "to");

        var hostStatus = TryReadStatus(to);
        var resolvedPort = port > 0 ? port : hostStatus?.HostPort ?? 0;
        if (resolvedPort <= 0)
        {
            RecordDetector("host-not-hosting");
            throw PlaytestSignal.Inconclusive(
                $"'{to}' does not report a game port, so '{name}' has nothing to join and the check is inconclusive.",
                "host-not-hosting",
                PlaytestJson.Detail(new Dictionary<string, object?> { ["host"] = to, ["joiner"] = name }));
        }

        var before = hostStatus?.ConnectedClients?.Length ?? 0;
        IReadOnlyList<ConnectedClient> lastRoster = hostStatus?.ConnectedClients ?? [];
        Exception? lastError = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                // Start from the menu. Reconnecting from a half-joined state is what the
                // settling window is about, and neither of these two is a reason to stop
                // trying: the disconnect is best-effort and the barrier after it is only
                // there to give the client time to land at the menu.
                try { Act(name, Endpoints.Disconnect, new DisconnectRequest(), blocking: true, noRetry: true); } catch (PlaytestSignal) { }
                try { WaitStage(name, Stage.Menu, 180); } catch (PlaytestSignal) { }

                Wait(gapSeconds);
                RecordDetector("connect-first-attempt");
                RecordAttempts(attempt);
                Say($"[Playtest]   {name} retrying the join to {to} (attempt {attempt} of {attempts})");
            }

            // Read immediately before the connect, so a caller measuring per-join output
            // baselines from the attempt that actually landed rather than from before the
            // retries.
            long? seqBefore = null;
            try
            {
                var response = _deps.Transport.Send(ResolvePort(name), Endpoints.ConsoleLog + "?limit=1", null, TimeSpan.FromSeconds(30));
                seqBefore = RigWire.Deserialize<ConsoleLogResponse>(response.Body)?.NextSeq;
            }
            catch (RigTransportException)
            {
                seqBefore = null;
            }

            var arrived = false;
            try
            {
                Act(name, Endpoints.Connect, new ConnectRequest { Address = address, Port = resolvedPort }, blocking: true);
                WaitStage(name, Stage.InWorld, 600);
                arrived = true;
            }
            catch (PlaytestSignal ex)
            {
                // Keep it: if every attempt fails this way, the last one explains the give-up
                // better than the roster count does, and it keeps its own detector instead of
                // being relabelled as a roster problem it is not.
                lastError = ex;
            }

            if (arrived)
            {
                // Poll rather than read once: inWorld on the joiner and the row appearing in
                // the server roster are two different instants.
                var deadline = _deps.Clock.UtcNow.AddSeconds(rosterPollSeconds);
                do
                {
                    var roster = TryReadStatus(to)?.ConnectedClients;
                    if (roster is not null)
                    {
                        lastRoster = roster;
                        if (roster.Length > before)
                        {
                            RecordAttempts(attempt);
                            Say($"[Playtest]   {name} is in {to} roster ({roster.Length} client(s), attempt {attempt})");
                            return new JoinResult(name, to, roster, attempt, seqBefore);
                        }
                    }

                    Wait(2);
                }
                while (_deps.Clock.UtcNow < deadline);
            }
            else if (attempt >= attempts && lastError is not null)
            {
                throw lastError;
            }
        }

        // The detector's own probe, built at the raise site. In PowerShell the detector was
        // raised by name and no site ever constructed the probe it tests, so its Test was
        // unreachable in production and only ever ran against hand-built fixtures (P-03).
        var flake = Flakes.Resolve(new FlakeProbe(ProbeKind.PostState, name, Endpoints.Connect, attempts));
        var detector = flake?.Name ?? "joiner-not-in-roster";
        RecordDetector(detector);

        throw PlaytestSignal.Inconclusive(
            $"'{name}' reported a connection but the roster on '{to}' did not grow ({before} then {lastRoster.Count}) after {attempts} attempt(s), " +
            $"each polled for {rosterPollSeconds} s. {flake?.Summary} The rig could not be brought up, so the check is inconclusive and never failed.",
            detector,
            PlaytestJson.Detail(new Dictionary<string, object?>
            {
                ["joiner"] = name, ["host"] = to, ["before"] = before, ["after"] = lastRoster.Count, ["attempts"] = attempts,
            }));
    }

    internal StatusResponse? TryReadStatus(string name)
    {
        try
        {
            var response = _deps.Transport.Send(ResolvePort(name), Endpoints.Status, null, TimeSpan.FromSeconds(30));
            return response.HttpStatus is >= 200 and <= 299 ? RigWire.Deserialize<StatusResponse>(response.Body) : null;
        }
        catch (RigTransportException)
        {
            return null;
        }
    }

    public void SaveConsoleTail(string step, IReadOnlyList<string>? instances = null)
    {
        if (Evidence is null) return;

        foreach (var name in instances ?? Check.InstanceNames)
        {
            string text;
            try
            {
                var response = _deps.Transport.Send(ResolvePort(name), Endpoints.ConsoleLog + "?limit=120&source=console", null, TimeSpan.FromSeconds(30));
                var lines = RigWire.Deserialize<ConsoleLogResponse>(response.Body)?.Lines ?? [];
                text = string.Join('\n', lines.Select(l => l.Text ?? string.Empty));
            }
            catch (Exception ex)
            {
                // Never throws. A console that cannot be reached is a note in the bundle, not
                // the end of a check: the tail is evidence around the body, not the body.
                text = $"<console unreachable: {ex.Message}>";
            }

            var block = new StringBuilder()
                .Append('\n')
                .Append(CultureInfo.InvariantCulture, $"===== {step} ({Stamp()}) =====\n")
                .Append(text)
                .Append('\n')
                .ToString();

            Evidence.Write(EvidenceKind.Console, Slug.Of(name) + ".tail.txt", block, append: true);
        }
    }

    public string? WriteEvidence(string name, string content, EvidenceKind kind = EvidenceKind.Root, bool append = false) =>
        Evidence?.Write(kind, name, content, append);
}
