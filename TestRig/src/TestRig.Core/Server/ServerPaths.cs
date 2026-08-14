using TestRig.Core.Session;

namespace TestRig.Core.Server;

/// <summary>
/// Everything the dedicated-server half owns, in one object.
/// </summary>
/// <remarks>
/// Built on top of <see cref="RigPaths"/> rather than beside it. The session subsystem
/// already resolves this half's save root, log, pid files and control file, and two objects
/// each holding their own copy is precisely the drift that let the reset planner check one
/// save tree and delete another. Only the four paths the session layer has no opinion about
/// are added here.
/// </remarks>
public sealed class ServerPaths
{
    private readonly RigPaths _paths;

    public ServerPaths(RigPaths paths) => _paths = paths;

    /// <summary><c>TestRig/DedicatedServer/</c>.</summary>
    public string Root => _paths.DediRoot;

    /// <summary>The SteamCMD app 600760 install.</summary>
    public string InstallDir => _paths.DediInstall;

    /// <summary>Worlds, mods, logs and state. Never deleted by this rig.</summary>
    public string DataDir => _paths.DediData;

    public string Exe => Path.Combine(InstallDir, "rocketstation_DedicatedServer.exe");

    public string LogFile => _paths.ServerLog;

    public string ControlFile => _paths.ControlCmdFile;

    public string PidFile => _paths.ServerPidFile;

    public string HostPidFile => _paths.HostPidFile;

    /// <summary>Where <c>-settings SavePath</c> puts the server's worlds.</summary>
    public string SaveRoot => _paths.ServerSaveRoot;

    /// <summary>The StationeersLaunchPad load path on this half.</summary>
    public string ModsDir => Path.Combine(DataDir, "mods");

    /// <summary>The BepInEx Chainloader load path on this half.</summary>
    public string PluginsDir => Path.Combine(InstallDir, "BepInEx", "plugins");

    /// <summary>The baked config StationeersLaunchPad reads.</summary>
    public string ModConfig => Path.Combine(InstallDir, "modconfig.xml");

    public string SettingXml => _paths.ServerSettingXml;

    /// <summary>The mirrored BepInEx tree.</summary>
    public string BepInEx => Path.Combine(InstallDir, "BepInEx");

    /// <summary>Where the InspectorPlus readiness probe is dropped.</summary>
    public string InspectorRequests => Path.Combine(BepInEx, "inspector", "requests");

    /// <summary>The mirrored StationeersLaunchPad plugin, whose version selects the server zip.</summary>
    public string LaunchPadDll => Path.Combine(BepInEx, "plugins", "StationeersLaunchPad", "StationeersLaunchPad.dll");

    /// <summary>The mirrored BepInEx core, whose version is printed after a mirror.</summary>
    public string BepInExCoreDll => Path.Combine(BepInEx, "core", "BepInEx.dll");

    /// <summary>A world folder under the server's save root.</summary>
    public string World(string name) => Path.Combine(SaveRoot, name);
}
