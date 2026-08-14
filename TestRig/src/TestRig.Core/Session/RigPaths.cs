namespace TestRig.Core.Session;

/// <summary>
/// Every path and process image the session subsystem touches, resolved once.
/// </summary>
/// <remarks>
/// The PowerShell rig set these as script-scoped variables in two libraries
/// (<c>Initialize-RigLockPaths</c> and <c>Initialize-RigResetPaths</c>) and kept them
/// in step only because the reset library re-pointed the lock library on every call.
/// Spec 03-reset H.4 records the consequence: the reset planner's existence guard read
/// its own <c>$saveRoot</c> while the enumeration and the delete read the lock
/// library's <c>$RigDediSaveRoot</c>. They agree today by accident. Any caller that
/// initialised the two libraries independently would check one tree and delete
/// another. One object, one save root, no second copy.
/// </remarks>
public sealed class RigPaths
{
    /// <param name="rigHome">The <c>TestRig/</c> directory.</param>
    /// <param name="instanceRoot">
    /// Where hard-linked instance trees live. Normally on the game install's volume,
    /// which is why the reset cannot assume it sits under <paramref name="rigHome"/>.
    /// </param>
    /// <param name="sourceInstall">The developer's Stationeers install. Read-only, always.</param>
    /// <param name="userDataDir">The developer's Stationeers user-data folder. Read-only source for mod staleness.</param>
    /// <param name="additionalInstanceRoots">
    /// Every OTHER root the registry records, for a rig split across two of them.
    /// <para>
    /// CLIENT-007. A single root is enough for path resolution, because an instance's own
    /// entry names its own root, but the orphan scan asks a different question: "is this
    /// untracked game process running out of a rig tree, or is it the developer's own
    /// client?" A rig whose instances live under two roots had every process under the
    /// second one scoped <see cref="OrphanScope.Foreign"/> and therefore never reported.
    /// <c>ClientLayout.RecordedRoots()</c> is where the full set comes from.
    /// </para>
    /// </param>
    public RigPaths(
        string rigHome,
        string? instanceRoot = null,
        string? sourceInstall = null,
        string? userDataDir = null,
        string serverImage = "rocketstation_DedicatedServer",
        string clientImage = "rocketstation",
        IReadOnlyList<string>? hostWrapperImages = null,
        IReadOnlyList<string>? additionalInstanceRoots = null,
        string? sharedDataDir = null,
        string? playerPrefsKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rigHome);

        RigHome = rigHome;
        InstanceRoot = string.IsNullOrWhiteSpace(instanceRoot)
            ? Path.Combine(rigHome, "ClientRig", "instances")
            : instanceRoot;
        SourceInstall = string.IsNullOrWhiteSpace(sourceInstall) ? null : sourceInstall;
        UserDataDir = string.IsNullOrWhiteSpace(userDataDir) ? null : userDataDir;
        SharedDataDir = string.IsNullOrWhiteSpace(sharedDataDir) ? null : sharedDataDir;
        PlayerPrefsKey = string.IsNullOrWhiteSpace(playerPrefsKey)
            ? SharedStateReader.DefaultPlayerPrefsKey
            : playerPrefsKey;
        ServerImage = serverImage;
        ClientImage = clientImage;
        HostWrapperImages = hostWrapperImages ?? ["pwsh", "powershell"];

        AdditionalInstanceRoots = additionalInstanceRoots is null
            ? []
            : [.. additionalInstanceRoots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Where(r => !string.Equals(r, InstanceRoot, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public string RigHome { get; }
    public string InstanceRoot { get; }

    /// <summary>Other roots the registry records. Empty on the normal single-root rig.</summary>
    public IReadOnlyList<string> AdditionalInstanceRoots { get; }

    /// <summary><see cref="InstanceRoot"/> first, then <see cref="AdditionalInstanceRoots"/>.</summary>
    public IReadOnlyList<string> AllInstanceRoots => [InstanceRoot, .. AdditionalInstanceRoots];
    public string? SourceInstall { get; }
    public string? UserDataDir { get; }

    /// <summary>
    /// The per-user folder shared with the developer's own client, and never isolable.
    /// </summary>
    /// <remarks>
    /// <c>%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation</c> in practice (RESET-009).
    /// It holds <c>PlayerCookie-v2.xml</c> and <c>Blueprints\</c>, both of which the rig READS
    /// at a session boundary so it can name what moved, and never writes: Unity fixes
    /// <c>persistentDataPath</c> inside the serialized PlayerSettings, so there is no
    /// redirecting it. See <see cref="SharedStateReader"/>.
    /// </remarks>
    public string? SharedDataDir { get; }

    /// <summary>
    /// The game's PlayerPrefs registry key, read at a session boundary and never written
    /// (RESET-004).
    /// </summary>
    public string PlayerPrefsKey { get; }
    public string ServerImage { get; }
    public string ClientImage { get; }
    public IReadOnlyList<string> HostWrapperImages { get; }

    public string LockFile => Path.Combine(RigHome, "session.lock");
    public string DirtyFile => Path.Combine(RigHome, "session.dirty");
    public string SessionStateFile => Path.Combine(RigHome, "session.state.json");

    /// <summary>Never read; quoted in message text so a refusal can point at the rules.</summary>
    public string RulesPath => Path.Combine(RigHome, "CLAUDE.md");

    public string BaselineDir => Path.Combine(RigHome, "baseline");
    public string BaselineManifest => Path.Combine(BaselineDir, "manifest.json");
    public string BaselineStore => Path.Combine(BaselineDir, "content");

    public string DediRoot => Path.Combine(RigHome, "DedicatedServer");
    public string DediInstall => Path.Combine(DediRoot, "install");
    public string DediData => Path.Combine(DediRoot, "data");

    /// <summary>The one and only dedicated-server world root. See the type remarks.</summary>
    public string ServerSaveRoot => Path.Combine(DediData, "saves");

    public string ServerPidFile => Path.Combine(DediData, "server.pid");
    public string ServerLog => Path.Combine(DediData, "server.log");
    public string HostPidFile => Path.Combine(DediData, "host.pid");
    public string ControlCmdFile => Path.Combine(DediData, "control.cmd");
    public string ServerSettingXml => Path.Combine(DediData, "setting.xml");

    /// <summary>
    /// The developer's OWN save folder. Tier 1: listed, never read, never written.
    /// </summary>
    /// <remarks>
    /// Null only when the user-data folder itself could not be resolved, which is a fact a
    /// caller has to act on rather than paper over. Defect P-06 is what makes that
    /// distinction load-bearing: the playtest harness compares a listing of this folder on
    /// either side of a run, two missing roots hashed to the same sentinel and therefore
    /// compared EQUAL, and the verdict read IDENTICAL. A wrong path yielded a permanently
    /// green safety verdict on the one check whose whole job is to notice the rig writing
    /// into the developer's saves.
    /// </remarks>
    public string? UserSaveRoot =>
        string.IsNullOrEmpty(UserDataDir) ? null : Path.Combine(UserDataDir, "saves");

    public string ClientDataDir => Path.Combine(RigHome, "ClientRig", "data");
    public string ClientRegistryFile => Path.Combine(ClientDataDir, "rig.json");

    public string InstanceDataDir(string instance) => Path.Combine(ClientDataDir, instance);
    public string InstanceUserData(string instance) => Path.Combine(InstanceDataDir(instance), "userdata");

    /// <summary>
    /// A client instance's own world root. A listen host writes real worlds here, which
    /// is why the port extends session scoping to cover it (spec 03-reset H.5 item 1:
    /// the highest-plausibility real-world loss path in the whole subsystem).
    /// </summary>
    public string InstanceSaveRoot(string instance) => Path.Combine(InstanceUserData(instance), "saves");

    public string InstancePidFile(string instance) => Path.Combine(InstanceDataDir(instance), "game.pid");
    public string InstanceManifest(string instance) => Path.Combine(InstanceDataDir(instance), "instance.json");
    public string InstanceLogDir(string instance) => Path.Combine(InstanceDataDir(instance), "logs");

    /// <summary>Fallback tree location when <c>rig.json</c> records no root for the instance.</summary>
    public string DefaultInstanceTree(string instance) => Path.Combine(InstanceRoot, instance);
}
