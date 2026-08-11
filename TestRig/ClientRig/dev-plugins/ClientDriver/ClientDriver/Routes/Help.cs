using System.Collections.Generic;

namespace ClientDriver
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
                "POST /host                       {save|world, difficulty, start, port, serverName, password, maxPlayers, wait, timeoutMs, allowDuplicateIdentity, requireIsolatedSavePath}",
                "                                 become a listen host: load or create the world AND serve it on 127.0.0.1:<port>. Must start from the menu.",
                "                                 Asserts NetworkServer.IsHosting, never 'the call returned'. Refuses a ClientId clash and a non-isolated save path.",
                "POST /disconnect                 {wait, timeoutMs} leave to the main menu",
                "POST /quit                       {hard} exit the process",
                "GET  /saves                      local save list",
                "POST /save                       {name, wait, timeoutMs} persist the world and WAIT for the game to confirm it.",
                "                                 200 only on a confirmed save; 409 with requested:true and a warning when it was asked for but not confirmed.",
                "                                 Host or single player only: the game's save command is scoped HostOrSinglePlayer.",
                "POST /savepath                   {path, force} redirect the user-data root; refuses control characters and the developer's real user-data folder; GET reads it",
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
                "GET  /reflect?type=&member=      read any static field or property by full type name",
                "GET  /reflect/members?type=      every static member of a type with its runtime value type",
            };

            return new Json.Obj()
                .Bit("ok", true)
                .Str("plugin", Plugin.PluginName + " " + Plugin.PluginVersion)
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
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
                .StrArray("endpoints", endpoints)
                .ToString();
        }
    }
}
