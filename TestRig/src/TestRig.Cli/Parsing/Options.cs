namespace TestRig.Cli.Parsing;

/// <summary>
/// Every option the rig accepts, in surface order.
/// </summary>
/// <remarks>
/// One declaration feeds the parser, the per-verb applicability check and the printed
/// surface. Nothing here is repeated anywhere else, so the surface cannot drift from what
/// the parser accepts.
/// </remarks>
public static class Options
{
    // ---- global -----------------------------------------------------------

    public const string Json = "json";
    public const string Verbose = "verbose";

    // ---- targeting --------------------------------------------------------

    public const string Target = "target";

    // ---- the session lock -------------------------------------------------

    public const string Purpose = "purpose";
    public const string As = "as";
    public const string BreakLock = "break-lock";
    public const string Force = "force";
    public const string TtlMinutes = "ttl-minutes";
    public const string IdleCeilingMinutes = "idle-ceiling-minutes";
    public const string KeepState = "keep-state";
    public const string Release = "release";
    public const string DryRun = "dry-run";
    public const string AllowBulkWorldDelete = "allow-bulk-world-delete";

    // ---- worlds -----------------------------------------------------------

    public const string Load = "load";
    public const string Map = "map";
    public const string New = "new";
    public const string SaveName = "save-name";

    // ---- mods -------------------------------------------------------------

    public const string Mod = "mod";
    public const string Configuration = "configuration";
    /// <summary>
    /// Spelled <c>from-modconfig</c>, matching the file it names.
    /// </summary>
    /// <remarks>
    /// The server half's own refusal text names <c>--from-modconfig</c>, so a hyphen between
    /// <c>mod</c> and <c>config</c> here would have made the remedy the rig prints impossible
    /// to type. The file is <c>modconfig.xml</c>; the flag matches it.
    /// </remarks>
    public const string FromModConfig = "from-modconfig";

    // ---- driving ----------------------------------------------------------

    public const string Command = "command";
    public const string Path = "path";
    public const string Body = "body";
    public const string CallTimeoutSeconds = "call-timeout-seconds";
    public const string Stage = "stage";
    public const string WaitSeconds = "wait-seconds";
    public const string TimeoutSeconds = "timeout-seconds";

    // ---- instance shape ---------------------------------------------------

    public const string Role = "role";
    public const string Port = "port";
    public const string GamePort = "game-port";
    public const string UpdatePort = "update-port";
    public const string ClientId = "client-id";
    public const string Username = "username";
    public const string Width = "width";
    public const string Height = "height";
    public const string ForceGameplayInput = "force-gameplay-input";
    public const string SeedMods = "seed-mods";
    public const string Desktop = "desktop";
    public const string InstancesRoot = "instances-root";

    // ---- reading ----------------------------------------------------------

    public const string Tail = "tail";
    public const string Grep = "grep";
    public const string OutFile = "out-file";

    /// <summary>
    /// Read an instance's pre-BepInEx Unity log instead of its BepInEx log.
    /// </summary>
    /// <remarks>
    /// Every failure BEFORE BepInEx loads lands in
    /// <c>data/&lt;instance&gt;/logs/unity-&lt;stamp&gt;.log</c>, and no verb ever printed it,
    /// so the launcher could not show a hard boot failure at all (CLIENT-302).
    /// </remarks>
    public const string Unity = "unity";

    // ---- playtests --------------------------------------------------------

    /// <summary>A wildcard over check names, applied once.</summary>
    public const string Only = "only";

    /// <summary>Where a run's evidence bundle is written.</summary>
    public const string EvidenceRoot = "evidence-root";

    /// <summary>Names a playtest run's evidence folder and its report (PLAYTEST-002).</summary>
    public const string SuiteName = "suite-name";

    /// <summary>List the compiled-in checks and what each one needs. Runs nothing.</summary>
    public const string ListChecks = "list-checks";

    /// <summary>List the flake taxonomy in resolution order. Runs nothing, and needs no rig.</summary>
    public const string ListFlakes = "list-flakes";

    /// <summary>Accepted on every verb.</summary>
    public static readonly IReadOnlyList<string> Global = [Json, Verbose];

    /// <summary>
    /// The ten flags that describe one of N client instances.
    /// </summary>
    /// <remarks>
    /// Typing any of them against <c>--target server</c> fires refusal 21. <c>game-port</c>
    /// and <c>update-port</c> are deliberately absent: both are also the dedicated server's
    /// own start-time flags, so they have something to bind to there.
    /// </remarks>
    public static readonly IReadOnlyList<string> InstanceShape =
    [
        Role, Port, ClientId, Username, Width, Height, ForceGameplayInput, SeedMods, Desktop, InstancesRoot,
    ];

    public static readonly IReadOnlyList<string> Stages = ["ping", "modsLoaded", "menu", "inWorld", "process"];
    public static readonly IReadOnlyList<string> Roles = ["client", "host"];
    public static readonly IReadOnlyList<string> Configurations = ["Release", "Debug"];

    /// <summary>The catalogue, in the order the surface prints it.</summary>
    public static readonly IReadOnlyList<OptionSpec> All =
    [
        new(Json, OptionKind.Flag, "off", "Structured output instead of prose. Nothing needs to scrape a sentence."),
        new(Verbose, OptionKind.Flag, "off", "Detail lines that are otherwise suppressed."),

        new(Target, OptionKind.Text, "", "all | server | clients | <instance>[,<instance>]. Case-insensitive."),

        new(Purpose, OptionKind.Text, "", "Why you are taking the rig. Required by 'lock'."),
        new(As, OptionKind.Text, "", "The lock owner id, printed by 'lock'. Required by every mutating verb."),
        new(BreakLock, OptionKind.Flag, "off", "Take a LIVE lock off another session. Human-gated: only on the user's say-so."),
        new(Force, OptionKind.Flag, "off", "Override a refusal inside your own session. Never touches a lock."),
        new(TtlMinutes, OptionKind.Number, "10", "Liveness heartbeat. Refresh about once a minute while driving a test."),
        new(IdleCeilingMinutes, OptionKind.Number, "60", "Absolute idle ceiling on the owner's own actions, busy rig or not."),
        new(KeepState, OptionKind.Flag, "off", "Skip the state restore at either end of a session, loudly."),
        new(Release, OptionKind.Flag, "off", "'stop' only: release the lock once both halves are down."),
        new(DryRun, OptionKind.Flag, "off", "'reset' only: print the plan and change nothing."),
        new(AllowBulkWorldDelete, OptionKind.Flag, "off", "'reset' only: delete more worlds in one restore than the ceiling allows."),

        new(Load, OptionKind.Text, "", "The existing world the dedicated server starts into. Needs --map."),
        new(Map, OptionKind.Text, "", "The map a server world uses."),
        new(New, OptionKind.Text, "", "Create a brand-new server world on this map. Mutually exclusive with --load."),
        new(SaveName, OptionKind.Text, "", "The world name to write. Required on the server half, optional on a client."),

        new(Mod, OptionKind.Text, "", "Comma-separated mod names for 'deploy'. Also positional: testrig deploy SprayPaintPlus."),
        new(Configuration, OptionKind.Choice, "Release", "Which build of a mod to deploy.", Configurations),
        new(FromModConfig, OptionKind.Text, "", "Alternate modconfig.xml source for 'update-mods --target server'."),

        new(Command, OptionKind.Text, "", "One line for the dedicated server's stdin. Fire and forget."),
        new(Path, OptionKind.Text, "", "A control-plane path, for example /status."),
        new(Body, OptionKind.Text, "", "Raw JSON request body for 'call'. Never parsed here."),
        new(CallTimeoutSeconds, OptionKind.Number, "0", "0 derives the timeout from the request itself."),
        new(Stage, OptionKind.Choice, "menu", "Readiness stage to wait for.", Stages),
        new(WaitSeconds, OptionKind.Number, "300", "Blocking-wait budget: a readiness barrier, or a save awaiting confirmation."),
        new(TimeoutSeconds, OptionKind.Number, "30", "Process-teardown grace for 'stop'. Never a save budget."),

        new(Role, OptionKind.Choice, "client", "What an instance is for. A host runs the simulation and plays.", Roles),
        new(Port, OptionKind.Number, "0", "Control-plane port. 0 derives 27700 + index."),
        new(GamePort, OptionKind.Number, "0", "RakNet port. 0 derives 27800 + index for an instance, 28016 for the server."),
        new(UpdatePort, OptionKind.Number, "0", "Dedicated server update port. 0 means 28015."),
        new(ClientId, OptionKind.Text, "", "Steam-shaped client id for a new instance. Must be a non-zero number."),
        new(Username, OptionKind.Text, "", "In-game name for a new instance. Defaults to the instance name."),
        new(Width, OptionKind.Number, "800", "Instance window width."),
        new(Height, OptionKind.Number, "600", "Instance window height."),
        new(ForceGameplayInput, OptionKind.Flag, "on", "Keep gameplay input alive on an unfocused window. Off with --no-force-gameplay-input.", null, true),
        new(SeedMods, OptionKind.Flag, "on", "Seed a new instance's mods from the developer's set. Off with --no-seed-mods.", null, true),
        new(Desktop, OptionKind.Text, "StationeersRig", "The Win32 desktop instances run on. Created, never switched to."),
        new(InstancesRoot, OptionKind.Text, "", "Where instance trees live. Overrides STATIONEERS_CLIENTRIG_ROOT."),

        new(Tail, OptionKind.Number, "50", "How many log lines to show."),
        new(Grep, OptionKind.Text, "", "Regex filter over the log. Combines with --tail: filter first, then tail the matches."),
        new(OutFile, OptionKind.Text, "", "Write 'snapshot' output to this file instead of stdout."),
        new(Unity, OptionKind.Flag, "off", "'logs' on an instance: the pre-BepInEx Unity log, where a hard boot failure lands."),

        new(Only, OptionKind.Text, "*", "Wildcard over playtest check names. Applied once, over the compiled-in set."),
        new(EvidenceRoot, OptionKind.Text, "", "Where a playtest run writes its bundle. Defaults to TestRig/playtest/evidence."),
        new(SuiteName, OptionKind.Text, "testrig", "Names a playtest run's evidence folder and its report."),
        new(ListChecks, OptionKind.Flag, "off", "List the compiled-in checks and what each needs. Runs nothing."),
        new(ListFlakes, OptionKind.Flag, "off", "List the flake taxonomy in resolution order. Runs nothing, and needs no rig."),
    ];

    private static readonly Dictionary<string, OptionSpec> ByKey = BuildIndex();

    private static Dictionary<string, OptionSpec> BuildIndex()
    {
        var map = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        foreach (var spec in All) map[spec.Key] = spec;
        return map;
    }

    public static OptionSpec Get(string name) => ByKey[OptionSpec.Normalize(name)];

    public static bool TryGetExact(string key, out OptionSpec spec) => ByKey.TryGetValue(key, out spec!);

    /// <summary>Every option whose key starts with <paramref name="prefix"/>, for the unique-prefix rule.</summary>
    public static IReadOnlyList<OptionSpec> WithPrefix(string prefix)
    {
        var hits = new List<OptionSpec>();
        foreach (var spec in All)
            if (spec.Key.StartsWith(prefix, StringComparison.Ordinal))
                hits.Add(spec);
        return hits;
    }
}
