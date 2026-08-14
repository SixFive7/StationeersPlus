using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// Pid files, and the three layers that make a claim believable.
/// </summary>
/// <remarks>
/// The number alone is defeated by pid reuse; the number plus the image is defeated by reuse
/// BY THE SAME IMAGE, which is the normal case here because two <c>rocketstation</c>
/// processes is what this rig exists to run.
/// </remarks>
public sealed class PidFileTests
{
    private const string PidFile = @"C:\rig\data\client1\game.pid";
    private static readonly DateTimeOffset Started = new(2026, 8, 14, 11, 30, 0, TimeSpan.Zero);

    private static (FakeFileSystem Fs, FakeProcessTable Processes) Rig()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\rig\data\client1");
        return (fs, new FakeProcessTable());
    }

    // ---- reading -----------------------------------------------------------

    [Fact]
    public void AMissingEmptyOrCorruptFileReadsAsNullRatherThanThrowing()
    {
        // Both PowerShell launchers cast with [int], which THROWS on a corrupt file, next to a
        // library version using TryParse. The library version won.
        var (fs, _) = Rig();
        Assert.Null(PidFiles.Read(fs, PidFile));

        fs.AddFile(PidFile, "");
        Assert.Null(PidFiles.Read(fs, PidFile));

        fs.AddFile(PidFile, "not a number");
        Assert.Null(PidFiles.Read(fs, PidFile));

        fs.AddFile(PidFile, "  4242 \r\n");
        Assert.Equal(4242, PidFiles.Read(fs, PidFile));
    }

    [Fact]
    public void TheFileOnDiskStaysABareIntegerSoTheSessionBusyProbeCanStillParseIt()
    {
        // The session layer parses the WHOLE trimmed contents of game.pid and server.pid as an
        // integer. A second line would make every running instance invisible to the lock's busy
        // signal, and an abandoned session would look reclaimable mid-test. The start time goes
        // in a sidecar for exactly this reason.
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 4242, Started);

        Assert.Equal("4242", fs.ReadAllText(PidFile));
        Assert.True(fs.FileExists(PidFile + PidFiles.StartedSuffix));

        var paths = new RigPaths(@"C:\rig", @"D:\instances");
        var probe = new BusyProbe(fs, processes, paths);
        Assert.Equal(4242, probe.ReadPid(PidFile));
    }

    // ---- liveness ----------------------------------------------------------

    [Fact]
    public void TheImageIsCheckedAndNotJustTheNumber()
    {
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 4242, Started);

        processes.Add(4242, "notepad", Started);
        Assert.False(PidFiles.ClientAlive(fs, processes, PidFile));

        processes.Kill(4242).Add(4242, "rocketstation", Started);
        Assert.True(PidFiles.ClientAlive(fs, processes, PidFile));
    }

    [Fact]
    public void AReusedPidWithTheSameImageIsRejectedByTheRecordedStartTime()
    {
        // The hole an image check cannot close. Two rocketstation processes is the normal case
        // here, so a number and an image are not enough on their own.
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 4242, Started);

        processes.Add(4242, "rocketstation", Started.AddHours(6));
        Assert.False(PidFiles.ClientAlive(fs, processes, PidFile));

        processes.Kill(4242).Add(4242, "rocketstation", Started);
        Assert.True(PidFiles.ClientAlive(fs, processes, PidFile));
    }

    [Fact]
    public void ASubSecondDifferenceIsToleratedBecauseTheStampIsWrittenToWholeSeconds()
    {
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 4242, Started.AddMilliseconds(640));
        processes.Add(4242, "rocketstation", Started);
        Assert.True(PidFiles.ClientAlive(fs, processes, PidFile));
    }

    [Fact]
    public void AFileWrittenByTheOldRigWithNoSidecarFallsBackToTheWriteTimeMargin()
    {
        var (fs, processes) = Rig();
        fs.Now = Started;
        fs.AddFile(PidFile, "4242");

        // Inside the margin: believed, which keeps a live instance's claim.
        processes.Add(4242, "rocketstation", Started.AddMinutes(1));
        Assert.True(PidFiles.ClientAlive(fs, processes, PidFile));

        // Well past it: a different process wearing a recycled number.
        processes.Kill(4242).Add(4242, "rocketstation", Started.AddHours(3));
        Assert.False(PidFiles.ClientAlive(fs, processes, PidFile));
    }

    [Fact]
    public void ASidecarWithNoStartTimeIsRemovedRatherThanLeftDescribingAPreviousProcess()
    {
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 1111, Started);
        Assert.True(fs.FileExists(PidFile + PidFiles.StartedSuffix));

        // A wrong sidecar is worse than none, because the reader trusts it exactly.
        PidFiles.Write(fs, PidFile, 2222, null);
        Assert.False(fs.FileExists(PidFile + PidFiles.StartedSuffix));

        fs.Now = Started;
        processes.Add(2222, "rocketstation", Started);
        Assert.True(PidFiles.ClientAlive(fs, processes, PidFile));
    }

    [Fact]
    public void DeletingAClaimTakesTheSidecarWithIt()
    {
        var (fs, _) = Rig();
        PidFiles.Write(fs, PidFile, 4242, Started);
        PidFiles.Delete(fs, PidFile);

        Assert.False(fs.FileExists(PidFile));
        Assert.False(fs.FileExists(PidFile + PidFiles.StartedSuffix));
    }

    // ---- the wrapper -------------------------------------------------------

    [Fact]
    public void TheWrapperCheckAcceptsAnyOfItsImagesAndRejectsEverythingElse()
    {
        var (fs, processes) = Rig();
        var file = @"C:\rig\data\host.pid";
        fs.AddDirectory(@"C:\rig\data");

        foreach (var image in RigConstants.HostWrapperImageNames)
        {
            PidFiles.Write(fs, file, 900, Started);
            processes.Kill(900).Add(900, image, Started);
            Assert.True(PidFiles.WrapperAlive(fs, processes, file), $"{image} should count as a wrapper");
        }

        processes.Kill(900).Add(900, "rocketstation", Started);
        Assert.False(PidFiles.WrapperAlive(fs, processes, file));
    }

    [Fact]
    public void ANullOrZeroPidIsFalseWithoutProbingAtAll()
    {
        var (_, processes) = Rig();
        processes.Add(0, "pwsh", Started);

        Assert.False(PidFiles.WrapperAlive(processes, null));
        Assert.False(PidFiles.WrapperAlive(processes, 0));
    }

    [Fact]
    public void TheThreeConvenienceChecksEachPinTheirOwnImage()
    {
        var (fs, processes) = Rig();
        PidFiles.Write(fs, PidFile, 77, Started);
        processes.Add(77, RigConstants.ServerImageName, Started);

        Assert.True(PidFiles.ServerAlive(fs, processes, PidFile));
        Assert.False(PidFiles.ClientAlive(fs, processes, PidFile));
        Assert.False(PidFiles.WrapperAlive(fs, processes, PidFile));
    }
}
