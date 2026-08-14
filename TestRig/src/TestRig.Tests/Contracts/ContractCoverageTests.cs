using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TestRig.Contracts;
using Xunit;

namespace TestRig.Tests.Contracts;

/// <summary>
///     Exhaustive checks over every contract type, driven by reflection so a type added
///     later is covered without anyone remembering to extend a list.
/// </summary>
/// <remarks>
///     The launcher publishes AOT, where reflection-based serialization is trimmed away.
///     A response type missing from <c>RigJsonContext</c> therefore does not degrade to
///     slow, it throws at runtime, on a rig somebody had to take the lock for. These tests
///     move that failure to the build.
/// </remarks>
public sealed class ContractCoverageTests
{
    private static readonly Assembly ContractsAssembly = typeof(Endpoints).Assembly;

    private static IEnumerable<Type> ResponseTypes =>
        ContractsAssembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IWireResult).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    private static IEnumerable<Type> RequestTypes =>
        ContractsAssembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.Name.EndsWith("Request", StringComparison.Ordinal))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    /// <summary>
    ///     68 endpoint handlers, 68 response records, plus <see cref="WireError"/> for the
    ///     universal failure body. <c>/</c> and <c>/help</c> share <c>HelpResponse</c>, and
    ///     <c>/input/mouse</c> gets its own record deriving from the <c>/input/key</c> one
    ///     because the router delegates into the same handler.
    /// </summary>
    /// <remarks>
    ///     64 came from <c>ClientDriver</c>; the four scenario paths came with the merged
    ///     TestRig plugin and are answered by the dedicated server.
    /// </remarks>
    [Fact]
    public void ThereIsOneResponseRecordPerEndpointHandlerPlusTheErrorEnvelope()
    {
        Assert.Equal(69, ResponseTypes.Count());
        Assert.Equal(68, Endpoints.All.Count(p => p != Endpoints.Root));
    }

    [Fact]
    public void ThereIsOneRequestRecordPerEndpointHandler()
    {
        Assert.Equal(68, RequestTypes.Count());
    }

    /// <summary>
    ///     Guards the two tests below from being vacuous. If <c>GetTypeInfo</c> answered
    ///     for everything, or if <see cref="RigJson.Options"/> quietly fell back to
    ///     reflection, the coverage tests would pass no matter what was registered.
    /// </summary>
    [Fact]
    public void TheGeneratedContextRefusesATypeItWasNotGiven()
    {
        Assert.Null(RigJsonContext.Default.GetTypeInfo(typeof(ContractCoverageTests)));

        Assert.ThrowsAny<Exception>(() =>
            JsonSerializer.Deserialize<ContractCoverageTests>("{}", RigJson.Options));
    }

    /// <summary>
    ///     Every response type must be reachable from the source-generated context.
    ///     <c>GetTypeInfo</c> returns null for a type that was never registered.
    /// </summary>
    [Fact]
    public void EveryResponseTypeIsRegisteredInTheSourceGeneratedContext()
    {
        var missing = ResponseTypes
            .Where(t => RigJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void EveryRequestTypeIsRegisteredInTheSourceGeneratedContext()
    {
        var missing = RequestTypes
            .Where(t => RigJsonContext.Default.GetTypeInfo(t) is null)
            .Select(t => t.Name)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    ///     A default instance of every response type survives a serialize and deserialize
    ///     through the generated serializer, and comes back equal. Records give value
    ///     equality, and a default instance holds no arrays, so equality here is a real
    ///     comparison rather than a reference check.
    /// </summary>
    [Fact]
    public void EveryResponseTypeRoundTripsThroughTheGeneratedSerializer()
    {
        var failures = new List<string>();

        foreach (Type type in ResponseTypes)
        {
            JsonTypeInfo info = RigJsonContext.Default.GetTypeInfo(type)!;
            object original = Activator.CreateInstance(type)!;

            try
            {
                string json = JsonSerializer.Serialize(original, info);
                object? back = JsonSerializer.Deserialize(json, info);
                if (!Equals(original, back)) failures.Add(type.Name + " came back unequal: " + json);
            }
            catch (Exception ex)
            {
                failures.Add(type.Name + " threw: " + ex.Message);
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void EveryRequestTypeRoundTripsThroughTheGeneratedSerializer()
    {
        var failures = new List<string>();

        foreach (Type type in RequestTypes)
        {
            JsonTypeInfo info = RigJsonContext.Default.GetTypeInfo(type)!;
            object original = Activator.CreateInstance(type)!;

            try
            {
                string json = JsonSerializer.Serialize(original, info);
                object? back = JsonSerializer.Deserialize(json, info);
                if (!Equals(original, back)) failures.Add(type.Name + " came back unequal: " + json);
            }
            catch (Exception ex)
            {
                failures.Add(type.Name + " threw: " + ex.Message);
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    ///     Every response type carries <c>ok</c>, and it must bind. The whole
    ///     success-detection rule rests on this one field existing everywhere.
    /// </summary>
    [Fact]
    public void EveryResponseTypeBindsTheOkField()
    {
        var failures = new List<string>();

        foreach (Type type in ResponseTypes)
        {
            JsonTypeInfo info = RigJsonContext.Default.GetTypeInfo(type)!;

            try
            {
                var parsed = (IWireResult)JsonSerializer.Deserialize("{\"ok\":true}", info)!;
                if (!parsed.Ok) failures.Add(type.Name + " did not bind ok:true");

                var refused = (IWireResult)JsonSerializer.Deserialize("{\"ok\":false}", info)!;
                if (refused.Ok) failures.Add(type.Name + " did not bind ok:false");
            }
            catch (Exception ex)
            {
                failures.Add(type.Name + " threw: " + ex.Message);
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    ///     Nothing may rely on the default PascalCase naming policy. A property without an
    ///     explicit wire name is a field whose spelling can drift the moment someone
    ///     renames the C# member, which is exactly the class of silent break this assembly
    ///     exists to stop.
    /// </summary>
    [Fact]
    public void EveryContractPropertyDeclaresItsWireName()
    {
        var offenders = new List<string>();

        foreach (Type type in ContractTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name == "EqualityContract") continue;
                if (property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null) continue;
                if (property.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;

                offenders.Add(type.Name + "." + property.Name);
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>
    ///     Two properties on one type must not claim the same wire name. The plugin's JSON
    ///     writer is append-only and will happily emit a key twice, so a duplicate here
    ///     would be a real ambiguity rather than a compile error.
    /// </summary>
    [Fact]
    public void NoContractTypeDeclaresTheSameWireNameTwice()
    {
        var offenders = new List<string>();

        foreach (Type type in ContractTypes())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>();
                if (attribute is null) continue;
                if (!seen.Add(attribute.Name)) offenders.Add(type.Name + " -> " + attribute.Name);
            }
        }

        Assert.Empty(offenders);
    }

    private static IEnumerable<Type> ContractTypes() =>
        ContractsAssembly.GetExportedTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, Namespace: "TestRig.Contracts" })
            .Where(t => t != typeof(RigJsonContext))
            .Where(t => t != typeof(Endpoints))
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null);

    /// <summary>
    ///     Path to request and response record, written out so the mapping is checked
    ///     rather than assumed. A count alone would pass with two types for one endpoint
    ///     and none for another.
    /// </summary>
    private static readonly Dictionary<string, (string Request, string Response)> Bindings = new(StringComparer.Ordinal)
    {
        [Endpoints.Root] = ("HelpRequest", "HelpResponse"),
        [Endpoints.Help] = ("HelpRequest", "HelpResponse"),
        [Endpoints.Ping] = ("PingRequest", "PingResponse"),
        [Endpoints.Instance] = ("InstanceRequest", "InstanceResponse"),
        [Endpoints.Identity] = ("IdentityRequest", "IdentityResponse"),
        [Endpoints.Status] = ("StatusRequest", "StatusResponse"),
        [Endpoints.Player] = ("PlayerRequest", "PlayerResponse"),
        [Endpoints.Colors] = ("ColorsRequest", "ColorsResponse"),
        [Endpoints.Plugins] = ("PluginsRequest", "PluginsResponse"),
        [Endpoints.Nearby] = ("NearbyRequest", "NearbyResponse"),
        [Endpoints.ConsoleLog] = ("ConsoleLogRequest", "ConsoleLogResponse"),
        [Endpoints.ConsoleClear] = ("ConsoleClearRequest", "ConsoleClearResponse"),
        [Endpoints.ConsoleBuffer] = ("ConsoleBufferRequest", "ConsoleBufferResponse"),
        [Endpoints.ConsoleExec] = ("ConsoleExecRequest", "ConsoleExecResponse"),
        [Endpoints.ConsolePrint] = ("ConsolePrintRequest", "ConsolePrintResponse"),
        [Endpoints.ConsoleCommands] = ("ConsoleCommandsRequest", "ConsoleCommandsResponse"),
        [Endpoints.Connect] = ("ConnectRequest", "ConnectResponse"),
        [Endpoints.Host] = ("HostRequest", "HostResponse"),
        [Endpoints.Disconnect] = ("DisconnectRequest", "DisconnectResponse"),
        [Endpoints.Quit] = ("QuitRequest", "QuitResponse"),
        [Endpoints.Saves] = ("SavesRequest", "SavesResponse"),
        [Endpoints.Save] = ("SaveRequest", "SaveResponse"),
        [Endpoints.SavePath] = ("SavePathRequest", "SavePathResponse"),
        [Endpoints.Load] = ("LoadRequest", "LoadResponse"),
        [Endpoints.NewWorld] = ("NewWorldRequest", "NewWorldResponse"),
        [Endpoints.WaitFor] = ("WaitForRequest", "WaitForResponse"),
        [Endpoints.InputKey] = ("InputKeyRequest", "InputKeyResponse"),
        [Endpoints.InputScroll] = ("InputScrollRequest", "InputScrollResponse"),
        [Endpoints.InputMouse] = ("InputMouseRequest", "InputMouseResponse"),
        [Endpoints.InputMousePosition] = ("InputMousePositionRequest", "InputMousePositionResponse"),
        [Endpoints.InputReleaseAll] = ("InputReleaseAllRequest", "InputReleaseAllResponse"),
        [Endpoints.InputClear] = ("InputClearRequest", "InputClearResponse"),
        [Endpoints.InputKeyMap] = ("InputKeyMapRequest", "InputKeyMapResponse"),
        [Endpoints.InputEnable] = ("InputEnableRequest", "InputEnableResponse"),
        [Endpoints.DiagInput] = ("DiagInputRequest", "DiagInputResponse"),
        [Endpoints.DiagJoin] = ("DiagJoinRequest", "DiagJoinResponse"),
        [Endpoints.PlayerTeleport] = ("PlayerTeleportRequest", "PlayerTeleportResponse"),
        [Endpoints.PlayerLook] = ("PlayerLookRequest", "PlayerLookResponse"),
        [Endpoints.PlayerUse] = ("PlayerUseRequest", "PlayerUseResponse"),
        [Endpoints.PlayerSwapHands] = ("PlayerSwapHandsRequest", "PlayerSwapHandsResponse"),
        [Endpoints.Inventory] = ("InventoryRequest", "InventoryResponse"),
        [Endpoints.InventoryMove] = ("InventoryMoveRequest", "InventoryMoveResponse"),
        [Endpoints.InventoryGive] = ("InventoryGiveRequest", "InventoryGiveResponse"),
        [Endpoints.InventoryArm] = ("InventoryArmRequest", "InventoryArmResponse"),
        [Endpoints.SpawnHand] = ("SpawnHandRequest", "SpawnHandResponse"),
        [Endpoints.SpawnWorld] = ("SpawnWorldRequest", "SpawnWorldResponse"),
        [Endpoints.SpawnStructure] = ("SpawnStructureRequest", "SpawnStructureResponse"),
        [Endpoints.Prefabs] = ("PrefabsRequest", "PrefabsResponse"),
        [Endpoints.ModSettingsList] = ("ModSettingsListRequest", "ModSettingsListResponse"),
        [Endpoints.ModSettings] = ("ModSettingsRequest", "ModSettingsResponse"),
        [Endpoints.Modal] = ("ModalRequest", "ModalResponse"),
        [Endpoints.ModalClick] = ("ModalClickRequest", "ModalClickResponse"),
        [Endpoints.CursorForce] = ("CursorForceRequest", "CursorForceResponse"),
        [Endpoints.Screenshot] = ("ScreenshotRequest", "ScreenshotResponse"),
        [Endpoints.Config] = ("ConfigRequest", "ConfigResponse"),
        [Endpoints.ConfigSet] = ("ConfigSetRequest", "ConfigSetResponse"),
        [Endpoints.ConfigReload] = ("ConfigReloadRequest", "ConfigReloadResponse"),
        [Endpoints.Reflect] = ("ReflectRequest", "ReflectResponse"),
        [Endpoints.ReflectMembers] = ("ReflectMembersRequest", "ReflectMembersResponse"),
        [Endpoints.ReflectInstance] = ("ReflectInstanceRequest", "ReflectInstanceResponse"),
        [Endpoints.Thing] = ("ThingRequest", "ThingResponse"),
        [Endpoints.ThingMembers] = ("ThingMembersRequest", "ThingMembersResponse"),
        [Endpoints.Dlc] = ("DlcRequest", "DlcResponse"),
        [Endpoints.DlcRemove] = ("DlcRemoveRequest", "DlcRemoveResponse"),
        [Endpoints.DlcRestore] = ("DlcRestoreRequest", "DlcRestoreResponse"),
        [Endpoints.Scenarios] = ("ScenariosRequest", "ScenariosResponse"),
        [Endpoints.ScenarioRun] = ("ScenarioRunRequest", "ScenarioRunResponse"),
        [Endpoints.ScenarioArm] = ("ScenarioArmRequest", "ScenarioArmResponse"),
        [Endpoints.ScenarioDisarm] = ("ScenarioDisarmRequest", "ScenarioDisarmResponse"),
    };

    [Fact]
    public void EveryPathBindsToARequestAndAResponseType()
    {
        var failures = new List<string>();

        foreach (string path in Endpoints.All)
        {
            if (!Bindings.TryGetValue(path, out (string Request, string Response) names))
            {
                failures.Add(path + " has no request/response binding");
                continue;
            }

            foreach (string name in new[] { names.Request, names.Response })
            {
                Type? type = ContractsAssembly.GetType("TestRig.Contracts." + name, throwOnError: false);
                if (type is null) failures.Add(path + " names " + name + ", which does not exist");
                else if (RigJsonContext.Default.GetTypeInfo(type) is null)
                    failures.Add(path + " names " + name + ", which is not registered in RigJsonContext");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    ///     No response record may exist that no path returns, and no path may return a
    ///     record another path already owns except where the router itself shares a
    ///     handler. Only <c>/</c> and <c>/help</c> do that.
    /// </summary>
    [Fact]
    public void EveryResponseRecordIsReturnedByExactlyOnePathExceptTheHelpAlias()
    {
        var bound = Bindings.Values.Select(v => v.Response).ToList();
        var declared = ResponseTypes.Select(t => t.Name).Where(n => n != nameof(WireError)).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(declared, bound.ToHashSet(StringComparer.Ordinal));
        Assert.Equal(bound.Count - 1, bound.Distinct(StringComparer.Ordinal).Count());
    }
}
