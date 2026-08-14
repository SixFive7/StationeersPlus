using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// The crash marker. Ported from rig-lock.tests.ps1 section 5 and rig-reset.tests.ps1
/// section A.
/// </summary>
public sealed class DirtyMarkerTests
{
    [Fact]
    public void ACleanRigHasNoMarkerAndReadsAsClean()
    {
        var rig = new RigFixture();

        var state = rig.Marker.GetState();

        Assert.False(state.Dirty);
        Assert.False(state.Crashed);
        Assert.False(state.WriterAlive);
        Assert.Equal("", state.Owner);
        Assert.Null(state.Marker);
        Assert.Equal("clean (no dirty marker)", DirtyMarker.Describe(state));
    }

    [Fact]
    public void WritingTheMarkerRecordsEveryField()
    {
        var rig = new RigFixture();

        Assert.True(rig.Marker.Write("abc12345", "network paint", "Start"));

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.Equal("abc12345", fields.Get(DirtyMarker.KeyOwner));
        Assert.Equal("network paint", fields.Get(DirtyMarker.KeyPurpose));
        Assert.Equal("Start", fields.Get(DirtyMarker.KeyReason));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(DirtyMarker.KeyMarkedAt));
        Assert.Equal(rig.Boot.BootId, fields.Get(DirtyMarker.KeyBootId));
        Assert.Equal("4242", fields.Get(DirtyMarker.KeyWriterPid));
        Assert.Equal("pwsh", fields.Get(DirtyMarker.KeyWriterImage));
        Assert.Equal("RIGTEST", fields.Get(DirtyMarker.KeyHost));
    }

    [Fact]
    public void TheMarkerIsWrittenDurably()
    {
        // Its whole job is to survive a power cut in the next few seconds, because it is
        // precisely what tells the next session that this one did not finish.
        var rig = new RigFixture();

        rig.Marker.Write("abc12345", "p", "Start");

        Assert.Contains(Path.GetFullPath(rig.Paths.DirtyFile), rig.Fs.DurableWrites);
    }

    [Fact]
    public void WritingIsIdempotentForTheSameOwnerAndBoot()
    {
        var rig = new RigFixture();

        Assert.True(rig.Marker.Write("abc12345", "p", "Start"));
        Assert.False(rig.Marker.Write("abc12345", "p", "Save"));
        Assert.False(rig.Marker.Write("abc12345", "different purpose", "Deploy"));
        Assert.Equal("Start", FieldText.Parse(rig.MarkerText()).Get(DirtyMarker.KeyReason));
    }

    [Fact]
    public void ADifferentOwnerRewritesTheMarkerWholesale()
    {
        var rig = new RigFixture();
        rig.Marker.Write("aaa11111", "first session", "Start");
        rig.Clock.AdvanceMinutes(30);

        Assert.True(rig.Marker.Write("bbb22222", "second session", "Save"));

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.Equal("bbb22222", fields.Get(DirtyMarker.KeyOwner));
        Assert.Equal("Save", fields.Get(DirtyMarker.KeyReason));
        Assert.Equal(RigTime.Stamp(rig.Clock.UtcNow), fields.Get(DirtyMarker.KeyMarkedAt));
    }

    [Fact]
    public void ARebootMakesAnExistingMarkerNotThisBoot()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        Assert.True(rig.Marker.GetState().SameBoot);

        rig.Boot.Reboot();

        var state = rig.Marker.GetState();
        Assert.True(state.Dirty);
        Assert.False(state.SameBoot);
        Assert.True(state.Crashed);
        Assert.Contains("the machine has restarted since", DirtyMarker.Describe(state));
    }

    [Fact]
    public void AMarkerFromAnotherBootIsRewrittenEvenBySameOwner()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Boot.Reboot();

        Assert.True(rig.Marker.Write("abc12345", "p", "Save"));
        Assert.Equal(rig.Boot.BootId, FieldText.Parse(rig.MarkerText()).Get(DirtyMarker.KeyBootId));
    }

    [Fact]
    public void ALiveWriterProcessMeansTheSessionDidNotCrash()
    {
        var rig = new RigFixture();
        rig.Processes.Add(4242, "pwsh");
        rig.Marker.Write("abc12345", "p", "Start");

        var state = rig.Marker.GetState();

        Assert.True(state.WriterAlive);
        Assert.False(state.Crashed);
        Assert.Contains("its launcher process is STILL RUNNING (pid 4242)", DirtyMarker.Describe(state));
    }

    [Fact]
    public void ADeadWriterProcessMeansTheSessionCrashed()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");

        var state = rig.Marker.GetState();

        Assert.False(state.WriterAlive);
        Assert.True(state.Crashed);
        Assert.Contains("its launcher process is gone", DirtyMarker.Describe(state));
    }

    [Fact]
    public void ARecycledWriterPidWithADifferentImageIsNotAlive()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Processes.Add(4242, "notepad");

        Assert.False(rig.Marker.GetState().WriterAlive);
    }

    [Fact]
    public void TheWriterPidIsNotConsultedAtAllAcrossAReboot()
    {
        // A pid from before a reboot names whatever process inherited that number, and
        // trusting it is how a crashed session's mess gets mistaken for a live one's.
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Processes.Add(4242, "pwsh");
        rig.Boot.Reboot();

        var state = rig.Marker.GetState();

        Assert.False(state.WriterAlive);
        Assert.True(state.Crashed);
    }

    [Fact]
    public void AMarkerWithAnEmptyWriterImageMatchesOnTheBarePid()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        var fields = FieldText.Parse(rig.MarkerText());
        fields.Set(DirtyMarker.KeyWriterImage, "");
        rig.Fs.AddFile(rig.Paths.DirtyFile, fields.Render([]));
        rig.Processes.Add(4242, "anything-at-all");

        Assert.True(rig.Marker.GetState().WriterAlive);
    }

    [Fact]
    public void AFileWithNoOwnerKeyIsNotAMarker()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.DirtyFile, "# just a comment\npurpose=nothing\n");

        Assert.Null(rig.Marker.Read());
        Assert.False(rig.Marker.GetState().Dirty);
    }

    [Fact]
    public void AnEmptyMarkerFileIsNotAMarker()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.DirtyFile, "");

        Assert.Null(rig.Marker.Read());
    }

    [Fact]
    public void ClearingRemovesTheMarker()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");

        rig.Marker.Clear();

        Assert.False(rig.MarkerExists());
        Assert.False(rig.Marker.GetState().Dirty);
    }

    [Fact]
    public void ClearingAnAbsentMarkerIsANoOpRatherThanAnError()
    {
        var rig = new RigFixture();

        rig.Marker.Clear();

        Assert.False(rig.MarkerExists());
    }

    [Fact]
    public void AMarkerThatCannotBeDeletedIsAClearRefusal()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.DeleteFailures[Path.GetFullPath(rig.Paths.DirtyFile)] = "held open";

        var ex = Assert.Throws<RigRefusalException>(rig.Marker.Clear);

        Assert.Equal(RigRefusalKind.Broken, ex.Kind);
        Assert.Contains("Could not delete", ex.Message);
    }

    [Fact]
    public void AnUnreadableMarkerThrowsRatherThanReadingAsClean()
    {
        // A read failure that reads as "nothing has happened here" is the answer that gets a
        // rig restored on top of a live session's work.
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Fs.ReadFailures[Path.GetFullPath(rig.Paths.DirtyFile)] = "sharing violation";

        var ex = Assert.Throws<RigRefusalException>(() => rig.Marker.Read());

        Assert.Contains("Refusing to treat an unreadable file as an absent one", ex.Message);
    }

    [Fact]
    public void AControlCharacterInThePurposeCannotCorruptTheMarker()
    {
        var rig = new RigFixture();

        rig.Marker.Write("abc12345", "line one\nowner=hijacked", "Start");

        var fields = FieldText.Parse(rig.MarkerText());
        Assert.Equal("abc12345", fields.Get(DirtyMarker.KeyOwner));
        Assert.DoesNotContain("\n", fields.GetOrEmpty(DirtyMarker.KeyPurpose));
    }

    [Fact]
    public void AnUnknownCurrentBootIdNeverMatches()
    {
        // Fails closed: an unidentifiable boot reads as "not this boot", which is the cheap
        // answer (restore again, keep the world).
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        rig.Boot.BootId = "unknown";

        Assert.False(rig.Marker.IsSameBoot(rig.Marker.Read()));
    }

    [Fact]
    public void AMarkerWithNoBootIdNeverMatches()
    {
        var rig = new RigFixture();
        rig.Marker.Write("abc12345", "p", "Start");
        var fields = FieldText.Parse(rig.MarkerText());
        fields.Set(DirtyMarker.KeyBootId, "");
        rig.Fs.AddFile(rig.Paths.DirtyFile, fields.Render([]));

        Assert.False(rig.Marker.IsSameBoot(rig.Marker.Read()));
    }

    [Fact]
    public void BootIdComparisonIsExactSoAnApproxIdNeverMatchesAnExactOne()
    {
        var rig = new RigFixture();
        rig.Boot.BootId = "boot:2026-08-14T06:00:00Z";
        rig.Marker.Write("abc12345", "p", "Start");

        rig.Boot.BootId = "approx:2026-08-14T06:00:00Z";

        Assert.False(rig.Marker.IsSameBoot(rig.Marker.Read()));
    }
}
