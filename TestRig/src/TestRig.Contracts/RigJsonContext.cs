using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     The serializer settings both sides of the wire agree on.
/// </summary>
/// <remarks>
///     <para>
///     Number handling is deliberately left strict. This assembly reproduces the plugin's
///     own inconsistency about reference ids and client ids, which travel as JSON numbers
///     on some endpoints and as JSON strings on others, because a number parsed through
///     double loses precision above 2^53. If a future plugin change swaps one for the
///     other, strict handling makes the deserializer throw instead of quietly producing a
///     null, and a throw is what a caller can act on.
///     </para>
///     <para>
///     Nulls are not written, because most response records carry many optional members
///     and a request record carries mostly unset ones. The plugin treats an absent key and
///     a null key identically on the way in.
///     </para>
/// </remarks>
public static class RigJson
{
#if NET10_0_OR_GREATER
    /// <summary>
    ///     The source-generated options. The launcher publishes AOT, where reflection-based
    ///     serialization is trimmed away, so this is not an optimisation but the only way
    ///     the binary can serialize anything at all.
    /// </summary>
    public static JsonSerializerOptions Options => RigJsonContext.Default.Options;

    /// <summary>The source-generated type resolver, for callers that pass a context explicitly.</summary>
    public static JsonSerializerContext Context => RigJsonContext.Default;
#else
    private static readonly JsonSerializerOptions Reflection = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>
    ///     Reflection-based options for the netstandard2.0 consumer. The plugin runs under
    ///     the game's Mono runtime with nothing trimmed, so reflection is available there
    ///     and a source generator is not.
    /// </summary>
    public static JsonSerializerOptions Options => Reflection;
#endif
}

#if NET10_0_OR_GREATER

// The source generator only exists on the modern target, and the AOT launcher requires it.
// Every request and response type in the assembly is registered below; a type that is
// missing here throws at runtime under AOT rather than falling back to reflection, so the
// list is exhaustive by necessity, not by tidiness.
//
// Shared blocks (EpochBlock, ValueBlock, the Thing rows, and so on) are reachable from the
// registered responses and are generated with them. They are listed anyway so a test can
// round-trip a block on its own.

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]

// ---- envelope ------------------------------------------------------------
[JsonSerializable(typeof(WireError))]

// ---- shared blocks -------------------------------------------------------
[JsonSerializable(typeof(EpochBlock))]
[JsonSerializable(typeof(InstanceBlock))]
[JsonSerializable(typeof(PeerRow))]
[JsonSerializable(typeof(PeersBlock))]
[JsonSerializable(typeof(ValueBlock))]
[JsonSerializable(typeof(LocationBlock))]
[JsonSerializable(typeof(LocationChainLink))]
[JsonSerializable(typeof(ThingIdentity))]
[JsonSerializable(typeof(ThingRow))]
[JsonSerializable(typeof(ThingFieldRow))]
[JsonSerializable(typeof(MemberRow))]
[JsonSerializable(typeof(NearbyThingRow))]
[JsonSerializable(typeof(SlotOccupant))]
[JsonSerializable(typeof(InventorySlotRow))]
[JsonSerializable(typeof(CursorTarget))]
[JsonSerializable(typeof(PlayerBlock))]
[JsonSerializable(typeof(ConnectedClient))]
[JsonSerializable(typeof(ForegroundBlock))]
[JsonSerializable(typeof(ConsoleTeeBlock))]
[JsonSerializable(typeof(DriverBlock))]
[JsonSerializable(typeof(JoinTraceEvent))]
[JsonSerializable(typeof(JoinTraceBlock))]
[JsonSerializable(typeof(PeerProbeBlock))]
[JsonSerializable(typeof(ModalBlock))]
[JsonSerializable(typeof(ChainLinkCounts))]
[JsonSerializable(typeof(ChainBlock))]
[JsonSerializable(typeof(InputPatchesBlock))]
[JsonSerializable(typeof(InputGateBlock))]
[JsonSerializable(typeof(WindowBlock))]
[JsonSerializable(typeof(KeyGateBlock))]
[JsonSerializable(typeof(ScrollGateBlock))]
[JsonSerializable(typeof(ObservedKeyReads))]
[JsonSerializable(typeof(ColorRow))]
[JsonSerializable(typeof(PluginRow))]
[JsonSerializable(typeof(ConsoleLine))]
[JsonSerializable(typeof(ConsoleBufferLine))]
[JsonSerializable(typeof(ConfigEntryRow))]
[JsonSerializable(typeof(ModSettingsModRow))]
[JsonSerializable(typeof(DlcKnownRow))]
[JsonSerializable(typeof(DlcState))]
[JsonSerializable(typeof(DlcScopeDelta))]

// ---- status --------------------------------------------------------------
[JsonSerializable(typeof(HelpRequest))]
[JsonSerializable(typeof(HelpResponse))]
[JsonSerializable(typeof(PingRequest))]
[JsonSerializable(typeof(PingResponse))]
[JsonSerializable(typeof(InstanceRequest))]
[JsonSerializable(typeof(InstanceResponse))]
[JsonSerializable(typeof(IdentityRequest))]
[JsonSerializable(typeof(IdentityResponse))]
[JsonSerializable(typeof(StatusRequest))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ColorsRequest))]
[JsonSerializable(typeof(ColorsResponse))]
[JsonSerializable(typeof(PluginsRequest))]
[JsonSerializable(typeof(PluginsResponse))]

// ---- world and session ---------------------------------------------------
[JsonSerializable(typeof(ConnectRequest))]
[JsonSerializable(typeof(ConnectResponse))]
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(DisconnectRequest))]
[JsonSerializable(typeof(DisconnectResponse))]
[JsonSerializable(typeof(QuitRequest))]
[JsonSerializable(typeof(QuitResponse))]
[JsonSerializable(typeof(WaitForRequest))]
[JsonSerializable(typeof(WaitForResponse))]
[JsonSerializable(typeof(SavesRequest))]
[JsonSerializable(typeof(SavesResponse))]
[JsonSerializable(typeof(SaveRequest))]
[JsonSerializable(typeof(SaveResponse))]
[JsonSerializable(typeof(SavePathRequest))]
[JsonSerializable(typeof(SavePathResponse))]
[JsonSerializable(typeof(LoadRequest))]
[JsonSerializable(typeof(LoadResponse))]
[JsonSerializable(typeof(NewWorldRequest))]
[JsonSerializable(typeof(NewWorldResponse))]
[JsonSerializable(typeof(NearbyRequest))]
[JsonSerializable(typeof(NearbyResponse))]
[JsonSerializable(typeof(ModalRequest))]
[JsonSerializable(typeof(ModalResponse))]
[JsonSerializable(typeof(ModalClickRequest))]
[JsonSerializable(typeof(ModalClickResponse))]
[JsonSerializable(typeof(ModSettingsListRequest))]
[JsonSerializable(typeof(ModSettingsListResponse))]
[JsonSerializable(typeof(ModSettingsRequest))]
[JsonSerializable(typeof(ModSettingsResponse))]

// ---- player --------------------------------------------------------------
[JsonSerializable(typeof(PlayerRequest))]
[JsonSerializable(typeof(PlayerResponse))]
[JsonSerializable(typeof(PlayerTeleportRequest))]
[JsonSerializable(typeof(PlayerTeleportResponse))]
[JsonSerializable(typeof(PlayerLookRequest))]
[JsonSerializable(typeof(PlayerLookResponse))]
[JsonSerializable(typeof(PlayerUseRequest))]
[JsonSerializable(typeof(PlayerUseResponse))]
[JsonSerializable(typeof(PlayerSwapHandsRequest))]
[JsonSerializable(typeof(PlayerSwapHandsResponse))]
[JsonSerializable(typeof(CursorForceRequest))]
[JsonSerializable(typeof(CursorForceResponse))]

// ---- inventory -----------------------------------------------------------
[JsonSerializable(typeof(InventoryRequest))]
[JsonSerializable(typeof(InventoryResponse))]
[JsonSerializable(typeof(InventoryMoveRequest))]
[JsonSerializable(typeof(InventoryMoveResponse))]
[JsonSerializable(typeof(InventoryGiveRequest))]
[JsonSerializable(typeof(InventoryGiveResponse))]
[JsonSerializable(typeof(InventoryArmRequest))]
[JsonSerializable(typeof(InventoryArmResponse))]

// ---- spawning ------------------------------------------------------------
[JsonSerializable(typeof(SpawnHandRequest))]
[JsonSerializable(typeof(SpawnHandResponse))]
[JsonSerializable(typeof(SpawnWorldRequest))]
[JsonSerializable(typeof(SpawnWorldResponse))]
[JsonSerializable(typeof(SpawnStructureRequest))]
[JsonSerializable(typeof(SpawnStructureResponse))]
[JsonSerializable(typeof(PrefabsRequest))]
[JsonSerializable(typeof(PrefabsResponse))]

// ---- console -------------------------------------------------------------
[JsonSerializable(typeof(ConsoleLogRequest))]
[JsonSerializable(typeof(ConsoleLogResponse))]
[JsonSerializable(typeof(ConsoleClearRequest))]
[JsonSerializable(typeof(ConsoleClearResponse))]
[JsonSerializable(typeof(ConsoleBufferRequest))]
[JsonSerializable(typeof(ConsoleBufferResponse))]
[JsonSerializable(typeof(ConsoleExecRequest))]
[JsonSerializable(typeof(ConsoleExecResponse))]
[JsonSerializable(typeof(ConsolePrintRequest))]
[JsonSerializable(typeof(ConsolePrintResponse))]
[JsonSerializable(typeof(ConsoleCommandsRequest))]
[JsonSerializable(typeof(ConsoleCommandsResponse))]

// ---- input ---------------------------------------------------------------
[JsonSerializable(typeof(InputKeyRequest))]
[JsonSerializable(typeof(InputKeyResponse))]
[JsonSerializable(typeof(InputScrollRequest))]
[JsonSerializable(typeof(InputScrollResponse))]
[JsonSerializable(typeof(InputMouseRequest))]
[JsonSerializable(typeof(InputMouseResponse))]
[JsonSerializable(typeof(InputMousePositionRequest))]
[JsonSerializable(typeof(InputMousePositionResponse))]
[JsonSerializable(typeof(InputReleaseAllRequest))]
[JsonSerializable(typeof(InputReleaseAllResponse))]
[JsonSerializable(typeof(InputClearRequest))]
[JsonSerializable(typeof(InputClearResponse))]
[JsonSerializable(typeof(InputKeyMapRequest))]
[JsonSerializable(typeof(InputKeyMapResponse))]
[JsonSerializable(typeof(InputEnableRequest))]
[JsonSerializable(typeof(InputEnableResponse))]

// ---- diagnostics ---------------------------------------------------------
[JsonSerializable(typeof(DiagInputRequest))]
[JsonSerializable(typeof(DiagInputResponse))]
[JsonSerializable(typeof(DiagJoinRequest))]
[JsonSerializable(typeof(DiagJoinResponse))]
[JsonSerializable(typeof(ScreenshotRequest))]
[JsonSerializable(typeof(ScreenshotResponse))]

// ---- config --------------------------------------------------------------
[JsonSerializable(typeof(ConfigRequest))]
[JsonSerializable(typeof(ConfigResponse))]
[JsonSerializable(typeof(ConfigSetRequest))]
[JsonSerializable(typeof(ConfigSetResponse))]
[JsonSerializable(typeof(ConfigReloadRequest))]
[JsonSerializable(typeof(ConfigReloadResponse))]

// ---- reflection ----------------------------------------------------------
[JsonSerializable(typeof(ReflectRequest))]
[JsonSerializable(typeof(ReflectResponse))]
[JsonSerializable(typeof(ReflectMembersRequest))]
[JsonSerializable(typeof(ReflectMembersResponse))]
[JsonSerializable(typeof(ReflectInstanceRequest))]
[JsonSerializable(typeof(ReflectInstanceResponse))]
[JsonSerializable(typeof(ThingRequest))]
[JsonSerializable(typeof(ThingResponse))]
[JsonSerializable(typeof(ThingMembersRequest))]
[JsonSerializable(typeof(ThingMembersResponse))]

// ---- DLC entitlement -----------------------------------------------------
[JsonSerializable(typeof(DlcRequest))]
[JsonSerializable(typeof(DlcResponse))]
[JsonSerializable(typeof(DlcRemoveRequest))]
[JsonSerializable(typeof(DlcRemoveResponse))]
[JsonSerializable(typeof(DlcRestoreRequest))]
[JsonSerializable(typeof(DlcRestoreResponse))]

// ---- scenarios -----------------------------------------------------------
[JsonSerializable(typeof(ScenarioRow))]
[JsonSerializable(typeof(ScenarioLine))]
[JsonSerializable(typeof(ScenariosRequest))]
[JsonSerializable(typeof(ScenariosResponse))]
[JsonSerializable(typeof(ScenarioRunRequest))]
[JsonSerializable(typeof(ScenarioRunResponse))]
[JsonSerializable(typeof(ScenarioArmRequest))]
[JsonSerializable(typeof(ScenarioArmResponse))]
[JsonSerializable(typeof(ScenarioDisarmRequest))]
[JsonSerializable(typeof(ScenarioDisarmResponse))]
public sealed partial class RigJsonContext : JsonSerializerContext
{
}

#endif
