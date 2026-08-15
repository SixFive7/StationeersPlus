using TestRig.Core.Infrastructure;
using TestRig.Core.Session;
using TestRig.Tests.Infrastructure;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// LOCK-074, LOCK-077, LOCK-078: the lock file is staged and swapped in, never truncated
/// in place, so a reader outside the critical section never sees a partial file.
/// </summary>
/// <remarks>
/// This is the one place in the suite that points the session lock at a REAL volume, and
/// it has to be: the property under test is a filesystem property. An in-memory fake
/// replaces a write with a dictionary assignment, which is atomic whatever the production
/// code does, so the fake would certify the bug.
///
/// The bug it pins: writers are serialised by the session mutex, readers are not.
/// <c>GetStatus</c> (the <c>status</c> verb) and <c>ReadState</c> (which <c>stop</c> calls
/// first, by design) both read outside the critical section. A truncate-in-place write
/// hands one of them a file with no <c>owner</c> key, <c>ReadLock</c> returns null,
/// <c>LockClassifier</c> reports <c>LockState.None</c>, and every caller reads that as
/// "the rig is free". Two agents then drive one rig, which is the single thing the whole
/// subsystem exists to prevent.
///
/// Verified to FAIL against the previous implementation (<c>RigFiles.WriteAtomic</c> calling
/// <c>File.WriteAllText</c>): 33 of 347 reads saw a lock file with no <c>owner</c> key, and
/// re-verified 2026-08-15 at 22 to 40 torn reads out of about 350, in 12 runs out of 12.
///
/// This test owns ONE property, and <see cref="WritesThatMustComplete"/> is where that scope
/// is drawn. The neighbouring property, that a durable write retries a held destination rather
/// than failing on the first rename, is pinned deterministically by
/// <c>SystemFileSystemTests.WriteAllTextDurable_RetriesThroughATransientHoldOnTheDestination</c>,
/// so a failure tells you which of the two broke.
/// </remarks>
public sealed class LockFileAtomicityTests : IDisposable
{
    /// <summary>
    /// Enough writes to lose the race, few enough to stay a unit test.
    /// </summary>
    /// <remarks>
    /// The window a truncating write leaves open is one file open plus a buffered write, so
    /// it is short in wall-clock terms and wide open to a reader spinning on another core.
    /// The payload below is what makes it comfortably observable rather than marginal.
    /// </remarks>
    private const int WriteRounds = 120;

    /// <summary>
    /// A lock file large enough that a truncating write cannot complete between two reads.
    /// </summary>
    /// <remarks>
    /// A real lock file is a few hundred bytes. Padding is a magnifying glass on a window
    /// that exists at any size: <c>FileMode.Create</c> truncates to zero on open, and every
    /// byte after that is time the file on disk is not the file either reader wanted.
    /// </remarks>
    private const int PurposePadding = 48 * 1024;

    private readonly TempDirectory _temp = new("lock-atomicity");

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void AConcurrentReaderNeverSeesAPartiallyWrittenLockFile()
    {
        var fs = new SystemFileSystem();
        var paths = new RigPaths(_temp.Path);
        var service = BuildService(fs, paths);

        const string owner = "a0000001";
        SeedLockFile(fs, paths, owner);

        var stop = false;
        var reads = 0;
        var torn = 0;
        var refused = 0;
        Exception? readerFault = null;

        var reader = new Thread(() =>
        {
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    // Exactly what `status` does: read the lock file with no mutex held.
                    if (service.ReadLock() is null) torn++;
                    reads++;
                }
            }
            catch (Exception ex)
            {
                readerFault = ex;
            }
        })
        { IsBackground = true };

        reader.Start();
        try
        {
            for (var i = 0; i < WriteRounds; i++)
            {
                try
                {
                    service.RefreshIfMine(owner);
                }
                catch (RigRefusalException)
                {
                    refused++;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            reader.Join(TimeSpan.FromSeconds(30));
        }

        Assert.Null(readerFault);

        // The reader has to have actually raced the writer, or a green result means nothing.
        Assert.True(reads > 50, $"the reader only managed {reads} reads, so it never raced the writer");

        Assert.True(torn == 0,
            $"{torn} of {reads} reads saw a lock file with no owner key, which every caller reads as "
            + "'the rig is free'");

        // And so does the writer, or torn == 0 is a statement about nothing happening.
        Assert.True(WriteRounds - refused >= WritesThatMustComplete,
            $"only {WriteRounds - refused} of {WriteRounds} writes completed, so the reader was never "
            + $"racing anything. See {nameof(WritesThatMustComplete)}.");

        // And the survivor is a whole lock file, not a plausible-looking fragment.
        var final = service.ReadLock();
        Assert.NotNull(final);
        Assert.Equal(owner, final!.GetOrEmpty(LockFields.Owner));
        Assert.Equal(PurposePadding, final.GetOrEmpty(LockFields.Purpose).Length);
    }

    /// <summary>
    /// How many of the writes must get through, and why the rest are counted, not fatal.
    /// </summary>
    /// <remarks>
    /// This is not a lax assertion, it is a scope boundary, and not having drawn it cost two
    /// failures in five full-suite runs.
    ///
    /// The reader above re-opens a 48 KB file with NO backoff, so the destination name is
    /// occupied almost continuously. <c>SystemFileSystem.OpenShared</c> passes
    /// FileShare.Delete, so the replace's delete succeeds and leaves the name delete-pending
    /// until the reader's handle closes, and the rename onto it fails until then. With ten
    /// attempts and 275ms of total backoff the writer sometimes lost, and threw "Could not
    /// replace the rig lock file after 10 attempts" out of <c>RefreshIfMine</c>. The
    /// torn-read assertion was never what failed: <c>torn</c> was 0 every time.
    ///
    /// So the flake was a SECOND property riding along in this test, and a synthetic one. No
    /// rig reader behaves like that. Measured 2026-08-15: the only unsynchronised reads of
    /// the lock file are one per <c>status</c> process and one per <c>unlock</c>, because
    /// everything else (the gate, both refreshes, the acquire path <c>lock --wait-seconds</c>
    /// polls with, and both release phases) reads inside the same named mutex every writer
    /// holds. A <c>testrig status</c> process costs 165ms warm and 631ms cold, so a real rig
    /// cannot re-read the lock file faster than about 6 times a second, and at that rate a
    /// replace needs 1 attempt, occasionally 2. Even at 65 reads a second it needed 3. The
    /// hammer above runs at roughly 1,800. Widening <c>DurableWriteAttempts</c> to suit it
    /// would be tuning a production limit to a workload nothing produces, and would not even
    /// work: re-measured at 25 attempts, the same hammer still drove a write to 10.
    ///
    /// Pacing the reader was the other candidate and is worse, because it loses the teeth
    /// silently. Measured against the planted regression, a 500us gap still caught 34 to 47
    /// torn reads but a 1,000us gap caught 0 to 4, a cliff driven by the reader and writer
    /// phase-locking rather than by the sampling rate. A test that goes green on a slightly
    /// faster machine while proving nothing is the failure mode this suite exists to avoid.
    ///
    /// So the reader stays a hammer, which is what gives the torn-read assertion its teeth,
    /// and a write that loses to it is counted instead of failing the run. The count is
    /// still asserted on, because a durable write that failed EVERY round would make
    /// <c>torn == 0</c> a statement about nothing happening. Half is far below anything
    /// measured (0 refusals in 12 isolated runs) and far above a broken write, which would
    /// lose every round.
    /// </remarks>
    private const int WritesThatMustComplete = WriteRounds / 2;

    /// <summary>
    /// LOCK-078: no staging file is left behind, at any point a reader could see one.
    /// </summary>
    /// <remarks>
    /// A leftover .tmp beside session.lock reads as debris from a crash that did not
    /// happen, which is the signal the marker next to it exists to carry.
    /// </remarks>
    [Fact]
    public void TheStagingFileIsNeverLeftBesideTheLock()
    {
        var fs = new SystemFileSystem();
        var paths = new RigPaths(_temp.Path);
        var service = BuildService(fs, paths);

        const string owner = "a0000002";
        SeedLockFile(fs, paths, owner);

        for (var i = 0; i < 20; i++) service.RefreshIfMine(owner);

        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
        Assert.Equal(["session.lock"], Directory.GetFiles(_temp.Path).Select(Path.GetFileName));
    }

    private static SessionLockService BuildService(SystemFileSystem fs, RigPaths paths)
    {
        var clock = new FakeClock();
        var processes = new FakeProcessTable();
        var launcher = new LauncherIdentity(4242, "testrig", "RIGTEST");
        var worlds = new WorldScanner(fs, paths);

        return new SessionLockService(
            fs,
            clock,
            new FakeSleeper(clock),
            new FakeCrossProcessLock(),
            new RecordingOutput(),
            paths,
            new BusyProbe(fs, processes, paths),
            new DirtyMarker(fs, clock, processes, new FakeBootIdentity(), paths, worlds, launcher),
            launcher);
    }

    private static void SeedLockFile(SystemFileSystem fs, RigPaths paths, string owner)
    {
        var stamp = RigTime.Stamp(new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));
        var fields = new FieldText();
        fields.Set(LockFields.Owner, owner);
        fields.Set(LockFields.Purpose, new string('p', PurposePadding));
        fields.Set(LockFields.AcquiredAt, stamp);
        fields.Set(LockFields.RefreshedAt, stamp);
        fields.Set(LockFields.ActiveAt, stamp);
        fields.Set(LockFields.TtlMinutes, "10");
        fields.Set(LockFields.IdleCeilingMinutes, "60");
        fields.Set(LockFields.Host, "RIGTEST");

        fs.WriteAllTextDurable(paths.LockFile, fields.Render(["# atomicity probe"]));
    }
}
