using TestRig.Cli.Dispatch;
using TestRig.Cli.Output;
using TestRig.Cli.Parsing;
using TestRig.Cli.Refusals;
using TestRig.Cli.Verbs;
using TestRig.Core.Abstractions;
using TestRig.Core.Infrastructure;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Playtest;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Seams;
using CoreRefusals = TestRig.Core.Rig.RefusalMatrix;

// Core has its own port of the refusal matrix and the target resolver, so both namespaces
// declare these names. The CLI's copies are the ones the dispatcher speaks.
using ResolvedTarget = TestRig.Cli.Verbs.ResolvedTarget;
using TargetKind = TestRig.Cli.Verbs.TargetKind;
using RefusalMatrix = TestRig.Cli.Refusals.RefusalMatrix;
using TargetResolver = TestRig.Cli.Verbs.TargetResolver;

namespace TestRig.Cli;

/// <summary>
/// Parse, resolve the target, consult the refusal matrix, dispatch, format the result.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the behaviour and must not be rearranged. Refusals fire before the lock
/// is asserted and before any work, because a refusal corrects the caller's model of the rig
/// and is worth nothing once a side effect has already happened.
/// </para>
/// <para>
/// No rig logic lives here. Every arm marshals its arguments and hands them to
/// <c>TestRig.Core</c>; where Core does not have the member yet, the arm still marshals and
/// then reports the gap by name rather than guessing.
/// </para>
/// </remarks>
public static class CliApp
{
    public static int Run(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var wantsJson = CommandLine.Peek(args, Options.Json);
        var verbose = CommandLine.Peek(args, Options.Verbose);

        Stream? jsonStream = wantsJson ? Console.OpenStandardOutput() : null;
        IRigOutput output = wantsJson
            ? new JsonOutput(jsonStream!)
            : new HumanOutput(Console.Out, Console.Error, verbose);

        var verb = string.Empty;
        var exit = ExitCodes.Ok;
        string? error = null;

        try
        {
            var cmd = CommandLine.Parse(args);
            verb = cmd.Verb;

            // The surface is its own document in both forms: as JSON it IS the answer, so it
            // is not wrapped in the per-command envelope a caller would then have to unwrap.
            if (verb.Length == 0 || string.Equals(verb, "help", StringComparison.Ordinal))
            {
                var home = RigComposition.ResolveRigHome();
                var (root, source) = RigComposition.ResolveInstancesRoot(home, cmd.Text(Options.InstancesRoot));
                if (wantsJson)
                {
                    Console.Out.WriteLine(Surface.ToJson(root, source));
                    Console.Out.Flush();
                    jsonStream?.Dispose();
                    return ExitCodes.Ok;
                }

                Surface.WriteHuman(output, root, source);
                output.Flush(verb, ExitCodes.Ok, null);
                return ExitCodes.Ok;
            }

            exit = Execute(cmd, output);
        }
        catch (RefusalException ex)
        {
            verb = ex.Verb;
            output.Line(OutputLevel.Info, string.Empty);
            output.Line(
                OutputLevel.Info,
                ex.Target.Length > 0 ? $"testrig {ex.Verb} --target {ex.Target}" : $"testrig {ex.Verb}");
            output.Refusal(ex.Resolved);
            exit = ExitCodes.Refused;
        }
        catch (CliUsageException ex)
        {
            error = ex.Message;
            exit = ExitCodes.UsageError;
        }
        catch (RigRefusalException ex)
        {
            // Core's own refusal matrix prefixes the rendered block with a sentinel so a
            // PowerShell caller could tell a teaching refusal from a crash. Here the type
            // already carries that distinction, so the sentinel is stripped rather than
            // printed: leaving it in would put "[testrig refusal]" at the top of every
            // refused command for no reader's benefit.
            var message = StripSentinel(ex.Message);

            if (ex.Refusal is not null)
            {
                output.Line(OutputLevel.Info, string.Empty);
                output.Refusal(ex.Refusal);
                output.Value("refusalKind", ex.Kind.ToString());
            }

            error = message;
            exit = MapRefusal(ex.Kind);
        }
        catch (RigConfigurationException ex)
        {
            // "Your machine is not set up", not "this command is wrong" and not a refusal
            // with an alternative. Printed plainly, with no stack trace over the top of it;
            // every message of this kind already names DEV.md.
            error = ex.Message;
            output.Value("configurationError", true);
            exit = ExitCodes.Failed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            error = ex.Message;
            exit = ExitCodes.Failed;
        }

        output.Flush(verb, exit, error);
        jsonStream?.Dispose();
        return exit;
    }

    /// <summary>
    /// A typed refusal from Core carries its own kind, so a caller can branch on the exit
    /// code without reading the sentence.
    /// </summary>
    /// <remarks>
    /// The PowerShell rig exited 1 for contention, for a lapsed reservation, for an unlock by
    /// a non-owner and for a genuinely broken rig alike, and the playtest harness collapsed
    /// every non-zero exit into "inconclusive / rig-unavailable" as a result.
    /// </remarks>
    public static int MapRefusal(RigRefusalKind kind) => kind switch
    {
        RigRefusalKind.Refused => ExitCodes.Refused,
        RigRefusalKind.HeldByAnotherSession => ExitCodes.LockHeldByOther,
        RigRefusalKind.NoLockHeld => ExitCodes.LockNotHeld,
        RigRefusalKind.RigBusy => ExitCodes.RigBusy,
        _ => ExitCodes.Failed,
    };

    private static int Execute(ParsedCommand cmd, IRigOutput output)
    {
        var verb = cmd.Verb;
        if (!VerbTable.TryGet(verb, out var spec)) throw UnknownVerb(verb);

        using var mutex = new CrossProcessLock();
        using var rig = RigComposition.Build(output, cmd.Text(Options.InstancesRoot), mutex);

        // Internal, and deliberately outside everything: no target, no refusal matrix, no
        // lock. The 'start' that spawned this wrapper already holds one.
        if (string.Equals(verb, "host-mode", StringComparison.Ordinal))
        {
            RejectOptionsTheVerbDoesNotRead(cmd, spec);
            rig.Server.HostMode(
                cmd.Text(Options.Load), cmd.Text(Options.Map), cmd.Text(Options.New),
                cmd.Number(Options.GamePort), cmd.Number(Options.UpdatePort));
            return ExitCodes.Ok;
        }

        var known = rig.InstanceNames();
        var resolved = TargetResolver.Resolve(
            verb,
            cmd.Text(Options.Target),
            known,
            allowUnknown: string.Equals(verb, "create", StringComparison.Ordinal));

        // The matrix runs before the option check, because refusal 21 applies to EVERY verb:
        // an instance-shape flag typed against the dedicated server is a wrong model of the
        // rig whether or not the verb happens to read that flag, and saying so is worth more
        // than "this verb ignores it".
        RefusalMatrix.Assert(verb, resolved, BuildRefusalInputs(cmd));
        RejectOptionsTheVerbDoesNotRead(cmd, spec);

        if (spec.NeedsLock) rig.Lock.AssertHeld(verb, cmd.Text(Options.As), "testrig");

        output.Value("verb", verb);
        output.Value("target", resolved.Spec);
        output.Value("targetKind", RefusalMatrix.KindName(resolved.Kind));
        output.Value("instances", resolved.Names);

        return verb switch
        {
            "lock" => Lock(cmd, output, rig),
            "unlock" => Unlock(cmd, output, rig),
            "refresh-lock" => RefreshLock(cmd, output, rig),
            "capture-baseline" => CaptureBaseline(cmd, output, rig),
            "reset" => Reset(cmd, output, rig),
            "status" => Status(cmd, output, rig, resolved),
            "list" => List(rig, resolved),
            "logs" => Logs(cmd, rig, resolved),
            "snapshot" => Snapshot(cmd, rig, resolved),
            "update-game" => UpdateGame(cmd, rig, resolved),
            "update-mods" => UpdateMods(cmd, rig, resolved),
            "deploy" => Deploy(cmd, rig, resolved),
            "create" => Create(cmd, rig, resolved),
            "remove" => Remove(cmd, rig, resolved),
            "start" => Start(cmd, rig, resolved),
            "stop" => Stop(cmd, output, rig, resolved),
            "save" => Save(cmd, rig, resolved),
            "wait" => Wait(cmd, rig, resolved),
            "call" => Call(cmd, rig, resolved),
            "send" => Send(cmd, rig),
            "playtest" => Playtest(cmd, output, rig),
            _ => throw new InvalidOperationException(
                $"'{verb}' is in the verb table and has no dispatch arm. That is a bug in "
                + "TestRig/src/TestRig.Cli/CliApp.cs, not a problem with the command."),
        };
    }

    // ---- the session -------------------------------------------------------

    private static int Lock(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var purpose = cmd.Text(Options.Purpose);
        if (purpose.Length == 0)
        {
            throw new CliUsageException(
                "'lock' requires --purpose \"<short reason>\", e.g. --purpose \"Playtesting network paint for "
                + "SprayPaintPlus\". See TestRig/CLAUDE.md.");
        }

        var result = rig.Lock.AcquireAsync(new AcquireOptions
        {
            Purpose = purpose,
            CallerId = NullIfEmpty(cmd.Text(Options.As)),

            // Only when typed, both of them. Forwarding a default is what silently dropped a
            // session that had taken the rig with a 240-minute ceiling back to 60 on its next
            // re-assert, with no warning.
            TtlMinutes = cmd.NumberIfTyped(Options.TtlMinutes),
            IdleCeilingMinutes = cmd.NumberIfTyped(Options.IdleCeilingMinutes),

            // Likewise, and this one is load bearing: the global default is 300 and the lock's
            // own is 0 (refuse at once). Forwarding 300 would turn every lock into a
            // five-minute queue.
            WaitSeconds = cmd.WasTyped(Options.WaitSeconds) ? cmd.Number(Options.WaitSeconds) : 0,

            BreakLock = cmd.Flag(Options.BreakLock),
            KeepState = cmd.Flag(Options.KeepState),
            Tool = "testrig",
            OnReclaim = () => Reclaim(output, rig),
        }).GetAwaiter().GetResult();

        // 'owner' and 'acquireKind' are emitted by the lock service itself, as values rather
        // than formatted lines, so nothing has to regex them. The human sink turns the owner
        // into the TESTRIG-OWNER contract line and puts it last.
        output.Value("purpose", result.Purpose);
        output.Value("ttlMinutes", result.TtlMinutes);
        output.Value("idleCeilingMinutes", result.IdleCeilingMinutes);
        output.Value("stateWasReset", result.StateWasReset);
        output.Value("busyDetail", result.BusyDetail);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Tears down what a reclaimed session left running, on BOTH halves.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever on a reclaim, never on a break: <c>--break-lock</c> is a human authorising
    /// the transfer of a reservation, not the killing of whatever is on the rig, and
    /// <see cref="SessionLockService"/> is where that distinction is enforced.
    /// </para>
    /// <para>
    /// The client half is force-killed rather than torn down in order, deliberately: the
    /// session that owned these instances has been silent for at least the idle ceiling,
    /// there is no test left to preserve, and a hung client's control plane is exactly the
    /// thing likely not to answer. The server half goes through its normal teardown, which
    /// already force-kills on a timeout.
    /// </para>
    /// <para>
    /// Neither half may take the acquisition down with it. The lock is already ours by this
    /// point, and refusing to hand it over because a dead session's leftovers would not die
    /// leaves nobody able to use the rig at all.
    /// </para>
    /// </remarks>
    private static void Reclaim(IRigOutput output, RigComposition rig)
    {
        Attempt("client instances", rig.Clients.StopOrphansByPid);
        Attempt("the dedicated server", rig.Server.Teardown);

        void Attempt(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
            {
                output.Line(
                    OutputLevel.Warning,
                    $"[Lock] Reclaimed the rig, but tearing down {what} left by the reclaimed session failed: "
                    + $"{ex.Message}. Check with: testrig status, and kill anything untracked by pid.");
            }
        }
    }

    private static int Unlock(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var callerId = cmd.Text(Options.As);
        var held = rig.Lock.ReadLock();
        if (held is not null && callerId.Length > 0 && LockFields.SameOwner(held.Get(LockFields.Owner), callerId))
        {
            var busy = rig.Busy.Probe();
            if (busy.Busy)
            {
                output.Line(
                    OutputLevel.Warning,
                    $"[Unlock] Releasing while the rig is still busy ({busy.Detail}). Stop it first: "
                    + $"testrig stop --target all --as {callerId}");
            }
        }

        var result = rig.Lock.Release(
            NullIfEmpty(callerId),
            cmd.Flag(Options.BreakLock),
            cmd.Flag(Options.Force),
            cmd.Flag(Options.KeepState));

        output.Line(OutputLevel.Info, result.Message);
        output.Value("status", result.Status.ToString());
        output.Value("owner", result.Owner);
        output.Value("restoreSkipped", result.RestoreSkipped);
        output.Value("restoreFailure", result.RestoreFailure);

        // One table, in Core, shared with 'stop --release' and the playtest engine's teardown.
        // Each of the three used to carry its own copy, and all three exited 0 on a rig that
        // had no lock at all, which is the code a caller reads as "released".
        return RigExitCodes.For(result.Status);
    }

    private static int RefreshLock(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var callerId = cmd.Text(Options.As);
        if (callerId.Length == 0)
            throw new CliUsageException("'refresh-lock' requires --as <id> (the owner id printed by 'lock').");

        var result = rig.Lock.Refresh(
            callerId,
            cmd.NumberIfTyped(Options.TtlMinutes),
            cmd.NumberIfTyped(Options.IdleCeilingMinutes));

        output.Line(OutputLevel.Info, result.Message);
        output.Value("owner", result.Owner);
        output.Value("ttlMinutes", result.TtlMinutes);
        output.Value("idleCeilingMinutes", result.IdleCeilingMinutes);
        return ExitCodes.Ok;
    }

    private static int CaptureBaseline(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var capture = rig.Baseline.Capture(rig.Planner.CheckGate(), cmd.Text(Options.As), cmd.Flag(Options.Force));
        output.Value("entries", capture.Entries);
        output.Value("stored", capture.Stored);
        output.Value("whatIf", capture.WhatIf);
        return ExitCodes.Ok;
    }

    private static int Reset(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var dryRun = cmd.Flag(Options.DryRun);
        var plan = rig.Planner.Build();
        var run = rig.Reset.Run(plan, new ResetOptions
        {
            WhatIf = dryRun,

            // The dry run deliberately ignores --keep-state: printing the plan is the whole
            // action, and a plan is the same plan either way.
            KeepState = !dryRun && cmd.Flag(Options.KeepState),

            // The bulk-delete ceiling refuses rather than warns, and its refusal names this
            // flag as the way past it. Without the flag bound, that remedy could not be typed.
            AllowBulkWorldDelete = cmd.Flag(Options.AllowBulkWorldDelete),
            Reason = dryRun ? "explicit reset (dry run)" : "explicit reset",
        });

        output.Value("refused", run.Refused);
        output.Value("refusalReason", run.RefusalReason);

        // A dry run is never itself refused, so 'refused' alone told a caller nothing about
        // the answer the dry run exists to give. These two carry it, and so does the exit
        // code below.
        output.Value("wouldRefuse", run.WouldRefuse);
        output.Value("wouldRefuseReason", run.WouldRefuseReason);

        output.Value("skipped", run.Skipped);
        output.Value("whatIf", run.WhatIf);
        output.Value("performed", run.Performed.Count);
        output.Value("failures", run.Failures);
        output.Value("worldDeletes", run.Plan.WorldDeleteCount);

        if (run.Refused || run.WouldRefuse) return ExitCodes.RigBusy;
        return run.Failures.Count > 0 ? ExitCodes.Failed : ExitCodes.Ok;
    }

    // ---- observation -------------------------------------------------------

    private static int Status(
        ParsedCommand cmd, IRigOutput output, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = NullIfEmpty(cmd.Text(Options.As));
        var status = rig.Lock.GetStatus(callerId);
        foreach (var line in StatusRenderer.Render(status, callerId, rig.Clock.UtcNow))
            output.Line(line.Level, line.Text);

        output.Value("lockState", status.State.ToString());
        output.Value("owner", status.Lock?.Get(LockFields.Owner));
        output.Value("purpose", status.Lock?.Get(LockFields.Purpose));
        output.Value("timerExpired", status.TimerExpired);
        output.Value("ceilingExceeded", status.CeilingExceeded);
        output.Value("busy", status.Busy.Busy);
        output.Value("busyDetail", status.Busy.Detail);
        output.Value("dirty", status.Dirty.Dirty);
        output.Value("crashed", status.Dirty.Crashed);
        output.Value("serverWorldsRecorded", status.ServerWorlds.Recorded);
        output.Value("serverWorldCount", status.ServerWorlds.Count);
        output.Value("clientWorldsRecorded", status.ClientWorlds.Recorded);
        output.Value("clientWorldCount", status.ClientWorlds.Count);

        // The two the session-boundary drift report reads. Neither is ever written by the rig
        // and neither can be isolated from the developer's own client, so naming them is the
        // only way an operator can tell WHICH folder and WHICH key a drift line came from.
        output.Value("sharedDataDir", rig.Paths.SharedDataDir);
        output.Value("playerPrefsKey", rig.Paths.PlayerPrefsKey);

        // Read-only and rig-wide. Whether the rig is locked is the thing automation polls
        // for, and it is already above this line.
        output.Line(OutputLevel.Info, string.Empty);

        var staleRows = 0;
        if (resolved.Server) staleRows += rig.Server.WriteStatus();
        if (resolved.Kind != TargetKind.Server) staleRows += rig.Clients.WriteStatus(resolved.Names);

        // RESET-060. Config class only, which is a handful of small files, and it is the class
        // a restore can actually act on. Without this the only way to ask "what has drifted"
        // was to run a reset, which is the one thing somebody asking has not decided to do.
        var configDrift = rig.Baseline.CompareConfig();
        output.Value("configDrift", configDrift.Count);
        if (configDrift.Count > 0)
        {
            output.Line(OutputLevel.Info, string.Empty);
            output.Line(OutputLevel.Warning,
                "config drift since the baseline (a reset puts these back; nothing here is fixed by status):");
            foreach (var line in configDrift) output.Line(OutputLevel.Warning, "  " + line);
        }

        // CLI-086. With nothing stale the section used to print NOTHING, which reads the same
        // as a section that failed to run. Silence is not an answer here: "the rig is current"
        // is the thing a caller most wants confirmed before a test.
        output.Line(OutputLevel.Info, string.Empty);
        output.Line(OutputLevel.Info, "staleness (game versions and deployed payloads; reported, never fixed here):");
        output.Line(OutputLevel.Info, staleRows == 0
            ? "  (nothing to report)"
            : $"  {staleRows} row(s), listed above.");
        output.Value("staleRows", staleRows);

        return ExitCodes.Ok;
    }

    private static int List(RigComposition rig, ResolvedTarget resolved)
    {
        if (resolved.Server) rig.Server.WriteListRow();
        if (resolved.Kind != TargetKind.Server) rig.Clients.WriteListRows(resolved.Names);
        return ExitCodes.Ok;
    }

    private static int Logs(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var query = new LogQuery(cmd.Number(Options.Tail), cmd.Text(Options.Grep), cmd.Flag(Options.Unity));
        if (resolved.Server) rig.Server.Logs(query);
        if (resolved.Kind != TargetKind.Server) rig.Clients.Logs(resolved.Names, query);
        return ExitCodes.Ok;
    }

    private static int Snapshot(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        rig.Clients.Snapshot(resolved.Names, cmd.Text(Options.OutFile));
        return ExitCodes.Ok;
    }

    // ---- provisioning ------------------------------------------------------

    private static int UpdateGame(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);
        if (resolved.Server) rig.Server.UpdateGame(callerId);
        if (resolved.Kind != TargetKind.Server)
            rig.Clients.UpdateGame(callerId, resolved.Names, cmd.Text(Options.Desktop));
        return ExitCodes.Ok;
    }

    private static int UpdateMods(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);
        if (resolved.Server) rig.Server.UpdateMods(callerId, cmd.Text(Options.FromModConfig));
        if (resolved.Kind != TargetKind.Server) rig.Clients.UpdateMods(callerId, resolved.Names);
        return ExitCodes.Ok;
    }

    private static int Deploy(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);
        var configuration = cmd.Choice(Options.Configuration);
        var mods = SplitList(cmd.Text(Options.Mod));

        if (resolved.Server) rig.Server.Deploy(callerId, mods, configuration);
        if (resolved.Kind != TargetKind.Server) rig.Clients.Deploy(callerId, resolved.Names, mods, configuration);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// Builds ONE instance, forwarding only what was actually typed.
    /// </summary>
    /// <remarks>
    /// Every identity field is null unless the caller wrote it, because <c>create --force</c>
    /// is the routine way to pick up a new plugin build and Core keeps an untyped value from
    /// the existing entry. Forwarding the parser's defaults instead would demote a host on
    /// every rebuild, move a game port out from under a joiner, and reset the ClientId the
    /// server keys a player's body on.
    /// </remarks>
    private static int Create(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        if (resolved.Names.Count != 1)
            throw new CliUsageException("'create' builds one instance at a time. Name it with --target <name>.");

        rig.Clients.Create(
            cmd.Text(Options.As),
            resolved.Names[0],
            cmd.Flag(Options.Force),
            new InstanceShape(
                Role: TextIfTyped(cmd, Options.Role),
                Port: cmd.NumberIfTyped(Options.Port),
                GamePort: cmd.NumberIfTyped(Options.GamePort),
                ClientId: TextIfTyped(cmd, Options.ClientId),
                Username: TextIfTyped(cmd, Options.Username),
                Width: cmd.NumberIfTyped(Options.Width),
                Height: cmd.NumberIfTyped(Options.Height),
                ForceGameplayInput: FlagIfTyped(cmd, Options.ForceGameplayInput),
                SeedMods: cmd.Flag(Options.SeedMods),

                // Null when the flag was not typed, so a rebuild keeps the set the instance
                // already records. An empty --under-test "" is a typed answer and clears it.
                UnderTest: cmd.WasTyped(Options.UnderTest) ? SplitList(cmd.Text(Options.UnderTest)) : null,
                Desktop: cmd.Text(Options.Desktop)));
        return ExitCodes.Ok;
    }

    private static int Remove(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        if (resolved.Names.Count != 1)
            throw new CliUsageException("'remove' deletes one instance at a time. Name it with --target <name>.");

        rig.Clients.Remove(cmd.Text(Options.As), resolved.Names[0], cmd.Flag(Options.Force), cmd.Text(Options.Desktop));
        return ExitCodes.Ok;
    }

    // ---- lifecycle ---------------------------------------------------------

    private static int Start(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);

        // Clients first, then the server. A joiner needs its host in a world before it can
        // connect, and an instance boots only to the menu, so bringing the client half up
        // first costs nothing and leaves the ordering under the caller's control.
        if (resolved.Kind != TargetKind.Server)
        {
            rig.Clients.Start(callerId, resolved.Names, cmd.Text(Options.Desktop));
        }

        if (resolved.Server)
        {
            rig.Server.Start(
                callerId, cmd.Text(Options.Load), cmd.Text(Options.Map), cmd.Text(Options.New),
                cmd.Number(Options.GamePort), cmd.Number(Options.UpdatePort));
        }

        return ExitCodes.Ok;
    }

    private static int Stop(
        ParsedCommand cmd, IRigOutput output, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = NullIfEmpty(cmd.Text(Options.As));
        var breakLock = cmd.Flag(Options.BreakLock);

        // FIRST, and do not reorder. The expired-and-busy branch self-renews a foreign lock
        // and reports LiveForeign; the release predicate below has no busy term at all, so
        // without this call a --release would strip a live foreign lock mid-test.
        var state = rig.Lock.ReadStateAndRenew(callerId, refreshIfMine: false);
        output.Value("lockState", state.State.ToString());

        if (state.State == LockState.LiveForeign)
        {
            if (!breakLock)
            {
                throw new RigRefusalException(
                    RigRefusalKind.HeldByAnotherSession,
                    "[Stop] Refusing to stop a rig held by another live session. Report this to the user. Only the "
                    + "user may authorize --break-lock. See TestRig/CLAUDE.md.");
            }

            output.Line(
                OutputLevel.Warning,
                "[Stop] --break-lock: stopping a rig held by another live session.");
        }

        // Clients before the server: a joiner still attached when its server goes down leaves
        // the host holding a peer that never said goodbye, which is the state a world would be
        // saved in.
        //
        // A failure in either half stops the sequence and is not swallowed, so the release
        // below cannot run over a rig that is still up. Releasing after a failed teardown
        // leaves instances running with no lock and no timer to save anybody.
        if (resolved.Kind != TargetKind.Server)
        {
            rig.Clients.Stop(
                callerId ?? string.Empty, resolved.Names, cmd.Number(Options.TimeoutSeconds),
                cmd.Number(Options.WaitSeconds), cmd.Text(Options.SaveName), cmd.Flag(Options.Force));
        }

        if (resolved.Server)
        {
            rig.Server.Stop(
                callerId ?? string.Empty, cmd.Text(Options.SaveName),
                cmd.Number(Options.TimeoutSeconds), cmd.Number(Options.WaitSeconds));
        }

        // The teardown's own exit code, which --release may downgrade below.
        var exit = ExitCodes.Ok;

        if (cmd.Flag(Options.Release))
        {
            var release = rig.Lock.ReleaseForStop(callerId, breakLock, cmd.Flag(Options.KeepState));
            exit = RigExitCodes.For(release.Status);
            output.Line(exit == ExitCodes.Ok ? OutputLevel.Info : OutputLevel.Warning, release.Message);
            output.Value("releaseStatus", release.Status.ToString());

            // A release that did not happen must not exit 0. The teardown above succeeded and
            // said so, but '--release' also asked for the session to end, and a caller that
            // reads 0 as "the rig is free and mine is finished" is wrong on a rig that had no
            // lock at all or has since been taken by somebody else. 'stop' WITHOUT --release
            // still exits 0 with no lock, which is what keeps orphan cleanup always possible.
        }

        output.Line(OutputLevel.Info, "[Stop] Done.");
        return exit;
    }

    private static int Save(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);
        var saveName = cmd.Text(Options.SaveName);
        var waitSeconds = cmd.Number(Options.WaitSeconds);

        // Server first here, the opposite of stop: the world holder writes, then the clients.
        if (resolved.Server) rig.Server.Save(callerId, saveName, waitSeconds);
        if (resolved.Kind != TargetKind.Server) rig.Clients.Save(callerId, resolved.Names, saveName, waitSeconds);
        return ExitCodes.Ok;
    }

    private static int Wait(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var callerId = cmd.Text(Options.As);
        var stage = cmd.Choice(Options.Stage);
        var waitSeconds = cmd.Number(Options.WaitSeconds);

        // No lock is needed, but one the caller holds is refreshed: a 600-second barrier
        // outlasts the 10-minute TTL.
        if (callerId.Length > 0) rig.Lock.RefreshIfMine(callerId);

        if (resolved.Server) rig.Server.Wait(callerId, ServerStage(stage), waitSeconds);
        if (resolved.Kind != TargetKind.Server)
            rig.Clients.Wait(callerId, resolved.Names, ClientStage(stage), waitSeconds);
        return ExitCodes.Ok;
    }

    /// <summary>
    /// The server's readiness stages. Explicit, and it throws on anything else.
    /// </summary>
    /// <remarks>
    /// PowerShell mapped <c>process</c> to <c>process</c> and EVERYTHING else to
    /// <c>inWorld</c>. That is only ever correct because the refusal matrix already rejected
    /// the three client stages, so a stage added to the set later would have been silently
    /// mis-served. Two of those three are legitimate here now, because the merged plugin gives
    /// this half a control plane to ping and a loaded-plugin count to count; only
    /// <c>menu</c> is still refused, and a dedicated server genuinely never has one.
    /// </remarks>
    public static ReadinessStage ServerStage(string stage) => stage switch
    {
        "process" => ReadinessStage.Process,
        "ping" => ReadinessStage.Ping,
        "modsLoaded" => ReadinessStage.ModsLoaded,
        "inWorld" => ReadinessStage.InWorld,
        _ => throw new InvalidOperationException(
            $"'{stage}' is not a dedicated-server readiness stage and should have been refused before now. "
            + "That is a bug in the refusal matrix, not a problem with the command."),
    };

    /// <summary>An instance's readiness stages. <c>process</c> means "the control plane answers".</summary>
    public static ReadinessStage ClientStage(string stage) => stage switch
    {
        "process" => ReadinessStage.Ping,
        "ping" => ReadinessStage.Ping,
        "modsLoaded" => ReadinessStage.ModsLoaded,
        "menu" => ReadinessStage.Menu,
        "inWorld" => ReadinessStage.InWorld,
        _ => throw new InvalidOperationException($"'{stage}' is not a client readiness stage."),
    };

    // ---- driving -----------------------------------------------------------

    /// <summary>
    /// One HTTP request per selected target, on BOTH halves.
    /// </summary>
    /// <remarks>
    /// The dedicated server used to be refused here on the grounds that it had no control
    /// plane. One plugin loads into both halves now and the server answers on its own port, so
    /// the refusal was describing a rig that no longer exists while the plane was up and
    /// replying. <c>--target all</c> therefore reaches the server AND every instance; the
    /// server goes first, because it is the authority and a fan-out that read the clients first
    /// would report them against a server state nobody had looked at.
    /// </remarks>
    private static int Call(ParsedCommand cmd, RigComposition rig, ResolvedTarget resolved)
    {
        var path = cmd.Text(Options.Path);
        if (path.Length == 0)
            throw new CliUsageException("'call' requires --path <control-plane path>, for example --path /status.");

        if (!resolved.Server && resolved.Names.Count == 0)
        {
            throw new CliUsageException(
                "'call' needs at least one target. Name an instance with --target <name>, fan out with "
                + "--target clients, or talk to the dedicated server with --target server.");
        }

        var as_ = cmd.Text(Options.As);
        var body = cmd.Text(Options.Body);
        var timeout = cmd.Number(Options.CallTimeoutSeconds);

        if (resolved.Server) rig.Server.Call(as_, path, body, timeout);
        if (resolved.Names.Count > 0) rig.Clients.Call(as_, resolved.Names, path, body, timeout);

        return ExitCodes.Ok;
    }

    private static int Send(ParsedCommand cmd, RigComposition rig)
    {
        var command = cmd.Text(Options.Command);
        if (command.Length == 0) throw new CliUsageException("'send' requires --command '<console text>'.");

        rig.Server.Send(cmd.Text(Options.As), command);
        return ExitCodes.Ok;
    }

    // ---- playtests ---------------------------------------------------------

    /// <summary>
    /// Runs the checks compiled into this binary, with nobody at the keyboard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No lock is taken here, deliberately. The harness takes and releases one PER CHECK,
    /// which is what buys a state reset per check: the reset is between sessions by design,
    /// so two checks under one lock would get none.
    /// </para>
    /// <para>
    /// The three outcomes reach the caller as three exit codes. A fail accuses the mod; an
    /// inconclusive says the rig never got far enough to have an opinion, and collapsing the
    /// two is what the PowerShell harness did to every non-zero exit it saw.
    /// </para>
    /// </remarks>
    private static int Playtest(ParsedCommand cmd, IRigOutput output, RigComposition rig)
    {
        var checks = TestRig.Playtests.Playtests.All;

        // --list-flakes runs first and is the ONLY thing that survives an empty check set: the
        // taxonomy is a fact about the code and answers with no rig, no lock and no game at
        // all, which is the property PLAYTEST-011 exists for.
        if (cmd.Flag(Options.ListFlakes))
        {
            output.Line(OutputLevel.Info, PlaytestListing.Flakes(new FlakeCatalogue()));
            return ExitCodes.Ok;
        }

        // An empty set fails, INCLUDING for --list-checks, and that is the fix for a measured
        // defect rather than tidiness: the shipped binary once carried zero checks and this
        // verb answered with a bare header and exit 0, which reads as a clean answer. The rule
        // itself lives in PlaytestListing, so it cannot be defeated by reordering this method,
        // and the renderer holds it too.
        PlaytestListing.AssertAnyCompiledIn(checks);

        if (cmd.Flag(Options.ListChecks))
        {
            output.Value("checkCount", checks.Count);
            output.Value("checks", checks.Select(static c => c.Spec.Name).ToArray());
            output.Line(OutputLevel.Info, PlaytestListing.Checks(checks, cmd.Text(Options.Only)));
            return ExitCodes.Ok;
        }

        var (tier1, tier1Warning) = Tier1SaveFolder.Resolve(rig.FileSystem, rig.Paths);
        if (tier1Warning is not null) output.Line(OutputLevel.Warning, "[Playtest] " + tier1Warning);

        var evidenceRoot = cmd.Text(Options.EvidenceRoot);
        if (evidenceRoot.Length == 0) evidenceRoot = Path.Combine(rig.Paths.RigHome, "playtest", "evidence");

        var suite = new SuiteRunner(new PlaytestDependencies
        {
            Transport = new CoreRigTransport(rig.Transport),
            Launcher = new CoreRigLauncher(rig.Lock, rig.ClientHalf, rig.Recorder, () => Reclaim(output, rig)),
            Registry = new CoreRigRegistry(rig.Registry),
            Files = rig.FileSystem,
            LogFiles = new SystemLogFiles(rig.FileSystem),
            Clock = rig.Clock,
            Sleeper = SystemSleeper.Instance,
            RigHome = rig.Paths.RigHome,
            Tier1SaveRoot = tier1,
            Log = line => output.Line(OutputLevel.Info, line),
        }).Run(new SuiteRequest
        {
            SuiteName = cmd.Text(Options.SuiteName),
            Checks = checks,
            EvidenceRoot = evidenceRoot,
            Only = cmd.Text(Options.Only),
            LockWaitSeconds = cmd.WasTyped(Options.WaitSeconds) ? cmd.Number(Options.WaitSeconds) : 0,

            // The only way to hand a staged rig from one check to the next: the reset runs
            // between sessions and the harness takes one lock per check.
            KeepState = cmd.Flag(Options.KeepState),
        });

        output.Value("suite", suite.Suite);
        output.Value("passed", suite.Passed);
        output.Value("failed", suite.Failed);
        output.Value("inconclusive", suite.Inconclusive);
        output.Value("evidence", evidenceRoot);
        output.Value("tier1Verdict", SaveInventoryScanner.VerdictText(suite.Tier1.Verdict));

        // Returned unchanged, never mapped. A translation here is what made the bundle and the
        // process disagree: run.md printed "Exit code 2" on a run that correctly exited 8.
        return suite.ExitCode;
    }

    // ---- plumbing ----------------------------------------------------------

    private static RefusalInputs BuildRefusalInputs(ParsedCommand cmd)
    {
        var typed = new List<string>();
        foreach (var flag in Options.InstanceShape)
            if (cmd.WasTyped(flag))
                typed.Add(flag);

        return new RefusalInputs(
            cmd.Choice(Options.Stage),
            cmd.Text(Options.SaveName),
            cmd.Text(Options.Load).Length > 0 || cmd.Text(Options.New).Length > 0,
            typed);
    }

    /// <summary>
    /// An option a verb does not read is a usage error, not a silent no-op.
    /// </summary>
    /// <remarks>
    /// The measured case: <c>--dry-run</c> bound on all twenty-two verbs and was honoured by
    /// <c>reset</c> alone, so <c>testrig stop --target all --dry-run</c> stopped the rig
    /// without a word about the flag it ignored.
    /// </remarks>
    private static void RejectOptionsTheVerbDoesNotRead(ParsedCommand cmd, VerbSpec spec)
    {
        foreach (var typed in cmd.TypedOptions)
        {
            if (spec.Options.Contains(typed) || Options.Global.Contains(typed)) continue;

            var reads = spec.Options.Count > 0
                ? string.Join(", ", spec.Options.Select(static o => "--" + o))
                : "(none)";
            throw new CliUsageException(
                $"--{typed} is not read by '{spec.Name}', so it would have done nothing. "
                + $"'{spec.Name}' reads: {reads}. Run 'testrig' with no verb for the whole surface.");
        }
    }

    private static CliUsageException UnknownVerb(string verb)
    {
        var close = VerbTable.Suggest(verb);
        var hint = close.Count > 0 ? $" Did you mean: {string.Join(", ", close)}?" : string.Empty;
        return new CliUsageException(
            $"'{verb}' is not a testrig verb.{hint} Run 'testrig' with no verb for the whole surface. "
            + $"Verbs: {string.Join(", ", VerbTable.PublicNames)}");
    }

    /// <summary>Comma-separated, trimmed, empties dropped. A single name stays a one-element list.</summary>
    private static IReadOnlyList<string> SplitList(string value)
    {
        if (value.Length == 0) return [];
        var items = new List<string>();
        foreach (var part in value.Split(','))
        {
            var item = part.Trim();
            if (item.Length > 0) items.Add(item);
        }

        return items;
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    /// <summary>The option's value only when it was typed, otherwise null.</summary>
    private static string? TextIfTyped(ParsedCommand cmd, string option) =>
        cmd.WasTyped(option) ? cmd.Text(option) : null;

    /// <summary>The flag's value only when it was typed, otherwise null.</summary>
    /// <remarks>
    /// A flag that defaults ON is the case this exists for: <c>--force-gameplay-input</c> is
    /// true whether or not anybody wrote it, so reading <see cref="ParsedCommand.Flag"/>
    /// alone cannot tell a deliberate true from the default, and Core needs null to mean
    /// "keep whatever the existing entry says".
    /// </remarks>
    private static bool? FlagIfTyped(ParsedCommand cmd, string option) =>
        cmd.WasTyped(option) ? cmd.Flag(option) : null;

    /// <summary>
    /// Drops Core's refusal sentinel from a message before it is printed.
    /// </summary>
    /// <remarks>
    /// <c>TestRig.Core.Rig.RefusalMatrix</c> prefixes its rendered block with
    /// <c>[testrig refusal]</c> so a shell caller could tell a teaching refusal from a crash.
    /// Here the exception type carries that distinction and the exit code encodes it, so the
    /// marker is redundant and printing it would only put noise at the top of every refusal.
    /// </remarks>
    private static string StripSentinel(string message)
    {
        if (!message.StartsWith(CoreRefusals.Sentinel, StringComparison.Ordinal)) return message;
        return message[CoreRefusals.Sentinel.Length..].TrimStart('\r', '\n');
    }
}
