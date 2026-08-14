using System.Globalization;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Seams;

namespace TestRig.Playtest.Readers;

/// <summary>
///     The thirteen readers, typed against the shared wire contract.
/// </summary>
/// <remarks>
///     <para>
///     Every narrowing here goes through a Contracts record. That is the difference between
///     this catalogue and the PowerShell one it replaces: there, narrowing walked a parsed
///     JSON blob by string, so the fake transport could answer <c>/dlc</c> with
///     <c>{ok, owned}</c> while the real endpoint puts it at <c>state.owned</c>, and 399
///     assertions stayed green through 54 divergences of that shape. A test double here has to
///     construct <see cref="DlcResponse"/>, so the same drift is a compile error.
///     </para>
///     <para>
///     The <c>-Select</c> path still runs over JSON, because that is the check author's own
///     contract and a check names fields the engine has never heard of. What changed is that
///     the JSON it runs over is produced FROM the typed record, so a field the contract does
///     not have cannot appear in it.
///     </para>
/// </remarks>
public static class ReaderCatalogue
{
    /// <summary>What each reader is, for the listing and for the drift guard.</summary>
    public static IReadOnlyDictionary<Reader, string> Descriptions { get; } = new Dictionary<Reader, string>
    {
        [Reader.Status] = "GET /status. The one computed answer to what this process is: role, hosting, hostPort, connectedClients, phase, save hygiene.",
        [Reader.Roster] = "GET /status then connectedClients. The SERVER-side roster, which is what makes did-the-joiner-arrive assertable from the host.",
        [Reader.Config] = "GET /config?guid=<mod>. Every ConfigEntry of a loaded plugin, as the running process sees it. Of '<Section>/<Key>' picks one.",
        [Reader.Thing] = "GET /thing?refIds=&fields=. An INSTANCE field on one Thing, per machine. Of '<refId>' picks the Thing, Of '<refId>/<Field>' picks one field row so select value and select matchesPrefab work.",
        [Reader.Reflect] = "GET /reflect?type=&member=. Any STATIC field or property by full type name. Instance fields belong to the thing reader.",
        [Reader.Nearby] = "GET /nearby. Things around the player; Of '<referenceId>' picks one.",
        [Reader.Console] = "GET /console/log. The sequence-numbered tee, for a line a mod printed. A BOUNDED RING: boot-time lines are routinely evicted, so read those through bepinexlog instead.",
        [Reader.BepInExLog] = "The instance BepInEx/LogOutput.log FILE. No ring and no eviction, and the state reset empties it per session, so it is the authority for anything printed during boot.",
        [Reader.Inventory] = "GET /inventory. Every slot of a character. Of '<slot key or index>' picks one.",
        [Reader.Plugins] = "GET /plugins. Every plugin found by assembly scan.",
        [Reader.SavePath] = "GET /savepath. Where this process writes, and whether that is isolated from the developer folder.",
        [Reader.Player] = "GET /player. The player block only.",
        [Reader.Dlc] = "GET /dlc. What this process believes it is entitled to.",
    };

    /// <summary>The endpoint a reader uses, or null when it does not use the control plane.</summary>
    public static string? Endpoint(Reader reader) => reader switch
    {
        Reader.Status or Reader.Roster => Endpoints.Status,
        Reader.Config => Endpoints.Config,
        Reader.Thing => Endpoints.Thing,
        Reader.Reflect => Endpoints.Reflect,
        Reader.Nearby => Endpoints.Nearby,
        Reader.Console => Endpoints.ConsoleLog,
        Reader.Inventory => Endpoints.Inventory,
        Reader.Plugins => Endpoints.Plugins,
        Reader.SavePath => Endpoints.SavePath,
        Reader.Player => Endpoints.Player,
        Reader.Dlc => Endpoints.Dlc,
        _ => null,
    };

    /// <summary>Whether a reader accepts reader args as a query string.</summary>
    /// <remarks>
    ///     The readers that do not take a query ignore args entirely, exactly as the
    ///     PowerShell path switch did: only thing, inventory, config, reflect, nearby and
    ///     console appended one.
    /// </remarks>
    public static bool TakesQuery(Reader reader) => reader is
        Reader.Thing or Reader.Inventory or Reader.Config or Reader.Reflect or Reader.Nearby or Reader.Console;

    /// <summary>The reader's name as it appears in a report and an evidence file name.</summary>
    public static string Name(Reader reader) => reader switch
    {
        Reader.BepInExLog => "bepinexlog",
        Reader.SavePath => "savepath",
        Reader.Dlc => "dlc",
        _ => reader.ToString().ToLowerInvariant(),
    };

    /// <summary>
    ///     Narrows a response to the thing <paramref name="of"/> names, before the select path
    ///     runs.
    /// </summary>
    /// <remarks>
    ///     Keeping this out of the select path means a check never has to know which JSON
    ///     shape a given endpoint happens to use for its collection.
    /// </remarks>
    public static JsonNode? Narrow(Reader reader, string body, string of)
    {
        switch (reader)
        {
            case Reader.Status:
                return Node(RigWire.Deserialize<StatusResponse>(body));

            case Reader.Roster:
            {
                var rows = RigWire.Deserialize<StatusResponse>(body)?.ConnectedClients ?? [];
                if (of.Length == 0) return Nodes(rows);

                var row = rows.FirstOrDefault(r => Same(r.ClientId, of));
                return Node(row);
            }

            case Reader.Config:
            {
                var response = RigWire.Deserialize<ConfigResponse>(body);
                if (of.Length == 0) return Node(response);

                var (section, key) = Split(of);
                var row = (response?.Entries ?? []).FirstOrDefault(e =>
                    Same(e.Section, section) && (key.Length == 0 || Same(e.Key, key)));
                return Node(row);
            }

            case Reader.Thing:
            {
                var rows = RigWire.Deserialize<ThingResponse>(body)?.Things ?? [];
                if (of.Length == 0) return Nodes(rows);

                var (id, field) = Split(of);
                var row = rows.FirstOrDefault(t =>
                    Same(t.RequestedRefId, id) ||
                    t.ReferenceId is { } reference && Same(reference.ToString(CultureInfo.InvariantCulture), id));

                if (row is null) return null;
                if (field.Length == 0) return Node(row);

                // Of '<refId>/<Field>' picks one field row, so select value and select
                // matchesPrefab read what a check actually wants.
                var fieldRow = (row.Fields ?? []).FirstOrDefault(f => Same(f.Name, field) || Same(f.ResolvedName, field));
                return Node(fieldRow);
            }

            case Reader.Nearby:
            {
                var response = RigWire.Deserialize<NearbyResponse>(body);
                var rows = response?.Things ?? [];

                // The PowerShell fallback treated the WHOLE response as the row set when
                // things was empty, which against the real {ok:false,"no local player"} shape
                // yields one row that is the error envelope. An empty scan is an empty scan.
                if (of.Length == 0) return Nodes(rows);

                var row = rows.FirstOrDefault(t =>
                    t.ReferenceId is { } reference && Same(reference.ToString(CultureInfo.InvariantCulture), of));
                return Node(row);
            }

            case Reader.Console:
                return Node(RigWire.Deserialize<ConsoleLogResponse>(body));

            case Reader.Inventory:
            {
                var response = RigWire.Deserialize<InventoryResponse>(body);
                if (of.Length == 0) return Node(response);

                var row = (response?.Slots ?? []).FirstOrDefault(s =>
                    Same(s.Key, of) || Same(s.Index.ToString(CultureInfo.InvariantCulture), of));
                return Node(row);
            }

            case Reader.Plugins:
                return Node(RigWire.Deserialize<PluginsResponse>(body));

            case Reader.SavePath:
                return Node(RigWire.Deserialize<SavePathResponse>(body));

            case Reader.Reflect:
                return Node(RigWire.Deserialize<ReflectResponse>(body));

            case Reader.Player:
                // Defect P-16. The catalogue has always documented this reader as "the player
                // block only", and the PowerShell narrowing had no player case at all, so it
                // fell through to the default and returned the whole {ok, epoch, player}
                // envelope. A check written to the documentation (select 'present') read
                // absent against the real endpoint, and the fake's shape (which had no player
                // wrapper) hid it in every test.
                return Node(RigWire.Deserialize<PlayerResponse>(body)?.Player);

            case Reader.Dlc:
                return Node(RigWire.Deserialize<DlcResponse>(body));

            default:
                return PlaytestJson.TryParse(body);
        }
    }

    private static JsonNode? Node(object? value) => value is null ? null : RigWire.ToNode(value);

    private static JsonArray Nodes<T>(IEnumerable<T> rows) where T : class
    {
        var array = new JsonArray();
        foreach (var row in rows) array.Add(RigWire.ToNode(row));
        return array;
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static (string Head, string Tail) Split(string of)
    {
        var at = of.IndexOf('/', StringComparison.Ordinal);
        return at < 0 ? (of, string.Empty) : (of[..at], of[(at + 1)..]);
    }
}
