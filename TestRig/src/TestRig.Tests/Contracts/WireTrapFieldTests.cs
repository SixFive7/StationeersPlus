using System.Text.Json;
using TestRig.Contracts;
using Xunit;

namespace TestRig.Tests.Contracts;

/// <summary>
///     Each test here deserializes a literal sample in the plugin's own spelling and
///     asserts the value lands in the right property. They exist because the PowerShell
///     playtest fake invented its own shapes, nothing compared them to the plugin's, and
///     399 assertions stayed green while every real check read a field that was not there.
/// </summary>
public sealed class WireTrapFieldTests
{
    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, RigJson.Options)!;

    // ---- /player: position is an array, and the block is nested -----------

    /// <summary>
    ///     Divergence D-52. The fake emitted <c>position: {x,y,z}</c>, so a selector
    ///     written as <c>position.x</c> worked in every test and read null against the
    ///     live endpoint.
    /// </summary>
    [Fact]
    public void PlayerPositionIsAnArrayNotAnObject()
    {
        var parsed = Parse<PlayerResponse>(
            """
            {"ok":true,"epoch":{"session":3},"player":{"present":true,"referenceId":118,
             "position":[1.5,2.5,3.5],"rotationEuler":[0,90,0],"dead":false}}
            """);

        Assert.True(parsed.Ok);
        Assert.NotNull(parsed.Player);
        Assert.True(parsed.Player!.Present);
        Assert.Equal(new[] { 1.5, 2.5, 3.5 }, parsed.Player.Position);
        Assert.Equal(118L, parsed.Player.ReferenceId);
        Assert.Equal(3L, parsed.Epoch!.Session);
    }

    /// <summary>The fake's own shape must not bind. If it did, this contract would be no stronger than the fake.</summary>
    [Fact]
    public void PlayerPositionRejectsTheFakesObjectForm()
    {
        Assert.Throws<JsonException>(() => Parse<PlayerResponse>(
            """{"ok":true,"player":{"present":true,"position":{"x":1.5,"y":2.5,"z":3.5}}}"""));
    }

    /// <summary>
    ///     Divergence D-51. The fake had no <c>player</c> wrapper at all. Reading the
    ///     player block means reading <c>player</c>, and there is nothing at the top level
    ///     to fall back to.
    /// </summary>
    [Fact]
    public void PlayerBlockIsNestedUnderPlayer()
    {
        var absent = Parse<PlayerResponse>("""{"ok":true,"player":{"present":false}}""");

        Assert.NotNull(absent.Player);
        Assert.False(absent.Player!.Present);
        Assert.Null(absent.Player.Position);
    }

    // ---- clientId travels as a string ------------------------------------

    /// <summary>
    ///     Divergence D-16. Above 2^53 a JSON number parsed through double loses
    ///     precision, so the plugin renders a ClientId as text on <c>/instance</c>,
    ///     <c>/identity</c> and every roster row. The sample below is 2^53 + 1, which is
    ///     the first value a double cannot represent.
    /// </summary>
    [Fact]
    public void InstanceClientIdIsAStringAndSurvivesAboveTwoToTheFiftyThird()
    {
        var parsed = Parse<InstanceResponse>(
            """
            {"ok":true,"instance":{"name":"hostie","port":27701,"role":"host","gamePort":27800,
             "clientId":"9007199254740993","username":"hostie"},
             "effectiveClientId":"9007199254740993"}
            """);

        Assert.Equal("9007199254740993", parsed.Instance!.ClientId);
        Assert.Equal("9007199254740993", parsed.EffectiveClientId);
        Assert.NotEqual(9007199254740993d.ToString("R"), parsed.Instance.ClientId);
    }

    [Fact]
    public void RosterRowClientIdIsAString()
    {
        var parsed = Parse<StatusResponse>(
            """
            {"ok":true,"connectedClients":[
              {"clientId":"900000000002","username":"joiner","state":"Connected",
               "isHost":false,"connectionId":"189151461494586169"}]}
            """);

        ConnectedClient row = Assert.Single(parsed.ConnectedClients!);
        Assert.Equal("900000000002", row.ClientId);
        Assert.Equal("Connected", row.State);
        Assert.Equal("189151461494586169", row.ConnectionId);
    }

    /// <summary>
    ///     <c>connectionId</c> is a RakNet <c>long</c>, and it is a <b>string</b> on the wire
    ///     like the <c>clientId</c> beside it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     This was typed <c>int?</c> and the sample here asserted <c>connectionId == 2</c>,
    ///     which is why 1,688 tests never caught it. The two values below are the ones
    ///     measured on one real join, and both are past 2^53, let alone 2^31: the deserializer
    ///     threw on the value, <c>RigWire.Deserialize</c> returned null for the WHOLE
    ///     <c>/status</c> payload, and four of eight playtest checks reported
    ///     <c>inconclusive (joiner-not-in-roster)</c> against a rig that was joining
    ///     perfectly.
    ///     </para>
    ///     <para>
    ///     A listen host's own row carries <c>"0"</c>, from
    ///     <c>NetworkServer.PopulateHostClient</c>, so the small case has to keep working too.
    ///     Both are asserted here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void RosterConnectionIdIsAStringAndSurvivesTheMeasuredRakNetIds()
    {
        var parsed = Parse<StatusResponse>(
            """
            {"ok":true,"connectedClients":[
              {"clientId":"900000000001","username":"hostie","state":"Connected",
               "isHost":true,"connectionId":"0"},
              {"clientId":"900000000002","username":"joiner","state":"Connected",
               "isHost":false,"connectionId":"189151461494586169"},
              {"clientId":"900000000003","username":"second","state":"Connected",
               "isHost":false,"connectionId":"1044835390751713754"}]}
            """);

        Assert.Equal(3, parsed.ConnectedClients!.Length);

        // The host's own row: small, and still a string.
        Assert.Equal("0", parsed.ConnectedClients[0].ConnectionId);
        Assert.True(parsed.ConnectedClients[0].IsHost);

        // Both measured joiner ids, exact to the last digit. Rendered through a double they
        // would come back as 189151461494586180 and 1044835390751713800.
        Assert.Equal("189151461494586169", parsed.ConnectedClients[1].ConnectionId);
        Assert.Equal("1044835390751713754", parsed.ConnectedClients[2].ConnectionId);

        Assert.NotEqual(189151461494586169d.ToString("R"), parsed.ConnectedClients[1].ConnectionId);
        Assert.NotEqual(1044835390751713754d.ToString("R"), parsed.ConnectedClients[2].ConnectionId);
    }

    /// <summary>
    ///     The old spelling must not bind. A bare number where the contract wants a string is
    ///     the exact shape that took the endpoint down, so it has to be a throw and not a
    ///     tolerated coercion.
    /// </summary>
    [Fact]
    public void RosterConnectionIdRejectsTheOldNumericSpelling()
    {
        Assert.Throws<JsonException>(() => Parse<StatusResponse>(
            """{"ok":true,"connectedClients":[{"clientId":"900000000002","connectionId":2}]}"""));
    }

    /// <summary>
    ///     The asymmetry is real and this assembly reproduces it rather than harmonising
    ///     it: <c>/status.localClientId</c> is a JSON number while the same id is a string
    ///     everywhere else.
    /// </summary>
    [Fact]
    public void StatusLocalClientIdIsANumberUnlikeEveryOtherClientIdField()
    {
        var parsed = Parse<StatusResponse>("""{"ok":true,"localClientId":900000000002}""");

        Assert.Equal(900000000002L, parsed.LocalClientId);
    }

    // ---- /dlc: state.owned, comma-joined, with a mask twin ---------------

    /// <summary>
    ///     Divergences D-01 through D-06 in one sample. The fake answered
    ///     <c>{ok:true, owned:['ExamplePack']}</c>: no state object, <c>owned</c> at the
    ///     top level, and an array where the plugin emits a comma-joined string.
    /// </summary>
    [Fact]
    public void DlcOwnedIsACommaJoinedStringUnderStateWithAnIntegerMaskTwin()
    {
        var parsed = Parse<DlcResponse>(
            """
            {"ok":true,"instance":"hostie","epoch":{"session":4},
             "state":{"ownedMask":6,"owned":"MetallicPaints,ExamplePack",
                      "sharedMask":2,"shared":"MetallicPaints","overridden":true,
                      "baselineOwnedMask":7,"baselineOwned":"BasePack,MetallicPaints,ExamplePack",
                      "baselineSharedMask":3,"baselineShared":"BasePack,MetallicPaints",
                      "removedOwnedMask":1,"removedOwned":"BasePack",
                      "removedSharedMask":1,"removedShared":"BasePack",
                      "baselineSession":4,"removeCalls":1,
                      "ownedFieldReachable":true,"gameInitialized":true},
             "known":[{"name":"BasePack","value":1},{"name":"MetallicPaints","value":2}],
             "direction":"REMOVAL ONLY.","sequence":["remove before world entry"]}
            """);

        DlcState state = Assert.IsType<DlcState>(parsed.State);

        Assert.Equal("MetallicPaints,ExamplePack", state.Owned);
        Assert.Equal(6, state.OwnedMask);
        Assert.Equal("MetallicPaints", state.Shared);
        Assert.Equal(2, state.SharedMask);
        Assert.Equal("BasePack", state.RemovedOwned);
        Assert.Equal(1, state.RemovedOwnedMask);
        Assert.True(state.Overridden);
        Assert.True(state.GameInitialized);
        Assert.True(state.OwnedFieldReachable);
        Assert.Equal("hostie", parsed.Instance);
        Assert.Equal(2, parsed.Known!.Length);
        Assert.Equal(4L, parsed.Epoch!.Session);
    }

    /// <summary>
    ///     The eight baseline and removed members are absent until the first removal.
    ///     Nullable value types keep "never removed anything" distinguishable from
    ///     "removed nothing".
    /// </summary>
    [Fact]
    public void DlcBaselineMembersAreAbsentBeforeTheFirstRemoval()
    {
        var parsed = Parse<DlcResponse>(
            """
            {"ok":true,"state":{"ownedMask":7,"owned":"BasePack,MetallicPaints,ExamplePack",
             "sharedMask":0,"shared":"None","overridden":false,
             "ownedFieldReachable":true,"gameInitialized":true}}
            """);

        Assert.False(parsed.State!.Overridden);
        Assert.Null(parsed.State.RemovedOwned);
        Assert.Null(parsed.State.RemovedOwnedMask);
        Assert.Null(parsed.State.BaselineSession);
        Assert.Equal("None", parsed.State.Shared);
    }

    /// <summary>
    ///     <c>baselineSession</c> is a copy of <c>epoch.session</c>, so it is a <c>long</c>
    ///     here as it is there.
    /// </summary>
    /// <remarks>
    ///     It was <c>int?</c> on both DLC records while <c>EpochBlock.Session</c> was
    ///     <c>long</c>, which is the same defect class as <c>connectionId</c>: one wire type
    ///     narrower than the value's own source. The counter never approaches either bound in
    ///     practice, so this asserts the agreement rather than an observed overflow.
    /// </remarks>
    [Fact]
    public void DlcBaselineSessionIsALongLikeTheEpochSessionItCopies()
    {
        var parsed = Parse<DlcResponse>(
            """
            {"ok":true,"epoch":{"session":4294967296},
             "state":{"ownedMask":6,"owned":"MetallicPaints","sharedMask":0,"shared":"None",
                      "overridden":true,"baselineSession":4294967296,"removeCalls":1,
                      "ownedFieldReachable":true,"gameInitialized":true}}
            """);

        Assert.Equal(4294967296L, parsed.Epoch!.Session);
        Assert.Equal(4294967296L, parsed.State!.BaselineSession);

        var restore = Parse<DlcRestoreResponse>(
            """{"ok":true,"instance":"joiner","baselineSession":4294967296}""");
        Assert.Equal(4294967296L, restore.BaselineSession);
    }

    /// <summary>The pre-initialisation refusal carries <c>gameInitialized</c> at the top level as well as inside state.</summary>
    [Fact]
    public void DlcRemoveRefusalBeforeInitCarriesGameInitializedAtTheTopLevel()
    {
        var parsed = Parse<DlcRemoveResponse>(
            """
            {"ok":false,"instance":"joiner","gameInitialized":false,
             "error":"refusing to remove entitlement before the game has initialised.",
             "state":{"ownedMask":0,"owned":"None","sharedMask":0,"shared":"None",
                      "overridden":false,"ownedFieldReachable":true,"gameInitialized":false}}
            """);

        Assert.False(parsed.Ok);
        Assert.False(parsed.GameInitialized);
        Assert.False(parsed.State!.GameInitialized);
        Assert.Null(parsed.Owned);
        Assert.Null(parsed.Shared);
    }

    [Fact]
    public void DlcRemoveScopeDeltaCarriesAMaskTwinOnEveryName()
    {
        var parsed = Parse<DlcRemoveResponse>(
            """
            {"ok":true,"instance":"joiner","requestedMask":1,"requested":"BasePack","scope":"owned",
             "owned":{"beforeMask":7,"before":"BasePack,MetallicPaints,ExamplePack",
                      "afterMask":6,"after":"MetallicPaints,ExamplePack",
                      "clearedMask":1,"cleared":"BasePack",
                      "alreadyAbsentMask":0,"alreadyAbsent":"None"},
             "scopeWarning":null}
            """);

        DlcScopeDelta owned = Assert.IsType<DlcScopeDelta>(parsed.Owned);
        Assert.Equal(7, owned.BeforeMask);
        Assert.Equal(6, owned.AfterMask);
        Assert.Equal("BasePack", owned.Cleared);
        Assert.Equal(1, owned.ClearedMask);
        Assert.Equal("None", owned.AlreadyAbsent);
        Assert.Equal("owned", parsed.Scope);
        Assert.Null(parsed.Shared);
    }

    // ---- /console/log: rows, not bare strings ----------------------------

    /// <summary>
    ///     Divergence D-41. The fake emitted bare strings. A check wanting
    ///     <c>lines[0].level</c> could not be written against it, let alone tested.
    /// </summary>
    [Fact]
    public void ConsoleLogLinesAreRowObjects()
    {
        var parsed = Parse<ConsoleLogResponse>(
            """
            {"ok":true,"nextSeq":42,"dropped":0,"truncated":1,"bufferedLines":12,
             "bufferedChars":840,"count":2,
             "lines":[{"seq":40,"t":123.5,"src":"console","level":"action","text":"first"},
                      {"seq":41,"t":124.0,"src":"bepinex","level":"Info","text":"second","truncated":true}]}
            """);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(42L, parsed.NextSeq);
        Assert.Equal(40L, parsed.Lines![0].Seq);
        Assert.Equal("console", parsed.Lines[0].Src);
        Assert.Equal("action", parsed.Lines[0].Level);
        Assert.Equal("first", parsed.Lines[0].Text);
        Assert.Null(parsed.Lines[0].Truncated);
        Assert.Equal("bepinex", parsed.Lines[1].Src);
        Assert.True(parsed.Lines[1].Truncated);
        Assert.Equal(124.0, parsed.Lines[1].T);
    }

    [Fact]
    public void ConsoleLogRejectsTheFakesBareStringLines()
    {
        Assert.Throws<JsonException>(() => Parse<ConsoleLogResponse>(
            """{"ok":true,"nextSeq":1,"count":1,"lines":["[Example] a console line"]}"""));
    }

    /// <summary>The console-log payload with <c>command</c> spliced in, which is why the type derives rather than restating.</summary>
    [Fact]
    public void ConsoleExecIsAConsoleLogPayloadPlusCommand()
    {
        var parsed = Parse<ConsoleExecResponse>(
            """
            {"ok":true,"command":"dlc shared","nextSeq":9,"dropped":0,"truncated":0,
             "bufferedLines":3,"bufferedChars":90,"count":1,
             "lines":[{"seq":8,"t":1.0,"src":"console","level":"info","text":"None"}]}
            """);

        Assert.Equal("dlc shared", parsed.Command);
        Assert.Equal(1, parsed.Count);
        Assert.Equal("None", parsed.Lines![0].Text);
        Assert.IsAssignableFrom<ConsoleLogResponse>(parsed);
    }

    /// <summary>The game's own ring is a different row shape: <c>i</c>, <c>time</c>, <c>color</c>, <c>text</c>.</summary>
    [Fact]
    public void ConsoleBufferRowsAreNotConsoleLogRows()
    {
        var parsed = Parse<ConsoleBufferResponse>(
            """
            {"ok":true,"count":1,"bufferSize":1024,
             "lines":[{"i":0,"time":"12:01:02","color":3,"text":"newest first"}]}
            """);

        Assert.Equal(0, parsed.Lines![0].I);
        Assert.Equal("12:01:02", parsed.Lines[0].Time);
        Assert.Equal(3u, parsed.Lines[0].Color);
        Assert.Equal(1024, parsed.BufferSize);
    }

    /// <summary>
    ///     A console row's <c>color</c> is an <b>unsigned</b> packed ImGui colour, and every
    ///     opaque one is past <c>int.MaxValue</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     The same defect as <c>connectionId</c>, found by auditing the assembly after it:
    ///     <c>ConsoleWindow.ConsoleLine.Color</c> is a <c>uint</c>, the plugin writes it as a
    ///     bare number, and this was typed <c>int</c>. The alpha byte sets the high bit, so
    ///     the DEFAULT colour alone overflowed and one row was enough to take the whole
    ///     <c>/console/buffer</c> response down.
    ///     </para>
    ///     <para>
    ///     The sample above uses 3, which fits an <c>int</c>, which is precisely why nothing
    ///     caught it. Both bounds are asserted here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ConsoleBufferColorSurvivesAnOpaquePackedColourAboveIntMaxValue()
    {
        var parsed = Parse<ConsoleBufferResponse>(
            """
            {"ok":true,"count":3,"bufferSize":1024,
             "lines":[{"i":0,"time":"12:01:02","color":4289374890,"text":"the default grey"},
                      {"i":1,"time":"12:01:01","color":4294901760,"text":"opaque red"},
                      {"i":2,"time":"12:01:00","color":0,"text":"headless, where ImGui is stubbed"}]}
            """);

        Assert.Equal(4289374890u, parsed.Lines![0].Color);
        Assert.Equal(4294901760u, parsed.Lines[1].Color);
        Assert.Equal(0u, parsed.Lines[2].Color);

        // Both of the first two are past int.MaxValue, which is the whole point.
        Assert.True(parsed.Lines[0].Color > int.MaxValue);
        Assert.True(parsed.Lines[1].Color > int.MaxValue);
    }

    // ---- /nearby: customColorIndex, not colorIndex -----------------------

    /// <summary>
    ///     Divergence D-46. The fake called the field <c>colorIndex</c>. A name divergence
    ///     that reads as an absent field is the hardest kind to notice, so this test binds
    ///     the real name and proves the fake's name does not bind.
    /// </summary>
    [Fact]
    public void NearbyRowsUseCustomColorIndex()
    {
        var parsed = Parse<NearbyResponse>(
            """
            {"ok":true,"epoch":{"session":2},"scanned":180,"count":1,
             "things":[{"referenceId":442,"prefabName":"StructureWall","type":"Structure",
                        "distance":3.25,"position":[1,2,3],"paintable":true,
                        "customColorIndex":4,"inSlot":false}]}
            """);

        NearbyThingRow row = Assert.Single(parsed.Things!);
        Assert.Equal(4, row.CustomColorIndex);
        Assert.Equal(442L, row.ReferenceId);
        Assert.Equal(3.25, row.Distance);
        Assert.Equal(180, parsed.Scanned);
        Assert.Equal(1, parsed.Count);
    }

    [Fact]
    public void NearbyRowsDoNotBindTheFakesColorIndexSpelling()
    {
        var parsed = Parse<NearbyResponse>(
            """{"ok":true,"things":[{"referenceId":442,"colorIndex":4}]}""");

        Assert.Null(Assert.Single(parsed.Things!).CustomColorIndex);
    }

    /// <summary>Divergence D-49. Reading <c>/nearby</c> at the menu is <c>ok:false</c> with an error, not an empty list.</summary>
    [Fact]
    public void NearbyAtTheMenuIsAnOkFalseShape()
    {
        var parsed = Parse<NearbyResponse>("""{"ok":false,"error":"no local player"}""");

        Assert.False(parsed.Ok);
        Assert.Equal("no local player", parsed.Error);
        Assert.Null(parsed.Things);
    }

    // ---- /thing: valueType, customColorIndex, location -------------------

    /// <summary>
    ///     Divergences D-31, D-32, D-33 and D-35 in one sample. <c>valueType</c> is the
    ///     field that says whether a rendered value answers the question at all:
    ///     <c>Thing.CustomColor</c> renders as the type name with
    ///     <c>matchesPrefab:true</c> whether painted or not, and a campaign spent a day on
    ///     a mod defect that did not exist because of it.
    /// </summary>
    [Fact]
    public void ThingFieldRowsCarryValueTypeAndTheRowCarriesCustomColorIndexAndLocation()
    {
        var parsed = Parse<ThingResponse>(
            """
            {"ok":true,"instance":"hostie","epoch":{"session":5},
             "requested":1,"found":1,"missing":[],
             "things":[{"instance":"hostie","requestedRefId":"442","found":true,
               "referenceId":442,"prefabName":"ItemSprayCan","type":"SprayCan",
               "typeFullName":"Assets.Scripts.Objects.Items.SprayCan","assembly":"Assembly-CSharp",
               "displayName":"Spray Can","position":[1,2,3],"paintable":true,"customColorIndex":7,
               "location":{"authoritative":true,"canBeInSlot":true,"inSlot":true,
                 "whereIs":"in Bob's left hand","slotIndex":0,"slotKey":"LeftHand",
                 "slotType":"Tool","isHandSlot":true,"parentId":118,"parentType":"Human",
                 "parentName":"Bob","parentPrefab":"Human","parentClientId":"900000000002",
                 "handSide":"left","parentIsLocalPlayer":true,"isActiveHand":true,
                 "chain":[{"referenceId":118,"type":"Human","prefabName":"Human","slotKey":"LeftHand"}],
                 "rootId":118,"rootType":"Human","rootName":"Bob"},
               "fields":[{"name":"CustomColor","ok":true,"kind":"property",
                 "resolvedName":"CustomColor","declaredBy":"Thing","declaredType":"ColorSwatch",
                 "isNull":false,"value":"Assets.Scripts.Objects.ColorSwatch",
                 "valueJson":"Assets.Scripts.Objects.ColorSwatch",
                 "valueType":"ColorSwatch","matchesPrefab":true}]}]}
            """);

        ThingRow row = Assert.Single(parsed.Things!);

        Assert.Equal(7, row.CustomColorIndex);
        Assert.Equal("442", row.RequestedRefId);
        Assert.Equal(442L, row.ReferenceId);

        LocationBlock location = Assert.IsType<LocationBlock>(row.Location);
        Assert.True(location.Authoritative);
        Assert.True(location.InSlot);
        Assert.Equal("left", location.HandSide);
        Assert.Equal("900000000002", location.ParentClientId);
        Assert.Equal(118L, location.RootId);
        Assert.Equal("LeftHand", Assert.Single(location.Chain!).SlotKey);

        ThingFieldRow field = Assert.Single(row.Fields!);
        Assert.Equal("ColorSwatch", field.ValueType);
        Assert.Equal("CustomColor", field.ResolvedName);
        Assert.Equal("Thing", field.DeclaredBy);
        Assert.True(field.MatchesPrefab);
        Assert.False(field.IsNull);
    }

    /// <summary>
    ///     <c>matchesPrefab</c> is nullable because <c>comparePrefab=false</c> makes it
    ///     null, and a null must not read as "differs".
    /// </summary>
    [Fact]
    public void MatchesPrefabIsNullableRatherThanFalseByDefault()
    {
        var parsed = Parse<ThingResponse>(
            """
            {"ok":true,"requested":1,"found":1,
             "things":[{"requestedRefId":"442","found":true,
               "fields":[{"name":"CustomColor","ok":true,"isNull":false,"matchesPrefab":null}]}]}
            """);

        Assert.Null(Assert.Single(Assert.Single(parsed.Things!).Fields!).MatchesPrefab);
    }

    /// <summary>Divergence D-37. A missing Thing is a row with <c>found:false</c> and a per-row error, and the whole response is 409.</summary>
    [Fact]
    public void ThingMissingIdsAreStringsAndTheRowCarriesItsOwnError()
    {
        var parsed = Parse<ThingResponse>(
            """
            {"ok":false,"requested":2,"found":1,"missing":["999"],
             "things":[{"requestedRefId":"442","found":true,"referenceId":442},
                       {"requestedRefId":"999","found":false,"error":"no Thing with reference id 999"}]}
            """);

        Assert.False(parsed.Ok);
        Assert.Equal(new[] { "999" }, parsed.Missing);
        Assert.Equal(2, parsed.Requested);
        Assert.Equal(1, parsed.Found);
        Assert.False(parsed.Things![1].Found);
        Assert.Equal("no Thing with reference id 999", parsed.Things[1].Error);
    }

    // ---- the value block's shape-shifting valueJson ----------------------

    /// <summary>
    ///     <c>valueJson</c> is genuinely polymorphic: a bool, a number, a string, or an
    ///     array for a vector or a colour. It is typed as a raw element so a caller can
    ///     inspect it without the deserializer guessing.
    /// </summary>
    [Fact]
    public void ValueJsonAcceptsEveryShapeThePluginEmits()
    {
        var boolean = Parse<ReflectResponse>(
            """{"ok":true,"type":"X","member":"Flag","kind":"field","isNull":false,"value":"True","valueJson":true,"valueType":"Boolean"}""");
        Assert.Equal(JsonValueKind.True, boolean.ValueJson!.Value.ValueKind);
        Assert.Equal("True", boolean.Value);

        var colour = Parse<ReflectResponse>(
            """{"ok":true,"isNull":false,"value":"1,1,1,1","valueJson":[1,1,1,1],"valueType":"Color"}""");
        Assert.Equal(JsonValueKind.Array, colour.ValueJson!.Value.ValueKind);
        Assert.Equal(4, colour.ValueJson.Value.GetArrayLength());

        var integer = Parse<ReflectResponse>(
            """{"ok":true,"isNull":false,"value":"9007199254740993","valueJson":"9007199254740993","valueType":"Int64"}""");
        Assert.Equal(JsonValueKind.String, integer.ValueJson!.Value.ValueKind);
        Assert.Equal("9007199254740993", integer.Value);
    }

    /// <summary>An expanded dictionary is a list of value blocks that each carry their own key, and it nests.</summary>
    [Fact]
    public void ValueBlockIsRecursiveForExpandedDictionariesAndKeyProbes()
    {
        var parsed = Parse<ReflectResponse>(
            """
            {"ok":true,"type":"Registry","member":"ByReferenceId","kind":"field",
             "isNull":false,"count":2,"value":"Dictionary`2 count=2","valueType":"Dictionary`2",
             "key":"442","containsKey":true,
             "keyValue":{"isNull":false,"value":"ItemSprayCan #442","valueType":"SprayCan",
                         "referenceId":"442","prefabName":"ItemSprayCan"},
             "entries":[{"key":"442","isNull":false,"value":"a","valueType":"String"},
                        {"key":"445","isNull":false,"value":"b","valueType":"String"}]}
            """);

        Assert.Equal(2, parsed.Count);
        Assert.True(parsed.ContainsKey);
        Assert.Equal("442", parsed.Key);
        Assert.Equal("SprayCan", parsed.KeyValue!.ValueType);
        Assert.Equal("442", parsed.KeyValue.ReferenceId);
        Assert.Equal(2, parsed.Entries!.Length);
        Assert.Equal("445", parsed.Entries[1].Key);
    }

    /// <summary>
    ///     A reference id inside a value block is a <b>string</b>, while the same id on a
    ///     Thing row is a number. Both spellings are reproduced rather than harmonised.
    /// </summary>
    [Fact]
    public void ReferenceIdIsAStringInAValueBlockAndANumberOnAThingRow()
    {
        var block = Parse<ReflectResponse>(
            """{"ok":true,"isNull":false,"value":"ItemSprayCan #442","valueType":"SprayCan","referenceId":"442"}""");
        Assert.Equal("442", block.ReferenceId);

        var row = Parse<ThingResponse>(
            """{"ok":true,"things":[{"requestedRefId":"442","found":true,"referenceId":442}]}""");
        Assert.Equal(442L, Assert.Single(row.Things!).ReferenceId);
    }

    // ---- assorted spellings that are easy to get wrong -------------------

    /// <summary><c>default</c> is a C# keyword, so the property name and the wire name differ here on purpose.</summary>
    [Fact]
    public void ConfigEntryRowBindsTheDefaultKeyword()
    {
        var parsed = Parse<ConfigResponse>(
            """
            {"ok":true,"guid":"net.example","configPath":"C:\\rig\\net.example.cfg","count":1,
             "entries":[{"section":"Client - Visual","key":"Beam Color","type":"String",
                         "value":"Cyan","default":"White","description":"(Client-local) ..."}]}
            """);

        ConfigEntryRow entry = Assert.Single(parsed.Entries!);
        Assert.Equal("White", entry.Default);
        Assert.Equal("Cyan", entry.Value);
        Assert.Equal("Client - Visual", entry.Section);
        Assert.Equal("net.example", parsed.Guid);
    }

    /// <summary>
    ///     Divergence D-27. A bool setting renders as <c>"True"</c>/<c>"False"</c> and an
    ///     enum as its member NAME, because the plugin calls <c>ToString()</c> on the boxed
    ///     value. The fake rendered every value as the literal <c>'x'</c>.
    /// </summary>
    [Fact]
    public void ConfigValuesAreRenderedTextNotJsonTypes()
    {
        var parsed = Parse<ConfigResponse>(
            """
            {"ok":true,"guid":"net.example","count":2,
             "entries":[{"section":"S","key":"Flag","type":"Boolean","value":"True","default":"False"},
                        {"section":"S","key":"Mode","type":"PaintScope","value":"WithinFamily","default":"Off"}]}
            """);

        Assert.Equal("True", parsed.Entries![0].Value);
        Assert.Equal("WithinFamily", parsed.Entries[1].Value);
    }

    /// <summary>Divergence D-14 and D-15: the roster is the server's answer, and rows carry <c>state</c>.</summary>
    [Fact]
    public void HostSuccessCarriesTheRosterAndTheJoinTarget()
    {
        var parsed = Parse<HostResponse>(
            """
            {"ok":true,"role":"listenHost","hosting":true,"hostPort":27800,"serverName":"hostie",
             "hasPassword":false,"world":"Lunar","save":null,"savePath":"C:\\rig\\hostie\\saves",
             "saveRoot":"instance","localClientId":"900000000001","username":"hostie",
             "playersInGame":1,"connectedClients":[],"joinWith":"127.0.0.1:27800"}
            """);

        Assert.True(parsed.Ok);
        Assert.Equal("listenHost", parsed.Role);
        Assert.True(parsed.Hosting);
        Assert.Equal("instance", parsed.SaveRoot);
        Assert.Equal("900000000001", parsed.LocalClientId);
        Assert.Empty(parsed.ConnectedClients!);
        Assert.Equal("127.0.0.1:27800", parsed.JoinWith);
    }

    /// <summary>Divergence D-18. The stage-2 failure shape has never been exercised by anything.</summary>
    [Fact]
    public void HostStageTwoFailureCarriesResultNotHostingAndAConsoleTail()
    {
        var parsed = Parse<HostResponse>(
            """
            {"ok":false,"result":"notHosting","hosting":false,"role":"singlePlayer",
             "requestedPort":27800,"world":"Lunar","save":null,
             "error":"the world is up but NetworkServer.IsHosting is false",
             "consoleTail":["line one","line two"]}
            """);

        Assert.False(parsed.Ok);
        Assert.Equal("notHosting", parsed.Result);
        Assert.False(parsed.Hosting);
        Assert.Equal(27800, parsed.RequestedPort);
        Assert.Equal(2, parsed.ConsoleTail!.Length);
    }

    /// <summary>Divergence D-21. The duplicate-identity refusal is a distinct shape with <c>peers</c> and <c>override</c>.</summary>
    [Fact]
    public void ConnectDuplicateIdentityRefusalBindsPeersAndOverride()
    {
        var parsed = Parse<ConnectResponse>(
            """
            {"ok":false,"error":"refusing to join: ClientId 900000000001 is also claimed by hostie on 27701.",
             "peers":{"conflictDetected":true,"conflict":"hostie on 27701","lastScanUtc":"2026-08-14T09:00:00Z",
                      "peers":[{"port":27701,"reachable":true,"name":"hostie",
                                "clientId":"900000000001","conflicts":true,"error":null}],
                      "peerCount":1},
             "override":"pass allowDuplicateIdentity=true to join anyway"}
            """);

        Assert.False(parsed.Ok);
        Assert.True(parsed.Peers!.ConflictDetected);
        Assert.Equal("900000000001", Assert.Single(parsed.Peers.Peers!).ClientId);
        Assert.StartsWith("pass allowDuplicateIdentity=true", parsed.Override);
    }

    /// <summary>The save entry shape follows the game's own type, so a row is member name to rendered text.</summary>
    [Fact]
    public void SavesRowsAreStringMaps()
    {
        var parsed = Parse<SavesResponse>(
            """
            {"ok":true,"count":1,
             "saves":[{"StationName":"Lunar Base","WorldName":"Lunar","LastWrite":"2026-08-14","Corrupt":null}]}
            """);

        Dictionary<string, string?> row = Assert.Single(parsed.Saves!);
        Assert.Equal("Lunar Base", row["StationName"]);
        Assert.Null(row["Corrupt"]);
    }

    /// <summary>
    ///     <c>consumed = delivered AND gate.open</c> for a key, and
    ///     <c>consumed = delivered AND gate.checkDisplaySlotInputRan &gt; 0</c> for a
    ///     scroll. Two different rules, two different gate blocks.
    /// </summary>
    [Fact]
    public void InputGateBlocksDifferBetweenKeyAndScroll()
    {
        var key = Parse<InputKeyResponse>(
            """
            {"ok":true,"instance":"hostie","key":"F","resolvedVia":"KeyMap.UseItem","mode":"tap",
             "frames":3,"consumed":true,"delivered":true,
             "observed":{"getKey":3,"getKeyDown":1,"getKeyUp":1},
             "gate":{"open":true,"shutReason":null,"cursorVisible":false,"consoleOpen":false,
                     "keyInputState":"Normal","keyMapPollRan":3,"inventoryManagerUpdateRan":3,
                     "normalModeRan":3},
             "settled":true,"settledMeans":"the requested frames elapsed"}
            """);

        Assert.True(key.Consumed);
        Assert.Equal(3, key.Gate!.KeyMapPollRan);
        Assert.Equal(1, key.Observed!.GetKeyDown);

        var scroll = Parse<InputScrollResponse>(
            """
            {"ok":true,"instance":"hostie","notches":1,"frames":1,"repeat":1,
             "consumed":true,"delivered":true,"scrollReads":1,
             "gate":{"open":true,"shutReason":null,"cursorVisible":false,"consoleOpen":false,
                     "checkDisplaySlotInputRan":1,"normalModeRan":1}}
            """);

        Assert.True(scroll.Consumed);
        Assert.Equal(1, scroll.Gate!.CheckDisplaySlotInputRan);
    }

    /// <summary><c>/input/mouse</c> answers the <c>/input/key</c> shape because the router delegates into the same handler.</summary>
    [Fact]
    public void InputMouseResponseIsTheInputKeyShape()
    {
        var parsed = Parse<InputMouseResponse>(
            """{"ok":true,"instance":"hostie","key":"Mouse0","resolvedVia":"KeyCode","mode":"tap","frames":3,"consumed":true,"delivered":true,"settled":true}""");

        Assert.IsAssignableFrom<InputKeyResponse>(parsed);
        Assert.Equal("Mouse0", parsed.Key);
        Assert.True(parsed.Consumed);
    }

    /// <summary>The chain keys carry dots because they are <c>Type.Method</c> as the probe names them.</summary>
    [Fact]
    public void DiagInputChainKeysCarryDots()
    {
        var parsed = Parse<DiagInputResponse>(
            """
            {"ok":true,"instance":"hostie","frame":900,
             "patches":{"patchUnityInput":true,"inputInjectionEnabled":true,"getKey":true,
                        "getKeyDown":true,"mouseScrollDelta":true},
             "chain":{"GameManager.Update":{"enter":900,"exit":900,"unbalanced":0,"lastEnterFrame":900},
                      "KeyMap.PollInputs":{"enter":880,"exit":880,"unbalanced":0,"lastEnterFrame":900},
                      "installed":["GameManager.Update","KeyMap.PollInputs"],"lastError":null},
             "heldKeys":"F,LeftShift"}
            """);

        Assert.Equal(900L, parsed.Chain!.GameManagerUpdate!.Enter);
        Assert.Equal(0L, parsed.Chain.KeyMapPollInputs!.Unbalanced);
        Assert.Equal(2, parsed.Chain.Installed!.Length);
        Assert.Equal("F,LeftShift", parsed.HeldKeys);
        Assert.Null(parsed.Chain.InventoryManagerNormalMode);
    }

    /// <summary>
    ///     A slot has three wire states and all three must be distinguishable: no such
    ///     slot, an empty slot, and an occupied one.
    /// </summary>
    [Fact]
    public void SlotOccupantDistinguishesAbsentFromEmptyFromOccupied()
    {
        var absent = Parse<PlayerResponse>("""{"ok":true,"player":{"present":true,"activeHand":null}}""");
        Assert.Null(absent.Player!.ActiveHand);

        var empty = Parse<PlayerResponse>("""{"ok":true,"player":{"present":true,"activeHand":{"empty":true}}}""");
        Assert.True(empty.Player!.ActiveHand!.Empty);
        Assert.Null(empty.Player.ActiveHand.ReferenceId);

        var full = Parse<PlayerResponse>(
            """
            {"ok":true,"player":{"present":true,"activeHand":{"empty":false,"referenceId":442,
             "prefabName":"ItemSprayCan","displayName":"Spray Can","type":"SprayCan","quantity":1,
             "isSprayCan":true,"paintColorIndex":4,"paintMaterial":"PaintYellow",
             "paintColorName":"Yellow"}}}
            """);
        Assert.False(full.Player!.ActiveHand!.Empty);
        Assert.True(full.Player.ActiveHand.IsSprayCan);
        Assert.Equal(4, full.Player.ActiveHand.PaintColorIndex);
        Assert.Null(full.Player.ActiveHand.IsSprayGun);
    }

    /// <summary>Divergence D-08. The epoch block is what makes two readings across a world transition distinguishable.</summary>
    [Fact]
    public void EpochBlockBindsEveryMemberIncludingTheStalenessWarning()
    {
        var parsed = Parse<StatusResponse>(
            """
            {"ok":true,"epoch":{"instance":"hostie","port":27701,"session":7,"phase":"inWorld",
             "gameState":"Running","role":"listenHost","networkRole":"Server","networkState":"Connected",
             "hosting":true,"hostPort":27800,"authoritative":true,"worldId":"lunar-1","clients":1,
             "frame":9000,"sampledSecondsAgo":12.5,"stale":true,"sessionChangedAtFrame":4000,
             "sessionChangedSecondsAgo":90.25,"lastChange":"gameState None -> Running; hosting false -> true",
             "warning":"the epoch cache is stale"}}
            """);

        EpochBlock epoch = Assert.IsType<EpochBlock>(parsed.Epoch);
        Assert.Equal(7L, epoch.Session);
        Assert.Equal("listenHost", epoch.Role);
        Assert.True(epoch.Stale);
        Assert.Equal(12.5, epoch.SampledSecondsAgo);
        Assert.Equal("the epoch cache is stale", epoch.Warning);
    }
}
