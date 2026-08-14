using System.ComponentModel;
using System.Text;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// SystemFileSystem against a real volume.
/// </summary>
public sealed class SystemFileSystemTests : IDisposable
{
    private readonly TempDirectory _temp = new("fs");
    private readonly SystemFileSystem _fs = new();

    public void Dispose() => _temp.Dispose();

    // ---- durable writes --------------------------------------------------

    [Fact]
    public void WriteAllTextDurable_ReplacesAPreExistingTarget()
    {
        var path = _temp.File("session.lock");
        File.WriteAllText(path, "owner=older-session-with-a-much-longer-body\nttl=10\n");

        _fs.WriteAllTextDurable(path, "owner=new\n");

        Assert.Equal("owner=new\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllTextDurable_LeavesNoTemporaryFileBehind()
    {
        var path = _temp.File("session.dirty");
        File.WriteAllText(path, "stale");

        _fs.WriteAllTextDurable(path, "boot=boot-20260814T031500Z\npid=1234\n");

        // A leftover .tmp beside session.lock reads as debris from a crash that did not
        // happen, which is exactly the signal the marker exists to carry.
        Assert.Empty(Directory.GetFiles(_temp.Path, "*.tmp"));
        Assert.Single(Directory.GetFiles(_temp.Path));
    }

    [Fact]
    public void WriteAllTextDurable_CreatesTheFileAndItsParentWhenAbsent()
    {
        var path = _temp.File(Path.Combine("nested", "deeper", "session.lock"));

        _fs.WriteAllTextDurable(path, "owner=first\n");

        Assert.Equal("owner=first\n", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllTextDurable_WritesUtf8WithoutAByteOrderMark()
    {
        var path = _temp.File("marker.txt");

        _fs.WriteAllTextDurable(path, "purpose=éè\n");

        var bytes = File.ReadAllBytes(path);
        AssertNoByteOrderMark(bytes);
        Assert.Equal("purpose=éè\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WriteAllText_WritesUtf8WithoutAByteOrderMark()
    {
        var path = _temp.File("plain.txt");

        _fs.WriteAllText(path, "aéb");

        var bytes = File.ReadAllBytes(path);
        AssertNoByteOrderMark(bytes);
        Assert.Equal("aéb", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void ReadAllText_StripsAByteOrderMarkSomeOtherToolWrote()
    {
        // Windows PowerShell 5.1 writes one; the rig must still read those files.
        var path = _temp.File("bom.txt");
        File.WriteAllText(path, "owner=abc", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("owner=abc", _fs.ReadAllText(path));
    }

    // ---- hard links ------------------------------------------------------

    [Fact]
    public void CreateHardLink_SharesTheFileData()
    {
        var target = _temp.File("target.bin");
        File.WriteAllText(target, "original");
        var link = _temp.File("link.bin");

        _fs.CreateHardLink(link, target);

        Assert.True(File.Exists(link));

        // Written through the link, read through the target.
        File.WriteAllText(link, "through the link");
        Assert.Equal("through the link", File.ReadAllText(target));

        // And the other way, because a copy would pass the first check and fail this one.
        File.WriteAllText(target, "through the target");
        Assert.Equal("through the target", File.ReadAllText(link));
    }

    [Fact]
    public void CreateHardLink_ArgumentOrderIsLinkThenTarget()
    {
        // The order inverts relative to New-Item -Path/-Value, and getting it backwards
        // writes a link INTO the source, which for the rig is the developer's install.
        var target = _temp.File("source-of-truth.bin");
        File.WriteAllText(target, "payload");
        var link = _temp.File("new-name.bin");

        _fs.CreateHardLink(link, target);

        Assert.True(File.Exists(link));
        Assert.True(File.Exists(target));
        Assert.Equal("payload", File.ReadAllText(link));
    }

    [Fact]
    public void CreateHardLink_FailureNamesTheLinkTheTargetAndTheError()
    {
        var link = _temp.File("wanted.bin");
        var missing = _temp.File("no-such-target.bin");

        var ex = Assert.Throws<Win32Exception>(() => _fs.CreateHardLink(link, missing));

        // The PowerShell aborted a 1,050 file tree naming none of these three.
        Assert.Contains(link, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ERROR_FILE_NOT_FOUND", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, ex.NativeErrorCode);
    }

    [Fact]
    public void CreateHardLink_NamesTheAlreadyExistsCaseByItsWin32Name()
    {
        var target = _temp.File("target.bin");
        File.WriteAllText(target, "payload");
        var link = _temp.File("occupied.bin");
        File.WriteAllText(link, "something is already here");

        var ex = Assert.Throws<Win32Exception>(() => _fs.CreateHardLink(link, target));

        Assert.Equal(183, ex.NativeErrorCode);
        Assert.Contains("ERROR_ALREADY_EXISTS", ex.Message, StringComparison.Ordinal);
        Assert.Contains(link, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(target, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- enumeration -----------------------------------------------------

    [Fact]
    public void EnumerateFiles_IncludesHiddenAndSystemFiles()
    {
        var visible = _temp.File("visible.txt");
        File.WriteAllText(visible, "x");
        var hidden = _temp.File("hidden.txt");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden | FileAttributes.System);

        var files = _fs.EnumerateFiles(_temp.Path, "*", recurse: false);

        Assert.Contains(visible, files);
        Assert.Contains(hidden, files);

        // The control, so this assertion cannot pass by accident: the default
        // EnumerationOptions, which is what Get-ChildItem without -Force behaves like,
        // silently drops the same file. That omission is what shortened both hard-link
        // loops.
        var withDefaults = Directory.GetFiles(_temp.Path, "*", new EnumerationOptions());
        Assert.DoesNotContain(hidden, withDefaults);
        Assert.Contains(visible, withDefaults);
    }

    [Fact]
    public void EnumerateFiles_RecursesWhenAsked()
    {
        var nested = _temp.Dir(Path.Combine("a", "b"));
        var deep = Path.Combine(nested, "deep.txt");
        File.WriteAllText(deep, "x");
        File.WriteAllText(_temp.File("top.txt"), "x");

        Assert.Equal(2, _fs.EnumerateFiles(_temp.Path, "*", recurse: true).Count);
        Assert.Single(_fs.EnumerateFiles(_temp.Path, "*", recurse: false));
        Assert.Contains(deep, _fs.EnumerateFiles(_temp.Path, "*", recurse: true));
    }

    [Fact]
    public void EnumerateFiles_HonoursTheSearchPattern()
    {
        File.WriteAllText(_temp.File("keep.dll"), "x");
        File.WriteAllText(_temp.File("skip.txt"), "x");

        var dlls = _fs.EnumerateFiles(_temp.Path, "*.dll", recurse: false);

        Assert.Single(dlls);
        Assert.EndsWith("keep.dll", dlls[0], StringComparison.Ordinal);
    }

    [Fact]
    public void EnumerateDirectories_IncludesHiddenDirectoriesAndDoesNotRecurse()
    {
        var plain = _temp.Dir("plain");
        var hidden = _temp.Dir("hidden");
        _temp.Dir(Path.Combine("plain", "child"));
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        var dirs = _fs.EnumerateDirectories(_temp.Path);

        Assert.Equal(2, dirs.Count);
        Assert.Contains(plain, dirs);
        Assert.Contains(hidden, dirs);
    }

    // ---- reads -----------------------------------------------------------

    [Fact]
    public void ReadTailLines_ReturnsTheLastLinesInOrder()
    {
        var path = _temp.File("LogOutput.log");
        File.WriteAllLines(path, Enumerable.Range(1, 500).Select(i => $"line {i}"));

        var tail = _fs.ReadTailLines(path, 3);

        Assert.Equal(["line 498", "line 499", "line 500"], tail);
    }

    [Fact]
    public void ReadTailLines_ReturnsEverythingWhenTheFileIsShorterThanAsked()
    {
        var path = _temp.File("short.log");
        File.WriteAllLines(path, ["only", "two"]);

        Assert.Equal(["only", "two"], _fs.ReadTailLines(path, 50));
        Assert.Empty(_fs.ReadTailLines(path, 0));
    }

    [Fact]
    public void Reads_SucceedWhileAnotherHandleHoldsTheFileOpenForWriting()
    {
        // BepInEx holds LogOutput.log open for the life of an instance, and the pid file
        // and setting.xml belong to a live game. FileShare.Read, which File.ReadAllText
        // uses, fails against every one of them.
        var path = _temp.File("held.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("first line\nsecond line\n"u8);
        writer.Flush();

        Assert.Equal("first line\nsecond line\n", _fs.ReadAllText(path));
        Assert.Equal(["first line", "second line"], _fs.ReadLines(path));
        Assert.Equal(["second line"], _fs.ReadTailLines(path, 1));
        Assert.Equal(23, _fs.ReadAllBytes(path).Length);
    }

    // ---- deletes and metadata --------------------------------------------

    [Fact]
    public void DeleteFile_RemovesAReadOnlyFileAndIsIdempotent()
    {
        var path = _temp.File("readonly.bin");
        File.WriteAllText(path, "x");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        _fs.DeleteFile(path);
        Assert.False(File.Exists(path));

        // Asking again is success, not an error: the caller wanted it gone.
        _fs.DeleteFile(path);
        _fs.DeleteFile(_temp.File(Path.Combine("no-such-dir", "no-such-file")));
    }

    [Fact]
    public void DeleteDirectory_RemovesATreeContainingReadOnlyFiles()
    {
        // An instance tree is ~1,050 hard links into a Steam install and inherits its
        // attributes. Directory.Delete stops dead on the first read-only one, leaving a
        // half-deleted tree that makes the next create refuse with "already exists".
        var tree = _temp.Dir(Path.Combine("instance", "rocketstation_Data"));
        var file = Path.Combine(tree, "resources.assets");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

        _fs.DeleteDirectory(_temp.File("instance"), recursive: true);

        Assert.False(Directory.Exists(_temp.File("instance")));

        // And a directory that is already gone is success.
        _fs.DeleteDirectory(_temp.File("instance"), recursive: true);
    }

    [Fact]
    public void GetLastWriteTimeUtc_RefusesToAnswerForAMissingFile()
    {
        // The BCL answers 1601-01-01, which every staleness comparison reads as
        // infinitely old, and that answer redeploys a mod or rebuilds a tree for the
        // wrong reason.
        Assert.Throws<FileNotFoundException>(() => _fs.GetLastWriteTimeUtc(_temp.File("absent.dll")));
    }

    [Fact]
    public void GetLastWriteTimeUtc_AndGetFileLength_ReadTheRealValues()
    {
        var path = _temp.File("stamped.bin");
        File.WriteAllText(path, "12345");

        Assert.Equal(5, _fs.GetFileLength(path));
        Assert.Equal(TimeSpan.Zero, _fs.GetLastWriteTimeUtc(path).Offset);
        Assert.True(_fs.GetLastWriteTimeUtc(path) > DateTimeOffset.UtcNow.AddMinutes(-5));
    }

    [Fact]
    public void CopyFile_CreatesTheDestinationParent()
    {
        var source = _temp.File("mod.dll");
        File.WriteAllText(source, "payload");
        var destination = _temp.File(Path.Combine("userdata", "mods", "Local_Thing", "Thing.dll"));

        _fs.CopyFile(source, destination, overwrite: false);

        Assert.Equal("payload", File.ReadAllText(destination));
    }

    [Fact]
    public void FileExists_AndDirectoryExists_DoNotConfuseTheTwo()
    {
        var dir = _temp.Dir("a-directory");
        var file = _temp.File("a-file");
        File.WriteAllText(file, "x");

        Assert.True(_fs.DirectoryExists(dir));
        Assert.False(_fs.FileExists(dir));
        Assert.True(_fs.FileExists(file));
        Assert.False(_fs.DirectoryExists(file));
    }

    private static void AssertNoByteOrderMark(byte[] bytes)
    {
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        Assert.False(hasBom, "the rig writes UTF-8 with no byte order mark; the baseline stores these files byte for byte");
    }
}
