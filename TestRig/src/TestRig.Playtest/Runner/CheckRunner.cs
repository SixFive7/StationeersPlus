using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;
using TestRig.Playtest.Seams;

namespace TestRig.Playtest.Runner;

/// <summary>What one check produced.</summary>
public sealed record CheckResult(
    string Name,
    CheckOutcome Outcome,
    string Text,
    bool Degraded,
    int Retries,
    int WorstAttempts,
    string Detector,
    IReadOnlyList<string> Detectors,
    string Message,
    string Detail,
    long DurationMs,
    string EvidenceFolder,
    string LockOwner,
    IReadOnlyList<string> TeardownNotes,
    int AssertionCount)
{
    public JsonObject ToJson(string startedUtc, string endedUtc) => new()
    {
        ["name"] = Name,
        ["outcome"] = OutcomeText(Outcome),
        ["text"] = Text,
        ["degraded"] = Degraded,
        ["retries"] = Retries,
        ["worstAttempts"] = WorstAttempts,
        ["assertions"] = AssertionCount,
        ["detector"] = Detector,
        ["detectors"] = PlaytestJson.Array(Detectors),
        ["message"] = Message,
        ["detail"] = Detail,
        ["durationMs"] = DurationMs,
        ["evidence"] = EvidenceFolder,
        ["lockOwner"] = LockOwner,
        ["teardownNotes"] = PlaytestJson.Array(TeardownNotes),
        ["startedUtc"] = startedUtc,
        ["endedUtc"] = endedUtc,
    };

    /// <summary>The outcome as a report writes it.</summary>
    public static string OutcomeText(CheckOutcome outcome) => outcome switch
    {
        CheckOutcome.Pass => "pass",
        CheckOutcome.Fail => "fail",
        _ => "inconclusive",
    };

    /// <summary>
    ///     How an outcome renders, degradation included.
    /// </summary>
    /// <remarks>
    ///     The floor of two exists so a degraded pass never renders as fewer than two
    ///     attempts, which would read as a clean run that was somehow still degraded.
    /// </remarks>
    public static string Format(CheckOutcome outcome, bool degraded, int worstAttempts, string detector) => outcome switch
    {
        CheckOutcome.Pass when degraded => string.Create(CultureInfo.InvariantCulture, $"pass (degraded, {Math.Max(2, worstAttempts)} attempts)"),
        CheckOutcome.Pass => "pass",
        CheckOutcome.Fail => "fail",
        _ => string.IsNullOrEmpty(detector) ? "inconclusive" : $"inconclusive ({detector})",
    };
}

/// <summary>
///     Runs one check: the lock, bring-up, attestation, the body, teardown, the verdict.
/// </summary>
/// <remarks>
///     <para>
///     <b>Each check takes and releases the lock itself.</b> That buys a state reset per
///     check, since the reset is between sessions by design and two checks under one lock
///     would get none. It costs the reset time, and it risks another agent taking the rig
///     between checks, which is reported as inconclusive and never as a failure.
///     </para>
///     <para>
///     <b>Every path that took the lock releases it, the failures included.</b> Acquisition is
///     two steps: the lock file is written, and then the between-session state reset runs on
///     top of it. Only the first is atomic, so an acquisition that FAILS can still have
///     reserved the rig, and the two branches that reject a grant have to release before they
///     throw. They used to throw from outside the try/finally that owns the release, which on
///     2026-08-16 left a live suite's rig locked by an owner nothing could name.
///     </para>
///     <para>
///     <b>Teardown is guaranteed and it is by name.</b> Instances are stopped one at a time,
///     joiners first and hosts last. A rig-wide stop would reach every instance on the
///     machine including another session's live test. A stop that fails does not skip the
///     release, because an instance left up holds the rig but a lock left held blocks every
///     other agent too. Both are recorded.
///     </para>
/// </remarks>
public sealed class CheckRunner
{
    private readonly PlaytestDependencies _deps;

    public CheckRunner(PlaytestDependencies dependencies) =>
        _deps = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

    /// <summary>How long teardown gives a stop before it is considered failed.</summary>
    public const int StopTimeoutSeconds = 60;

    public CheckResult Run(
        IPlaytestCheck check,
        CheckEvidence? evidence,
        string evidenceFolder,
        int lockWaitSeconds,
        bool keepState = false)
    {
        ArgumentNullException.ThrowIfNull(check);

        var spec = check.Spec;
        var startedUtc = _deps.Clock.UtcNow;
        var context = new PlaytestContext(_deps, spec, new FlakeCatalogue(), evidence, string.Empty);

        var outcome = CheckOutcome.Pass;
        var detector = string.Empty;
        var message = string.Empty;
        var detail = "null";

        try
        {
            RunUnderLock(check, context, evidence, lockWaitSeconds, keepState);
        }
        catch (Exception ex)
        {
            var classified = SignalClassifier.Classify(ex);
            outcome = classified.Outcome;
            detector = classified.Detector;
            message = classified.Message;
            detail = classified.Detail;
            context.RecordDetector(classified.Detector);
        }

        // Two post-hoc gates, and both only ever downgrade a pass. A fail from an unattested
        // check is still a fail: an assertion that read a wrong value read a wrong value.
        if (outcome == CheckOutcome.Pass && !context.BinaryAttested)
        {
            outcome = CheckOutcome.Inconclusive;
            detector = Detectors.BinaryNotAttested;
            message =
                "The check body completed but never attested the binary under test, so its result says nothing about any particular build and cannot be a pass. " +
                "Attestation runs automatically before the body; reaching here means it was skipped, which happens when bring-up never completed.";
            context.RecordDetector(detector);
        }

        if (outcome == CheckOutcome.Pass && context.AssertionCount == 0)
        {
            outcome = CheckOutcome.Inconclusive;
            detector = Detectors.NoAssertions;
            message =
                "The check body completed without making a single assertion, so nothing was measured about the mod and the result cannot be a pass. " +
                "An empty body reported a clean pass in the PowerShell harness, which had no assertion counter anywhere.";
            context.RecordDetector(detector);
        }

        var endedUtc = _deps.Clock.UtcNow;
        var result = new CheckResult(
            spec.Name,
            outcome,
            CheckResult.Format(outcome, context.Degraded, context.WorstAttempts, detector),
            context.Degraded,
            context.Retries,
            context.WorstAttempts,
            detector,
            context.RecordedDetectors,
            message,
            detail,
            (long)(endedUtc - startedUtc).TotalMilliseconds,
            evidenceFolder,
            context.Owner,
            [.. context.TeardownNotes],
            context.AssertionCount);

        evidence?.Write(EvidenceKind.Root, "check.json",
            PlaytestJson.Write(result.ToJson(Stamps.Format(startedUtc), Stamps.Format(endedUtc))));

        foreach (var warning in context.Flakes.Warnings) _deps.Log?.Invoke(warning);

        return result;
    }

    private void RunUnderLock(
        IPlaytestCheck check,
        PlaytestContext context,
        CheckEvidence? evidence,
        int lockWaitSeconds,
        bool keepState)
    {
        var spec = check.Spec;
        var grant = _deps.Launcher.AcquireLock(spec.Purpose, spec.TtlMinutes, lockWaitSeconds, keepState);

        // Written BEFORE success is checked, so a refused lock still leaves its explanation
        // in the bundle.
        evidence?.Write(EvidenceKind.Root, "hygiene-reset.txt", grant.StateResetReport);

        if (!grant.Success)
        {
            // A FAILED acquisition can still have RESERVED the rig, and that is not a
            // contradiction: the lock file is written inside the critical section and the
            // between-session state reset runs afterwards, under the reservation that write
            // created. A reset that throws therefore leaves a real lock behind, owned by this
            // check. Measured 2026-08-16 on a live suite: the reset hit a sharing violation on
            // an instance's Unity log, this branch threw from OUTSIDE the try/finally that owns
            // the release, and the rig stayed locked by owner 8dd76948 until a human cleared
            // it, taking the two checks behind it with it.
            var release = ReleaseALockNoSessionCanUse(context, grant, keepState);

            throw PlaytestSignal.Inconclusive(
                $"The rig session lock could not be taken, so nothing was driven and nothing was measured about the mod. {grant.Message}{release.Sentence}",
                Detectors.RigUnavailable,
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["exit"] = grant.ExitCode,
                    ["message"] = grant.Message,
                    ["lockWasTaken"] = grant.NeedsRelease,
                    ["owner"] = grant.Owner,
                    ["released"] = release.Released,
                    ["releaseExit"] = release.ExitCode,
                }));
        }

        if (string.IsNullOrWhiteSpace(grant.Owner))
        {
            // The one path that genuinely cannot clean up after itself: the rig IS reserved
            // and no id came back, so there is nothing to release with. It costs the rest of
            // the run, so it says exactly what to do rather than leaving that to be worked out
            // from a leftover file.
            const string note =
                "the rig is LEFT LOCKED: the lock was granted with no owner id, so this check had nothing to release with";
            context.TeardownNotes.Add(note);
            _deps.Log?.Invoke("[Playtest] " + note);

            throw PlaytestSignal.Inconclusive(
                "The lock was taken but the launcher reported no owner id, so nothing can be driven safely and nothing " +
                "here can release it. THE RIG IS LEFT LOCKED, and every check behind this one ends rig-unavailable " +
                "until it is cleared:\n" +
                "  1. testrig status            (prints the owner id of whatever holds the rig)\n" +
                "  2. testrig stop --target all (a running instance holds the rig with no timer to save you)\n" +
                "  3. testrig unlock --as <owner>\n" +
                "Release it with unlock rather than deleting TestRig/session.lock, because the release is also what " +
                "restores the rig. --break-lock is human-gated and is not the tool for this.",
                Detectors.RigUnavailable,
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["message"] = grant.Message,
                    ["lockLeftHeld"] = true,
                }));
        }

        context.Owner = grant.Owner;

        // Everything from here is inside the try, so the release covers every path that
        // follows the lock genuinely being ours, evidence writes included. There used to be
        // four statements between the acquisition and the try, and any of them throwing
        // stranded the rig exactly as the two branches above did.
        try
        {
            _deps.Log?.Invoke($"[Playtest]   lock owner {grant.Owner}");

            evidence?.Write(EvidenceKind.Root, "lock.txt", new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"owner   : {grant.Owner}\n")
                .Append(CultureInfo.InvariantCulture, $"purpose : {spec.Purpose}\n")
                .Append(CultureInfo.InvariantCulture, $"ttl     : {spec.TtlMinutes} min\n")
                .Append(CultureInfo.InvariantCulture, $"acquired: {context.Stamp()}\n")
                .ToString());

            AssertInstancesAreProvisionedForThisMod(context);
            BringUp(context);
            context.AssertBinaryUnderTest();
            context.SaveConsoleTail("after bring-up");
            check.Run(context);
        }
        finally
        {
            context.SaveConsoleTail("after check body");
            StopInstances(context);

            var release = _deps.Launcher.ReleaseLock(grant.Owner, keepState);
            context.RecordLauncher("unlock", $"-As {grant.Owner}", release);
            if (!release.Success)
            {
                var note = $"the rig lock could not be released (exit {release.ExitCode}): {release.Message}";
                context.TeardownNotes.Add(note);
                _deps.Log?.Invoke("[Playtest] " + note);
            }

            evidence?.Write(EvidenceKind.Root, "lock.txt", new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"released: {context.Stamp()} (exit {release.ExitCode})\n")
                .Append(CultureInfo.InvariantCulture, $"notes   : {string.Join(" | ", context.TeardownNotes)}\n")
                .ToString(), append: true);
        }
    }

    /// <summary>
    ///     Gives back a lock the rig took and then would not let this check use.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>The release outcome is reported ALONGSIDE the reason, never instead of it.</b> A
    ///     release also RESTORES, so it can fail for its own reasons, and a check whose message
    ///     had been overwritten by "the restore failed" would have lost the one thing it knows:
    ///     why nothing was measured about the mod. So this returns a sentence to append and
    ///     never throws.
    ///     </para>
    ///     <para>
    ///     A restore that fails during the release does not stop the release. That is Core's
    ///     rule, not this one's: a failed restore leaves the rig marked dirty so the next
    ///     acquisition tries again, because a hung session holding the rig on top of a mess is
    ///     worse than a mess.
    ///     </para>
    /// </remarks>
    private (bool Released, int ExitCode, string Sentence) ReleaseALockNoSessionCanUse(
        PlaytestContext context, LockGrant grant, bool keepState)
    {
        if (!grant.NeedsRelease) return (false, 0, string.Empty);

        var release = _deps.Launcher.ReleaseLock(grant.Owner, keepState);
        context.RecordLauncher("unlock", $"-As {grant.Owner}", release);

        if (release.Success)
        {
            var note = $"the lock this check had already taken (owner {grant.Owner}) was released after the acquisition failed";
            context.TeardownNotes.Add(note);
            _deps.Log?.Invoke("[Playtest] " + note);

            return (true, release.ExitCode,
                $" The lock this attempt had already taken (owner {grant.Owner}) was released, so the rig is free for "
                + "the next check.");
        }

        var failure =
            $"the rig is STILL LOCKED by owner {grant.Owner}: releasing it after a failed acquisition also failed "
            + $"(exit {release.ExitCode}): {release.Message}";
        context.TeardownNotes.Add(failure);
        _deps.Log?.Invoke("[Playtest] " + failure);

        return (false, release.ExitCode,
            $" Reported alongside that, not instead of it: the rig is STILL LOCKED by owner {grant.Owner}, because "
            + $"releasing the lock this attempt had already taken also failed (exit {release.ExitCode}): "
            + $"{release.Message} Clear it before the next run with: testrig unlock --as {grant.Owner}.");
    }

    /// <summary>
    ///     Refuses before bring-up when an instance is not provisioned to test this mod.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>This is what closes the loop, and it is cheap because nothing has to be
    ///     declared.</b> The mod comes from the check's own source location via
    ///     <c>[CallerFilePath]</c>, and the under-test set comes from each instance's registry
    ///     row. Both are already facts; all that was missing was comparing them.
    ///     </para>
    ///     <para>
    ///     Without it a check can run to completion against the DEVELOPER'S published copy of
    ///     the mod, seeded from their own folder, while believing it measured this
    ///     repository's build. Attestation catches the case where nothing is deployed, but not
    ///     the one where a seeded copy sits at the deployed path or loads beside it: two
    ///     copies load, every Harmony patch registers twice, and the output stays plausible.
    ///     </para>
    ///     <para>
    ///     It runs BEFORE bring-up, because the whole cost of a check is starting game
    ///     instances and this answer needs none of them.
    ///     </para>
    /// </remarks>
    internal void AssertInstancesAreProvisionedForThisMod(PlaytestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var mod = Attestation.ModIdentityResolver.Resolve(context.Check.SourceFile, _deps.Files).ModName;
        var rows = _deps.Registry.Rows();

        foreach (var instance in context.Check.InstanceNames)
        {
            var row = rows.FirstOrDefault(r =>
                string.Equals(r.InstanceName, instance, StringComparison.OrdinalIgnoreCase));

            if (row is null)
            {
                throw PlaytestSignal.Inconclusive(
                    $"'{instance}' is not a provisioned instance, so this check cannot be brought up on it. " +
                    $"Create it: testrig create --target {instance} --under-test {mod} --as <id>",
                    Detectors.ModNotUnderTestHere);
            }

            if (row.UnderTest.Any(m => string.Equals(m, mod, StringComparison.OrdinalIgnoreCase))) continue;

            var records = row.UnderTest.Count == 0 ? "nothing" : string.Join(", ", row.UnderTest);
            throw PlaytestSignal.Inconclusive(
                $"'{instance}' is not provisioned to test '{mod}' (it records {records}), so it carries the " +
                $"DEVELOPER'S published copy of '{mod}' rather than this repository's build. Running here would " +
                "measure a mod this repository did not produce, or load two copies of it and double every " +
                "Harmony patch, and the result would look entirely plausible either way.\n\n" +
                $"  testrig create --target {instance} --force --under-test {mod} --as <id>\n" +
                $"  testrig deploy {mod} --target {instance} --as <id>\n\n" +
                "Every mod OUTSIDE that set stays at its published state on purpose: this repository carries " +
                "work in progress for those too, and one of them changing the behaviour of the mod under test " +
                "is exactly what the separation prevents.",
                Detectors.ModNotUnderTestHere);
        }
    }

    /// <summary>
    ///     Hosts first and all the way into their world, then joiners.
    /// </summary>
    /// <remarks>
    ///     Every post-condition is read back from the AUTHORITY: a host is hosting when ITS
    ///     OWN status says hosting and listenHost, not when the host endpoint answered 200;
    ///     a joiner has arrived when the HOST roster carries it, not when connect answered ok.
    ///     Both of those were live failures before they were rules.
    /// </remarks>
    internal void BringUp(PlaytestContext context, int bootWaitSeconds = 300, int worldWaitSeconds = 600)
    {
        var spec = context.Check;
        var hosts = spec.Instances.Where(i => i.Role == InstanceRole.Host).ToList();
        var clients = spec.Instances.Where(i => i.Role != InstanceRole.Host).ToList();

        foreach (var host in hosts)
        {
            context.StartInstanceProcess(host.Name);
            context.WaitStage(host.Name, Stage.Menu, bootWaitSeconds);

            if (host.World is null && host.Save is null) continue;

            var hosted = context.Act(host.Name, Contracts.Endpoints.Host, new Contracts.HostRequest
            {
                World = host.World,
                Save = host.Save,
                Port = host.GamePort,
            }, blocking: true);

            context.WaitStage(host.Name, Stage.InWorld, worldWaitSeconds);

            // A world created from a world id is named by the host endpoint, with a first named
            // save. When that does not happen the world cannot be saved by anything, and the
            // symptom surfaces at TEARDOWN as a refusal to quit on top of an unsaved world, which
            // is the worst place to learn it. Recorded here instead, on the check that caused it,
            // while it is still fixable. Not fatal: the check itself is unaffected, and calling it
            // inconclusive would throw away a real measurement over a housekeeping failure.
            if (host.World is not null)
            {
                var named = hosted.As<Contracts.HostResponse>()?.StationNameAssigned;
                if (named is false)
                {
                    context.TeardownNotes.Add(
                        $"'{host.Name}' created world '{host.World}' with no station name, so nothing can save it: " +
                        "the ordered teardown will refuse and the world is lost on quit.");
                    _deps.Log?.Invoke(
                        $"[Playtest]   {host.Name}: world '{host.World}' has NO station name; it cannot be saved.");
                }
            }

            var status = context.TryReadStatus(host.Name);
            var probe = new FlakeProbe(ProbeKind.PostState, host.Name, Contracts.Endpoints.Host, Status: status);
            var flake = context.Flakes.Resolve(probe);
            if (flake is not null)
            {
                context.RecordDetector(flake.Name);
                throw PlaytestSignal.Inconclusive(
                    $"'{host.Name}' answered the host endpoint but is not hosting: status reports hosting={status?.Hosting} role={status?.Role}. {flake.Summary} " +
                    "The rig could not be brought up, so the check is inconclusive and never failed.",
                    flake.Name,
                    PlaytestJson.Detail(new Dictionary<string, object?>
                    {
                        ["instance"] = host.Name, ["hosting"] = status?.Hosting, ["role"] = status?.Role,
                    }));
            }

            _deps.Log?.Invoke($"[Playtest]   {host.Name} is hosting on port {status?.HostPort}");
        }

        foreach (var client in clients)
        {
            context.StartInstanceProcess(client.Name);
            context.WaitStage(client.Name, Stage.Menu, bootWaitSeconds);

            var target = client.ConnectTo ?? (hosts.Count > 0 ? hosts[0].Name : null);
            if (target is null) continue;

            context.ConnectJoiner(client.Name, target, client.Address);
        }
    }

    /// <summary>Stops what this check started, non-hosts first, one at a time, by name.</summary>
    internal void StopInstances(PlaytestContext context)
    {
        if (string.IsNullOrEmpty(context.Owner) || context.Started.Count == 0) return;

        var hosts = context.Check.HostNames;
        var ordered = context.Started.Where(n => !hosts.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Concat(context.Started.Where(n => hosts.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var name in ordered)
        {
            var stop = _deps.Launcher.StopInstance(name, context.Owner, StopTimeoutSeconds, force: false);
            context.RecordLauncher("stop", $"-Target {name}", stop);
            if (stop.Success) continue;

            // ONE retry with force. The launcher refuses to quit on top of a world whose save it
            // could not confirm, and unhandled that leaves the instance up and the rig lock held.
            // It used to fire on EVERY host check, because a world created from a world id had no
            // station name and a save with no name had nothing to save under; the host endpoint
            // now names what it creates, and BringUp records it when that fails. This stays as the
            // backstop for every other reason a save can fail to confirm.
            var forced = _deps.Launcher.StopInstance(name, context.Owner, StopTimeoutSeconds, force: true);
            context.RecordLauncher("stop", $"-Target {name} -Force", forced);

            context.TeardownNotes.Add(forced.Success
                ? $"stopped '{name}' with -Force after: {stop.Message}"
                : $"stop of '{name}' failed even with -Force (exit {forced.ExitCode}): {forced.Message}");

            if (!forced.Success) _deps.Log?.Invoke($"[Playtest] stop of '{name}' failed even with -Force: {forced.Message}");
        }

        context.Started.Clear();
    }
}
