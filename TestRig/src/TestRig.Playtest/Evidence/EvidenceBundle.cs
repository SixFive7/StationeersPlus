using System.Globalization;
using System.Text.Json.Nodes;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Evidence;

/// <summary>
///     One bundle per run: everything needed to audit a run nobody watched.
/// </summary>
/// <remarks>
///     Nothing in the bundle is conditional on the outcome except the attestation report,
///     which only exists once attestation completed. Both console tails, both save
///     inventories, the full request log and the per-check record are written for pass, fail
///     and inconclusive alike, so the failing run is never the one with the thinnest evidence.
/// </remarks>
public sealed class EvidenceBundle
{
    private readonly IFileSystem _files;

    public EvidenceBundle(IFileSystem files, string root, string suiteName, DateTimeOffset startedUtc)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        Root = root;
        SuiteName = suiteName;
        StartedUtc = startedUtc;

        _files.CreateDirectory(root);
        _files.CreateDirectory(Path.Combine(root, "checks"));
    }

    public string Root { get; }

    public string SuiteName { get; }

    public DateTimeOffset StartedUtc { get; }

    /// <summary>Writes a file at the bundle root.</summary>
    public string Write(string name, string content)
    {
        var path = Path.Combine(Root, name);
        _files.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    ///     Creates a check's folder and all four subfolders, whether or not they get used.
    /// </summary>
    /// <remarks>
    ///     The index is what disambiguates two checks with the same name, so the folder is
    ///     named from both.
    /// </remarks>
    public CheckEvidence NewCheck(int index, string checkName)
    {
        var folder = Path.Combine(Root, "checks", string.Create(CultureInfo.InvariantCulture, $"{index:d2}-{Slug.Of(checkName)}"));
        return new CheckEvidence(_files, folder);
    }
}

/// <summary>One check's folder inside a bundle.</summary>
public sealed class CheckEvidence
{
    private readonly IFileSystem _files;

    internal CheckEvidence(IFileSystem files, string root)
    {
        _files = files;
        Root = root;

        _files.CreateDirectory(root);
        foreach (var kind in Enum.GetValues<EvidenceKind>())
        {
            if (kind == EvidenceKind.Root) continue;
            _files.CreateDirectory(Path.Combine(root, Folder(kind)));
        }
    }

    public string Root { get; }

    /// <summary>The folder name each evidence kind lives in.</summary>
    public static string Folder(EvidenceKind kind) => kind switch
    {
        EvidenceKind.Requests => "requests",
        EvidenceKind.Observations => "observations",
        EvidenceKind.Console => "console",
        EvidenceKind.Launcher => "launcher",
        _ => string.Empty,
    };

    /// <summary>The bundle-relative reference a record uses to point at another record.</summary>
    public static string Reference(EvidenceKind kind, string name) =>
        kind == EvidenceKind.Root ? name : Folder(kind) + "/" + name;

    /// <summary>Writes, or appends, one evidence file. Returns its bundle-relative reference.</summary>
    public string Write(EvidenceKind kind, string name, string content, bool append = false)
    {
        var folder = kind == EvidenceKind.Root ? Root : Path.Combine(Root, Folder(kind));
        var path = Path.Combine(folder, name);

        if (append) _files.AppendAllText(path, content);
        else _files.WriteAllText(path, content);

        return Reference(kind, name);
    }

    /// <summary>The absolute path of an evidence file, without writing it.</summary>
    public string PathOf(EvidenceKind kind, string name) =>
        kind == EvidenceKind.Root ? Path.Combine(Root, name) : Path.Combine(Root, Folder(kind), name);
}

/// <summary>The per-artifact record shapes. Key order is fixed; see PlaytestJson.</summary>
public static class EvidenceRecords
{
    /// <summary>One request, written on EVERY attempt including the failures.</summary>
    public static string Request(
        int sequence,
        string utc,
        string instance,
        string method,
        string path,
        int attempt,
        long elapsedMs,
        string requestBody,
        int httpStatus,
        JsonNode? response,
        string error)
    {
        var obj = new JsonObject
        {
            ["sequence"] = sequence,
            ["utc"] = utc,
            ["instance"] = instance,
            ["method"] = method,
            ["path"] = path,
            ["attempt"] = attempt,
            ["elapsedMs"] = elapsedMs,
            ["requestBody"] = requestBody,
            ["httpStatus"] = httpStatus,
            ["response"] = response?.DeepClone(),
            ["error"] = error,
        };

        return PlaytestJson.Write(obj);
    }

    /// <summary>
    ///     One observation.
    /// </summary>
    /// <remarks>
    ///     <b>Defect P-05.</b> The PowerShell record carried the reader args on the in-memory
    ///     observation and omitted them from the file, so a bundle reader could not
    ///     reconstruct which query produced a thing, config or console reading: two
    ///     observations of the same reader and the same select path, one of them the baseline
    ///     and one the re-read, were indistinguishable on disk.
    /// </remarks>
    public static string Observation(
        string instance,
        string reader,
        string select,
        string of,
        JsonNode? readerArgs,
        JsonNode? value,
        string source,
        string capturedUtc,
        string request)
    {
        var obj = new JsonObject
        {
            ["instance"] = instance,
            ["reader"] = reader,
            ["select"] = select,
            ["of"] = of,
            ["readerArgs"] = readerArgs?.DeepClone(),
            ["value"] = value?.DeepClone(),
            ["source"] = source,
            ["capturedUtc"] = capturedUtc,
            ["request"] = request,
        };

        return PlaytestJson.Write(obj);
    }

    /// <summary>One launcher invocation.</summary>
    public static string Launcher(string verb, string arguments, string startedUtc, int exitCode, string message, string output) =>
        string.Create(CultureInfo.InvariantCulture,
            $"# testrig {verb} {arguments}\n# started : {startedUtc}\n# exit    : {exitCode}\n\n--- message ---\n{message}\n--- output ---\n{output}\n");
}
