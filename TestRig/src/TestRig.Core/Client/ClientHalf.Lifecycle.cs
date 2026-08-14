using System.Globalization;
using System.Text.Json;
using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Core.Infrastructure;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

public sealed partial class ClientHalf
{
    // =====================================================================
    // start
    // =====================================================================

    /// <summary>
    /// Launches each selected instance on the rig's isolated desktop.
    /// </summary>
    /// <remarks>
    /// A client instance boots TO THE MENU and no further. It has no way to take a world on
    /// its command line, so entering a world is a separate step over the control plane. That
    /// is the opposite of the dedicated server, which cannot start without a world at all,
    /// and it is why the two halves cannot share one meaning for 'start'.
    /// </remarks>
    public async Task StartAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? callerId = null,
        string desktop = RigConstants.DefaultDesktop,
        CancellationToken ct = default)
    {
        AssertGate("start", callerId);

        if (entries.Count == 0)
        {
            Say("[Start] No client instances selected.");
            return;
        }

        // Pre-flight the WHOLE set before launching anything, and refuse rather than skip.
        // Both of these used to be a warning and a continue. A skipped start is the worst
        // possible outcome: the command comes back looking successful, the instance that was
        // skipped is still in whatever world it was already in, and every assertion
        // afterwards runs against a rig that is not the one the caller asked for.
        foreach (var entry in entries)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);

            if (!_fs.FileExists(paths.Exe))
            {
                // The root is named ALONG WITH WHERE IT CAME FROM, because the usual cause is
                // that the tree is somewhere else entirely: an instance built under an
                // explicit root used to be looked for beside the launcher, and the message
                // read as "unprovisioned" while the tree sat on another volume (CLIENT-110).
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[Start] Instance '{entry.InstanceName}' is in the registry but has no tree at {paths.Exe}. "
                    + $"That location came from {paths.RootSource}. Rebuild it there (testrig create --target "
                    + $"{entry.InstanceName} --force --as <id>), or name the root the tree actually has with "
                    + "--instances-root <root>, which also records it for every later command.");
            }

            var claimed = PidFiles.Read(_fs, paths.PidFile);
            if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"[Start] Instance '{entry.InstanceName}' is already running (PID {claimed}). Nothing was "
                    + $"started. Stop it first (testrig stop --target {entry.InstanceName} --as <id>) or check: "
                    + "testrig status. A start that silently skipped would leave it in whatever world it is "
                    + "already in.");
            }

            if (claimed is not null)
            {
                // A pid file whose process is gone, or whose number now belongs to something
                // that is not the game. Refusing to start over a recycled id would make a
                // crashed instance unstartable until somebody deleted the file by hand
                // (CLIENT-112).
                Say($"[{entry.InstanceName}] Stale game.pid ignored: PID {claimed} is not a live game client. "
                    + "This start replaces it.");
            }
        }

        if (!string.IsNullOrEmpty(desktop))
        {
            _launcher.EnsureDesktop(desktop);
            Say($"[Start] Desktop: WinSta0\\{desktop} (created if absent, never switched to)");
        }
        else
        {
            Warn("[Start] No --desktop given. Instances will run on the developer's desktop and WILL take the "
                 + "foreground. Debugging only.");
        }

        // Once, before the loop (CLIENT-098 fixed): every manifest carries the whole rig's
        // port list, so the content does not change per instance, and the PowerShell rewrote
        // all N of them N times.
        WriteAllManifests(desktop);

        // Hosts first. Process order is not the real constraint (that is "the host is IN ITS
        // WORLD before a joiner connects", which only the /host and /connect sequence can
        // enforce), but starting them in this order costs nothing and puts the longest pole
        // in the ground first.
        var ordered = entries.Where(static e => e.IsHost).Concat(entries.Where(static e => !e.IsHost)).ToList();

        foreach (var entry in ordered)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);

            foreach (var dir in new[] { paths.Data, paths.UserData, paths.LogDir }) _fs.CreateDirectory(dir);

            var stamp = _clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var unityLog = Path.Combine(paths.LogDir, $"unity-{stamp}.log");

            // -logFile with a UNIQUE path is mandatory, and not for the reason it looks like.
            // Two instances without it both start fine; what happens is that the second
            // starter wins Player.log, the first instance's log is discarded with no error,
            // and Player-prev.log is zeroed by two rotations in one second, destroying the
            // developer's previous log (CLIENT-118).
            //
            // -settings SavePath is DELIBERATELY ABSENT (CLIENT-119). Passing it makes
            // StationeersLaunchPad scan an empty SavePath\mods\, find nothing, and rewrite
            // the developer's SHARED modconfig.xml with every Local entry deleted. Measured
            // on a first boot: five local mod entries silently removed, 289 lines to 274,
            // nothing warned. The redirect is SavePathOverride, written at provision time.
            //
            // -screen-* are kept even though the game overwrites them a moment later, so the
            // window is the right size before the plugin's patches run and there is no
            // fullscreen flash. The values come from the ENTRY, not from a launcher default
            // (CLIENT-121 fixed).
            var width = entry.Width ?? CreateOptions.DefaultWidth;
            var height = entry.Height ?? CreateOptions.DefaultHeight;

            var commandLine = WindowsCommandLine.Build(
                paths.Exe,
                "-logFile", unityLog,
                "-settingspath", paths.Settings,
                "-screen-width", width.ToString(CultureInfo.InvariantCulture),
                "-screen-height", height.ToString(CultureInfo.InvariantCulture),
                "-screen-fullscreen", "0");

            var pid = _launcher.Start(new InstanceLaunch(
                ExePath: paths.Exe,
                CommandLine: commandLine,
                // The instance's DATA directory, not the game tree: imgui.ini and
                // output_log.txt are resolved against the working directory (CLIENT-126).
                WorkingDirectory: paths.Data,
                Desktop: string.IsNullOrEmpty(desktop) ? null : desktop,
                ManifestPath: paths.Manifest));

            // The start time closes pid reuse exactly, rather than by a margin: two
            // rocketstation processes is the normal case here, so an image check alone is
            // not enough.
            var started = _processes.TryGet((int)pid)?.StartTimeUtc;
            PidFiles.Write(_fs, paths.PidFile, (int)pid, started);

            Say($"[{entry.InstanceName}] PID {pid}, role {entry.RoleOr()}, port {entry.Port}, gamePort "
                + $"{entry.GamePortOr(0)}, log {unityLog}");
        }

        Say("[Start] Boot to the main menu takes roughly 100 seconds. Wait for it with:");
        Say("[Start]   testrig wait --target clients --stage menu");

        // The one ordering rule that cannot be enforced from out here, stated where it is
        // needed rather than left to a document nobody opens: a joiner has nothing to reach
        // until the host is hosting, and /connect against a host that is still loading fails
        // in a way that reads like a bad address (CLIENT-130).
        var firstHost = ordered.FirstOrDefault(static e => e.IsHost);
        if (firstHost is not null)
        {
            var hostPort = firstHost.GamePortOr(0);
            Say("[Start] This set contains a host. The host must be IN ITS WORLD before any joiner connects:");
            Say($"[Start]   testrig wait --target {firstHost.InstanceName} --stage menu");
            Say($"[Start]   testrig call --target {firstHost.InstanceName} --as <id> --path {Endpoints.Host} "
                + "--body '{\"world\":\"Lunar\"}'");
            Say($"[Start]   testrig wait --target {firstHost.InstanceName} --stage inWorld --wait-seconds 600");
            Say($"[Start]   then each joiner: --path {Endpoints.Connect} --body "
                + $"'{{\"address\":\"127.0.0.1\",\"port\":{hostPort}}}'");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    // =====================================================================
    // stop
    // =====================================================================

    /// <summary>
    /// Host-aware ordered teardown.
    /// </summary>
    /// <remarks>
    /// <para>
    /// NOT lock-gated, so an orphan or a dead session can always be cleaned up (CLIENT-213).
    /// The consequence the PowerShell never wrote down is that the crash marker is written
    /// inside the lock assertion, so a session whose ONLY mutating action is a stop leaves
    /// no <c>session.dirty</c> at all and the between-session restore has nothing to trigger
    /// on. Decided explicitly here: a stop that actually stops something marks the rig dirty
    /// through <see cref="MarkDirtyForStop"/>, because it changed rig state whether or not a
    /// lock was held.
    /// </para>
    /// <para>
    /// Stopping is the single most destructive action on this half: a stop of every instance
    /// ends whatever was running, and a torn-down client cannot report afterwards that its
    /// run was interrupted, so the results of the interrupted test simply look wrong.
    /// </para>
    /// </remarks>
    public async Task StopAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? callerId = null,
        int teardownSeconds = 0,
        int waitSeconds = 0,
        string? saveName = null,
        bool force = false,
        CancellationToken ct = default)
    {
        if (teardownSeconds <= 0) teardownSeconds = RigConstants.TeardownGraceSeconds;
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        if (entries.Count == 0)
        {
            // CLIENT-196 fixed: the PowerShell returned in silence here while start printed a
            // message for the identical case, so a stop against an empty rig produced nothing
            // but the dispatcher's closing line and read as a hang.
            Say("[Stop] No client instances selected.");
            return;
        }

        // Classify the WHOLE rig before touching any of it. Registry insertion order used to
        // decide the teardown, which normally meant the host went first and took the world
        // down under every joiner still in it. The refusals below are only worth having if
        // they fire while the rig is intact, so nothing is stopped until every one has
        // passed (CLIENT-197).
        var everything = await ClassifyRigAsync(ct).ConfigureAwait(false);
        var targetNames = entries.Select(static e => e.InstanceName).ToHashSet(StringComparer.Ordinal);
        var targets = everything.Where(r => targetNames.Contains(r.Name)).ToList();
        var outside = everything.Where(r => !targetNames.Contains(r.Name)).ToList();

        foreach (var host in targets.Where(static r => r.Class == InstanceClass.Host))
        {
            var risk = InstanceRoles.HostTeardownRisk(host, targets, outside);

            foreach (var stale in risk.StaleRosterEntries) Warn($"[Stop] {stale}");
            if (!risk.Blocked) continue;

            var text = $"[Stop] '{host.Name}' is hosting and something that is not part of this teardown is "
                       + "still attached to it:\n    " + string.Join("\n    ", risk.Reasons);

            if (!force)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    text + "\nNothing was stopped. Take the joiners down too (--target clients, or name them), "
                         + "or pass --force to end the world under them. --force is the same-session override "
                         + "and never touches the rig lock; taking a lock off another session is --break-lock.");
            }
            Warn(text + "\n[Stop] --force: ending it under them anyway.");
        }

        foreach (var unknown in targets.Where(static r => r.Class == InstanceClass.PossiblyHost))
        {
            var text = $"[Stop] '{unknown.Name}' is running but cannot be classified ({unknown.ClassSource}). It "
                       + "may be holding a world, and with no control plane it cannot be asked to save one, so "
                       + "killing it would take an unsaved world with it.";

            if (!force)
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    text + "\nNothing was stopped. Give it a moment and retry (a booting instance answers within "
                         + "roughly 100 s), or pass --force to kill it and accept the loss.");
            }
            Warn(text + "\n[Stop] --force: killing it anyway.");
        }

        var sequence = InstanceRoles.InTeardownOrder(targets);

        if (sequence.Count > 1)
        {
            Say("[Stop] Order: " + string.Join(
                " -> ", sequence.Select(static r => $"{r.Name} [{InstanceRoles.Name(r.Class)}]")));
        }

        MarkDirtyForStop(sequence, callerId);

        foreach (var rt in sequence)
        {
            if (rt.Class == InstanceClass.Stopped)
            {
                // Still cleaned up, and the flag still cleared: stop is idempotent and also
                // serves as a cleanup verb, and the stopped case is the one that matters most
                // because it is how a CRASHED host gets cleaned before its next run
                // (CLIENT-205).
                PidFiles.Delete(_fs, rt.Paths.PidFile);
                Say($"[{rt.Name}] Not running.");
                ClearStartLocalHost(rt);
                continue;
            }

            if (rt.NeedsDisconnect)
            {
                var disconnect = await DisconnectAsync(rt, teardownSeconds, ct).ConfigureAwait(false);
                if (disconnect.Ok)
                {
                    Say($"[{rt.Name}] Left its session ({disconnect.Detail}).");
                }
                else if (force)
                {
                    Warn($"[{rt.Name}] Would not leave its session ({disconnect.Detail}); --force, continuing.");
                }
                else
                {
                    throw new RigRefusalException(
                        RigRefusalKind.Refused,
                        $"[Stop] '{rt.Name}' would not leave its session ({disconnect.Detail}). Stopping the "
                        + "sequence here: killing it instead would leave the host holding a peer that never said "
                        + "goodbye, and that is the state the host is about to save. Everything after it in the "
                        + "order is still up. Fix it, or pass --force.");
                }
            }

            if (rt.OwnsWorld)
            {
                var saved = await SaveWorldAsync(rt, saveName, waitSeconds, ct).ConfigureAwait(false);
                if (!saved)
                {
                    if (!force)
                    {
                        throw new RigRefusalException(
                            RigRefusalKind.Refused,
                            $"[Stop] '{rt.Name}' holds a world and its save was not confirmed. Stopping the "
                            + "sequence here rather than quitting on top of it. Retry, save it by hand (testrig "
                            + $"save --target {rt.Name} --as <id>), or pass --force to quit and accept the loss.");
                    }
                    Warn($"[{rt.Name}] Save not confirmed; --force, quitting anyway. Treat that world as lost.");
                }
            }

            await StopProcessAsync(rt, teardownSeconds, ct).ConfigureAwait(false);
            ClearStartLocalHost(rt);
        }
    }

    /// <summary>
    /// Records that this session changed rig state, even when it held no lock.
    /// </summary>
    /// <remarks>
    /// CLIENT-213, decided rather than inherited. <c>stop</c> is deliberately ungated so an
    /// orphan can always be cleaned up, and the PowerShell wrote the crash marker only from
    /// inside the lock assertion, so a session whose only mutating action was a stop left the
    /// rig looking clean while it had just torn instances down. Whether that was intended is
    /// stated nowhere. It is not intended here: a stop that really stopped something leaves
    /// the marker, so the next acquisition restores.
    ///
    /// A stop that found nothing running changes nothing, and marks nothing.
    /// </remarks>
    private void MarkDirtyForStop(IReadOnlyList<InstanceRuntime> sequence, string? callerId)
    {
        if (!sequence.Any(static r => r.Alive)) return;

        try
        {
            // The marker is idempotent per (owner, boot), so a session that already marked
            // the rig through a gated command keeps its FIRST mutation's timestamp and its
            // recorded world sets. An ungated stop with no caller id still leaves one, under
            // a name that says plainly where it came from.
            _marker.Write(
                string.IsNullOrWhiteSpace(callerId) ? "(ungated-stop)" : callerId,
                "stop",
                "stop");
        }
        catch (Exception ex) when (ex is RigRefusalException or IOException or UnauthorizedAccessException)
        {
            Warn($"[Stop] Could not write the session dirty marker ({ex.Message}). The rig will look clean to "
                 + "the next session even though instances were torn down; run testrig reset --as <id> by hand.");
        }
    }

    /// <summary>Asks the control plane to quit, then kills after the grace period.</summary>
    private async Task StopProcessAsync(InstanceRuntime rt, int graceSeconds, CancellationToken ct)
    {
        var live = PidFiles.LiveProcess(_fs, _processes, rt.Paths.PidFile, [RigConstants.ClientImageName]);
        if (live is null)
        {
            PidFiles.Delete(_fs, rt.Paths.PidFile);
            Say($"[{rt.Name}] Not running.");
            return;
        }

        var pid = live.Value.Pid;

        // A clean Application.Quit lets the game flush its own state instead of being killed
        // mid-write (CLIENT-184).
        var quit = JsonSerializer.Serialize(new QuitRequest { Hard = false }, RigJsonContext.Default.QuitRequest);
        var answer = await _control.RawAsync(rt.Entry.Port, Endpoints.Quit, quit, 5, ct).ConfigureAwait(false);

        if (answer.Answered) Say($"[{rt.Name}] Quit requested.");
        else Say($"[{rt.Name}] Control plane did not answer; going straight to a kill.");

        var deadline = _clock.UtcNow.AddSeconds(graceSeconds);
        while (_clock.UtcNow < deadline
               && PidFiles.LiveProcess(_fs, _processes, rt.Paths.PidFile, [RigConstants.ClientImageName]) is not null)
        {
            await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
        }

        if (PidFiles.LiveProcess(_fs, _processes, rt.Paths.PidFile, [RigConstants.ClientImageName]) is not null)
        {
            Warn($"[{rt.Name}] Still alive after {graceSeconds}s; killing PID {pid}.");
            await ForceKill.NowAsync(_processes, pid, ct).ConfigureAwait(false);
            await _sleeper.DelayAsync(PollInterval, ct).ConfigureAwait(false);
        }

        PidFiles.Delete(_fs, rt.Paths.PidFile);
        Say($"[{rt.Name}] Stopped.");
    }

    /// <summary>
    /// Clears <c>StartLocalHost</c> in a stopped instance's own <c>setting.xml</c>.
    /// </summary>
    /// <remarks>
    /// That setting decides whether entering a world hosts it, and the game persists its
    /// settings on a clean exit. <c>data/&lt;instance&gt;/setting.xml</c> is NOT reset by a
    /// rebuild (which replaces the TREE; everything under data/ except userdata/mods
    /// survives), so a value left behind by a hosting run outlives the rebuild that was
    /// supposed to give a clean instance. The next run then comes up hosting when the test
    /// believes it is a joiner, and nothing anywhere says so.
    ///
    /// BOTH forms are patched (CLIENT-190, CLIENT-191): the element form and the attribute
    /// form. Only ever called AFTER the process is gone, because the game rewrites this file
    /// on exit (CLIENT-194).
    /// </remarks>
    private void ClearStartLocalHost(InstanceRuntime rt)
    {
        var file = rt.Paths.Settings;
        if (!_fs.FileExists(file)) return;

        try
        {
            var text = _fs.ReadAllText(file);
            if (string.IsNullOrEmpty(text)) return;

            var patched = System.Text.RegularExpressions.Regex.Replace(
                text, @"(<StartLocalHost\s*>)\s*true\s*(</StartLocalHost\s*>)", "${1}false${2}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            patched = System.Text.RegularExpressions.Regex.Replace(
                patched, @"(StartLocalHost\s*=\s*"")true("")", "${1}false${2}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (string.Equals(patched, text, StringComparison.Ordinal)) return;

            // UTF-8 with no byte order mark, pinned (CLIENT-192 fixed) on a file the baseline
            // may store byte for byte.
            _fs.WriteAllText(file, patched);
            Say($"[{rt.Name}] Cleared StartLocalHost in {file} (it survives create --force, and a stale one "
                + "silently makes the next run a host).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"[{rt.Name}] Could not check StartLocalHost in {file} ({ex.Message}). If this instance ever "
                 + "hosted, confirm the flag before reusing it as a joiner.");
        }
    }

    // =====================================================================
    // disconnect and save
    // =====================================================================

    /// <summary>The outcome of asking an instance to leave its session.</summary>
    public readonly record struct DisconnectOutcome(bool Ok, string Detail);

    /// <summary>
    /// Leaves the session cleanly and confirms it happened.
    /// </summary>
    /// <remarks>
    /// A killed client leaves the host holding a peer that never said goodbye, which is
    /// exactly the state the host is about to save.
    ///
    /// Success is <c>ok</c> true OR a result of <c>menu</c>, EVEN WHEN <c>ok</c> IS ABSENT
    /// (CLIENT-172). A port keying only on <c>ok</c> would treat a clean disconnect from an
    /// older plugin build as a failure and stop the teardown sequence.
    ///
    /// The HTTP timeout is the requested timeout plus fifteen seconds, so the plugin gives up
    /// first and can explain (CLIENT-171).
    /// </remarks>
    public async Task<DisconnectOutcome> DisconnectAsync(
        InstanceRuntime rt, int timeoutSeconds = 30, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(
            new DisconnectRequest { Wait = true, TimeoutMs = timeoutSeconds * 1000 },
            RigJsonContext.Default.DisconnectRequest);

        var answer = await _control
            .RawAsync(rt.Entry.Port, Endpoints.Disconnect, body, timeoutSeconds + 15, ct)
            .ConfigureAwait(false);

        if (!answer.Answered) return new DisconnectOutcome(false, ControlPlane.ErrorDetail(answer));

        DisconnectResponse? parsed = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(answer.Body))
            {
                parsed = JsonSerializer.Deserialize(answer.Body, RigJsonContext.Default.DisconnectResponse);
            }
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null) return new DisconnectOutcome(false, ControlPlane.ErrorDetail(answer));

        var ok = parsed.Ok || string.Equals(parsed.Result, "menu", StringComparison.Ordinal);
        return new DisconnectOutcome(ok, ok ? $"result={parsed.Result}" : ControlPlane.ErrorDetail(answer));
    }

    /// <summary>
    /// Asks an instance to write its world, then waits for the plugin to say it saw it land.
    /// </summary>
    /// <remarks>
    /// Same contract as the server half's save, for the same reason: "the request was
    /// accepted" and "the world is on disk" are different facts, and only the second one
    /// survives a teardown. With no confirmation this WARNS and returns false. It never
    /// reports a success it did not see; the contract is confirmed or warn, never both
    /// (CLIENT-182).
    /// </remarks>
    public async Task<bool> SaveWorldAsync(
        InstanceRuntime rt, string? saveName, int waitSeconds, CancellationToken ct = default)
    {
        var request = new SaveRequest
        {
            Wait = true,
            TimeoutMs = waitSeconds * 1000,
            Name = string.IsNullOrEmpty(saveName) ? null : saveName,
        };
        var body = JsonSerializer.Serialize(request, RigJsonContext.Default.SaveRequest);
        var label = string.IsNullOrEmpty(saveName) ? "the current world" : $"'{saveName}'";

        Say($"[{rt.Name}] Saving {label} (up to {waitSeconds}s for confirmation) ...");

        var answer = await _control
            .RawAsync(rt.Entry.Port, Endpoints.Save, body, waitSeconds + 30, ct)
            .ConfigureAwait(false);

        if (!answer.Answered)
        {
            // A refusal and a timeout both come back with the plugin's own explanation in the
            // body, so THAT is what gets reported rather than a status code (CLIENT-176).
            Warn($"[{rt.Name}] Save NOT confirmed: {ControlPlane.ErrorDetail(answer)}");
            Warn($"[{rt.Name}] Treat this world as NOT saved. testrig logs --target {rt.Name}, or GET "
                 + $"{Endpoints.ConsoleLog}?contains=Saved, shows what the game actually did.");
            return false;
        }

        SaveResponse? parsed = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(answer.Body))
            {
                parsed = JsonSerializer.Deserialize(answer.Body, RigJsonContext.Default.SaveResponse);
            }
        }
        catch (JsonException)
        {
            parsed = null;
        }

        if (parsed is null)
        {
            Warn($"[{rt.Name}] Save NOT confirmed: {ControlPlane.ErrorDetail(answer)}");
            Warn($"[{rt.Name}] Treat this world as NOT saved.");
            return false;
        }

        if (!parsed.Ok)
        {
            Warn($"[{rt.Name}] {Endpoints.Save} refused: {parsed.Error ?? ControlPlane.ErrorDetail(answer)}. "
                 + "Treat this world as NOT saved.");
            return false;
        }

        // 'confirmed' is how the plugin distinguishes "I saw it land" from "I asked"
        // (CLIENT-178).
        if (!parsed.Confirmed)
        {
            Warn($"[{rt.Name}] {Endpoints.Save} was accepted but not confirmed inside its own timeout "
                 + $"(result={parsed.Result}). It may have completed silently or failed; check the logs. Treat "
                 + "this world as NOT saved.");
            return false;
        }

        // The resolved path from whichever field the plugin sent, and the confirmation method
        // from whichever it sent (CLIENT-179, CLIENT-180). The tolerance is what lets an older
        // plugin build stay usable.
        var where = FirstNonEmpty(parsed.SavePath, parsed.SaveRoot, parsed.ResolvedName, parsed.Name);
        var how = FirstNonEmpty(parsed.ConfirmedBy, parsed.Result);

        // A confirmation plus a zero-byte file is the shape of a save reported before the
        // archive finished streaming, so the size is worth printing (CLIENT-181).
        var size = parsed.SizeBytes > 0
            ? string.Format(CultureInfo.InvariantCulture, ", {0:N1} KB", parsed.SizeBytes / 1024.0)
            : "";

        Say($"[{rt.Name}] Save confirmed{(how.Length > 0 ? $" ({how})" : "")}"
            + $"{(where.Length > 0 ? $" -> {where}" : "")}{size}.");
        return true;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static v => !string.IsNullOrEmpty(v)) ?? "";

    // =====================================================================
    // save (the verb)
    // =====================================================================

    /// <summary>
    /// Asks each selected instance to write its world.
    /// </summary>
    /// <remarks>
    /// A save name is OPTIONAL here and required on the server half. That asymmetry is real
    /// rather than sloppy: a client instance knows the world's current name and can save
    /// under it, and a dedicated server's console cannot (CLIENT-221).
    /// </remarks>
    public async Task SaveAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? callerId = null,
        string? saveName = null,
        int waitSeconds = 0,
        CancellationToken ct = default)
    {
        AssertGate("save", callerId);
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        var failed = 0;
        var attempted = 0;

        foreach (var entry in entries)
        {
            var rt = await RuntimeAsync(entry, ct).ConfigureAwait(false);

            if (!rt.Alive)
            {
                Warn($"[{rt.Name}] Not running; there is nothing to save.");
                failed++;
                continue;
            }
            if (!rt.Answered)
            {
                Warn($"[{rt.Name}] Control plane did not answer ({rt.Error}); the world could not be saved.");
                failed++;
                continue;
            }
            if (string.Equals(rt.LiveRole, "joinedClient", StringComparison.Ordinal))
            {
                // CLIENT-218 fixed: the PowerShell warned and then tried anyway, which
                // guarantees a 409 and a non-zero failure count on a rig where nothing is
                // wrong. Skipped, and not counted, because there is genuinely nothing here to
                // save.
                Warn($"[{rt.Name}] is a joined client: the world belongs to whoever hosts it, so saving from "
                     + "here does not persist it. Skipped. Save on the host instead.");
                continue;
            }

            attempted++;
            if (!await SaveWorldAsync(rt, saveName, waitSeconds, ct).ConfigureAwait(false)) failed++;
        }

        _output.Value("saveAttempted", attempted);
        _output.Value("saveFailed", failed);

        if (failed > 0)
        {
            Warn($"[Save] {failed} of {entries.Count} instance(s) did not confirm a save. Do not treat those "
                 + $"worlds as persisted: testrig logs --target <name>, and GET {Endpoints.ConsoleLog}, show what "
                 + "each instance actually did.");
        }
    }

    // =====================================================================
    // remove
    // =====================================================================

    /// <summary>
    /// Deletes one instance's tree AND its save root.
    /// </summary>
    /// <remarks>
    /// Tier 3 by design, but for a HOST that save root IS the world every joiner was in, so
    /// the refusal is stronger than the one <c>stop</c> applies for the stated reason: a
    /// stopped host can be started again, a deleted world cannot (CLIENT-226).
    ///
    /// The freed index recycles the control port, the game port and the ClientId, so the next
    /// create inherits anything still referring to the old instance (CLIENT-231). That is
    /// named in the closing message rather than left to be discovered.
    /// </remarks>
    public async Task RemoveAsync(
        string instance,
        string? callerId = null,
        bool force = false,
        string desktop = RigConstants.DefaultDesktop,
        CancellationToken ct = default)
    {
        if (instance.Contains(',', StringComparison.Ordinal))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, "'remove' takes one instance at a time.");
        }

        AssertGate("remove", callerId);

        var paths = _layout.PathsFor(instance);

        if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Instance '{instance}' is running. Stop it first: testrig stop --target {instance} --as <id>");
        }

        var entry = _registry.Find(instance);
        if (entry is not null && entry.IsHost)
        {
            var reasons = new List<string>();
            var others = _registry.Read()
                .Where(e => !RigRegistry.SameInstance(e.InstanceName, instance))
                .ToList();

            foreach (var other in await RuntimesAsync(others, ct).ConfigureAwait(false))
            {
                if (!other.Alive) continue;
                if (string.Equals(other.LiveRole, "joinedClient", StringComparison.Ordinal))
                {
                    reasons.Add($"'{other.Name}' is a joined client");
                }
                else if (!other.Answered)
                {
                    reasons.Add($"'{other.Name}' is running but its control plane does not answer, so it cannot "
                                + "be ruled out as a joiner");
                }
            }

            if (reasons.Count > 0)
            {
                var text = $"[Remove] '{instance}' is a host, and removing it deletes its world at "
                           + $"{paths.UserData}, while:\n    " + string.Join("\n    ", reasons);
                if (!force)
                {
                    throw new RigRefusalException(
                        RigRefusalKind.Refused,
                        text + "\nNothing was deleted. Stop the other instances first, or pass --force.");
                }
                Warn(text + "\n[Remove] --force: deleting it anyway.");
            }
        }

        var freedIndex = entry?.Index;

        if (_fs.DirectoryExists(paths.Tree)) _fs.DeleteDirectory(paths.Tree, recursive: true);
        if (_fs.DirectoryExists(paths.Data)) _fs.DeleteDirectory(paths.Data, recursive: true);

        _registry.Remove(instance);
        WriteAllManifests(desktop);

        Say($"[Remove] Instance '{instance}' deleted. The source install is untouched: only hard links and "
            + "per-instance copies were removed.");

        if (freedIndex is not null)
        {
            Say($"[Remove] Index {freedIndex} is free again, so the next create with no flags reuses it, and "
                + $"with it port {RigConstants.ControlPortBase + freedIndex}, game port "
                + $"{RigConstants.GamePortBase + freedIndex} and ClientId {900000000000L + freedIndex.Value}. "
                + "Anything still referring to the old instance, a saved world or a joiner's cached address, "
                + "would be inherited by the new one.");
        }
    }

    // =====================================================================
    // wait
    // =====================================================================

    /// <summary>
    /// Blocks until every selected instance reaches a stage.
    /// </summary>
    /// <remarks>
    /// Read-only, so it is NOT lock-gated (CLIENT-247). It does refresh a lock you already
    /// hold, because a barrier can legitimately run longer than the TTL and losing the rig
    /// halfway through a wait would be absurd; it is a silent no-op when you hold nothing
    /// (CLIENT-248).
    ///
    /// The refresh is rate-limited to once a minute rather than once per poll (CLIENT-248
    /// fixed): the PowerShell refreshed every 2 s, which is up to 300 durable writes on a
    /// 600 s barrier, each taking the named mutex. Once a minute is the cadence the rules
    /// document, against a 10 minute TTL.
    /// </remarks>
    public async Task WaitAsync(
        IReadOnlyList<InstanceEntry> entries,
        string? callerId = null,
        ReadinessStage stage = ReadinessStage.Menu,
        int waitSeconds = 0,
        CancellationToken ct = default)
    {
        if (waitSeconds <= 0) waitSeconds = RigConstants.WaitDefaultSeconds;

        if (entries.Count == 0)
        {
            Say("[Wait] No client instances selected.");
            return;
        }

        _lock.RefreshIfMine(callerId);
        var lastRefresh = _clock.UtcNow;
        var deadline = _clock.UtcNow.AddSeconds(waitSeconds);
        var stageName = ReadinessStages.Name(stage);

        Say($"[Wait] Barrier: {entries.Count} instance(s) must reach stage '{stageName}' within {waitSeconds}s.");

        var pending = entries.ToDictionary(static e => e.InstanceName, static e => e.Port, StringComparer.Ordinal);

        while (pending.Count > 0 && _clock.UtcNow < deadline)
        {
            foreach (var (name, port) in pending.ToList())
            {
                if (!await _control.ReachedStageAsync(port, stage, ct).ConfigureAwait(false)) continue;
                Say($"[Wait] {name} reached '{stageName}'.");
                pending.Remove(name);
            }

            if (pending.Count == 0) break;

            await _sleeper.DelayAsync(BarrierInterval, ct).ConfigureAwait(false);

            if (_clock.UtcNow - lastRefresh >= TimeSpan.FromMinutes(1))
            {
                _lock.RefreshIfMine(callerId);
                lastRefresh = _clock.UtcNow;
            }
        }

        if (pending.Count > 0)
        {
            foreach (var (name, port) in pending)
            {
                var (status, error) = await _control.StatusAsync(port, 5, ct).ConfigureAwait(false);
                var detail = status is null
                    ? $"control plane did not answer ({error})"
                    : $"phase={status.Phase} gameInitialized={status.GameInitialized} plugins={status.LoadedPluginCount}";
                Warn($"[{name}] Did not reach '{stageName}': {detail}");
            }

            // The most common cause is worth naming: a transient Steam Workshop query failure
            // parks StationeersLaunchPad on its own error screen forever with the plugin count
            // stuck at 2 (CLIENT-255).
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"[Wait] {pending.Count} instance(s) did not reach '{stageName}' within {waitSeconds}s. If "
                + "plugins is stuck at 2 with gameInitialized false, StationeersLaunchPad hit a transient Steam "
                + "Workshop failure and is parked on its error screen: stop the instance and start it again, "
                + "which clears it.");
        }

        Say($"[Wait] All instances reached '{stageName}'.");
    }
}
