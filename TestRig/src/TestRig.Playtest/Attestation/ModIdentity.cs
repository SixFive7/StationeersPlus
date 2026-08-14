using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Attestation;

/// <summary>
///     Which mod a check is about, derived from where the check lives.
/// </summary>
/// <param name="ModName">The code name: the folder under <c>Mods/</c>.</param>
/// <param name="Guid">The BepInEx plugin guid, from the mod's own About.xml.</param>
/// <param name="RepoRoot">The monorepo root.</param>
/// <param name="BuildDllPath">The build under test, by the repository's fixed csproj convention.</param>
/// <param name="DeployedRelativePath">Where it lands inside an instance's data folder.</param>
public sealed record ModIdentity(
    string ModName,
    string Guid,
    string RepoRoot,
    string BuildDllPath,
    string DeployedRelativePath);

/// <summary>
///     Derives a check's mod identity from the check's own source location.
/// </summary>
/// <remarks>
///     <para>
///     This replaces five declared hashtable keys that nothing validated. Three of them
///     (<c>Mod</c>, <c>DllPath</c>, <c>DeployedRelativePath</c>) were always derivable and
///     were declared anyway; the other two were counts a check asserted about its own build.
///     A check that omitted any of them silently skipped every attestation step that could
///     see a build and still reported a clean pass.
///     </para>
///     <para>
///     The input is <c>[CallerFilePath]</c>, which the compiler writes into the call site.
///     A check cannot lie about a value it does not supply, and it cannot drift, because
///     moving the file changes the answer.
///     </para>
/// </remarks>
public static partial class ModIdentityResolver
{
    /// <summary>The folder a mod's checks live in, under <c>Mods/&lt;Mod&gt;/</c>.</summary>
    public const string PlaytestsFolder = "playtests";

    /// <summary>The folder mods live in, under the repository root.</summary>
    public const string ModsFolder = "Mods";

    /// <summary>
    ///     Resolves the identity, or throws an inconclusive signal explaining what is wrong.
    /// </summary>
    public static ModIdentity Resolve(string checkSourceFile, IFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(checkSourceFile))
        {
            throw PlaytestSignal.Inconclusive(
                "The check did not record where it was written, so the mod under test cannot be derived and nothing can be attested. " +
                "CheckSpec takes its source file from [CallerFilePath]; passing that argument explicitly, or constructing the spec somewhere other than the check's own file, defeats it.",
                Detectors.ModIdentityUnresolved);
        }

        var normalized = checkSourceFile.Replace('/', '\\');
        var segments = normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i + 2 < segments.Length; i++)
        {
            if (!string.Equals(segments[i], ModsFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(segments[i + 2], PlaytestsFolder, StringComparison.OrdinalIgnoreCase)) continue;

            var modName = segments[i + 1];
            var repoRoot = string.Join('\\', segments.Take(i));

            // Windows absolute paths lose their leading separator only when the path was
            // rooted at a share; a drive-rooted path keeps "C:" as its first segment.
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal)) repoRoot = @"\\" + repoRoot;

            var modRoot = Path.Combine(repoRoot, ModsFolder, modName, modName);
            var identity = new ModIdentity(
                modName,
                ReadModId(files, Path.Combine(modRoot, "About", "About.xml"), modName),
                repoRoot,
                Path.Combine(modRoot, "bin", "Release", modName + ".dll"),
                Path.Combine("userdata", "mods", modName, modName + ".dll"));

            return identity;
        }

        throw PlaytestSignal.Inconclusive(
            $"The check lives at '{checkSourceFile}', which is not under Mods/<Mod>/{PlaytestsFolder}/, so the mod under test cannot be derived from it. " +
            "Attestation is derived from a check's location precisely so it cannot be declared wrongly; move the check next to the mod it is about.",
            Detectors.ModIdentityUnresolved);
    }

    /// <summary>
    ///     The plugin guid, from the mod's own About.xml.
    /// </summary>
    /// <remarks>
    ///     About.xml is already the single source of truth for the mod id and is read by the
    ///     game itself, so nothing new has to be kept in sync. Extraction is a substring match
    ///     rather than an XML parse: the whole file is BBCode-laden mod description text and
    ///     the one element that matters is unambiguous.
    /// </remarks>
    private static string ReadModId(IFileSystem files, string aboutPath, string modName)
    {
        if (!files.FileExists(aboutPath))
        {
            throw PlaytestSignal.Inconclusive(
                $"'{modName}' has no About.xml at '{aboutPath}', so its plugin guid cannot be derived and the live process cannot be asked what it loaded. " +
                "Every mod in this repository has one; an absent one means the check is pointed at something that is not a mod folder.",
                Detectors.ModIdentityUnresolved);
        }

        var match = ModIdPattern().Match(files.ReadAllText(aboutPath));
        if (!match.Success)
        {
            throw PlaytestSignal.Inconclusive(
                $"'{aboutPath}' has no <ModID> element, so the plugin guid cannot be derived. Attestation asks the running process what it loaded for that guid, and there is nothing to ask about.",
                Detectors.ModIdentityUnresolved);
        }

        return match.Groups[1].Value.Trim();
    }

    [GeneratedRegex(@"<ModID>\s*([^<]+?)\s*</ModID>", RegexOptions.IgnoreCase)]
    private static partial Regex ModIdPattern();
}
