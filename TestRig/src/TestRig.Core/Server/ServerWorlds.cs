using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Server;

/// <summary>The world names a <c>--new</c> may name, read out of the install.</summary>
/// <param name="Names">Accepted world ids, ordinally sorted. Empty when the catalogue could not be read.</param>
/// <param name="Readable">
/// Whether the catalogue was found at all. False means "we do not know", which is a different
/// answer from "there are none" and must never turn into a refusal.
/// </param>
public readonly record struct WorldCatalogue(IReadOnlyList<string> Names, bool Readable)
{
    /// <summary>Whether a name is accepted, case-insensitively as the game matches it.</summary>
    public bool Accepts(string name) =>
        Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Which worlds the dedicated server will start, before it is started.
/// </summary>
/// <remarks>
/// <para>
/// The server accepts <c>-new &lt;World&gt;</c> and rejects an unknown one AFTER a full boot,
/// with a single line into its log: <c>No such world name: Moon. Valid worlds: Europa3,
/// Lunar, Mars2, MimasHerschel, Venus, Vulcan (Deprecated), Vulcan2.</c> It then keeps running
/// with no world at all, forever, which is the state a readiness barrier used to report as
/// ready. Ninety seconds of boot to learn that a name was wrong is a bad trade when the answer
/// is on disk before launch.
/// </para>
/// <para>
/// The source is <c>StreamingAssets/Worlds/&lt;Folder&gt;/&lt;File&gt;.xml</c>, one or more
/// per folder, each declaring <c>&lt;World Id="..."&gt;</c>. Measured on 0.2.6428.27798: the
/// id is NOT the folder name in four of nine cases (<c>Europa</c> holds <c>Europa3</c>,
/// <c>Mimas</c> holds <c>MimasHerschel</c>, <c>Vulcan</c> holds both <c>Vulcan</c> and
/// <c>Vulcan2</c> in two files), so a scan of folder names would refuse four valid worlds and
/// accept three invalid ones. Worlds carrying <c>&lt;IsTutorial Value="true" /&gt;</c> are
/// excluded, which is exactly what makes the parsed set the seven the server printed.
/// </para>
/// <para>
/// A catalogue that cannot be read is reported as unknown and validates nothing. Refusing a
/// valid world because a game update moved the data files would be a worse failure than the
/// one this prevents, and the game itself remains the authority either way.
/// </para>
/// </remarks>
public static partial class ServerWorlds
{
    /// <summary>The folder holding one subfolder per world, under the install's data folder.</summary>
    public const string WorldsFolder = "Worlds";

    /// <summary>The line the server logs when it rejects a world name.</summary>
    /// <remarks>
    /// Verbatim from <c>Assembly-CSharp</c>, where it is a composite format string
    /// (<c>"No such world name: {0}. Valid worlds: {1}."</c>). A readiness wait scans for this
    /// prefix, because the server prints it and then carries on running with no world.
    /// </remarks>
    public const string RejectionMarker = "No such world name:";

    /// <summary>Reads the accepted world ids out of a dedicated-server install.</summary>
    /// <param name="fs">The filesystem.</param>
    /// <param name="installDir">The install root, the folder holding the server executable.</param>
    public static WorldCatalogue Read(IFileSystem fs, string installDir)
    {
        ArgumentNullException.ThrowIfNull(fs);

        var worldsDir = WorldsDirIn(fs, installDir);
        if (worldsDir is null) return new WorldCatalogue([], Readable: false);

        var names = new SortedSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var dir in fs.EnumerateDirectories(worldsDir))
            {
                foreach (var file in fs.EnumerateFiles(dir, "*.xml", recurse: false))
                {
                    foreach (var id in IdsIn(fs.ReadAllText(file))) names.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorldCatalogue([], Readable: false);
        }

        // An install whose Worlds folder exists but yields nothing is a shape this code no
        // longer understands, not a game with no worlds. Unknown, so it validates nothing.
        return names.Count == 0
            ? new WorldCatalogue([], Readable: false)
            : new WorldCatalogue([.. names], Readable: true);
    }

    /// <summary>
    /// The world ids one world file declares, tutorials excluded.
    /// </summary>
    /// <remarks>
    /// A substring scan rather than an XML parse, deliberately: this reads game data files
    /// that a mod or an update may extend, and failing to validate is the correct outcome for
    /// a shape this does not recognise, while throwing on one would refuse a start that the
    /// game would have accepted.
    /// </remarks>
    public static IReadOnlyList<string> IdsIn(string xml)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(xml)) return found;

        foreach (Match element in WorldElement().Matches(xml))
        {
            var id = IdAttribute().Match(element.Value);
            if (!id.Success) continue;

            // The element's own body up to the next <World, so a tutorial marker belonging to
            // a later sibling cannot exclude this one.
            var bodyStart = element.Index + element.Length;
            var next = xml.IndexOf("<World", bodyStart, StringComparison.OrdinalIgnoreCase);
            var body = xml[bodyStart..(next < 0 ? xml.Length : next)];

            if (TutorialMarker().IsMatch(body)) continue;

            found.Add(id.Groups[1].Value);
        }

        return found;
    }

    /// <summary>
    /// The <c>Worlds</c> folder, whichever <c>*_Data</c> folder this install uses.
    /// </summary>
    /// <remarks>
    /// The dedicated server names it <c>rocketstation_DedicatedServer_Data</c> and the client
    /// names it <c>rocketstation_Data</c>. Both are searched rather than one being assumed, so
    /// the same reader works if this is ever pointed at a client install.
    /// </remarks>
    private static string? WorldsDirIn(IFileSystem fs, string installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return null;

        foreach (var data in new[] { "rocketstation_DedicatedServer_Data", "rocketstation_Data" })
        {
            var candidate = Path.Combine(installDir, data, "StreamingAssets", WorldsFolder);
            if (fs.DirectoryExists(candidate)) return candidate;
        }

        return null;
    }

    [GeneratedRegex(@"<World\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex WorldElement();

    [GeneratedRegex(@"\bId\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"<IsTutorial\b[^>]*Value\s*=\s*""true""", RegexOptions.IgnoreCase)]
    private static partial Regex TutorialMarker();
}
