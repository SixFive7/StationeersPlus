using TestRig.Core.Abstractions;

namespace TestRig.Core.Rig;

/// <summary>What kind of thing a name resolved to, which decides where it deploys.</summary>
public enum ModKind
{
    /// <summary>A released mod under <c>Mods/</c>.</summary>
    Mod,

    /// <summary>Work in progress under <c>Plans/</c>. Deployed by name or not at all.</summary>
    Plan,

    /// <summary>A developer plugin under the server half's <c>dev-plugins/</c>.</summary>
    DevPluginServer,

    /// <summary>A developer plugin under the client half's <c>dev-plugins/</c>.</summary>
    DevPluginClient,

    /// <summary>
    /// A developer plugin under the rig's own <c>TestRig/dev-plugins/</c>.
    /// </summary>
    /// <remarks>
    /// The merged <c>TestRig</c> plugin loads into BOTH halves, so it sits above either
    /// one's folder rather than inside it. Nothing could deploy it before this kind
    /// existed: the search knew three trees and this was not one of them.
    /// </remarks>
    DevPluginRig,
}

/// <summary>Which loader picks a payload up. A payload in both fires Awake twice.</summary>
public enum LoadPath
{
    /// <summary>
    /// <c>BepInEx/plugins/&lt;X&gt;/</c>, loaded by the BepInEx Chainloader before
    /// StationeersLaunchPad runs.
    /// </summary>
    Chainloader,

    /// <summary>
    /// <c>&lt;saveRoot&gt;/mods/Local_&lt;X&gt;/</c>, loaded by StationeersLaunchPad. Needs
    /// an <c>About/About.xml</c>.
    /// </summary>
    LaunchPad,
}

/// <summary>A resolved repository build: where its DLL is, and what it is.</summary>
public sealed record ModBuild(
    string Name,
    string Dir,
    ModKind Kind,
    string Configuration,
    string Dll,
    string About)
{
    /// <summary>
    /// Which loader this payload belongs to on a given half.
    /// </summary>
    /// <remarks>
    /// Both dev-plugin kinds take the StationeersLaunchPad path on the SERVER half because
    /// they carry an About.xml, while a released mod takes the Chainloader path there. On
    /// the CLIENT half it is the other way round for released mods, because
    /// <c>ClientDriver</c> already occupies the Chainloader path and has to load before
    /// StationeersLaunchPad runs.
    ///
    /// Spec D-14: the PowerShell staleness scan reported a dev-plugin found in the server's
    /// Chainloader folder with the remedy <c>deploy &lt;X&gt; -Target server</c>, which
    /// would MOVE the payload to the other load path rather than refresh it. Staleness has
    /// to know which path a payload belongs in, so the answer lives here rather than being
    /// re-derived at each report site.
    /// </remarks>
    public LoadPath LoadPathOn(RigHalf half) => half switch
    {
        RigHalf.Server => Kind is ModKind.DevPluginServer or ModKind.DevPluginClient or ModKind.DevPluginRig
            ? LoadPath.LaunchPad
            : LoadPath.Chainloader,
        _ => LoadPath.LaunchPad,
    };

    /// <summary>
    /// Whether this build is the rig's own control plane rather than a mod under test.
    /// </summary>
    /// <remarks>
    /// The control plane takes the Chainloader path on a client instance and nothing else
    /// does, because it has to load before StationeersLaunchPad runs. That is why
    /// <c>create</c> deploys it itself rather than leaving it to the deploy verb.
    /// </remarks>
    public bool IsControlPlane => Kind is ModKind.DevPluginClient or ModKind.DevPluginRig;
}

/// <summary>Which half a report or an action is about.</summary>
public enum RigHalf
{
    Server,
    Client,
}

/// <summary>Finding this repository's built mods.</summary>
public sealed class ModBuilds
{
    private readonly IFileSystem _fs;
    private readonly RigEnvironment _env;

    public ModBuilds(IFileSystem fs, RigEnvironment env)
    {
        _fs = fs;
        _env = env;
    }

    /// <summary>
    /// Where a mod's built DLL is, and what kind of thing it is, or null when the name
    /// matches nothing.
    /// </summary>
    /// <remarks>
    /// Search order: <c>Mods/&lt;X&gt;</c>, then <c>Plans/&lt;X&gt;</c>, then the rig's own
    /// <c>dev-plugins/&lt;X&gt;</c>, then each half's. First hit wins, so <c>Mods/</c> beats
    /// a name clash (COMMON-056).
    ///
    /// <c>TestRig/dev-plugins/</c> is searched BEFORE the two per-half folders, because the
    /// merged plugin replaces one plugin in each of them and shares its name with neither.
    /// It was missing from this list entirely, which is why nothing could deploy it.
    /// </remarks>
    public ModBuild? Find(string mod, string configuration = "Release")
    {
        if (string.IsNullOrWhiteSpace(mod)) return null;

        (string Dir, ModKind Kind)[] candidates =
        [
            (Path.Combine(_env.RepoRoot, "Mods", mod), ModKind.Mod),
            (Path.Combine(_env.RepoRoot, "Plans", mod), ModKind.Plan),
            (Path.Combine(_env.RigHome, "dev-plugins", mod), ModKind.DevPluginRig),
            (Path.Combine(_env.RigHome, "DedicatedServer", "dev-plugins", mod), ModKind.DevPluginServer),
            (Path.Combine(_env.RigHome, "ClientRig", "dev-plugins", mod), ModKind.DevPluginClient),
        ];

        foreach (var (dir, kind) in candidates)
        {
            if (!_fs.DirectoryExists(dir)) continue;

            return new ModBuild(
                mod,
                dir,
                kind,
                configuration,
                ResolveDll(dir, mod, configuration),
                Path.Combine(dir, mod, "About"));
        }

        return null;
    }

    /// <summary>
    /// The DLL path, tolerating the SDK's target-framework subfolder.
    /// </summary>
    /// <remarks>
    /// COMMON-058. The PowerShell hardcoded <c>&lt;dir&gt;\&lt;Mod&gt;\bin\&lt;Config&gt;\&lt;Mod&gt;.dll</c>,
    /// the pre-SDK layout. Any project that gains an <c>&lt;AppendTargetFrameworkToOutputPath&gt;</c>
    /// default becomes invisible to both deploy and staleness, and the only report is
    /// "not found. Skipping."
    ///
    /// The flat path still wins when it exists, so nothing about an existing tree changes.
    /// Otherwise one level of subfolder is searched and the NEWEST match wins, because a
    /// multi-targeted project has several and the freshest is the one just built. When
    /// nothing exists at all the flat path is returned anyway: it is what the caller's
    /// "build it first" message should name.
    /// </remarks>
    private string ResolveDll(string dir, string mod, string configuration)
    {
        var flat = Path.Combine(dir, mod, "bin", configuration, mod + ".dll");
        if (_fs.FileExists(flat)) return flat;

        var configDir = Path.Combine(dir, mod, "bin", configuration);
        if (!_fs.DirectoryExists(configDir)) return flat;

        string? best = null;
        var bestAt = DateTimeOffset.MinValue;

        foreach (var tfmDir in _fs.EnumerateDirectories(configDir))
        {
            var candidate = Path.Combine(tfmDir, mod + ".dll");
            if (!_fs.FileExists(candidate)) continue;

            var at = _fs.GetLastWriteTimeUtc(candidate);
            if (best is null || at > bestAt)
            {
                best = candidate;
                bestAt = at;
            }
        }

        return best ?? flat;
    }

    /// <summary>
    /// Every released mod, which is what a deploy with no named mod means.
    /// </summary>
    /// <remarks>
    /// <c>Plans/</c> and both <c>dev-plugins/</c> trees are deliberately excluded: work in
    /// progress and rig tooling deploy by name or not at all (COMMON-060). An absent
    /// <c>Mods/</c> is an empty set, not a throw (COMMON-061).
    /// </remarks>
    public IReadOnlyList<string> DeployableMods()
    {
        var root = Path.Combine(_env.RepoRoot, "Mods");
        if (!_fs.DirectoryExists(root)) return [];

        var names = new List<string>();
        foreach (var dir in _fs.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) continue;
            if (string.Equals(name, "Template", StringComparison.Ordinal)) continue;
            names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>
    /// Names a wipe took that this repository had deployed, from a set of folder names.
    /// </summary>
    /// <remarks>
    /// Spec D-10. The PowerShell intersected against released mods only, so every wiped
    /// <c>Plans/</c> mod and every wiped dev-plugin, which is exactly where dev-plugins are
    /// deployed, disappeared with no warning from the one message whose job is naming what
    /// the wipe took. Resolving each name through <see cref="Find"/> covers all four trees.
    /// </remarks>
    public IReadOnlyList<string> RepositoryFoldersAmong(IEnumerable<string> folderNames)
    {
        var lost = new List<string>();
        foreach (var folder in folderNames)
        {
            if (string.IsNullOrEmpty(folder)) continue;
            if (!folder.StartsWith("Local_", StringComparison.Ordinal)) continue;

            var bare = folder["Local_".Length..];
            if (bare.Length == 0) continue;
            if (Find(bare) is not null) lost.Add(folder);
        }
        return lost;
    }
}
