using System.Globalization;
using TestRig.Contracts;

namespace TestRig.Tests.Playtest.Fakes;

/// <summary>One driven game client, as the fake control plane models it.</summary>
/// <remarks>
///     Every value it publishes comes back out as a Contracts record, so a field renamed in
///     the wire contract breaks this file at compile time rather than turning a reading into
///     a silent null.
/// </remarks>
public sealed class InstanceState
{
    private readonly List<ConsoleLine> _console = [];
    private readonly Dictionary<string, Dictionary<string, string>> _config = new(StringComparer.OrdinalIgnoreCase);

    public InstanceState(string name, int port)
    {
        Name = name;
        Port = port;
    }

    public string Name { get; }

    public int Port { get; }

    public string Phase { get; set; } = "menu";

    public bool GameInitialized { get; set; } = true;

    public int LoadedPluginCount { get; set; } = 42;

    public bool Hosting { get; set; }

    public string Role { get; set; } = "menu";

    public int HostPort { get; set; }

    public string? RequestedWorld { get; set; }

    public string? RequestedSave { get; set; }

    /// <summary>
    /// The world's station name, empty until a first NAMED save assigns one.
    /// </summary>
    /// <remarks>
    /// A world created from a world id has none, and every later save resolves through it, so a
    /// save with no name has nothing to save under until this is set.
    /// </remarks>
    public string? StationName { get; set; }

    public int? ConnectedTo { get; set; }

    public List<ConnectedClient> Roster { get; } = [];

    /// <summary>A joiner that has connected but has not yet appeared server-side.</summary>
    public ConnectedClient? PendingArrival { get; set; }

    /// <summary>How many more roster reads answer before the pending arrival lands.</summary>
    public int PollsBeforeArrival { get; set; }

    public long NextSeq { get; private set; } = 1;

    public long NextReferenceId { get; set; } = 4000;

    public string? ActiveHandPrefab { get; set; }

    public Dictionary<string, FakeThing> Things { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Owned { get; set; } = "None";

    public string BaselineOwned { get; set; } = "None";

    public string Shared { get; set; } = "None";

    public string? RemovedOwned { get; set; }

    public PlayerBlock Player { get; set; } = new() { Present = true, ReferenceId = 900, DisplayName = "tester", Position = [1.5, 2.5, 3.5] };

    /// <summary>What a use on this instance does to the target. The test scripts it.</summary>
    public Action<InstanceState, FakeThing?>? OnUse { get; set; }

    public StatusResponse Status()
    {
        // The roster is the SERVER's answer, and the real plugin returns an empty one on
        // anything that is not a server. Reproducing that is what stops a test proving a
        // joiner arrived by asking the joiner.
        if (PendingArrival is not null)
        {
            if (PollsBeforeArrival > 0) PollsBeforeArrival--;
            else { Roster.Add(PendingArrival); PendingArrival = null; }
        }

        return new StatusResponse
        {
            Ok = true,
            InstanceName = Name,
            Port = Port,
            Phase = Phase,
            GameInitialized = GameInitialized,
            LoadedPluginCount = LoadedPluginCount,
            Hosting = Hosting,
            Role = Role,
            NetworkRole = Hosting ? "Server" : "Client",
            IsClient = !Hosting,
            IsServer = Hosting,
            HostPort = HostPort,
            ConnectedClients = Hosting ? [.. Roster] : [],
            SaveRootIsolated = true,
            WorldName = RequestedWorld,
            WorldId = RequestedWorld,
        };
    }

    public void Print(string source, string text)
    {
        _console.Add(new ConsoleLine { Seq = NextSeq++, Src = source, Level = "Info", Text = text });
    }

    /// <summary>
    ///     The console endpoint, with its filters actually applied.
    /// </summary>
    /// <remarks>
    ///     since drops anything below the sequence, contains is a case-INSENSITIVE substring,
    ///     source selects one of the two tees, and limit keeps the LAST n. The PowerShell fake
    ///     ignored all four and answered count:1 forever, which left the entire console
    ///     counting discipline the shipped suite is built on with no coverage at all.
    /// </remarks>
    public ConsoleLogResponse ConsoleLog(IReadOnlyDictionary<string, string> query)
    {
        IEnumerable<ConsoleLine> lines = _console;

        if (query.TryGetValue("since", out var since) && long.TryParse(since, CultureInfo.InvariantCulture, out var from))
            lines = lines.Where(l => l.Seq >= from);

        if (query.TryGetValue("source", out var source) && source.Length > 0)
            lines = lines.Where(l => string.Equals(l.Src, source, StringComparison.OrdinalIgnoreCase));

        if (query.TryGetValue("contains", out var contains) && contains.Length > 0)
            lines = lines.Where(l => (l.Text ?? string.Empty).Contains(contains, StringComparison.OrdinalIgnoreCase));

        var matched = lines.ToList();
        if (query.TryGetValue("limit", out var limitText) && int.TryParse(limitText, CultureInfo.InvariantCulture, out var limit) && limit > 0 && matched.Count > limit)
            matched = matched.GetRange(matched.Count - limit, limit);

        return new ConsoleLogResponse
        {
            Ok = true,
            NextSeq = NextSeq,
            Dropped = 0,
            Truncated = 0,
            BufferedLines = _console.Count,
            Count = matched.Count,
            Lines = [.. matched],
        };
    }

    public string? SetConfig(string guid, string section, string key, string value)
    {
        if (!_config.TryGetValue(guid, out var entries)) _config[guid] = entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        entries.TryGetValue(section + "/" + key, out var previous);
        entries[section + "/" + key] = value;
        return previous;
    }

    public ConfigResponse Config(string guid)
    {
        if (!_config.TryGetValue(guid, out var entries))
        {
            return new ConfigResponse { Ok = false, Error = $"no plugin with GUID '{guid}' found in any loaded assembly" };
        }

        var rows = entries
            .Select(pair =>
            {
                var cut = pair.Key.IndexOf('/', StringComparison.Ordinal);
                return new ConfigEntryRow
                {
                    Section = pair.Key[..cut],
                    Key = pair.Key[(cut + 1)..],
                    Type = bool.TryParse(pair.Value, out _) ? "Boolean" : "String",
                    Value = pair.Value,
                    Default = pair.Value,
                };
            })
            .ToArray();

        return new ConfigResponse { Ok = true, Guid = guid, ConfigPath = $"BepInEx\\config\\{guid}.cfg", Count = rows.Length, Entries = rows };
    }

    public NearbyResponse Nearby() => new()
    {
        Ok = true,
        Scanned = Things.Count,
        Count = Things.Count,
        Things = [.. Things.Values.Select(t => new NearbyThingRow
        {
            ReferenceId = t.ReferenceId,
            PrefabName = t.PrefabName,
            CustomColorIndex = t.CustomColorIndex,
            Distance = 1.5,
        })],
    };

    public PluginsResponse Plugins() => new()
    {
        Ok = true,
        Count = 1,
        Plugins = [new PluginRow { Guid = "net.example", Name = "Example", Version = "1.0.0" }],
    };

    public InventoryResponse Inventory() => new()
    {
        Ok = true,
        Instance = Name,
        IsLocalPlayer = true,
        HasSimulationAuthority = Hosting,
        Slots =
        [
            new InventorySlotRow
            {
                Index = 0, Key = "activeHand", Type = "Hand", IsHandSlot = true, IsActiveHand = true,
                Occupant = new SlotOccupant { Empty = ActiveHandPrefab is null, PrefabName = ActiveHandPrefab },
            },
        ],
    };

    public SavePathResponse SavePath() => new()
    {
        Ok = true,
        SavePath = $"C:\\rig\\ClientRig\\data\\{Name}\\userdata",
        InsideRealUserData = false,
    };

    public DlcState DlcState() => new()
    {
        Owned = Owned,
        Shared = Shared,
        Overridden = RemovedOwned is not null,
        RemovedOwned = RemovedOwned,
        BaselineOwned = BaselineOwned,
        OwnedFieldReachable = true,
        GameInitialized = GameInitialized,
    };

    public DlcResponse Dlc() => new()
    {
        Ok = true,
        Instance = Name,
        State = DlcState(),
        Known = [new DlcKnownRow { Name = "MetallicPaints", Value = 1 }],
        Direction = "REMOVAL ONLY.",
    };
}

/// <summary>One object in the fake world.</summary>
public sealed class FakeThing
{
    public long ReferenceId { get; init; }

    public string? PrefabName { get; init; }

    public int CustomColorIndex { get; set; }

    public bool Authoritative { get; set; }

    /// <summary>Member name to rendered value, exactly as the real field rows carry it.</summary>
    public Dictionary<string, string> Members { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ThingRow Row(string requestedId, IReadOnlyList<string> fields) => new()
    {
        Instance = "fake",
        RequestedRefId = requestedId,
        Found = true,
        ReferenceId = ReferenceId,
        PrefabName = PrefabName,
        Type = "Structure",
        Paintable = true,
        CustomColorIndex = CustomColorIndex,
        Location = new LocationBlock { Authoritative = Authoritative, InSlot = false, OnGround = true, WhereIs = "world" },
        Fields = [.. fields.Select(field => new ThingFieldRow
        {
            Name = field,
            Ok = Members.ContainsKey(field),
            Kind = "field",
            ResolvedName = field,
            DeclaredType = "System.Object",
            Value = Members.GetValueOrDefault(field),
            ValueType = "String",
            MatchesPrefab = false,
        })],
    };
}
