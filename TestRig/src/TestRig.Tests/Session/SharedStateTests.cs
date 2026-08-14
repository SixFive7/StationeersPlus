using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The shared per-user state: read, compared and reported, never restored.
/// </summary>
/// <remarks>
/// RESET-136 to RESET-152, RESET-004, RESET-009 and CLI-070. The rig cannot isolate
/// <c>PlayerCookie-v2.xml</c>, the PlayerPrefs key or <c>Blueprints\</c> from the developer's
/// own client, because Unity fixes <c>persistentDataPath</c> inside the serialized
/// PlayerSettings. Naming what moved at the session boundary is therefore the whole
/// mechanism, and RESET-143 calls it the honest half of the guarantee.
/// </remarks>
public sealed class SharedStateTests
{
    private static string Cookie(RigFixture rig) => Path.Combine(RigFixture.SharedData, "PlayerCookie-v2.xml");

    private static string Blueprint(string leaf) =>
        Path.Combine(RigFixture.SharedData, "Blueprints", leaf);

    // ---- the snapshot ------------------------------------------------------

    [Fact]
    public void TheCookieContributesItsSizeAndItsWorldCount()
    {
        // RESET-137.
        var rig = new RigFixture();
        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\"/><World name=\"b\" /><Other/></Root>");

        var values = rig.SharedState.Capture().Values;

        Assert.Equal("2", values["cookie.worlds"]);
        Assert.NotEqual("absent", values["cookie.bytes"]);
        Assert.NotEqual("unreadable", values["cookie.bytes"]);
    }

    [Fact]
    public void AnAbsentCookieSaysAbsentAndAnUnreadableOneSaysUnreadableWithNoWorldCount()
    {
        var rig = new RigFixture();
        Assert.Equal("absent", rig.SharedState.Capture().Values["cookie.bytes"]);

        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\" /></Root>");
        rig.Fs.ReadFailures[Path.GetFullPath(Cookie(rig))] = "locked by the game";

        var values = rig.SharedState.Capture().Values;

        Assert.Equal("unreadable", values["cookie.bytes"]);

        // No count at all, rather than zero: an unreadable cookie must not look like a cookie
        // with no worlds in it.
        Assert.False(values.ContainsKey("cookie.worlds"));
    }

    [Fact]
    public void EveryPlayerPrefsValueIsRecordedUnderItsOwnKey()
    {
        // RESET-138. The registry seam renders a REG_BINARY as bytes[N], which is what Unity
        // stores every PlayerPrefs string as; without it the snapshot is a wall of hex.
        var rig = new RigFixture();
        rig.Registry
            .Set(SharedStateReader.DefaultPlayerPrefsKey, "Screenmanager Resolution Width_h182942802", "2560")
            .Set(SharedStateReader.DefaultPlayerPrefsKey, "LastUsedName_h1122334455", "bytes[24]");

        var values = rig.SharedState.Capture().Values;

        Assert.Equal("2560", values["prefs.Screenmanager Resolution Width_h182942802"]);
        Assert.Equal("bytes[24]", values["prefs.LastUsedName_h1122334455"]);
    }

    [Fact]
    public void AValueLongerThanTwoHundredCharactersIsStoredAsAHash()
    {
        // RESET-139. The snapshot exists to spot a CHANGE, and a hash still changes when the
        // value does.
        var rig = new RigFixture();
        var long1 = new string('x', 400);
        var long2 = new string('x', 399) + "y";
        rig.Registry.Set(SharedStateReader.DefaultPlayerPrefsKey, "Blob", long1);

        var first = rig.SharedState.Capture().Values["prefs.Blob"];
        Assert.StartsWith("sha256:", first, StringComparison.Ordinal);
        Assert.Equal(7 + 16, first.Length);

        rig.Registry.Set(SharedStateReader.DefaultPlayerPrefsKey, "Blob", long2);
        Assert.NotEqual(first, rig.SharedState.Capture().Values["prefs.Blob"]);

        // And exactly at the boundary the value is kept whole.
        rig.Registry.Set(SharedStateReader.DefaultPlayerPrefsKey, "Blob", new string('y', 200));
        Assert.Equal(new string('y', 200), rig.SharedState.Capture().Values["prefs.Blob"]);
    }

    [Fact]
    public void AnUnreadablePlayerPrefsKeyCollapsesToOneEntry()
    {
        // RESET-140.
        var rig = new RigFixture();
        rig.Registry.Set(SharedStateReader.DefaultPlayerPrefsKey, "Something", "1");
        rig.Registry.Unreadable.Add(SharedStateReader.DefaultPlayerPrefsKey);

        var values = rig.SharedState.Capture().Values;

        Assert.Equal("unreadable", values["prefs"]);
        Assert.DoesNotContain(values.Keys, static k => k.StartsWith("prefs.", StringComparison.Ordinal));
    }

    [Fact]
    public void BlueprintsAreCountedRecursivelyAndZeroWhenTheFolderIsAbsent()
    {
        // RESET-141.
        var rig = new RigFixture();
        Assert.Equal("0", rig.SharedState.Capture().Values["blueprints.files"]);

        rig.Fs.AddFile(Blueprint("one.blueprint"), "x");
        rig.Fs.AddFile(Blueprint(Path.Combine("nested", "two.blueprint")), "x");

        Assert.Equal("2", rig.SharedState.Capture().Values["blueprints.files"]);
    }

    [Fact]
    public void TheSnapshotCarriesWhenAndWhereItWasTaken()
    {
        // RESET-142, RESET-004, RESET-009. Both sources are recorded, so a baseline taken
        // against a different folder or key is visibly not comparable.
        var rig = new RigFixture();

        var snapshot = rig.SharedState.Capture();

        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), snapshot.CapturedUtc);
        Assert.Equal(RigFixture.SharedData, snapshot.SharedDataDir);
        Assert.Equal(@"HKCU:\Software\Rocketwerkz\rocketstation", snapshot.PlayerPrefsKey);
        Assert.Equal(SharedStateReader.DefaultPlayerPrefsKey, rig.Paths.PlayerPrefsKey);
        Assert.EndsWith(
            Path.Combine("AppData", "LocalLow", "Rocketwerkz", "rocketstation"),
            rig.Paths.SharedDataDir!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ThereIsNoWayToPutAnyOfItBack()
    {
        // RESET-136 and RESET-147: the prohibition is structural, not a convention. Restoring
        // any of this would itself be the write the save rules forbid, so neither the reader
        // nor the registry seam has a counterpart writer, and nothing may add one.
        Assert.DoesNotContain(
            typeof(SharedStateReader).GetMethods(),
            static m => m.Name.Contains("Restore", StringComparison.Ordinal)
                        || m.Name.Contains("Write", StringComparison.Ordinal)
                        || m.Name.Contains("Apply", StringComparison.Ordinal));

        Assert.DoesNotContain(
            typeof(TestRig.Core.Abstractions.IRegistry).GetMethods(),
            static m => m.Name.StartsWith("Set", StringComparison.Ordinal)
                        || m.Name.StartsWith("Write", StringComparison.Ordinal)
                        || m.Name.StartsWith("Delete", StringComparison.Ordinal));
    }

    // ---- the comparison ----------------------------------------------------

    [Fact]
    public void TheComparisonHasExactlyThreeShapesAndSaysNothingWhenNothingMoved()
    {
        // RESET-144, RESET-145.
        var before = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cookie.bytes"] = "100",
            ["prefs.Gone"] = "old",
            ["blueprints.files"] = "3",
        };
        var after = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["cookie.bytes"] = "140",
            ["blueprints.files"] = "3",
            ["prefs.New"] = "fresh",
        };

        Assert.Empty(SharedStateReader.Compare(before, before));

        var deltas = SharedStateReader.Compare(before, after).Select(static d => d.ToString()).ToArray();

        Assert.Equal(
            [
                "cookie.bytes : '100' -> '140'",
                "prefs.Gone : 'old' -> gone",
                "prefs.New : new -> 'fresh'",
            ],
            deltas);
    }

    // ---- the report --------------------------------------------------------

    [Fact]
    public void NoBaselineIsSaidRatherThanReportedAsUnchanged()
    {
        // RESET-150. "No baseline" and "nothing moved" are different answers and the second
        // one would be a lie.
        var rig = new RigFixture();

        rig.State.WriteDrift(rig.Output);

        Assert.True(rig.Output.Said(SharedStateReport.NoBaseline));
    }

    [Fact]
    public void AnUnchangedRigGetsOneLineNamingAllThreeSources()
    {
        // RESET-151.
        var rig = new RigFixture();
        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\" /></Root>");
        rig.State.Save("2026-08-14T10:00:00Z");
        rig.Output.Clear();

        rig.State.WriteDrift(rig.Output);

        Assert.True(rig.Output.Said(SharedStateReport.Unchanged));
        Assert.True(rig.Output.Said("PlayerCookie-v2.xml"));
        Assert.True(rig.Output.Said("Blueprints"));
    }

    [Fact]
    public void DriftPrintsTheHeaderEveryDeltaAndTheClosingSentence()
    {
        // RESET-152.
        var rig = new RigFixture();
        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\" /></Root>");
        rig.State.Save("2026-08-14T10:00:00Z");
        rig.Output.Clear();

        // The developer opened their own client during the session, which is exactly the
        // event this exists to name.
        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\" /><World name=\"b\" /></Root>");
        rig.Fs.AddFile(Blueprint("new.blueprint"), "x");

        rig.State.WriteDrift(rig.Output);

        Assert.True(rig.Output.Said(SharedStateReport.DriftHeader));
        Assert.True(rig.Output.Said("cookie.worlds : '1' -> '2'"));
        Assert.True(rig.Output.Said("blueprints.files : '0' -> '1'"));
        Assert.True(rig.Output.Said(SharedStateReport.DriftFooter));
    }

    // ---- the state file ----------------------------------------------------

    [Fact]
    public void TheStateFileCarriesTheSnapshotAndBothItsSources()
    {
        // RESET-149. LastResetUtc is what lets the NEXT reset report which server config files
        // moved since, so it is written on every path including a refusal.
        var rig = new RigFixture();
        rig.Fs.AddFile(Cookie(rig), "<Root><World name=\"a\" /></Root>");
        rig.Registry.Set(SharedStateReader.DefaultPlayerPrefsKey, "Name", "bytes[8]");

        rig.State.Save("2026-08-14T09:00:00Z");

        Assert.Equal("2026-08-14T09:00:00Z", rig.State.ReadLastResetUtc());
        Assert.Equal(RigFixture.SharedData, rig.State.ReadSharedDataDir());
        Assert.Equal(SharedStateReader.DefaultPlayerPrefsKey, rig.State.ReadPlayerPrefsKey());

        var values = rig.State.ReadValues();
        Assert.Equal("1", values["cookie.worlds"]);
        Assert.Equal("bytes[8]", values["prefs.Name"]);
    }

    [Fact]
    public void ASaveWithNoStateFileYetHasNoBaselineRatherThanAnEmptyOne()
    {
        // RESET-148. An empty baseline would make every value read as "new", which is a drift
        // report that is entirely noise.
        var rig = new RigFixture();

        Assert.Null(rig.State.ReadBaseline());

        rig.Fs.AddFile(Cookie(rig), "<Root/>");
        rig.State.Save(null);

        Assert.NotNull(rig.State.ReadBaseline());
    }

    // ---- ordering ----------------------------------------------------------

    [Fact]
    public void TheDriftReportRunsOnlyAfterASuccessfulRelease()
    {
        // CLI-070. A drift report on a lock that is still held describes a session that is not
        // over; one on somebody else's lock attributes this session's changes to theirs.
        var rig = new RigFixture(wireRestore: false);
        var owner = rig.Lease();

        // Somebody else's lock: refused, and silent.
        rig.WriteLockFile("beef0000", purpose: "another session");
        rig.Output.Clear();

        Assert.Throws<RigRefusalException>(() => rig.Lock.Release("cafe0000"));
        Assert.False(rig.Output.Said("[State]"));

        // The owner's own release: reported.
        rig.WriteLockFile(owner);
        rig.Output.Clear();

        var released = rig.Lock.Release(owner);

        Assert.Equal(ReleaseStatus.Released, released.Status);
        Assert.True(rig.Output.Said("[State]"));
    }
}
