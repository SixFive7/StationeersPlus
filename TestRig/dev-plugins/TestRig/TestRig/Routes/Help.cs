using System.Collections.Generic;

namespace TestRig
{
    /// <summary>
    ///     The runtime endpoint catalogue served by <c>GET /help</c>. Kept beside the dispatch table
    ///     so the two are edited together; a route added to <see cref="Router.Handle"/> without a
    ///     line here is a route nobody will find.
    /// </summary>
    internal static partial class Router
    {
        private static string Help()
        {
            var endpoints = new List<string>
            {
                "GET  /ping                       liveness plus frame counter, never touches the main thread",
                "GET  /instance                   which instance this is: name, port, role, gamePort, identity, peer ports, duplicate-identity check",
                "GET  /status                     full client state: instance, gameState, role, hosting, hostPort, connectedClients, save hygiene, world, player, driver counters",
                "GET  /player                     player block only",
                "GET  /colors                     GameManager.CustomColors catalogue with swatch indices",
                "GET  /plugins                    every loaded BepInEx plugin with GUID and version",
                "GET  /nearby?radius=&filter=&limit=   Things around the player",
                "",
                "GET  /console/log?since=&limit=&contains=&source=   tee'd console + BepInEx lines, sequence numbered, with dropped/truncated counts",
                "POST /console/clear              empty the tee ring",
                "GET  /console/buffer?limit=&contains=   the game's own 1024-line console ring, newest first",
                "POST /console/exec               {command, waitFrames, waitMs} run a console command, return its output",
                "POST /console/print              {text, level=action|error|info} write a marker line",
                "GET  /console/commands?contains= registered console command names",
                "",
                "POST /connect                    {address, port, wait, timeoutMs, allowDuplicateIdentity} Direct Connect; refuses a known ClientId clash",
                "POST /host                       {save|world, difficulty, start, port, serverName, password, maxPlayers, wait, timeoutMs, allowDuplicateIdentity}",
                "                                 become a listen host: load or create the world AND serve it on 127.0.0.1:<port>. Must start from the menu.",
                "                                 Asserts NetworkServer.IsHosting, never 'the call returned'. Refuses a ClientId clash and a non-isolated save path.",
                "                                 The non-isolated refusal has NO override. requireIsolatedSavePath was removed; passing it is a 400.",
                "POST /disconnect                 {wait, timeoutMs} leave to the main menu",
                "POST /quit                       {hard} exit the process",
                "GET  /saves                      local save list",
                "POST /save                       {name, wait, timeoutMs} persist the world and WAIT for the game to confirm it.",
                "                                 200 only on a confirmed save; 409 with requested:true and a warning when it was asked for but not confirmed.",
                "                                 Host or single player only: the game's save command is scoped HostOrSinglePlayer.",
                "POST /savepath                   {path} redirect the user-data root; refuses control characters and the developer's real user-data folder; GET reads it",
                "                                 The tier-1 refusal has NO override. force= was removed; passing it is a 400 and nothing is changed.",
                "POST /identity                   {clientId, username} presented player identity; GET reads it plus the duplicate check",
                "POST /load                       {save, wait, timeoutMs} load a save by name",
                "POST /newworld                   {world, difficulty, start, wait, timeoutMs}; ids are Lunar, Mars2, Europa3, MimasHerschel, Venus, Vulcan2 (not 'Moon')",
                "POST /waitfor                    {phase=menu|joining|loading|inWorld, timeoutMs}",
                "",
                "POST /input/key                  {key, mode=tap|down|up, frames, wait, requireConsumed} KeyCode or KeyMap action name",
                "POST /input/scroll               {notches, frames=1, repeat, gapFrames, wait, requireConsumed} one frame is one notch",
                "POST /input/mouse                {button, mode, frames, requireConsumed} alias for Mouse0/Mouse1",
                "POST /input/mouseposition        {x, y} or {clear:true}; reports whether the game read it",
                "POST /input/releaseall           end every held key",
                "POST /input/clear                drop all synthetic input state",
                "GET  /input/keymap               every KeyMap action and its current binding",
                "POST /input/enable               {enabled} master switch for input injection",
                "GET  /diag/input                 why input did or did not land: patches, per-frame chain enter/exit counters, gate, window, foreground",
                "GET  /diag/join                  why a join did or did not land: the recorded trace of the last /connect (StartClient's result, RakNet",
                "                                 connection state over time, who tore the peer down and when) plus a live peer probe. Read this rather",
                "                                 than /status after a failed join: the failure state is undone by /connect's own cleanup before /status runs.",
                "",
                "POST /player/teleport            {position:[x,y,z]} or {x,y,z} or {offset:[dx,dy,dz]}; a remote client is snapped back by the server",
                "POST /player/look                {yaw, pitch} or {at:[x,y,z]}",
                "POST /player/use                 {targetId} or {cursor:true} use the held item on a target; no distance or aim gate",
                "POST /player/swaphands           swap active and inactive hand",
                "",
                "GET  /inventory?player=&humanId= every slot of a character with its key and index; no selector means the local player.",
                "                                 activeHand/inactiveHand only resolve for the character THIS process owns.",
                "POST /inventory/arm              {prefab, hand=activeHand|left|right|either, quantity, replace, searchRadius, timeoutMs}",
                "                                 ONE call to get an item into this client's hand, on ANY role including a JOINER.",
                "                                 Spawns through the server, waits for the Thing, moves it in with a MoveToSlotMessage,",
                "                                 waits for the server to agree. 200 only when the hand actually holds it.",
                "POST /inventory/move             {thing|from, to=activeHand, intoThing, replace, wait, timeoutMs} move an existing Thing",
                "                                 into a slot via OnServer.MoveToSlot, the same call every inventory drag makes. No",
                "                                 authority needed: on a client it is a MoveToSlotMessage. Waits for the slot to fill.",
                "POST /inventory/give             {prefab, player|clientId|humanId, slot=either|left|right|<key>|<index>, quantity, replace}",
                "                                 HOST ONLY: create a prefab straight into ANOTHER player's slot via OnServer.Create.",
                "                                 No ground hop. Cannot target a remote player's ACTIVE hand (that is client-local state).",
                "",
                "POST /spawn/hand                 {prefab} put a prefab in the active hand (host or single player only)",
                "POST /spawn/world                {prefab, position|offset|distance, viaServer} drop a prefab nearby",
                "POST /spawn/structure            {prefab, position|offset|distance, yaw, colorIndex} place a Structure",
                "GET  /prefabs?contains=&type=&limit=   prefab catalogue",
                "",
                "GET  /modsettings/list           every mod StationeersLaunchPad loaded, with Name and Id",
                "POST /modsettings                {mod, show} force that mod's LaunchPad settings panel on screen so /screenshot can read it",
                "",
                "GET  /modal                      is a confirmation dialog showing, and what does it say",
                "POST /modal/click                {button=1|2|3} dismiss it and run that button's callback",
                "",
                "POST /cursor/force               {targetId} pins target+collider together (refuses a target with no collider); {clear:true} resets the cursor",
                "",
                "GET  /screenshot?path=&supersize=&maxWidth=&inline=   PNG of the full backbuffer, UI included (maxWidth defaults to 1920, 0 disables downscale)",
                "",
                "GET  /config?guid=&filter=       every ConfigEntry of a loaded plugin",
                "POST /config/set                 {guid, section, key, value, save} write a live ConfigEntry",
                "POST /config/reload              {guid} re-read the .cfg from disk",
                "GET  /reflect?type=&member=&expand=&expandLimit=&key=   read any STATIC field or property by full",
                "                                 type name. expand=true lists a collection's entries; key=<k> answers",
                "                                 'does this dictionary contain that key' without dumping it.",
                "GET  /reflect/members?type=      every static member of a type with its runtime value type",
                "",
                "GET  /thing?refId=&refIds=&fields=&type=&comparePrefab=&expand=&expandLimit=&key=",
                "                                 READ ANY MEMBER OF ANY THING, on this instance. 'fields' is a",
                "                                 comma-separated list of instance field or property names, public or",
                "                                 private, on the Thing's runtime type or any base type. A dotted path",
                "                                 walks (ParentSlot.Parent.ReferenceId) and [n] indexes a list.",
                "                                 A member that does not exist answers ok:false naming the types it",
                "                                 searched, NEVER an empty value. Each field also carries prefabValue",
                "                                 and matchesPrefab: a value identical to the untouched prefab's is",
                "                                 indistinguishable from never having been set (Thing.EmissionColor",
                "                                 initialises to Color.white, so an unpainted object reads as glowing).",
                "                                 Every row carries a 'location' block: in a slot or on the ground,",
                "                                 whose slot, which hand, and whether THIS process is the authority.",
                "GET  /reflect/instance?refId=&member=&type=&expand=&key=   read ONE instance member on one object.",
                "                                 The instance twin of /reflect. 'type' pins WHICH declaring type the",
                "                                 member is looked up on, which is the only way to reach a private base",
                "                                 field a derived type shadows. Unwraps a ConfigEntry<T>.",
                "GET  /thing/members?refId=&type=&contains=&limit=&values=   every instance member of a Thing (or of a",
                "                                 bare type), with declaring type and current value. Diagnostic of last",
                "                                 resort. values=false skips invoking every property getter.",
                "",
                "GET  /dlc                        this process's DLC entitlement, the session pool, what has been",
                "                                 removed, and the ordering a removal must be sequenced into",
                "POST /dlc/remove                 {dlc, scope=owned|shared|both} REMOVE entitlement from THIS process.",
                "                                 REMOVAL ONLY: the only write it performs clears bits out of the value",
                "                                 already there, so no route, parameter or value can add entitlement.",
                "                                 A request carrying add/grant/set/give/own/unlock is refused, not",
                "                                 ignored. dlc takes a DLCType name, several comma-separated, 'all',",
                "                                 or a numeric mask. In memory, per process, never persisted.",
                "                                 SEQUENCE IT BEFORE WORLD ENTRY: a joiner announces",
                "                                 DLCManager.GetOwnedDLC() at the end of its join and a listen host",
                "                                 re-seeds the pool from it at the end of the load, so a removal after",
                "                                 world entry is silently undone. GET /dlc carries the full ordering.",
                "POST /dlc/restore                put back the baseline this process held before its first removal.",
                "                                 Takes no arguments: there is no value a caller can name that it writes.",
                "",
                "GET  /scenarios                  the in-process probe catalogue: every id, what is armed, where the armed set came from,",
                "                                 what has been dispatched, what the switch did not recognise, and what is blocked on a",
                "                                 missing mod assembly. Answers with no main-thread hop, so it works while the world is parked.",
                "POST /scenario/run               {id, ticks, timeoutMs} run one scenario for N SIMULATION TICKS and return the",
                "                                 [ScenarioRunner] lines it produced, with a pass/fail/inconclusive verdict. No log grep.",
                "                                 Refuses the two pollers and warns on a load-ordered probe rather than pretending.",
                "POST /scenario/arm               {id|ids, persist=true} arm for boot AND from the next simulation tick. Persists to a file",
                "                                 outside BepInEx/config, which the rig's state reset does not blank.",
                "POST /scenario/disarm            {persist=true} arm nothing.",
            };

            return new Json.Obj()
                .Bit("ok", true)
                .Str("plugin", Plugin.PluginName + " " + Plugin.PluginVersion)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Raw("host", HostProfile.Json())
                .Str("hostContract", "one plugin runs in both the game client and the dedicated server. " +
                                     "Endpoints this host cannot serve are refused with 409 and a body carrying " +
                                     "needs / because / instead, never a 404 and never an empty object that " +
                                     "reads like a real answer. host.kind says which process answered.")
                .Str("note", "Every body field can also be passed as a query parameter, which is the " +
                             "reliable way to send a Windows path. All engine work runs on the Unity main thread.")
                .Str("inputContract", "/input/* answers with 'consumed', meaning the game actually read " +
                                      "the synthetic value AND the per-frame consumer was running. " +
                                      "requireConsumed defaults to true, so an unconsumed input answers 409. " +
                                      "'settled' only means the frames elapsed; never assert on it.")
                .Str("roleContract", "/status.role is the one computed answer to 'what is this process': " +
                                     "menu, singlePlayer, joinedClient, listenHost or dedicated. Read it " +
                                     "rather than re-deriving from isClient/isServer, because a listen host " +
                                     "is NetworkRole.Server and so reports isClient=false.")
                .Str("epochContract", "every state-reporting response carries an 'epoch' block naming the " +
                                      "instance that answered and the stretch of its life the answer was " +
                                      "valid in. " + Epoch.ComparabilityRule + " epoch.session increments on " +
                                      "a change of game state, network role, network state, hosting or " +
                                      "world, and never on a joiner arriving (read epoch.clients for that). " +
                                      "epoch.sampledSecondsAgo is wall clock, so a stamp minutes old means " +
                                      "the main thread is not running frames and every value beside it " +
                                      "describes the past.")
                .Str("authorityContract", "epoch.authoritative and location.authoritative are " +
                                          "GameManager.RunSimulation: true on a listen host, a dedicated " +
                                          "server and single player, false on a joined client. A joiner " +
                                          "reporting an item in its hand proves the joiner thinks so; the " +
                                          "same read on the authority is the server's own record, which is " +
                                          "what separates a replicated change from a client-local one.")
                .Raw("epoch", Epoch.Json())
                .StrArray("endpoints", endpoints)
                .ToString();
        }
    }
}
