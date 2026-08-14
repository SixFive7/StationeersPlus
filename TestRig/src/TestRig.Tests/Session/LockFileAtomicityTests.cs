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
/// <c>File.WriteAllText</c>): 33 of 347 reads saw a lock file with no <c>owner</c> key.
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
            for (var i = 0; i < WriteRounds; i++) service.RefreshIfMine(owner);
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

        // And the survivor is a whole lock file, not a plausible-looking fragment.
        var final = service.ReadLock();
        Assert.NotNull(final);
        Assert.Equal(owner, final!.GetOrEmpty(LockFields.Owner));
        Assert.Equal(PurposePadding, final.GetOrEmpty(LockFields.Purpose).Length);
    }

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
