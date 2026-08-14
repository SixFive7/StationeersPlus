using System.Text.Json;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Readers;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The fake control plane cannot diverge from the plugin, because it is typed against the
///     same wire contract.
/// </summary>
/// <remarks>
///     The fakery audit found 54 field-level divergences between the PowerShell fake and the
///     real responses, across ten endpoints, with fifteen more endpoints faked not at all. All
///     399 assertions stayed green through every one of them, because the fake's shapes were
///     the test author's rather than the plugin's and nothing anywhere compared the two. These
///     assertions are not about the fake being right; they are about the class of mistake
///     being a compile error now.
/// </remarks>
public sealed class FakeFidelityTests
{
    private static FakeRigTransport Rig()
    {
        var transport = new FakeRigTransport();
        transport.Add("hostie", 27701);
        return transport;
    }

    private static JsonNode Body(TransportResponseLike response) => JsonNode.Parse(response.Body)!;

    private readonly record struct TransportResponseLike(int HttpStatus, string Body);

    private static TransportResponseLike Send(FakeRigTransport transport, string path, string? body = null)
    {
        var response = transport.Send(27701, path, body, TimeSpan.FromSeconds(1));
        return new TransportResponseLike(response.HttpStatus, response.Body);
    }

    [Fact]
    public void EveryStatusFieldTheHarnessReadsIsOnTheContractsRecord()
    {
        var response = Send(Rig(), Endpoints.Status);
        var status = JsonSerializer.Deserialize(response.Body, typeof(StatusResponse), RigJson.Context) as StatusResponse;

        Assert.NotNull(status);
        Assert.True(status.Ok);
        Assert.NotNull(status.Phase);
        Assert.NotNull(status.Role);
    }

    [Fact]
    public void TheStatusResponseCarriesTheFieldsTheTrapDocumentationNames()
    {
        // "Assert on /status.role, never on isClient/isServer": a listen host is
        // NetworkRole.Server and reports isClient false. The old fake could not express the
        // trap at all, so no test could prove the harness avoids it.
        var transport = Rig();
        transport.State("hostie").Hosting = true;
        transport.State("hostie").Role = "listenHost";

        var body = Body(Send(transport, Endpoints.Status));
        Assert.Equal("listenHost", body["role"]!.GetValue<string>());
        Assert.False(body["isClient"]!.GetValue<bool>());
        Assert.True(body["isServer"]!.GetValue<bool>());
    }

    [Fact]
    public void ARosterRowCarriesTheStateFieldThatSeparatesAHalfJoinFromASettledOne()
    {
        var transport = Rig();
        transport.State("hostie").Hosting = true;
        transport.State("hostie").Roster.Add(new ConnectedClient { ClientId = "900000000002", Username = "joiner", State = "settled" });

        var row = Body(Send(transport, Endpoints.Status))["connectedClients"]![0]!;
        Assert.Equal("settled", row["state"]!.GetValue<string>());
        Assert.Equal("900000000002", row["clientId"]!.GetValue<string>());
    }

    [Fact]
    public void AClientIdTravelsAsAStringBecauseANumberLosesPrecisionAboveTwoToTheFiftyThree()
    {
        var transport = Rig();
        transport.State("hostie").Hosting = true;
        transport.State("hostie").Roster.Add(new ConnectedClient { ClientId = "9007199254740993" });

        var row = Body(Send(transport, Endpoints.Status))["connectedClients"]![0]!;
        Assert.Equal(JsonValueKind.String, row["clientId"]!.GetValueKind());
    }

    [Fact]
    public void TheDlcResponsePutsEverythingUnderState()
    {
        // The exact divergence the port plan cites: the old fake answered {ok, owned} at the
        // top level while the real checks read state.owned, state.shared and
        // state.removedOwned.
        var transport = Rig();
        transport.State("hostie").Owned = "MetallicPaints";
        transport.State("hostie").Shared = "MetallicPaints";

        var body = Body(Send(transport, Endpoints.Dlc));
        Assert.Null(body["owned"]);
        Assert.Equal("MetallicPaints", body["state"]!["owned"]!.GetValue<string>());
        Assert.Equal("MetallicPaints", body["state"]!["shared"]!.GetValue<string>());
    }

    [Fact]
    public void OwnedIsACommaJoinedStringAndNotAnArray()
    {
        var transport = Rig();
        transport.State("hostie").Owned = "MetallicPaints,SomethingElse";

        var body = Body(Send(transport, Endpoints.Dlc));
        Assert.Equal(JsonValueKind.String, body["state"]!["owned"]!.GetValueKind());
    }

    [Fact]
    public void RemovingEntitlementReportsWhatItActuallyCleared()
    {
        var transport = Rig();
        transport.State("hostie").Owned = "MetallicPaints";
        transport.State("hostie").BaselineOwned = "MetallicPaints";

        Send(transport, Endpoints.DlcRemove, """{"dlc":"MetallicPaints","scope":"owned"}""");
        var body = Body(Send(transport, Endpoints.Dlc));

        Assert.Equal("MetallicPaints", body["state"]!["removedOwned"]!.GetValue<string>());
        Assert.True(body["state"]!["overridden"]!.GetValue<bool>());
    }

    [Fact]
    public void RestoringEntitlementPutsItBack()
    {
        var transport = Rig();
        transport.State("hostie").Owned = "MetallicPaints";
        transport.State("hostie").BaselineOwned = "MetallicPaints";

        Send(transport, Endpoints.DlcRemove, """{"dlc":"MetallicPaints","scope":"owned"}""");
        Send(transport, Endpoints.DlcRestore, "{}");

        var body = Body(Send(transport, Endpoints.Dlc));
        Assert.Equal("MetallicPaints", body["state"]!["owned"]!.GetValue<string>());
    }

    [Fact]
    public void AThingRowCarriesTheRowLevelCustomColorIndex()
    {
        // The documented workaround for the most expensive trap in the harness: CustomColor is
        // a reference-typed member whose rendering is the bare type name, so matchesPrefab is
        // always true and the field cannot answer the question. The old fake had no
        // customColorIndex at all, so both assertions that use it read absent.
        var transport = Rig();
        transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442, CustomColorIndex = 4 };

        var row = Body(Send(transport, Endpoints.Thing + "?refIds=442&fields=CustomColor"))["things"]![0]!;
        Assert.Equal(4, row["customColorIndex"]!.GetValue<int>());
    }

    [Fact]
    public void AThingRowCarriesTheLocationBlockWithItsAuthoritativeFlag()
    {
        // The single guard that separates "the world state" from "this machine's own view".
        var transport = Rig();
        transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442, Authoritative = true };

        var row = Body(Send(transport, Endpoints.Thing + "?refIds=442&fields=CustomColor"))["things"]![0]!;
        Assert.True(row["location"]!["authoritative"]!.GetValue<bool>());
    }

    [Fact]
    public void AFieldRowCarriesItsValueTypeSoACheckCanTellARenderingFromAValue()
    {
        // "Look at a field's valueType before using it as evidence; if the rendering is a type
        // name rather than a value, the field cannot answer the question."
        var transport = Rig();
        var thing = new FakeThing { ReferenceId = 442 };
        thing.Members["EmissionColor.r"] = "0";
        transport.State("hostie").Things["442"] = thing;

        var field = Body(Send(transport, Endpoints.Thing + "?refIds=442&fields=EmissionColor.r"))["things"]![0]!["fields"]![0]!;
        Assert.NotNull(field["valueType"]);
        Assert.Equal("EmissionColor.r", field["resolvedName"]!.GetValue<string>());
    }

    [Fact]
    public void AQueryLessThingReadIsTheEndpointsOwnFourHundred()
    {
        var response = Send(Rig(), Endpoints.Thing);
        Assert.Equal(400, response.HttpStatus);
        Assert.False(Body(response)["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void AThingReadThatCannotResolveEverythingIsAFourHundredAndNine()
    {
        var response = Send(Rig(), Endpoints.Thing + "?refIds=999&fields=CustomColor");
        Assert.Equal(409, response.HttpStatus);
        Assert.Contains("999", Body(response)["missing"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AConfigResponseCarriesOkAndTheGuidItWasAskedFor()
    {
        // The old fake had no ok field at all and ignored the guid entirely, so the wrong-guid
        // path was untested and would have been diagnosed as a config mismatch.
        var transport = Rig();
        transport.State("hostie").SetConfig("net.example", "Client - Group", "Key", "true");

        var body = Body(Send(transport, Endpoints.Config + "?guid=net.example"));
        Assert.True(body["ok"]!.GetValue<bool>());
        Assert.Equal("net.example", body["guid"]!.GetValue<string>());
        Assert.NotNull(body["configPath"]);
    }

    [Fact]
    public void AnUnknownGuidIsAnInBandFailureAtTwoHundred()
    {
        var response = Send(Rig(), Endpoints.Config + "?guid=net.nothing");
        Assert.Equal(200, response.HttpStatus);
        Assert.False(Body(response)["ok"]!.GetValue<bool>());
        Assert.Equal(RigOutcome.InBandFailure, RigResult<ConfigResponse>.Classify(200, false));
    }

    [Fact]
    public void AConfigEntryRendersItsValueTheWayTheBoxedValueDoes()
    {
        var transport = Rig();
        transport.State("hostie").SetConfig("net.example", "Client - Color Cycling", "Color Cycling", "WithinFamily");

        var entry = Body(Send(transport, Endpoints.Config + "?guid=net.example"))["entries"]![0]!;
        Assert.Equal("WithinFamily", entry["value"]!.GetValue<string>());
        Assert.Equal("String", entry["type"]!.GetValue<string>());
    }

    [Fact]
    public void AConsoleLineIsAnObjectAndNotABareString()
    {
        var transport = Rig();
        transport.State("hostie").Print("console", "a console line");

        var line = Body(Send(transport, Endpoints.ConsoleLog))["lines"]![0]!;
        Assert.Equal("a console line", line["text"]!.GetValue<string>());
        Assert.Equal("console", line["src"]!.GetValue<string>());
        Assert.True(line["seq"]!.GetValue<long>() > 0);
    }

    [Fact]
    public void ANearbyRowNamesItsColourTheWayTheRealOneDoes()
    {
        var transport = Rig();
        transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442, CustomColorIndex = 9 };

        var row = Body(Send(transport, Endpoints.Nearby))["things"]![0]!;
        Assert.Null(row["colorIndex"]);
        Assert.Equal(9, row["customColorIndex"]!.GetValue<int>());
    }

    [Fact]
    public void ThePlayerResponseNestsEverythingUnderPlayer()
    {
        var body = Body(Send(Rig(), Endpoints.Player));
        Assert.NotNull(body["player"]);
        Assert.True(body["player"]!["present"]!.GetValue<bool>());
    }

    [Fact]
    public void APlayerPositionIsAnArray()
    {
        var body = Body(Send(Rig(), Endpoints.Player));
        Assert.Equal(JsonValueKind.Array, body["player"]!["position"]!.GetValueKind());
    }

    [Fact]
    public void EveryEndpointTheShippedChecksDriveIsWiredHere()
    {
        // The PowerShell fake wired ten endpoints and NONE of the eight shipped checks could
        // run end to end against it, not even in simulation: every one drove at least one
        // endpoint that threw "nothing wired for".
        var transport = Rig();
        transport.State("hostie").Things["1"] = new FakeThing { ReferenceId = 1 };

        string[] driven =
        [
            Endpoints.Status, Endpoints.Config, Endpoints.ConfigSet, Endpoints.Thing + "?refIds=1&fields=CustomColor",
            Endpoints.Dlc, Endpoints.DlcRemove, Endpoints.DlcRestore, Endpoints.ConsoleLog, Endpoints.ConsoleExec,
            Endpoints.SpawnStructure, Endpoints.InventoryArm, Endpoints.PlayerUse, Endpoints.CursorForce,
            Endpoints.InputKey, Endpoints.InputMouse, Endpoints.InputScroll, Endpoints.Disconnect, Endpoints.Host,
            Endpoints.Connect, Endpoints.Nearby, Endpoints.Player, Endpoints.Plugins, Endpoints.SavePath,
            Endpoints.Inventory, Endpoints.Reflect, Endpoints.Ping,
        ];

        foreach (var path in driven)
        {
            var exception = Record.Exception(() => transport.Send(27701, path, "{}", TimeSpan.FromSeconds(1)));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void EveryReaderCanActuallyBeExercisedAgainstIt()
    {
        // Four of the thirteen readers could not be exercised at all against the old fake.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.LogFiles.Files[@"E:\rig\instances\hostie\BepInEx\LogOutput.log"] = ["a line"];
        fixture.Transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442 };

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        foreach (var reader in Enum.GetValues<Reader>())
        {
            object? args = reader switch
            {
                Reader.Thing => new ThingRequest { RefIds = "442", Fields = "CustomColor" },
                Reader.BepInExLog => new BepInExLogRequest(),
                Reader.Config => new ConfigRequest { Guid = "net.example" },
                _ => null,
            };

            if (reader == Reader.Config) fixture.Transport.State("hostie").SetConfig("net.example", "s", "k", "v");

            var exception = Record.Exception(() => ctx.Read("hostie", reader, ".", string.Empty, args));
            Assert.Null(exception);
        }
    }

    [Fact]
    public void TheErrorEnvelopeIsOneShapeAtEveryStatus()
    {
        var transport = Rig();
        transport.Refusals[Endpoints.Status] = (1, 409, "refused");

        var response = Send(transport, Endpoints.Status);
        Assert.Equal(409, response.HttpStatus);
        Assert.False(Body(response)["ok"]!.GetValue<bool>());
        Assert.Equal("refused", Body(response)["error"]!.GetValue<string>());
    }
}
