using System.Text.RegularExpressions;
using System.Xml.Linq;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Rig;

/// <summary>
/// The ambient machine values the rig reads: environment variables, the shell's
/// Documents folder, the machine name.
/// </summary>
/// <remarks>
/// A seam rather than direct calls, so the suite never reads the developer's real
/// environment (COMMON-004). The PowerShell took injectable overrides for exactly two of
/// these and read the rest out of process scope, which is why its tests had to be careful
/// about ordering.
/// </remarks>
public interface IAmbient
{
    string? GetVariable(string name);

    /// <summary>The Windows shell MyDocuments folder. Never hardcoded (COMMON-026).</summary>
    string MyDocuments { get; }

    string MachineName { get; }
}

/// <summary>The real machine.</summary>
public sealed class SystemAmbient : IAmbient
{
    public static readonly SystemAmbient Instance = new();

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string MyDocuments => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    public string MachineName => Environment.MachineName;
}

/// <summary>Where a resolved value came from, so a message can name its own source.</summary>
public sealed record ResolvedRoot(string Root, string Source);

/// <summary>
/// Everything outside the rig folder that the rig has to find: the developer's install,
/// SteamCMD, the game's user-data root, the instances root, and a game version.
/// </summary>
/// <remarks>
/// One validity test for the install path, replacing three. The server half checked for
/// <c>rocketstation_Data\Managed\Assembly-CSharp.dll</c>, the client half checked for
/// <c>rocketstation.exe</c>, and the reset checked for <c>BepInEx\config</c> and returned
/// null instead of throwing. A path pointing at a dedicated-server install passed the
/// first and failed the second, with two different messages, and the third silently
/// degraded. Both markers are checked here because both halves genuinely need both.
/// </remarks>
public sealed class RigEnvironment
{
    /// <summary>The version string every caller compares against when nothing could be read.</summary>
    /// <remarks>
    /// The literal matters: three separate staleness comparisons test against it
    /// (COMMON-039), and a null would change every verdict from "cannot tell" to "differs".
    /// </remarks>
    public const string UnknownVersion = "unknown";

    private readonly IFileSystem _fs;
    private readonly IAmbient _ambient;
    private readonly string? _steamcmdOverride;
    private readonly string? _userDataOverride;
    private readonly string? _installOverride;

    private string? _installCache;

    /// <param name="rigHome">The <c>TestRig/</c> directory.</param>
    /// <param name="repoRoot">Defaults to the rig home's parent (COMMON-002).</param>
    /// <param name="buildProps">Defaults to <c>&lt;RepoRoot&gt;\Directory.Build.props</c> (COMMON-003).</param>
    /// <param name="steamcmdPath">Injectable override, so a test never reads the real environment.</param>
    /// <param name="userDataDir">Injectable override for the tier-1 user-data root.</param>
    /// <param name="stationeersPath">
    /// Injectable override for the install itself, replacing the props lookup but NOT the
    /// validation: an override that does not point at a client install is refused with the
    /// same message a wrong props value gets.
    /// </param>
    public RigEnvironment(
        IFileSystem fs,
        string rigHome,
        IAmbient? ambient = null,
        string? repoRoot = null,
        string? buildProps = null,
        string? steamcmdPath = null,
        string? userDataDir = null,
        string? stationeersPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rigHome);

        _fs = fs;
        _ambient = ambient ?? SystemAmbient.Instance;
        _steamcmdOverride = steamcmdPath;
        _userDataOverride = userDataDir;
        _installOverride = string.IsNullOrWhiteSpace(stationeersPath) ? null : stationeersPath.Trim();

        RigHome = rigHome;
        RepoRoot = string.IsNullOrWhiteSpace(repoRoot)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(rigHome))) ?? rigHome
            : repoRoot;
        BuildProps = string.IsNullOrWhiteSpace(buildProps)
            ? Path.Combine(RepoRoot, "Directory.Build.props")
            : buildProps;
    }

    /// <summary>The <c>TestRig/</c> directory (COMMON-006).</summary>
    public string RigHome { get; }

    /// <summary>The monorepo root (COMMON-006).</summary>
    public string RepoRoot { get; }

    /// <summary>Where <c>&lt;StationeersPath&gt;</c> is read from.</summary>
    public string BuildProps { get; }

    // ---- the developer's install (COMMON-018 to COMMON-022) ----------------

    /// <summary>
    /// The developer's Stationeers CLIENT install. Read-only from the rig, always.
    /// </summary>
    /// <exception cref="RigConfigurationException">
    /// The props file is missing, the property is empty, or the path is not a client install.
    /// </exception>
    public string StationeersPath()
    {
        // Cached for the process lifetime: every verb asks, and the answer cannot change
        // under a running command.
        if (_installCache is not null) return _installCache;

        if (_installOverride is not null) return _installCache = Validate(_installOverride);

        if (!_fs.FileExists(BuildProps))
        {
            throw new RigConfigurationException(
                $"Directory.Build.props not found at {BuildProps}. Copy Directory.Build.props.template to "
                + "Directory.Build.props and set <StationeersPath>. See DEV.md.");
        }

        string? configured;
        try
        {
            var doc = XDocument.Parse(_fs.ReadAllText(BuildProps));
            configured = doc.Root?
                .Elements().Where(static e => e.Name.LocalName == "PropertyGroup")
                .Elements().FirstOrDefault(static e => e.Name.LocalName == "StationeersPath")?
                .Value;
        }
        catch (System.Xml.XmlException ex)
        {
            throw new RigConfigurationException(
                $"Directory.Build.props at {BuildProps} is not well-formed XML ({ex.Message}). See DEV.md.");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new RigConfigurationException(
                "<StationeersPath> in Directory.Build.props is empty. Set it to your Stationeers client install. See DEV.md.");
        }

        // Trimmed before use: an XML element value routinely carries the surrounding
        // whitespace of a pretty-printed file.
        return _installCache = Validate(configured.Trim());
    }

    /// <summary>Both install markers, checked in one place so an override cannot skip them.</summary>
    private string Validate(string path)
    {
        var missing = new List<string>();
        if (!_fs.FileExists(Path.Combine(path, "rocketstation.exe"))) missing.Add("rocketstation.exe");
        if (!_fs.FileExists(Path.Combine(path, "rocketstation_Data", "Managed", "Assembly-CSharp.dll")))
        {
            missing.Add(@"rocketstation_Data\Managed\Assembly-CSharp.dll");
        }

        if (missing.Count > 0)
        {
            throw new RigConfigurationException(
                $"<StationeersPath>={path} is missing {string.Join(" and ", missing)}. This rig needs the "
                + "Stationeers CLIENT install: the client half hard-links its tree, and the server half mirrors "
                + "its BepInEx loader. A dedicated-server install has neither. See DEV.md.");
        }

        return path;
    }

    /// <summary>Forgets the cached install path (COMMON-005), so a re-point cannot serve a stale answer.</summary>
    public void ForgetInstallCache() => _installCache = null;

    /// <summary>
    /// The install, or null when it cannot be resolved at all.
    /// </summary>
    /// <remarks>
    /// The session subsystem takes the install as a path rather than as a resolver, and it
    /// has to keep working on a machine where the props file is missing, because "is the rig
    /// locked" must not depend on an unrelated build property. One reader, degrading here
    /// rather than a second parser somewhere else that would drift from this one.
    /// </remarks>
    public string? StationeersPathOrNull()
    {
        try
        {
            return StationeersPath();
        }
        catch (RigConfigurationException)
        {
            return null;
        }
    }

    // ---- SteamCMD (COMMON-023 to COMMON-025) -------------------------------

    /// <summary>The SteamCMD executable, from the injected override then the environment.</summary>
    public string SteamcmdPath()
    {
        var path = !string.IsNullOrWhiteSpace(_steamcmdOverride)
            ? _steamcmdOverride
            : _ambient.GetVariable("STEAMCMD_PATH");

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new RigConfigurationException(
                "STEAMCMD_PATH environment variable is not set. Set it to the absolute path of steamcmd.exe. See DEV.md.");
        }
        if (!_fs.FileExists(path))
        {
            throw new RigConfigurationException($"STEAMCMD_PATH={path} does not exist. See DEV.md.");
        }
        return path;
    }

    // ---- the game's user data (COMMON-026) ---------------------------------

    /// <summary>
    /// The game's user-data root: Documents\My Games\Stationeers.
    /// </summary>
    /// <remarks>
    /// TIER 1. Read-only from the rig, unconditionally. Every tier-1 guard downstream keys
    /// off this path, and it is resolved from the shell folder rather than hardcoded so
    /// nothing here is tied to one developer's layout.
    /// </remarks>
    public string UserDataPath() =>
        !string.IsNullOrWhiteSpace(_userDataOverride)
            ? _userDataOverride
            : Path.Combine(_ambient.MyDocuments, "My Games", "Stationeers");

    // ---- the instances root (COMMON-027 to COMMON-029) ---------------------

    /// <summary>
    /// Where a NEW instance tree is built, and where that answer came from.
    /// </summary>
    /// <remarks>
    /// One resolution order, replacing three. The playtest harness's copy omitted the
    /// environment-variable step entirely, so a rig built under
    /// <c>STATIONEERS_CLIENTRIG_ROOT</c> was invisible to it. An instance that already
    /// EXISTS uses the root recorded in its registry entry instead; that lookup needs the
    /// registry and lives on the client half.
    /// </remarks>
    public ResolvedRoot DefaultInstancesRoot(string? typedOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(typedOverride))
        {
            return new ResolvedRoot(typedOverride, "--instances-root (typed on this command)");
        }

        var fromEnv = _ambient.GetVariable("STATIONEERS_CLIENTRIG_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return new ResolvedRoot(fromEnv, "$env:STATIONEERS_CLIENTRIG_ROOT");
        }

        return new ResolvedRoot(
            Path.Combine(RigHome, "ClientRig", "instances"),
            "the default ClientRig/instances folder");
    }

    /// <summary>The machine this launcher is running on, recorded in a provision stamp (CLIENT-095).</summary>
    public string MachineName => _ambient.MachineName;

    // ---- the game version (COMMON-039, COMMON-040) -------------------------

    /// <summary>
    /// The game version an install carries, or the literal <c>unknown</c>.
    /// </summary>
    /// <remarks>
    /// From <c>&lt;data folder&gt;\StreamingAssets\version.ini</c>, whose first line reads
    /// <c>UPDATEVERSION=Update 0.2.6428.27798</c>. The launcher's own copy read a
    /// <c>version.txt</c> at the install root; no such file has ever existed in any
    /// Stationeers install, so every provision stamp recorded the executable's Unity
    /// FileVersion instead, which is a different string from the one the baseline records.
    /// Nothing could compare a stamp against a baseline and a game update could never mark
    /// anything stale.
    ///
    /// Deliberately the same algorithm as <c>BaselineStore.GameVersion</c>: both data
    /// directories tried in order, a dotted number preferred, the stripped first line as a
    /// fallback. The two must agree or a stamp and a baseline become incomparable again,
    /// which is the exact fault this reader was written to fix.
    /// </remarks>
    public string InstallVersion(string? installDir)
    {
        if (string.IsNullOrWhiteSpace(installDir)) return UnknownVersion;

        foreach (var dataDir in new[] { "rocketstation_Data", "rocketstation_DedicatedServer_Data" })
        {
            var file = Path.Combine(installDir, dataDir, "StreamingAssets", "version.ini");
            if (!_fs.FileExists(file)) continue;

            string first;
            try
            {
                var lines = _fs.ReadLines(file);
                if (lines.Count == 0) continue;
                first = lines[0];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var match = Regex.Match(first, @"(\d+(?:\.\d+)+)");
            if (match.Success) return match.Value;

            var stripped = Regex.Replace(first, @"^\s*UPDATEVERSION\s*=\s*", "").Trim();
            if (!string.IsNullOrEmpty(stripped)) return stripped;
        }

        return UnknownVersion;
    }

    /// <summary>
    /// The developer's own install version, degrading to <c>unknown</c> on any failure.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="InstallVersion"/> because resolving the install path itself
    /// throws when the props file is wrong, and a status report must still print
    /// (SERVER-150, CLIENT-332).
    /// </remarks>
    public string SourceVersionOrUnknown()
    {
        try
        {
            return InstallVersion(StationeersPath());
        }
        catch (RigConfigurationException)
        {
            return UnknownVersion;
        }
    }

    /// <summary>
    /// Newest write time under a folder, assemblies preferred, or null when there is nothing.
    /// </summary>
    /// <remarks>
    /// The staleness comparator for both halves. Same algorithm as
    /// <c>ResetPlanner.NewestBuildTime</c>: <c>*.dll</c> first because a mod folder's DLL
    /// is what actually changed, everything otherwise so an About-only payload still has a
    /// time.
    /// </remarks>
    public DateTimeOffset? NewestBuildTime(string path)
    {
        if (!_fs.DirectoryExists(path)) return null;

        var files = _fs.EnumerateFiles(path, "*.dll", recurse: true);
        if (files.Count == 0) files = _fs.EnumerateFiles(path, "*", recurse: true);
        if (files.Count == 0) return null;

        DateTimeOffset? newest = null;
        foreach (var file in files)
        {
            var at = _fs.GetLastWriteTimeUtc(file);
            if (newest is null || at > newest) newest = at;
        }
        return newest;
    }
}

/// <summary>
/// The rig is configured wrong: a path that is missing, empty or pointing at the wrong
/// kind of install.
/// </summary>
/// <remarks>
/// Distinct from <see cref="Session.RigRefusalException"/>: a refusal teaches that the
/// command cannot mean what was asked, while this says the machine is not set up. The CLI
/// prints it plainly, with no stack trace, and both name DEV.md.
/// </remarks>
public sealed class RigConfigurationException : Exception
{
    public RigConfigurationException(string message) : base(message)
    {
    }
}
