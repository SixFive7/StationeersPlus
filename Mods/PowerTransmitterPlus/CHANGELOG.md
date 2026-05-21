# Changelog

Full version history for Power Transmitter Plus. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.7.2: Beam pulse position no longer jumps when power flow rate updates
- Pulse stripes now integrate position incrementally each frame instead of multiplying total elapsed time by the current speed, so a speed change only affects future motion. Previously every speed change retroactively rescaled the stripes' world position, producing visible position jumps. Most noticeable with mods that update source-draw rates frequently, such as Re-Volt's proportional power sharing.
- No multiplayer protocol change; v1.7.1 and v1.7.2 clients are wire-compatible.

## v1.7.1: Long-distance auto-aim pairs now establish links reliably
- Two dishes auto-aimed at each other now solve a joint mutual-aim geometry instead of each chasing the other's stale pose. Long-range pairs (150 m+) that previously slewed close but never linked now establish on the first servo settle.
- The link probe between transmitter and receiver was widened from a hairline raycast to a 0.5 m sphere cast filtered to receiver dish targets, so any sub-degree aim residual or mid-slew jitter still establishes the link.
- A post-load auto-aim pass re-solves every cached pair after each save load, so existing saves pick up the joint-solve improvement without players having to clear and reset auto-aim, and any cached target whose Thing has been deconstructed is cleared cleanly.
- All players on a server must be on v1.7.1 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.7.0: Joining multiplayer clients now receive host config at join time
- The seven host-authoritative settings (Cost Factor, Beam Color, Beam Width, Emission Intensity, Stripe Wavelength, Scroll Speed, Trough Brightness) now ride the IJoinSuffixSerializer payload alongside the auto-aim cache. The earlier rebroadcast on NetworkManager.PlayerConnected fired before the joiner entered the broadcast list and silently missed them; live SettingChanged updates were never affected and still flow as before.
- All players on a server must be on v1.7.0 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.6.2: Auto-aim cache stays in sync mid-session for joined multiplayer clients
- v1.6.1's cache only synced at join. v1.6.2 piggybacks the target ReferenceId onto each WirelessPower's per-tick delta via an unused NetworkUpdateFlags bit, so host-side target changes re-sync clients on the next tick.
- All players on a server must be on v1.6.2 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.6.1: Auto-aim cache now reaches joining multiplayer clients
- v1.6.0's join broadcast fired before the joiner entered the broadcast list. v1.6.1 switched to IJoinSuffixSerializer so the cache ships with the world snapshot.
- All players on a server must be on v1.6.1 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.6.0: Auto-aim targets persist across save/load and multiplayer join
- MicrowaveAutoAimTarget now reads the correct ReferenceId after loading a save and after a remote client joins. Previously the value reset to 0 on every save load and every fresh client connect, even though the dish was still aimed correctly and the link still worked.
- Persistence is implemented as a side-car XML file (pwrxmplus-autoaim.xml) inside the save ZIP. world.xml stays vanilla; uninstalling the mod after a save leaves no broken save state.
- Multiplayer: on every client connect the host broadcasts the current cache, so a joining client's IC10 reads match the host's. The existing IJoinValidator handshake is unchanged; this is a separate per-mod message on the established sync channel.
- All players on a server must be on v1.6.0 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.5.1: Beam visibility now follows link state, not power flow
- The beam between linked dishes is now drawn whenever the link is up, regardless of whether power is flowing. Previously the beam only appeared while power was being delivered, so a freshly-formed link with no load looked like no link at all.
- The pulse train continues to indicate power flow: scrolling stripes when power is moving, stationary "standing waves" when the link is up but no power is moving.
- All players on a server must be on v1.5.1 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.5.0: Wall and ceiling placement for transmitter and receiver dishes
- Transmitter and receiver dishes can now be built on walls and ceilings as well as on the floor. The placement cursor accepts every face the player aims at, and the rotate hotkeys cover yaw, pitch, and roll once the cursor is on a non-floor face. A wall-mounted transmitter can link to a ceiling-mounted receiver as long as their dishes face each other.
- Auto-Aim accuracy improved across orientations so a single MicrowaveAutoAimTarget write reliably forms the link on non-floor pairs.
- New server-authoritative setting Allow Non-Floor Placement (default on) under Server - Placement. When off, vanilla floor-only placement is preserved.
- Allow Non-Floor Placement requires a full Stationeers restart to take effect. Mismatches between client and host are caught at join time with a readable error.
- All players on a server must be on v1.5.0 or newer; the LaunchPad version handshake rejects mixed installs.

## v1.4.0: Enable Auto-Aim server toggle with join-time handshake
- New server-authoritative setting Enable Auto-Aim (default on) under Server - Features. When off at game start, MicrowaveAutoAimTarget (LogicType 6575) is not registered at all: no IC10 constant, no tablet dropdown entry, no Stationpedia page, no screen syntax highlighting, no Harmony patches. Feature surface disappears rather than being hidden.
- Changing Enable Auto-Aim requires a full Stationeers restart to take effect. The main-menu settings panel shows a crimson "Changes in configuration require a restart to apply" banner on change.
- In multiplayer, clients whose Enable Auto-Aim value does not match the host's are rejected at join time via LaunchPadBooster IJoinValidator with a readable error. The join handshake runs before world entry, so a mismatched client never loads the server's world.
- All players on a server must be on v1.4.0 or newer; the LaunchPadBooster version handshake rejects mixed installs before the feature-parity handshake runs.
- Existing player configs gain the new entry with default value true on first launch after updating. No gameplay change for anyone who does not toggle the setting.

## v1.3.1: Drop LogicType numeric values from user-facing docs
- Logic Readouts and Auto-Aim tables in the Workshop description no longer show per-name numeric IDs (6571-6576). The ID numbers are implementation detail; players write IC10 against the names, not the numbers.
- Simplified the "Custom logic type range" Compatibility note to describe the collision avoidance without spelling out the exact range.
- No gameplay change, no wire-format change.

## v1.3.0: Reorganize in-game settings panel into `Server - <Topic>` groups
- The seven settings now live in three collapsible groups: Server - Distance (Cost Factor), Server - Pulse (Stripe Wavelength, Scroll Speed, Trough Brightness), Server - Visual (Beam Color, Beam Width, Emission Intensity).
- Entries within each group use a deliberate order instead of alphabetical (master tweaks first, secondary ones after).
- Existing player configuration values reset to defaults on first launch after updating. The section names changed, so BepInEx treats the old stored values as orphaned and re-seeds with defaults.
- No gameplay change, no multiplayer protocol change.

## v1.2.0: Trough Brightness now syncs from host and takes effect live
- Host beam Trough Brightness is now broadcast alongside the other visual settings, so clients always match the host's look.
- Changing Trough Brightness on the host applies immediately to all connected clients (previously required a game restart because it was baked into a cached stripe texture).
- All players on a server must have v1.2.0; the LaunchPad handshake rejects mixed versions automatically.

## v1.1.5: Fix About.xml parse error that caused the mod to load as [Invalid About.xml]
- The v1.1.1 changelog entry contained a literal `<WorkshopHandle>` tag which the XML parser read as a child element inside `<ChangeLog>`, breaking About.xml deserialization.
- Escaped the angle brackets so the tag renders as plain text in Workshop change notes.
- No gameplay changes.

## v1.1.4: Repoint Reporting Issues and Source Code URLs at the StationeersPlus monorepo
- The Reporting Issues link in the About description now points at github.com/SixFive7/StationeersPlus/issues.
- The Source Code link now points at github.com/SixFive7/StationeersPlus/tree/master/Mods/PowerTransmitterPlus.
- No gameplay changes.

## v1.1.3: Screen syntax highlighting fix
- Fixed custom LogicType names rendering red on in-game computer and laptop screen previews.

## v1.1.2: Remove Shader Name config entry
- Removed the Shader Name config entry. The beam shader is now fixed to the built-in fallback chain (Legacy Shaders/Particles/Additive with three fallbacks).

## v1.1.1: Patch release (Workshop preview images in build output)
- Added `<WorkshopHandle>` to About.xml and ensured Preview.png / thumb.png copy to the build output for the Workshop upload pipeline.

## v1.1.0: Link-partner logic and host-broadcast beam visuals
- Added MicrowaveLinkedPartner (6576): read-only LogicType returning the ReferenceId of the currently linked partner dish on both transmitter and receiver.
- Host beam visual settings (width, color, emission intensity, stripe wavelength, scroll speed) are now always synced to all clients in multiplayer.

## v1.0.0: Initial release
- Draws a visible laser beam between linked microwave transmitter and receiver dishes.
- Scrolling energy pulses along the beam, speed proportional to power throughput.
- Replaces vanilla distance-based capacity derate with a configurable source-draw overhead (k factor, server-authoritative).
- Adds MicrowaveSourceDraw / MicrowaveDestinationDraw / MicrowaveTransmissionLoss / MicrowaveEfficiency logic readouts on both transmitter and receiver (LogicType values 6571-6574).
- Writable MicrowaveAutoAimTarget (6575): IC10 can aim the dish at any Thing by ReferenceId.
- IC10 name resolution for all five new LogicTypes.
- Full multiplayer support with server-authoritative simulation and live config broadcast.
