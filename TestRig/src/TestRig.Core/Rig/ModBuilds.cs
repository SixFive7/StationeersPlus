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
    ///
    /// The control plane is the one exception on the CLIENT half, and it has to be stated
    /// here rather than special-cased at a call site. Without it, <c>deploy TestRig
    /// --target &lt;instance&gt;</c> wrote the control plane into StationeersLaunchPad's
    /// folder and then DELETED the Chainloader copy as a stale duplicate, which is the exact
    /// inverse of what it needs: it must be up before StationeersLaunchPad runs. That left
    /// no route at all from a merged-plugin build into an existing instance short of
    /// rebuilding the tree.
    /// </remarks>
    public LoadPath LoadPathOn(RigHalf half) => half switch
    {
        RigHalf.Server => Kind is ModKind.DevPluginServer or ModKind.DevPluginClient or ModKind.DevPluginRig
            ? LoadPath.LaunchPad
            : LoadPath.Chainloader,
        _ => IsControlPlane ? LoadPath.Chainloader : LoadPath.LaunchPad,
    };

    /// <summary>
    /// Whether this build is the rig's own control plane rather than a mod under test.
    /// </summary>
    /// <remarks>
    /// The control plane takes the Chainloader path on a client instance and nothing else
    /// does, because it has to load before StationeersLaunchPad runs. That is why
    /// <c>create</c> deploys it itself rather than leaving it to the deploy verb.
    ///
    /// This is a question about LOAD PATH, and it is deliberately not the same question as
    /// <see cref="IsRigPlugin"/>. Conflating them would move <c>ScenarioRunner</c> onto a
    /// client's Chainloader path, which is where the client's control plane has to be.
    /// </remarks>
    public bool IsControlPlane => Kind is ModKind.DevPluginClient or ModKind.DevPluginRig;

    /// <summary>
    /// Whether this build is one of the rig's own in-game plugins, past or present.
    /// </summary>
    /// <remarks>
    /// This is what decides whether deploying it has to sweep the others, and it is a
    /// question about the NAME rather than about the folder the source sits in.
    /// <see cref="IsControlPlane"/> used to stand in for it, and because
    /// <c>ScenarioRunner</c> lives under the dedicated server's own <c>dev-plugins/</c> it
    /// answered false there, so nothing ever swept it: the server ran
    /// <c>plugins/ScenarioRunner</c> and <c>mods/Local_TestRig</c> at once, which is two
    /// scenario dispatchers and two sim-tick patches. The merged plugin's own duplicate
    /// refusal cannot see that, because it recognises a second copy of ITSELF by GUID.
    /// </remarks>
    public bool IsRigPlugin => ControlPlugins.Names.Contains(Name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Which half a report or an action is about.</summary>
public enum RigHalf
{
    Server,
    Client,
}

/// <summary>
/// Where a repository mod lands inside an instance, spelled once.
/// </summary>
/// <remarks>
/// <para>
/// StationeersLaunchPad loads a local mod from <c>&lt;saveRoot&gt;/mods/&lt;Folder&gt;/</c>,
/// and the rig deploys under a <c>Local_</c> prefix so a deployed mod is distinguishable at a
/// glance from the developer's own seeded copy of the same name.
/// </para>
/// <para>
/// <b>This type exists because two places derived that path independently and disagreed.</b>
/// The deploy wrote <c>userdata/mods/Local_&lt;Mod&gt;/&lt;Mod&gt;.dll</c> while the playtest
/// engine's attestation looked for <c>userdata/mods/&lt;Mod&gt;/&lt;Mod&gt;.dll</c>, so every
/// check on a CORRECTLY deployed instance answered <c>binary-not-deployed</c>. It found
/// anything at all only because the developer's stale seeded copy happened to sit at exactly
/// the unprefixed path, which is the build attestation exists to rule out. One derivation, in
/// Core, used by both.
/// </para>
/// </remarks>
public static class LaunchPadMods
{
    /// <summary>The prefix the rig deploys under.</summary>
    public const string LocalPrefix = "Local_";

    /// <summary>The save root's mod folder, relative to an instance's data directory.</summary>
    public const string ModsRelativeDir = "userdata\\mods";

    /// <summary>The folder name a deploy of <paramref name="mod"/> writes.</summary>
    public static string DeployedFolderName(string mod) => LocalPrefix + mod;

    /// <summary>The deployed folder, under a resolved <c>userdata/mods</c> directory.</summary>
    public static string DeployedDir(string modsDir, string mod) =>
        Path.Combine(modsDir, DeployedFolderName(mod));

    /// <summary>The deployed assembly, under a resolved <c>userdata/mods</c> directory.</summary>
    public static string DeployedDll(string modsDir, string mod) =>
        Path.Combine(DeployedDir(modsDir, mod), mod + ".dll");

    /// <summary>
    /// The deployed assembly relative to an instance's DATA directory.
    /// </summary>
    /// <remarks>
    /// What attestation resolves against <c>ClientRig/data/&lt;instance&gt;/</c>. Derived from
    /// the same two members the deploy uses, so the two cannot drift apart again.
    /// </remarks>
    public static string DeployedRelativeDll(string mod) =>
        Path.Combine(ModsRelativeDir, DeployedFolderName(mod), mod + ".dll");

    /// <summary>
    /// The folder the DEVELOPER'S own mod set seeds a mod of this name into.
    /// </summary>
    /// <remarks>
    /// Unprefixed, because that copy is a verbatim mirror of the developer's <c>mods/</c>
    /// folder. A deploy has to remove it: both folders carry an About.xml, StationeersLaunchPad
    /// loads both, and the mod is then loaded twice with every Harmony patch registered twice.
    /// </remarks>
    public static string SeededDir(string modsDir, string mod) => Path.Combine(modsDir, mod);
}

/// <summary>
/// The names the rig's in-game plugin has gone by, and what that means for a deploy.
/// </summary>
/// <remarks>
/// Both halves need this, which is why it is here and not in either one's layout type.
/// </remarks>
public static class ControlPlugins
{
    /// <summary>The merged plugin, which drives both halves and wins whenever it is built.</summary>
    public const string Merged = "TestRig";

    /// <summary>The CLIENT plugin the merged one replaces, kept as the fallback during the transition.</summary>
    public const string Legacy = "ClientDriver";

    /// <summary>The DEDICATED SERVER plugin the merged one replaces.</summary>
    /// <remarks>
    /// No HTTP control plane of its own: it is the scenario dispatcher and the sim-tick
    /// patch, which the merged plugin absorbed. It still has to be swept, because two
    /// dispatchers and two sim-tick patches is the same double-load the other pair causes.
    /// </remarks>
    public const string LegacyServer = "ScenarioRunner";

    /// <summary>
    /// Every name that has ever been one of the rig's in-game plugins, newest first.
    /// </summary>
    /// <remarks>
    /// The set exists because these are DIFFERENT plugins, not versions of one. The merged
    /// plugin refuses a second load of itself by GUID, and that check cannot see any
    /// predecessor at all: separate GUIDs means separate Awakes, separate Harmony
    /// registrations of the same methods and, for the pair that has one, two binds of the
    /// same control port. Deploying one therefore has to remove the others, which is what
    /// <see cref="Superseded"/> is for.
    ///
    /// <c>ScenarioRunner</c> was missing from this list, and it is the case that proves the
    /// list has to be about names rather than about which folder a source tree sits in: it
    /// lives under the dedicated server's own <c>dev-plugins/</c>, so every derived "is this
    /// the control plane" test answered false for it and nothing ever swept it. Measured on
    /// 2026-08-15, the server was carrying <c>BepInEx/plugins/ScenarioRunner/</c> and
    /// <c>data/mods/Local_TestRig/</c> at the same time.
    ///
    /// A new plugin is added at the FRONT of this list, never appended.
    /// </remarks>
    public static readonly IReadOnlyList<string> Names = [Merged, Legacy, LegacyServer];

    /// <summary>
    /// The plugin names that must NOT be present once <paramref name="deployed"/> is.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="Names"/> rather than hardcoded per call site, so this keeps
    /// working in every direction: a rig that has not built the merged plugin deploys a
    /// predecessor and this removes the merged one, exactly as the other way round. Nothing
    /// has to know which is newer.
    /// </remarks>
    public static IEnumerable<string> Superseded(string deployed) =>
        Names.Where(name => !string.Equals(name, deployed, StringComparison.OrdinalIgnoreCase));
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
            if (!folder.StartsWith(LaunchPadMods.LocalPrefix, StringComparison.Ordinal)) continue;

            var bare = folder[LaunchPadMods.LocalPrefix.Length..];
            if (bare.Length == 0) continue;
            if (Find(bare) is not null) lost.Add(folder);
        }
        return lost;
    }
}
