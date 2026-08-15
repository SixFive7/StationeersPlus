using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Seams;

namespace TestRig.Tests.Playtest.Fakes;

/// <summary>
///     A control plane built out of the shared wire contract.
/// </summary>
/// <remarks>
///     <para>
///     <b>Every response this produces is a TestRig.Contracts record.</b> That is the whole
///     point. The PowerShell fake was a hand-written state machine whose shapes were the test
///     author's rather than the plugin's, and nothing anywhere compared the two: it answered
///     the DLC endpoint with <c>{ok, owned}</c> while every real check reads
///     <c>state.owned</c> and <c>state.removedOwned</c>, its nearby rows carried
///     <c>colorIndex</c> where the real ones carry <c>customColorIndex</c>, and its console
///     lines were bare strings where the real ones are objects. Fifty-four field-level
///     divergences across ten endpoints, and 399 assertions stayed green through all of them.
///     Here a divergence of that kind does not compile.
///     </para>
///     <para>
///     It also FILTERS the console log by since, contains, source and limit, which the
///     PowerShell fake ignored entirely while always answering <c>count:1</c>. Fifteen
///     assertions across six shipped checks count console lines, and not one of them could be
///     simulated: an implementation that ignored every filter would have passed the whole
///     suite and matched the fake exactly.
///     </para>
/// </remarks>
public sealed class FakeRigTransport : IRigTransport
{
    private readonly Dictionary<int, string> _byPort = [];
    private readonly Dictionary<string, InstanceState> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request that was sent, in order, as "instance METHOD path".</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Every body that was sent, in the same order.</summary>
    public List<string> Bodies { get; } = [];

    /// <summary>Ports whose next N calls throw instead of answering.</summary>
    public Dictionary<string, int> TransportFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The message a transport failure carries. Flake detectors match on this.</summary>
    public string TransportFailureMessage { get; set; } = "The remote server refused the connection.";

    /// <summary>Bare paths whose next N calls answer with a refusal at the given status.</summary>
    public Dictionary<string, (int Remaining, int Status, string Error)> Refusals { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances whose /host answers 200 while /status keeps saying not hosting.</summary>
    public HashSet<string> HostDoesNotStick { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Instances whose created world comes up with no station name, so nothing can save it.</summary>
    public HashSet<string> StationNameDoesNotStick { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Connect attempts that answer with a timeout before one succeeds.</summary>
    public int ConnectFailuresBeforeSuccess { get; set; }

    /// <summary>How many roster polls to answer with the old roster before the joiner appears.</summary>
    public int RosterPollsBeforeArrival { get; set; }

    /// <summary>When true, a joiner never appears in any host roster.</summary>
    public bool JoinerNeverArrives { get; set; }

    public FakeRigTransport Add(string instance, int port)
    {
        _byPort[port] = instance;
        _state[instance] = new InstanceState(instance, port);
        return this;
    }

    public InstanceState State(string instance) => _state[instance];

    public TransportResponse Send(int port, string path, string? bodyJson, TimeSpan timeout)
    {
        if (!_byPort.TryGetValue(port, out var instance))
            throw new RigTransportException($"fake transport: no instance on port {port}");

        var bare = Bare(path);
        Requests.Add($"{instance} {(bodyJson is null ? "GET" : "POST")} {bare}");
        Bodies.Add(bodyJson ?? string.Empty);

        if (TransportFailures.TryGetValue(bare, out var failures) && failures > 0)
        {
            TransportFailures[bare] = failures - 1;
            throw new RigTransportException(TransportFailureMessage);
        }

        if (Refusals.TryGetValue(bare, out var refusal) && refusal.Remaining > 0)
        {
            Refusals[bare] = (refusal.Remaining - 1, refusal.Status, refusal.Error);
            return Json(refusal.Status, new WireError { Ok = false, Error = refusal.Error });
        }

        var state = _state[instance];
        var query = Query(path);
        var body = bodyJson is null ? null : JsonNode.Parse(bodyJson) as JsonObject;

        return bare switch
        {
            Endpoints.Ping => Json(200, new PingResponse { Ok = true, Instance = instance, Port = port, PumpAlive = true }),
            Endpoints.Status => Json(200, state.Status()),
            Endpoints.Host => Host(state, body),
            Endpoints.Connect => Connect(state, body),
            Endpoints.Disconnect => Disconnect(state),
            Endpoints.Config => Json(200, state.Config(query.GetValueOrDefault("guid", string.Empty))),
            Endpoints.ConfigSet => ConfigSet(state, body),
            Endpoints.Thing => Thing(state, query),
            Endpoints.Nearby => Json(200, state.Nearby()),
            Endpoints.Player => Json(200, new PlayerResponse { Ok = true, Player = state.Player }),
            Endpoints.Plugins => Json(200, state.Plugins()),
            Endpoints.Inventory => Json(200, state.Inventory()),
            Endpoints.SavePath => Json(200, state.SavePath()),
            Endpoints.Reflect => Json(200, new ReflectResponse { Ok = true, Type = query.GetValueOrDefault("type", string.Empty), Member = query.GetValueOrDefault("member", string.Empty), Value = "42", ValueType = "Int32" }),
            Endpoints.ConsoleLog => Json(200, state.ConsoleLog(query)),
            Endpoints.ConsoleExec => ConsoleExec(state, body),
            Endpoints.SpawnStructure => SpawnStructure(state, body),
            Endpoints.InventoryArm => InventoryArm(state, body),
            Endpoints.PlayerUse => PlayerUse(state, body),
            Endpoints.CursorForce => Json(200, new CursorForceResponse { Ok = true, TargetId = body is null ? null : (long?)ReadLong(body, "targetId") }),
            Endpoints.InputKey => Json(200, new InputKeyResponse { Ok = true, Instance = instance, Consumed = true, Delivered = true }),
            Endpoints.InputMouse => Json(200, new InputMouseResponse { Ok = true, Instance = instance, Consumed = true, Delivered = true }),
            Endpoints.InputScroll => Json(200, new InputScrollResponse { Ok = true, Instance = instance, Consumed = true, Delivered = true }),
            Endpoints.DlcRemove => DlcRemove(state, body),
            Endpoints.DlcRestore => DlcRestore(state),
            Endpoints.Dlc => Json(200, state.Dlc()),
            _ => throw new RigTransportException($"fake transport: nothing wired for {bare}"),
        };
    }

    // ---- handlers ---------------------------------------------------------

    private TransportResponse Host(InstanceState state, JsonObject? body)
    {
        var world = body?["world"]?.GetValue<string>();
        var save = body?["save"]?.GetValue<string>();
        state.RequestedWorld = world;
        state.RequestedSave = save;
        state.Phase = "inWorld";
        state.GameInitialized = true;

        if (!HostDoesNotStick.Contains(state.Name))
        {
            state.Hosting = true;
            state.Role = "listenHost";
            state.HostPort = 27801;
            state.Roster.Add(new ConnectedClient { ClientId = "900000000001", Username = state.Name, IsHost = true, State = "settled" });
        }

        // The plugin names a world it CREATED, by performing the first named save, because a
        // console 'new' leaves CurrentStationName empty and nothing can save a world without it.
        // Modelled here rather than assumed away: a check's world comes from a world id, so the
        // unnamed case is the DEFAULT case on this rig and a fake that always reports a name
        // would hide the very state the teardown trips over.
        var stationName = world is null
            ? null
            : body?["stationName"]?.GetValue<string>() ?? world;
        var named = stationName is { Length: > 0 } && !StationNameDoesNotStick.Contains(state.Name);
        if (named) state.StationName = stationName;

        return Json(200, new HostResponse
        {
            Ok = true,
            Role = state.Role,
            Hosting = state.Hosting,
            HostPort = state.HostPort,
            World = world,
            Save = save,
            StationName = stationName,
            StationNameAssigned = world is null ? null : named,
            Warning = world is not null && !named ? "hosting, but this world has no station name." : null,
            SaveRoot = "instance",
        });
    }

    private TransportResponse Connect(InstanceState state, JsonObject? body)
    {
        if (ConnectFailuresBeforeSuccess > 0)
        {
            ConnectFailuresBeforeSuccess--;
            return Json(200, new ConnectResponse { Ok = false, Result = "timeout", Target = Target(body) });
        }

        state.Phase = "inWorld";
        state.GameInitialized = true;
        state.Role = "joinedClient";
        state.ConnectedTo = body?["port"]?.GetValue<int>();

        if (!JoinerNeverArrives)
        {
            foreach (var host in _state.Values.Where(s => s.Hosting))
            {
                host.PendingArrival = new ConnectedClient
                {
                    ClientId = "900000000002", Username = state.Name, IsHost = false, State = "settled",
                };
                host.PollsBeforeArrival = RosterPollsBeforeArrival;
            }
        }

        return Json(200, new ConnectResponse { Ok = true, Result = "connected", Target = Target(body) });
    }

    private TransportResponse Disconnect(InstanceState state)
    {
        state.Phase = "menu";
        state.Role = "menu";
        foreach (var host in _state.Values)
        {
            host.Roster.RemoveAll(r => string.Equals(r.Username, state.Name, StringComparison.OrdinalIgnoreCase));
            host.PendingArrival = null;
        }

        return Json(200, new DisconnectResponse { Ok = true, Result = "menu" });
    }

    private static TransportResponse ConfigSet(InstanceState state, JsonObject? body)
    {
        var section = body?["section"]?.GetValue<string>() ?? string.Empty;
        var key = body?["key"]?.GetValue<string>() ?? string.Empty;
        var value = body?["value"]?.GetValue<string>() ?? string.Empty;
        var guid = body?["guid"]?.GetValue<string>() ?? string.Empty;

        var previous = state.SetConfig(guid, section, key, value);
        return Json(200, new ConfigSetResponse
        {
            Ok = true, Guid = guid, Section = section, Key = key, OldValue = previous, NewValue = value, SavedToDisk = false,
        });
    }

    private static TransportResponse Thing(InstanceState state, IReadOnlyDictionary<string, string> query)
    {
        // The real endpoint answers 400 for a query with no id at all, which is what makes a
        // re-read that dropped its reader args a measurement rather than a decoration.
        var ids = query.GetValueOrDefault("refIds") ?? query.GetValueOrDefault("refId")
            ?? query.GetValueOrDefault("ids") ?? query.GetValueOrDefault("id");

        if (string.IsNullOrEmpty(ids))
        {
            return Json(400, new WireError { Ok = false, Error = "one of refId, refIds, id or ids is required" });
        }

        var fields = (query.GetValueOrDefault("fields") ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var rows = new List<ThingRow>();
        var missing = new List<string>();
        foreach (var id in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!state.Things.TryGetValue(id, out var thing)) { missing.Add(id); continue; }
            rows.Add(thing.Row(id, fields));
        }

        return Json(missing.Count == 0 ? 200 : 409, new ThingResponse
        {
            Ok = missing.Count == 0,
            Instance = state.Name,
            Requested = ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Length,
            Found = rows.Count,
            Missing = [.. missing],
            Things = [.. rows],
        });
    }

    private static TransportResponse ConsoleExec(InstanceState state, JsonObject? body)
    {
        var command = body?["command"]?.GetValue<string>() ?? string.Empty;
        state.Print("console", $"> {command}");
        return Json(200, new ConsoleExecResponse { Ok = true, Command = command, NextSeq = state.NextSeq, Count = 0, Lines = [] });
    }

    private static TransportResponse SpawnStructure(InstanceState state, JsonObject? body)
    {
        var prefab = body?["prefab"]?.GetValue<string>() ?? string.Empty;
        var colorIndex = body?["colorIndex"] is { } node ? node.GetValue<int>() : 0;
        var id = state.NextReferenceId++;

        state.Things[id.ToString(CultureInfo.InvariantCulture)] = new FakeThing
        {
            ReferenceId = id, PrefabName = prefab, CustomColorIndex = colorIndex, Authoritative = state.Hosting,
        };

        return Json(200, new SpawnStructureResponse { Ok = true, Prefab = prefab, ColorIndex = colorIndex, ReferenceId = id });
    }

    private static TransportResponse InventoryArm(InstanceState state, JsonObject? body)
    {
        var prefab = body?["prefab"]?.GetValue<string>() ?? string.Empty;
        var id = state.NextReferenceId++;
        state.Things[id.ToString(CultureInfo.InvariantCulture)] = new FakeThing { ReferenceId = id, PrefabName = prefab };
        state.ActiveHandPrefab = prefab;

        return Json(200, new InventoryArmResponse
        {
            Ok = true, Instance = state.Name, Prefab = prefab, ReferenceId = id, Hand = "activeHand", Confirmed = true,
        });
    }

    private static TransportResponse PlayerUse(InstanceState state, JsonObject? body)
    {
        var targetId = body is null ? 0 : ReadLong(body, "targetId");
        var key = targetId.ToString(CultureInfo.InvariantCulture);
        state.Things.TryGetValue(key, out var thing);

        state.OnUse?.Invoke(state, thing);

        return Json(200, new PlayerUseResponse
        {
            Ok = true, Instance = state.Name, TargetId = targetId, TargetPrefab = thing?.PrefabName, HeldItem = state.ActiveHandPrefab,
        });
    }

    private static TransportResponse DlcRemove(InstanceState state, JsonObject? body)
    {
        var dlc = body?["dlc"]?.GetValue<string>() ?? string.Empty;
        if (dlc.Length > 0)
        {
            state.RemovedOwned = dlc;
            state.Owned = state.Owned.Replace(dlc, string.Empty, StringComparison.Ordinal).Trim(',');
            if (state.Owned.Length == 0) state.Owned = "None";
        }

        return Json(200, new DlcRemoveResponse { Ok = true, Instance = state.Name, Requested = dlc, Scope = "owned", State = state.DlcState() });
    }

    private static TransportResponse DlcRestore(InstanceState state)
    {
        state.Owned = state.BaselineOwned;
        state.RemovedOwned = null;
        return Json(200, new DlcRestoreResponse { Ok = true, Restored = true, OwnedAfter = state.Owned, State = state.DlcState() });
    }

    // ---- plumbing ---------------------------------------------------------

    private static string? Target(JsonObject? body) =>
        body is null ? null : $"{body["address"]?.GetValue<string>()}:{body["port"]?.GetValue<int>()}";

    private static long ReadLong(JsonObject body, string key) =>
        body[key] is { } node && node.GetValueKind() == JsonValueKind.Number ? node.GetValue<long>() : 0;

    private static TransportResponse Json<T>(int status, T value) where T : class =>
        new(status, JsonSerializer.Serialize(value, typeof(T), RigJson.Context));

    internal static string Bare(string path)
    {
        var cut = path.IndexOf('?', StringComparison.Ordinal);
        return Endpoints.Normalize(cut >= 0 ? path[..cut] : path);
    }

    internal static IReadOnlyDictionary<string, string> Query(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cut = path.IndexOf('?', StringComparison.Ordinal);
        if (cut < 0) return result;

        foreach (var pair in path[(cut + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) { result[Uri.UnescapeDataString(pair)] = string.Empty; continue; }
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }

        return result;
    }
}
