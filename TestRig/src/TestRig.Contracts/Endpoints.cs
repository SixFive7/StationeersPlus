using System.Diagnostics.CodeAnalysis;

namespace TestRig.Contracts;

/// <summary>
///     Every path the plugin's router answers, as a constant.
/// </summary>
/// <remarks>
///     <para>
///     Why constants and not strings at the call site: the PowerShell refusal matrix told
///     callers to drive <c>/console/run</c>. That path has never existed; the real one is
///     <see cref="ConsoleExec"/>. A typed path could not be typo'd, and
///     <see cref="Exists"/> answers the question the refusal matrix should have asked
///     before it printed advice.
///     </para>
///     <para>
///     <b>The HTTP method is not part of the contract.</b> The router parses
///     <c>HttpRequest.Method</c> and then never reads it (<c>Routes/Router.cs</c>,
///     <c>Transport/HttpServer.cs</c>). Every path below answers to GET, POST, PUT or
///     anything else. It also merges the query string into the parsed JSON body, with the
///     body winning on a key collision, so a request record can travel either way. The
///     GET and POST labels in <c>/help</c> and in the spec document intent, not
///     enforcement. Typing an endpoint as GET-only or POST-only would make this assembly
///     stricter than the server and would break callers that work today, so nothing here
///     records a method.
///     </para>
///     <para>
///     Path matching in the router is <c>TrimEnd('/')</c>, then empty becomes <c>"/"</c>,
///     then <c>ToLowerInvariant()</c>. <see cref="Normalize"/> reproduces that exactly, so
///     <c>/STATUS</c>, <c>/status/</c> and <c>/Status</c> all resolve to
///     <see cref="Status"/>.
///     </para>
/// </remarks>
public static class Endpoints
{
    // ---- help and liveness ------------------------------------------------

    /// <summary>The endpoint catalogue. Alias of <see cref="Help"/>; both hit one handler.</summary>
    public const string Root = "/";

    /// <summary>The endpoint catalogue. Alias of <see cref="Root"/>.</summary>
    public const string Help = "/help";

    /// <summary>Liveness. Never touches the Unity main thread, so it answers while the game is wedged.</summary>
    public const string Ping = "/ping";

    // ---- instance identity ------------------------------------------------

    public const string Instance = "/instance";
    public const string Identity = "/identity";

    // ---- observation ------------------------------------------------------

    public const string Status = "/status";
    public const string Player = "/player";
    public const string Colors = "/colors";
    public const string Plugins = "/plugins";
    public const string Nearby = "/nearby";

    // ---- console ----------------------------------------------------------

    public const string ConsoleLog = "/console/log";
    public const string ConsoleClear = "/console/clear";
    public const string ConsoleBuffer = "/console/buffer";

    /// <summary>
    ///     Run a console command and capture what it printed. NOTE: this is
    ///     <c>/console/exec</c>. There is no <c>/console/run</c>, which is what the
    ///     PowerShell refusal matrix used to point callers at.
    /// </summary>
    public const string ConsoleExec = "/console/exec";

    public const string ConsolePrint = "/console/print";
    public const string ConsoleCommands = "/console/commands";

    // ---- session and world ------------------------------------------------

    public const string Connect = "/connect";
    public const string Host = "/host";
    public const string Disconnect = "/disconnect";
    public const string Quit = "/quit";
    public const string Saves = "/saves";
    public const string Save = "/save";
    public const string SavePath = "/savepath";
    public const string Load = "/load";
    public const string NewWorld = "/newworld";
    public const string WaitFor = "/waitfor";

    // ---- input ------------------------------------------------------------

    public const string InputKey = "/input/key";
    public const string InputScroll = "/input/scroll";
    public const string InputMouse = "/input/mouse";
    public const string InputMousePosition = "/input/mouseposition";
    public const string InputReleaseAll = "/input/releaseall";
    public const string InputClear = "/input/clear";
    public const string InputKeyMap = "/input/keymap";
    public const string InputEnable = "/input/enable";

    // ---- diagnostics ------------------------------------------------------

    public const string DiagInput = "/diag/input";
    public const string DiagJoin = "/diag/join";

    // ---- player acts ------------------------------------------------------

    public const string PlayerTeleport = "/player/teleport";
    public const string PlayerLook = "/player/look";
    public const string PlayerUse = "/player/use";
    public const string PlayerSwapHands = "/player/swaphands";

    // ---- inventory --------------------------------------------------------

    public const string Inventory = "/inventory";
    public const string InventoryMove = "/inventory/move";
    public const string InventoryGive = "/inventory/give";
    public const string InventoryArm = "/inventory/arm";

    // ---- spawning ---------------------------------------------------------

    public const string SpawnHand = "/spawn/hand";
    public const string SpawnWorld = "/spawn/world";
    public const string SpawnStructure = "/spawn/structure";
    public const string Prefabs = "/prefabs";

    // ---- mod settings panel -----------------------------------------------

    public const string ModSettingsList = "/modsettings/list";
    public const string ModSettings = "/modsettings";

    // ---- modal dialogs ----------------------------------------------------

    public const string Modal = "/modal";
    public const string ModalClick = "/modal/click";

    // ---- cursor -----------------------------------------------------------

    public const string CursorForce = "/cursor/force";

    // ---- screenshot -------------------------------------------------------

    /// <summary>
    ///     The only endpoint whose success can be a non-JSON body: <c>inline=true</c>
    ///     returns raw <c>image/png</c> bytes at status 200.
    /// </summary>
    public const string Screenshot = "/screenshot";

    // ---- BepInEx config ---------------------------------------------------

    public const string Config = "/config";
    public const string ConfigSet = "/config/set";
    public const string ConfigReload = "/config/reload";

    // ---- reflection -------------------------------------------------------

    public const string Reflect = "/reflect";
    public const string ReflectMembers = "/reflect/members";
    public const string ReflectInstance = "/reflect/instance";
    public const string Thing = "/thing";
    public const string ThingMembers = "/thing/members";

    // ---- DLC entitlement --------------------------------------------------

    public const string Dlc = "/dlc";
    public const string DlcRemove = "/dlc/remove";
    public const string DlcRestore = "/dlc/restore";

    // ---- scenarios --------------------------------------------------------

    /// <summary>The scenario catalogue, with what is armed and what has been dispatched.</summary>
    /// <remarks>
    ///     Pure managed state, so it answers while the world is parked, which is exactly the
    ///     moment a caller most wants to know why nothing fired.
    /// </remarks>
    public const string Scenarios = "/scenarios";

    /// <summary>Runs one scenario for N simulation ticks and returns the lines it wrote.</summary>
    public const string ScenarioRun = "/scenario/run";

    /// <summary>Arms one or more scenarios, live from the next tick.</summary>
    public const string ScenarioArm = "/scenario/arm";

    /// <summary>Clears the armed set. It stops future ticks; it does not undo a probe.</summary>
    public const string ScenarioDisarm = "/scenario/disarm";

    /// <summary>
    ///     Every path the router switches on, in router order. 69 strings covering 68
    ///     distinct handlers, because <see cref="Root"/> and <see cref="Help"/> share one.
    /// </summary>
    /// <remarks>
    ///     The last four are the scenario endpoints the merged plugin added. They were the
    ///     only paths in its dispatch table still written as string literals, which is the
    ///     exact shape of the <c>/console/run</c> mistake this catalogue exists to prevent.
    /// </remarks>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Root, Help, Ping,
        Instance, Identity,
        Status, Player, Colors, Plugins, Nearby,
        ConsoleLog, ConsoleClear, ConsoleBuffer, ConsoleExec, ConsolePrint, ConsoleCommands,
        Connect, Host, Disconnect, Quit, Saves, Save, SavePath, Load, NewWorld, WaitFor,
        InputKey, InputScroll, InputMouse, InputReleaseAll, InputClear, InputKeyMap,
        InputEnable, InputMousePosition,
        DiagInput, DiagJoin,
        PlayerTeleport, PlayerLook, PlayerUse, PlayerSwapHands,
        Inventory, InventoryMove, InventoryGive, InventoryArm,
        SpawnHand, SpawnWorld, SpawnStructure, Prefabs,
        ModSettingsList, ModSettings,
        Modal, ModalClick,
        CursorForce,
        Screenshot,
        Config, ConfigSet, ConfigReload,
        Reflect, ReflectMembers,
        Thing, ThingMembers, ReflectInstance,
        Dlc, DlcRemove, DlcRestore,
        Scenarios, ScenarioRun, ScenarioArm, ScenarioDisarm,
    };

    private static readonly HashSet<string> Known = new HashSet<string>(All, StringComparer.Ordinal);

    /// <summary>
    ///     Reproduces the router's own path handling: trim trailing slashes, an empty
    ///     result becomes <c>"/"</c>, then lower-case with the invariant culture.
    /// </summary>
    public static string Normalize(string? path)
    {
        string trimmed = (path ?? Root).TrimEnd('/');
        if (trimmed.Length == 0) trimmed = Root;
        return trimmed.ToLowerInvariant();
    }

    /// <summary>
    ///     Does the plugin actually answer this path? Ask before sending, and before
    ///     printing advice that names a path. The 404 body is
    ///     <c>{"ok":false,"error":"unknown endpoint '&lt;path&gt;'. GET /help lists them all."}</c>,
    ///     which a caller only sees at runtime, on a rig it had to acquire the lock for.
    /// </summary>
    public static bool Exists(string? path) => Known.Contains(Normalize(path));

    /// <summary>
    ///     Normalizes and validates in one step. Returns false for a path the router does
    ///     not switch on.
    /// </summary>
    public static bool TryResolve(string? path, [NotNullWhen(true)] out string? resolved)
    {
        string candidate = Normalize(path);
        if (Known.Contains(candidate))
        {
            resolved = candidate;
            return true;
        }

        resolved = null;
        return false;
    }

    /// <summary>
    ///     Candidate replacements for an unknown path, matched on the first path segment.
    ///     <c>/console/run</c> returns the six real <c>/console/*</c> endpoints, one of
    ///     which is the <see cref="ConsoleExec"/> the caller meant. Empty when the segment
    ///     itself is unknown.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string? path)
    {
        string candidate = Normalize(path);
        if (Known.Contains(candidate)) return new[] { candidate };

        int slash = candidate.IndexOf('/', 1);
        if (slash <= 0) return Array.Empty<string>();

        string prefix = candidate.Substring(0, slash + 1);
        var hits = new List<string>();
        foreach (string known in All)
            if (known.StartsWith(prefix, StringComparison.Ordinal))
                hits.Add(known);

        return hits;
    }
}
