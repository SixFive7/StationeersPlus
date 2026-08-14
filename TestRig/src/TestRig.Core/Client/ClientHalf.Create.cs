using System.Globalization;
using System.Text.Json;
using TestRig.Core.Abstractions;
using TestRig.Core.Rig;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>
/// What <c>create</c> was asked for.
/// </summary>
/// <remarks>
/// Every identity field is NULLABLE, and null means "the caller did not type it". That
/// replaces the PowerShell's hand-maintained map of which flags were bound, which existed
/// only because <c>$PSBoundParameters</c> is per-scope and answers false inside any
/// function that asks. The distinction is load-bearing on a rebuild: a value that was not
/// typed is KEPT from the existing entry.
/// </remarks>
public sealed record CreateOptions
{
    public required string Instance { get; init; }

    public string? CallerId { get; init; }

    /// <summary>Rebuild an instance that already has a tree.</summary>
    public bool Force { get; init; }

    /// <summary>Null keeps the existing role, so a rebuild cannot silently demote a host.</summary>
    public string? Role { get; init; }

    /// <summary>Null keeps the existing control port.</summary>
    public int? Port { get; init; }

    /// <summary>Null keeps the existing game port, so a rebuild cannot move it under a joiner.</summary>
    public int? GamePort { get; init; }

    /// <summary>Null keeps the existing ClientId. The server keys a player's body on it.</summary>
    public string? ClientId { get; init; }

    /// <summary>Null keeps the existing username.</summary>
    public string? Username { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public bool? ForceGameplayInput { get; init; }

    /// <summary>Seed the instance's mod set from the developer's folder. On by default.</summary>
    public bool SeedMods { get; init; } = true;

    public string Desktop { get; init; } = RigConstants.DefaultDesktop;

    /// <summary>Default window width when nothing was typed and no entry exists.</summary>
    public const int DefaultWidth = 800;

    /// <summary>Default window height when nothing was typed and no entry exists.</summary>
    public const int DefaultHeight = 600;
}

public sealed partial class ClientHalf
{
    /// <summary>
    /// Files the game or a mod writes into the install ROOT.
    /// </summary>
    /// <remarks>
    /// These must NEVER be hard links: a hard link shares the file data, so a write here
    /// would reach into the developer's install (CLIENT-066).
    /// </remarks>
    private static readonly string[] RealCopyRootFiles = ["doorstop_config.ini", "Fixing The Controls modifiers.ini"];

    /// <summary>Regenerated, and resolved against the working directory, so not worth carrying (CLIENT-067).</summary>
    private static readonly string[] SkipRootFiles = ["imgui.ini", "output_log.txt"];

    /// <summary>
    /// Builds or rebuilds ONE instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rebuild replaces the instance TREE. It does NOT reset <c>data/&lt;instance&gt;/</c>:
    /// the save root, the logs, the pid file and the game-written <c>setting.xml</c> all
    /// survive, and only <c>userdata/mods</c> is rewritten. That is deliberate, because a
    /// staged save must not evaporate on a plugin rebuild, but it does mean a rebuild is not
    /// a clean slate. Stopping an instance clears <c>StartLocalHost</c> for the one case
    /// where a stale value would silently change what the next run is.
    /// </para>
    /// <para>
    /// ORDER CHANGED FROM THE POWERSHELL, deliberately (CLIENT-076 fixed). The registry
    /// entry is written BEFORE the save-path redirect is attempted. The redirect THROWS for
    /// a host when <c>stationeers.launchpad.cfg</c> does not exist yet, and the PowerShell
    /// threw after the tree was built and before the entry was written, leaving a tree with
    /// no registry entry: every one of the three remedies its own message named was
    /// unreachable, and the only escape was creating as a client, starting once, and
    /// rebuilding as a host, which nothing said. With the entry written first, the message's
    /// own remedy works.
    /// </para>
    /// </remarks>
    public async Task<InstanceEntry> CreateAsync(CreateOptions options, CancellationToken ct = default)
    {
        var name = options.Instance;

        // A comma-separated target is a list, and create builds exactly one thing.
        if (name.Contains(',', StringComparison.Ordinal))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, "'create' takes one instance at a time.");
        }

        AssertGate("create", options.CallerId);

        var source = _env.StationeersPath();

        // The whole read-validate-claim runs inside the registry's critical section, so two
        // concurrent creates cannot both pick the same free index and hand two instances one
        // ClientId. The tree build happens AFTER the section is released: holding a
        // cross-process mutex across a 1,050 file link operation would serialise minutes of
        // work for no gain.
        var claim = _registry.Update<CreateClaim>(current => BuildClaim(current, options, source));

        var paths = claim.Paths;

        if (_fs.DirectoryExists(paths.Tree))
        {
            Say($"[Provision] Removing existing tree {paths.Tree} ...");
            _fs.DeleteDirectory(paths.Tree, recursive: true);
        }

        var stats = new TreeStats();

        foreach (var dir in new[] { paths.Tree, paths.Data, paths.UserData, paths.LogDir })
        {
            _fs.CreateDirectory(dir);
        }

        Say("[Provision] Linking rocketstation_Data ...");
        // app.info is a real copy purely so a write cannot reach the source. It is NOT a
        // persistentDataPath lever: the player takes company and product from the serialized
        // PlayerSettings inside globalgamemanagers, and editing app.info changes nothing.
        TreeOps.LinkTree(
            _fs,
            Path.Combine(source, "rocketstation_Data"),
            Path.Combine(paths.Tree, "rocketstation_Data"),
            ["app.info"],
            stats);

        Say("[Provision] Linking MonoBleedingEdge ...");
        TreeOps.LinkTree(_fs, Path.Combine(source, "MonoBleedingEdge"), Path.Combine(paths.Tree, "MonoBleedingEdge"), null, stats);

        Say("[Provision] Copying BepInEx (real copy: config, plugins, cache and logs must be per-instance) ...");
        // A separate BepInEx root is what buys per-instance config, plugins, cache,
        // LogOutput.log and InspectorPlus request/snapshot folders in one move. The BepInEx
        // root is always beside rocketstation.exe and no environment variable relocates it.
        TreeOps.CopyTree(_fs, Path.Combine(source, "BepInEx"), paths.BepInEx);

        // The copied tree carries the developer's own logs, assembly cache and InspectorPlus
        // request and snapshot folders. All three are per-run state and none of them belongs
        // to a fresh instance; the cache is recreated empty so BepInEx does not have to.
        foreach (var stale in _fs.EnumerateFiles(paths.BepInEx, "LogOutput.log*", recurse: false))
        {
            _fs.DeleteFile(stale);
        }
        foreach (var leaf in new[] { "cache", "inspector" })
        {
            var dir = Path.Combine(paths.BepInEx, leaf);
            if (_fs.DirectoryExists(dir)) _fs.DeleteDirectory(dir, recursive: true);
        }
        _fs.CreateDirectory(Path.Combine(paths.BepInEx, "cache"));

        Say("[Provision] Handling root files ...");
        foreach (var file in _fs.EnumerateFiles(source, "*", recurse: false))
        {
            var leaf = Path.GetFileName(file);
            if (SkipRootFiles.Contains(leaf, StringComparer.OrdinalIgnoreCase)) continue;

            var target = Path.Combine(paths.Tree, leaf);
            long length;
            try
            {
                length = _fs.GetFileLength(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                length = 0;
            }

            if (RealCopyRootFiles.Contains(leaf, StringComparer.OrdinalIgnoreCase))
            {
                _fs.CopyFile(file, target, overwrite: true);
                stats.AddCopy(length);
            }
            else
            {
                _fs.CreateHardLink(target, file);
                stats.AddLink(length);
            }
        }

        DeployControlPlugin(paths);

        // Measured AFTER the plugin lands (CLIENT-072 fixed): the PowerShell counted the
        // BepInEx copy before deploying into it, so the summary was short by the plugin.
        var (bepFiles, bepBytes) = TreeOps.Measure(_fs, paths.BepInEx);
        stats.CopiedFiles += bepFiles;
        stats.CopiedBytes += bepBytes;

        WriteAllManifests(options.Desktop);
        WriteProvisionStamp(paths, claim.Entry, source);

        // Unconditional, and BEFORE the mod seed (CLIENT-075). It used to sit at the end of
        // the mod seed, behind that function's early return for a developer with no
        // modconfig.xml, so an instance provisioned on such a machine got no save redirect at
        // all and wrote into the developer's tier-1 user-data folder, behind a warning whose
        // text only mentioned mods. The redirect has nothing to do with mods and must not be
        // skippable by anything mod-related.
        WriteSavePathOverride(paths, claim.Entry.RoleOr());

        if (options.SeedMods) SeedMods(paths);

        Say("");
        Say($"[Provision] Instance '{name}' built.");
        Say(string.Format(CultureInfo.InvariantCulture,
            "[Provision]   hard-linked : {0,6} files, {1,8:N1} MB shared (near-zero new disk)",
            stats.LinkedFiles, stats.LinkedBytes / 1048576.0));
        Say(string.Format(CultureInfo.InvariantCulture,
            "[Provision]   real copies : {0,6} files, {1,8:N1} MB new disk",
            stats.CopiedFiles, stats.CopiedBytes / 1048576.0));
        Say($"[Provision]   role        : {claim.Entry.RoleOr()}");
        Say($"[Provision]   port        : {claim.Entry.Port}  (control plane, TCP, loopback only)");
        Say($"[Provision]   gamePort    : {claim.Entry.GamePortOr(0)}  (RakNet, UDP)");
        Say($"[Provision]   clientId    : {claim.Entry.ClientIdOr()}");
        Say($"[Provision]   username    : {claim.Entry.UsernameOr(name)}");
        Say($"[Provision]   tree        : {paths.Tree}  (root recorded in the registry; later commands need no --instances-root)");
        Say($"[Provision]   saveRoot    : {paths.UserData}");
        Say($"[Provision]   manifest    : {paths.Manifest}");

        _output.Value("instanceName", name);
        _output.Value("tree", paths.Tree);
        _output.Value("port", claim.Entry.Port);
        _output.Value("gamePort", claim.Entry.GamePortOr(0));
        _output.Value("clientId", claim.Entry.ClientIdOr());
        _output.Value("role", claim.Entry.RoleOr());
        _output.Value("linkedFiles", stats.LinkedFiles);
        _output.Value("copiedFiles", stats.CopiedFiles);

        if (claim.Entry.IsHost)
        {
            var gamePort = claim.Entry.GamePortOr(0);
            Say($"[Provision] Next: testrig start --target {name}, testrig wait --target {name} --stage menu,");
            Say($"[Provision]       then testrig call --target {name} --path /host --body '{{\"world\":\"Lunar\"}}'.");
            Say($"[Provision]       Joiners reach it at 127.0.0.1:{gamePort}, and the host must be in its world "
                + "BEFORE any joiner connects.");
        }
        else
        {
            Say($"[Provision] Next: testrig start --target {name} --as <id>");
        }

        // Returned rather than swallowed (CLIENT-084): update-game discards it, a future
        // caller may not.
        await Task.CompletedTask.ConfigureAwait(false);
        return claim.Entry;
    }

    private sealed record CreateClaim(InstanceEntry Entry, InstancePaths Paths);

    /// <summary>
    /// Everything create decides before it touches disk, inside the registry's critical section.
    /// </summary>
    private (IReadOnlyList<InstanceEntry> Entries, CreateClaim Claim) BuildClaim(
        IReadOnlyList<InstanceEntry> registry,
        CreateOptions options,
        string source)
    {
        var name = options.Instance;
        var existing = registry.FirstOrDefault(e => RigRegistry.SameInstance(e.InstanceName, name));

        // The index decides the defaults for port, game port and identity, so provisioning
        // three instances with no flags produces three distinct, non-colliding ones.
        var index = existing?.Index ?? LowestFreeIndex(registry);

        // THE root this instance is built in. A rebuild keeps its recorded root for the same
        // reason it keeps the role and the game port: create --force is the routine way to
        // pick up a new plugin build, and relocating an instance in passing would be a trap.
        var recordedRoot = existing?.RecordedRoot ?? "";
        var effectiveRoot = _layout.InstancesRootTyped
            ? _layout.InstancesDir
            : recordedRoot.Length > 0 ? recordedRoot : _layout.InstancesDir;

        if (_layout.InstancesRootTyped && recordedRoot.Length > 0
            && !string.Equals(recordedRoot, effectiveRoot, StringComparison.OrdinalIgnoreCase))
        {
            Warn($"[Provision] '{name}' was built under {recordedRoot} and --instances-root moves it to "
                 + $"{effectiveRoot}. The old tree at {Path.Combine(recordedRoot, name)} is NOT deleted (this "
                 + "launcher only ever removes the tree it is about to rebuild); delete it by hand once the "
                 + "rebuild succeeds.");
        }

        var paths = _layout.PathsInRoot(name, effectiveRoot);

        if (_fs.DirectoryExists(paths.Tree) && !options.Force)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Instance '{name}' already exists at {paths.Tree}. Pass --force to rebuild it, or delete it "
                + $"first: testrig remove --target {name} --as <id>");
        }

        if (PidFiles.ClientAlive(_fs, _processes, paths.PidFile))
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Instance '{name}' is running. Stop it first: testrig stop --target {name} --as <id>");
        }

        // Typed wins; otherwise the existing value; otherwise the index default. The
        // PowerShell kept only role and gamePort this way and reset the control port, the
        // ClientId and the username on every rebuild, which meant update-game silently
        // changed the identity the server keys a player's body on (CLIENT-052 to CLIENT-054,
        // CLIENT-306 fixed).
        var port = options.Port ?? (existing?.Port is > 0 ? existing.Port : RigConstants.ControlPortBase + index);
        var clientId = options.ClientId
                       ?? (existing?.ClientIdOr("") is { Length: > 0 } id
                           ? id
                           : (900000000000L + index).ToString(CultureInfo.InvariantCulture));
        var username = options.Username ?? existing?.UsernameOr(name) ?? name;
        var role = options.Role ?? existing?.RoleOr() ?? "client";
        var gamePort = options.GamePort ?? existing?.GamePortOr(RigConstants.GamePortBase + index)
                       ?? RigConstants.GamePortBase + index;
        var width = options.Width ?? existing?.Width ?? CreateOptions.DefaultWidth;
        var height = options.Height ?? existing?.Height ?? CreateOptions.DefaultHeight;
        var forceInput = options.ForceGameplayInput ?? existing?.ForceGameplayInput ?? true;

        if (!ulong.TryParse(clientId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedId))
        {
            throw new RigRefusalException(RigRefusalKind.Refused, $"--client-id '{clientId}' is not a decimal ulong.");
        }
        if (parsedId == 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                "--client-id 0 is the batch-mode sentinel and would collide with every other zero-id client. "
                + "Pick a non-zero value.");
        }

        var idClash = registry.FirstOrDefault(e =>
            !RigRegistry.SameInstance(e.InstanceName, name)
            && string.Equals(e.ClientIdOr(""), clientId, StringComparison.Ordinal));
        if (idClash is not null)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"ClientId {clientId} is already used by instance '{idClash.InstanceName}'. The server keys a "
                + "player's body on this id, so both instances would resolve onto one character. Pick a "
                + "different --client-id.");
        }

        PortGuards.AssertControlPortFree(registry, name, port, gamePort);
        PortGuards.AssertGamePortFree(registry, name, gamePort, port);

        // After the cheap identity and port guards, so a name clash is reported before a
        // volume misconfiguration and the caller fixes one thing at a time. It checks the
        // root this provision will ACTUALLY build in, which on a rebuild is the recorded one
        // (CLIENT-062).
        ClientLayout.AssertSameVolume(source, effectiveRoot);

        var entry = new InstanceEntry
        {
            InstanceName = name,
            Index = index,
            Role = role,
            Port = port,
            GamePort = gamePort,
            ClientId = clientId,
            Username = username,
            Width = width,
            Height = height,
            ForceGameplayInput = forceInput,
            InstancesRoot = effectiveRoot,
            ProvisionedUtc = RigTime.Stamp(_clock.UtcNow),
        };

        IReadOnlyList<InstanceEntry> next =
        [
            .. registry.Where(e => !RigRegistry.SameInstance(e.InstanceName, name)),
            entry,
        ];

        return (next, new CreateClaim(entry, paths));
    }

    /// <summary>The lowest positive integer not already in use (CLIENT-047).</summary>
    private static int LowestFreeIndex(IReadOnlyList<InstanceEntry> registry)
    {
        var used = registry.Select(static e => e.Index).ToHashSet();
        var i = 1;
        while (used.Contains(i)) i++;
        return i;
    }

    // ---- the control plugin -----------------------------------------------

    /// <summary>
    /// Copies <c>ClientDriver</c> into the new tree's Chainloader path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A missing DLL WARNS and does not throw (CLIENT-085): an instance without a control
    /// plane is still a useful thing to have built, and the warning names the exact build
    /// command.
    /// </para>
    /// <para>
    /// The whole build OUTPUT FOLDER is copied, not just the one file (CLIENT-086 fixed).
    /// The PowerShell copied exactly one file, so the moment the plugin gained a reference
    /// every instance would silently run without a control plane, and the only warning fires
    /// when the DLL ITSELF is missing. Dependencies and the PDB come with it.
    /// </para>
    /// <para>
    /// The plugin deliberately takes the Chainloader path and must NOT also sit under a
    /// StationeersLaunchPad mod folder: two loaders means Awake twice and every Harmony patch
    /// registered twice (CLIENT-087).
    /// </para>
    /// </remarks>
    /// <param name="plugin">
    /// The control plugin to deploy, or null to use whichever one this rig resolves to.
    /// <c>deploy</c> passes the build the caller NAMED, so asking for one by name cannot
    /// quietly install the other.
    /// </param>
    private void DeployControlPlugin(InstancePaths paths, ControlPluginBuild? plugin = null)
    {
        // Resolved, never named here: the merged TestRig plugin replaces ClientDriver and
        // shares no name with it, and a hardcoded name is what made the merged plugin
        // impossible to deploy at all.
        plugin ??= _layout.ControlPlugin;
        var dll = plugin.Dll;

        if (!_fs.FileExists(dll))
        {
            Warn($"[{paths.Name}] {plugin.Name}.dll not found at {dll}. Build it first: dotnet build "
                 + $"{plugin.Sln} -c Release. The instance will run without a control plane.");
            return;
        }

        var destination = Path.Combine(paths.BepInEx, "plugins", plugin.Name);
        _fs.CreateDirectory(destination);

        var buildDir = Path.GetDirectoryName(dll);
        if (!string.IsNullOrEmpty(buildDir) && _fs.DirectoryExists(buildDir))
        {
            foreach (var file in _fs.EnumerateFiles(buildDir, "*", recurse: false))
            {
                _fs.CopyFile(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
        }
        else
        {
            _fs.CopyFile(dll, Path.Combine(destination, plugin.Name + ".dll"), overwrite: true);
        }

        Say($"[Provision] {plugin.Name} -> {destination}");

        RemoveSupersededControlPlugins(paths, plugin.Name);
    }

    /// <summary>
    /// Deletes any OTHER control plugin from both of the instance's load paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not tidiness. The instance's <c>BepInEx/</c> is a real copy of the developer's own
    /// install, and that install carries <c>BepInEx/plugins/ClientDriver/</c>, so every
    /// freshly created instance arrives with the legacy control plane already in the
    /// Chainloader path. Deploying the merged plugin beside it leaves two DIFFERENT
    /// plugins, each of which loads: two Awakes, the same 32 methods patched twice, and two
    /// binds of the same control port. The merged plugin's own duplicate refusal cannot
    /// help, because it recognises a second copy of ITSELF by GUID and its predecessor
    /// carries a different one.
    /// </para>
    /// <para>
    /// Both load paths are swept, not just the one this deploy writes to, so a tree that
    /// was set up the other way round self-heals rather than needing a rebuild. The sweep is
    /// name-driven and idempotent, so running create twice costs one directory probe each.
    /// </para>
    /// </remarks>
    private void RemoveSupersededControlPlugins(InstancePaths paths, string deployed)
    {
        foreach (var superseded in ControlPlugins.Superseded(deployed))
        {
            (string Dir, string Loader)[] loadPaths =
            [
                (Path.Combine(paths.BepInEx, "plugins", superseded), "BepInEx Chainloader"),
                (Path.Combine(paths.ModsDir, "Local_" + superseded), "StationeersLaunchPad"),
            ];

            foreach (var (dir, loader) in loadPaths)
            {
                if (!_fs.DirectoryExists(dir)) continue;

                _fs.DeleteDirectory(dir, recursive: true);
                Say($"[{paths.Name}] removed the superseded control plugin '{superseded}' from the {loader} "
                    + $"load path ({dir}). '{deployed}' replaces it, and both loading at once would double "
                    + "every Harmony patch and fight over the control port.");
            }
        }
    }

    // ---- the save-path redirect -------------------------------------------

    /// <summary>
    /// Points the instance at its OWN save root.
    /// </summary>
    /// <remarks>
    /// The single thing standing between a driven session and the developer's tier-1 save
    /// folder. A failure to write it THROWS for a host and merely warns for a client
    /// (CLIENT-076), because a joining client reads a world the server owns and writes none
    /// of its own, while a host CREATES a world, and a host with no redirect creates it
    /// inside the developer's saves.
    ///
    /// The throw is re-wrapped so the remedy names the state the instance is actually in:
    /// the registry entry is already written by this point, so <c>start</c> then
    /// <c>create --force</c> genuinely works, which it did not in the PowerShell.
    /// </remarks>
    private void WriteSavePathOverride(InstancePaths paths, string role)
    {
        try
        {
            SavePathOverride.Write(_fs, _output, paths.BepInEx, paths.UserData, role, paths.Name, "Provision");
        }
        catch (RigRefusalException ex)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                ex.Message
                + $"\n\nThe tree at {paths.Tree} IS built and IS registered, so the remedy above works as "
                + $"written: testrig start --target {paths.Name} (it boots to the menu and writes the config, "
                + $"and a menu boot creates no world), then testrig create --target {paths.Name} --force "
                + "--role host --as <id>.",
                ex.Refusal);
        }
    }

    // ---- the mod seed -----------------------------------------------------

    /// <summary>
    /// Copies the developer's mod set into the instance's own save root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The developer's <c>mods/</c> and <c>modconfig.xml</c> are READ-ONLY sources, always.
    /// </para>
    /// <para>
    /// This WIPES <c>userdata/mods/</c>, which destroys every <c>Local_&lt;Mod&gt;</c> folder
    /// a deploy put there (CLIENT-090). The PowerShell's <c>update-mods</c> detected that and
    /// warned; <c>update-game</c> and a plain <c>create --force</c> reached the same code
    /// with NO such detection, so the instance under test silently reverted to the
    /// developer's own mod set. The warning is emitted here, at the one place the wipe
    /// happens, so every caller gets it.
    /// </para>
    /// </remarks>
    private void SeedMods(InstancePaths paths)
    {
        var userData = _env.UserDataPath();
        var sourceMods = Path.Combine(userData, "mods");
        var sourceConfig = Path.Combine(userData, "modconfig.xml");

        if (!_fs.FileExists(sourceConfig))
        {
            Warn($"[{paths.Name}] No modconfig.xml at {sourceConfig}; skipping the mod seed. The instance will "
                 + "load Workshop mods only.");
            return;
        }

        Say("[Provision] Seeding mods from the user data folder (read-only source) ...");

        var destinationMods = paths.ModsDir;

        // Named before the wipe, at the one place the wipe happens.
        var before = TreeOps.ChildDirectoryNames(_fs, destinationMods);
        var lost = _mods.RepositoryFoldersAmong(before);

        var sourcePresent = _fs.DirectoryExists(sourceMods);
        if (_fs.DirectoryExists(destinationMods)) _fs.DeleteDirectory(destinationMods, recursive: true);

        if (sourcePresent) TreeOps.CopyTree(_fs, sourceMods, destinationMods);
        else _fs.CreateDirectory(destinationMods);

        if (lost.Count > 0)
        {
            Warn($"[{paths.Name}] the mod seed removed {lost.Count} folder(s) this repository had deployed: "
                 + $"{string.Join(", ", lost)}. Re-deploy them: testrig deploy <Mod> --target {paths.Name} --as <id>");
        }

        // Local mod entries are absolute paths, and StationeersLaunchPad prunes entries whose
        // folder is not under the active save path, so each instance needs its own copy and
        // its own modconfig. Parsed and rewritten through the ONE shared reader and writer
        // rather than string-replaced, and DISABLED entries are carried through as disabled
        // rather than dropped (CLIENT-091, CLIENT-092).
        var rebased = new List<ModConfigEntry>();
        foreach (var entry in ModConfig.Read(_fs, sourceConfig))
        {
            var path = entry.Path;
            if (sourcePresent && path.Length > 0
                && path.StartsWith(sourceMods, StringComparison.OrdinalIgnoreCase))
            {
                path = destinationMods + path[sourceMods.Length..];
            }
            rebased.Add(entry with { Path = path });
        }
        ModConfig.Write(_fs, paths.ModConfig, rebased);

        // Neither of these is mod configuration, and dropping them changes what a driven
        // client looks like to the server (CLIENT-093).
        foreach (var leaf in new[] { "modrepos.xml", "PlayerCosmetics_0.xml" })
        {
            var file = Path.Combine(userData, leaf);
            if (_fs.FileExists(file)) _fs.CopyFile(file, Path.Combine(paths.UserData, leaf), overwrite: true);
        }
    }

    // ---- manifests and the stamp ------------------------------------------

    /// <summary>
    /// Rewrites EVERY instance's manifest.
    /// </summary>
    /// <remarks>
    /// Not just the one being provisioned: every manifest carries the whole rig's control
    /// port list, which is what lets an instance notice a sibling claiming the same ClientId
    /// (CLIENT-098, CLIENT-099).
    /// </remarks>
    public void WriteAllManifests(string desktop = RigConstants.DefaultDesktop)
    {
        var registry = _registry.Read();
        var ports = registry.Select(static e => e.Port).ToArray();

        foreach (var entry in registry)
        {
            var paths = _layout.PathsFor(entry.InstanceName, entry);
            _fs.CreateDirectory(paths.Data);

            var manifest = new InstanceManifest
            {
                InstanceName = entry.InstanceName,
                Role = entry.RoleOr(),
                Port = entry.Port,
                GamePort = entry.GamePortOr(0),
                ClientId = entry.ClientIdOr(),
                Username = entry.UsernameOr(entry.InstanceName),
                Window = new ManifestWindow
                {
                    ForceWindowed = true,
                    Width = entry.Width ?? CreateOptions.DefaultWidth,
                    Height = entry.Height ?? CreateOptions.DefaultHeight,
                },
                GameplayInput = new ManifestGameplayInput
                {
                    Force = entry.ForceGameplayInput ?? true,
                    Everywhere = false,
                },
                SavePath = paths.UserData,
                Desktop = desktop,
                RigRoot = _layout.ClientRoot,
                PeerPorts = ports,
            };

            _fs.WriteAllTextDurable(
                paths.Manifest,
                JsonSerializer.Serialize(manifest, ClientJsonContext.Default.InstanceManifest));
        }
    }

    /// <summary>Records when this tree was built and out of what.</summary>
    private void WriteProvisionStamp(InstancePaths paths, InstanceEntry entry, string sourceInstall)
    {
        var pluginBuilt = "";
        try
        {
            if (_fs.FileExists(_layout.PluginDll))
            {
                pluginBuilt = RigTime.Stamp(_fs.GetLastWriteTimeUtc(_layout.PluginDll));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // A stamp without a plugin build time is still worth writing.
        }

        var stamp = new ProvisionStamp
        {
            InstanceName = entry.InstanceName,
            ProvisionedUtc = entry.ProvisionedUtc ?? RigTime.Stamp(_clock.UtcNow),
            Role = entry.RoleOr(),
            Port = entry.Port,
            GamePort = entry.GamePortOr(0),
            Tree = paths.Tree,
            SourceInstall = sourceInstall,
            SourceVersion = _env.InstallVersion(sourceInstall),
            PluginBuiltUtc = pluginBuilt,
            LauncherHostname = _env.MachineName,
        };

        _fs.WriteAllTextDurable(paths.Stamp, JsonSerializer.Serialize(stamp, ClientJsonContext.Default.ProvisionStamp));
        Say($"[Provision]   stamp       : {paths.Stamp} (game {stamp.SourceVersion})");
    }
}
