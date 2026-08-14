using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>Who wrote the marker: the launcher process's own identity.</summary>
/// <param name="Pid">The launcher process id.</param>
/// <param name="ImageName">Its image name, empty when it could not be read.</param>
/// <param name="HostName">The machine name. Diagnostics only; nothing compares it.</param>
public readonly record struct LauncherIdentity(int Pid, string ImageName, string HostName);

/// <summary>What the marker on disk means right now.</summary>
public sealed record DirtyState(
    bool Dirty,
    bool SameBoot,
    bool WriterAlive,
    string Owner,
    string Purpose,
    string Reason,
    string MarkedAt,
    string BootId,
    FieldText? Marker)
{
    /// <summary>
    /// The writing session is gone. Always true for a marker from a previous boot, because
    /// a pid from before a reboot names whatever process inherited that number.
    /// </summary>
    /// <remarks>
    /// Gated on <see cref="Dirty"/>: with no marker at all, nothing crashed. PowerShell got
    /// the same answer by setting the field explicitly on the clean path, and a derived
    /// property that forgets the gate reports every clean rig as a crash site.
    /// </remarks>
    public bool Crashed => Dirty && !WriterAlive;

    public static DirtyState Clean() =>
        new(false, true, false, "", "", "", "", "", null);
}

/// <summary>The world set a session recorded before its first mutating action.</summary>
/// <param name="Recorded">A usable set was recorded. Only then can anything be deleted.</param>
/// <param name="Keys">The recorded keys. Case-insensitive: see the remarks.</param>
/// <param name="Reason">Why there is no set, for the report. Empty when recorded.</param>
/// <param name="Degraded">
/// Distinguishes "there is no marker, which is the ordinary clean state" from
/// "the marker is unreadable", which mean entirely different things to an agent.
/// </param>
/// <remarks>
/// <paramref name="Keys"/> is built with <see cref="StringComparer.OrdinalIgnoreCase"/>
/// on purpose. PowerShell hashtables are case-insensitive, so a case-only rename of a
/// world was harmless there; a naive port to a default <c>HashSet&lt;string&gt;</c>
/// makes it a live delete bug (spec 03-reset H.5 item 5).
/// </remarks>
public sealed record SessionWorldSnapshot(
    bool Recorded,
    IReadOnlySet<string> Keys,
    string Reason,
    bool Degraded)
{
    public int Count => Keys.Count;

    public bool Protects(string key) => Keys.Contains(key);

    public static SessionWorldSnapshot NotRecorded(string reason, bool degraded) =>
        new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), reason, degraded);
}

/// <summary>
/// The crash marker: written before a session's first mutating action, cleared only by
/// a completed state restore.
/// </summary>
/// <remarks>
/// Present at acquisition means the last session did not clean up. It carries the OS
/// boot identity as well as the writer's pid, so acquisition can tell a crashed session
/// from a live one and from a machine that rebooted underneath it.
///
/// It also carries the world sets, and that is what makes a world's lifetime
/// session-scoped: it goes down BEFORE the first mutating action, so everything it
/// lists is older than the session by construction, and a world the session goes on to
/// create can never sneak into its own "was already here" set.
/// </remarks>
public sealed class DirtyMarker
{
    public const string KeyOwner = "owner";
    public const string KeyPurpose = "purpose";
    public const string KeyReason = "reason";
    public const string KeyMarkedAt = "marked_at";
    public const string KeyBootId = "boot_id";
    public const string KeyWriterPid = "writer_pid";
    public const string KeyWriterImage = "writer_image";
    public const string KeyHost = "host";
    public const string KeyWorlds = "worlds";

    /// <summary>
    /// The client half's world set. New in the C# port: <c>ClientRig/data/&lt;instance&gt;/userdata/saves</c>
    /// had no session scoping at all and was wiped unconditionally on every reset, though
    /// a listen host writes real worlds there (spec 03-reset H.5 item 1, named as the
    /// highest-plausibility real-world loss path in the subsystem).
    /// </summary>
    public const string KeyClientWorlds = "client_worlds";

    private static readonly string[] Header =
    [
        "# Stationeers TestRig - DIRTY MARKER (auto-managed; do not hand-edit).",
        "# Written before the first mutating action of a session; cleared only by a",
        "# COMPLETED state restore. Present at acquisition means the last session did",
        "# not clean up (it crashed, was killed, or the machine went down).",
        "# worlds= and client_worlds= are the world sets as they stood at that moment. A",
        "# world absent from its set was created by this session and is deleted at the",
        "# boundary; a world in it is kept. A MISSING key means the set could not be",
        "# established, and then every world on that half is kept.",
        "# Rules: TestRig/CLAUDE.md.",
    ];

    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly IProcessTable _processes;
    private readonly IBootIdentity _boot;
    private readonly RigPaths _paths;
    private readonly WorldScanner _worlds;
    private readonly LauncherIdentity _launcher;

    public DirtyMarker(
        IFileSystem fs,
        IClock clock,
        IProcessTable processes,
        IBootIdentity boot,
        RigPaths paths,
        WorldScanner worlds,
        LauncherIdentity launcher)
    {
        _fs = fs;
        _clock = clock;
        _processes = processes;
        _boot = boot;
        _paths = paths;
        _worlds = worlds;
        _launcher = launcher;
    }

    /// <summary>The marker's fields, or null when there is no usable marker.</summary>
    /// <remarks>
    /// The presence of <c>owner</c> is what makes the file a marker at all. An empty
    /// file, a comment-only file, or anything hand-broken therefore reads as "present but
    /// unreadable", never as "clean".
    /// </remarks>
    public FieldText? Read()
    {
        var text = RigFiles.ReadTextOrNull(_fs, _paths.DirtyFile, "rig dirty marker");
        if (text is null) return null;
        var fields = FieldText.Parse(text);
        return fields.Contains(KeyOwner) ? fields : null;
    }

    /// <summary>Whether a marker was written since the machine last started.</summary>
    /// <remarks>
    /// Fails closed in every direction: a missing boot id, an unidentifiable current
    /// boot, or any mismatch all read as "not this boot", because everything that
    /// consults it treats "not this boot" as the cheap answer (restore again, keep the
    /// world) and "this boot" as the one that costs something.
    /// </remarks>
    public bool IsSameBoot(FieldText? marker)
    {
        if (marker is null) return false;
        var recorded = marker.Get(KeyBootId);
        if (string.IsNullOrEmpty(recorded)) return false;
        var current = _boot.GetBootId();
        if (string.IsNullOrEmpty(current) || current == "unknown") return false;
        return string.Equals(recorded, current, StringComparison.Ordinal);
    }

    /// <summary>What the marker means right now.</summary>
    public DirtyState GetState()
    {
        var marker = Read();
        if (marker is null) return DirtyState.Clean();

        var sameBoot = IsSameBoot(marker);
        var alive = false;

        // Only consulted when the boot matches. A pid from before a reboot names whatever
        // process inherited that number afterwards, and trusting it is how a crashed
        // session's mess gets mistaken for a live one's.
        if (sameBoot && int.TryParse(marker.GetOrEmpty(KeyWriterPid), out var writerPid))
        {
            var image = marker.GetOrEmpty(KeyWriterImage);
            alive = string.IsNullOrEmpty(image)
                ? _processes.TryGet(writerPid) is not null
                : _processes.TryGetMatching(writerPid, image) is not null;
        }

        return new DirtyState(
            Dirty: true,
            SameBoot: sameBoot,
            WriterAlive: alive,
            Owner: marker.GetOrEmpty(KeyOwner),
            Purpose: marker.GetOrEmpty(KeyPurpose),
            Reason: marker.GetOrEmpty(KeyReason),
            MarkedAt: marker.GetOrEmpty(KeyMarkedAt),
            BootId: marker.GetOrEmpty(KeyBootId),
            Marker: marker);
    }

    /// <summary>One-line human description, for status and for the acquisition warning.</summary>
    public static string Describe(DirtyState state)
    {
        if (!state.Dirty) return "clean (no dirty marker)";

        var how = !state.SameBoot
            ? "the machine has restarted since, so that session is definitely gone"
            : state.WriterAlive
                ? $"its launcher process is STILL RUNNING (pid {state.Marker?.GetOrEmpty(KeyWriterPid)})"
                : "its launcher process is gone";

        return $"dirty since {state.MarkedAt} by owner {state.Owner} ({state.Reason}); {how}";
    }

    /// <summary>
    /// Marks the rig dirty, idempotently per (owner, boot).
    /// </summary>
    /// <returns>True when a marker was written, false when an equivalent one was already there.</returns>
    /// <remarks>
    /// The idempotence is what makes the world sets correct: it preserves the FIRST
    /// mutation's timestamp and its world sets across every later gated command. A
    /// different owner rewrites it wholesale, which is how a handoff session takes over
    /// the debt (spec 03-reset H.3, decided explicitly in the port: the rewrite stands,
    /// and it is stated in the marker's own comment header that a set belongs to the
    /// session that wrote it).
    /// </remarks>
    public bool Write(string owner, string purpose, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        var bootId = _boot.GetBootId();
        var existing = Read();
        if (existing is not null
            && string.Equals(existing.GetOrEmpty(KeyOwner), owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.GetOrEmpty(KeyBootId), bootId, StringComparison.Ordinal))
        {
            return false;
        }

        var fields = new FieldText();
        fields.Set(KeyOwner, owner);
        fields.Set(KeyPurpose, Sanitise(purpose));
        fields.Set(KeyReason, Sanitise(reason));
        fields.Set(KeyMarkedAt, RigTime.Stamp(_clock.UtcNow));
        fields.Set(KeyBootId, bootId);
        fields.Set(KeyWriterPid, _launcher.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        fields.Set(KeyWriterImage, _launcher.ImageName);
        fields.Set(KeyHost, _launcher.HostName);

        // THE FIX. A scan that failed omits its key entirely, which lands in the degraded
        // path and keeps every world on that half. It is never written as an empty value,
        // because an empty value is a real answer meaning "there were no worlds" and that
        // answer authorises deleting everything found later.
        AddWorldKey(fields, KeyWorlds, _worlds.ScanServer());
        AddWorldKey(fields, KeyClientWorlds, _worlds.ScanClients());

        RigFiles.WriteDurable(_fs, _paths.DirtyFile, fields.Render(Header), "rig dirty marker");
        return true;
    }

    private static void AddWorldKey(FieldText fields, string key, WorldScan scan)
    {
        if (scan.Status != WorldScanStatus.Enumerated) return;
        fields.Set(key, string.Join("|", scan.Worlds.Select(static w => w.Key)));
    }

    /// <summary>
    /// Strips what the format cannot represent.
    /// </summary>
    /// <remarks>
    /// <c>purpose</c> is unescaped free text from the command line (spec 02-lock race
    /// R-12). A newline in it splits the line into a bogus second key on the next parse.
    /// PowerShell argument binding made that hard but not impossible; here it is simply
    /// prevented at the boundary.
    /// </remarks>
    private static string Sanitise(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var n = 0;
        foreach (var c in value)
        {
            buffer[n++] = char.IsControl(c) ? ' ' : c;
        }
        return new string(buffer[..n]).Trim();
    }

    /// <summary>Deletes the marker. Only ever called after a restore in which every action succeeded.</summary>
    public void Clear() => RigFiles.Delete(_fs, _paths.DirtyFile, "rig dirty marker");

    /// <summary>
    /// The world set the current marker recorded for one half, or why there is none.
    /// </summary>
    /// <remarks>
    /// Five outcomes, and every path that is not "recorded, this boot, and it parsed"
    /// fails closed to keep every world, because deleting one is the only irreversible
    /// thing the restore does.
    /// </remarks>
    public SessionWorldSnapshot ReadSessionWorlds(WorldScope scope)
    {
        var half = scope == WorldScope.Server ? "dedicated-server" : "client-instance";
        var key = scope == WorldScope.Server ? KeyWorlds : KeyClientWorlds;

        if (string.IsNullOrEmpty(_paths.DirtyFile) || !_fs.FileExists(_paths.DirtyFile))
        {
            return SessionWorldSnapshot.NotRecorded(
                "there is no session marker, so nothing has mutated this rig since the last completed "
                + "restore and no world on it belongs to a session",
                degraded: false);
        }

        var marker = Read();
        if (marker is null)
        {
            return SessionWorldSnapshot.NotRecorded(
                $"the session marker at {_paths.DirtyFile} is present but could not be read as a marker, "
                + "so the world set it should carry is unavailable",
                degraded: true);
        }

        if (!IsSameBoot(marker))
        {
            return SessionWorldSnapshot.NotRecorded(
                "the session marker was written before the machine last started, so nothing here can "
                + "vouch for the world set it recorded",
                degraded: true);
        }

        if (!marker.Contains(key))
        {
            return SessionWorldSnapshot.NotRecorded(
                $"the session marker records no {half} world set at all (the enumeration failed, or it was "
                + "written before the rig tracked them), so which worlds predate this session is unknown",
                degraded: true);
        }

        // Present but empty is a REAL answer: the rig had no worlds when the session
        // started. That is why the key's presence is tested and never its value.
        var raw = marker.GetOrEmpty(key);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split('|'))
        {
            // Deliberately NOT trimmed. See WorldKey.IsRoundTrippable: a name that needed
            // trimming never got written in the first place.
            if (part.Length > 0) keys.Add(part);
        }

        return new SessionWorldSnapshot(true, keys, string.Empty, false);
    }
}
