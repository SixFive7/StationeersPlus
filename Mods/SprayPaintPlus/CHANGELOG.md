# Changelog

Full version history for Spray Paint Plus. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.8.0: Paint whole elevators
- NEW: Painting any elevator shaft or level segment paints every shaft and level segment on the same elevator in one stroke, including the with-cable and without-cable build variants.
- NEW: Server toggle "Network Paint Elevators" (default On).
- The moving carriage is left out of the flood and painted on its own; painting the carriage directly does not repaint the shaft.
- Shift restricts to the single targeted piece; Ctrl gives a checkered pattern that alternates segments up the column.
- Multiplayer-safe; no save-format changes.
- REQUIRES: All players on a server must run 1.8.0 (matching-version handshake).

## v1.7.1: Fix wall flood painting across visual wall types
- FIX: Painting a wall now stays within the same visual wall type. Wall, Wall Flat, Wall Arched, Wall Iron, Wall Padded, etc. each flood as their own group. Previously they shared one C# class, so a Wall Flat stroke also repainted any Wall, Wall Arched, etc. bounding the same room.
- Floor visual variants get the same fix (one floor type no longer spills onto another in the same room).
- Large structures (frames, girders, etc.) keep the old behavior: visual variants still flood together, which is usually what is wanted on a connected build.
- Multiplayer-safe; no protocol or save-format changes.
- REQUIRES: All players on a server must run 1.7.1 (matching-version handshake).

## v1.7.0: Right-click color eyedropper on the spray can
- NEW: Right-click any paintable object with a spray can in hand to copy that object's color onto the can. Left-click the next item to match.
- NEW: Ctrl+right-click picks the as-built color the target would have coming out of its kit / build flow, independent of any later repaint. Useful when a structure has been repainted and you want to restore the original kit color.
- Shift+right-click is reserved for future use (currently no-op).
- The eyedropper only changes the held can's color; painting behavior itself is unchanged. Single-player, host, and remote client all take the same client-local code path; no new network messages.
- REQUIRES: All players on a server must run 1.7.0 (matching-version handshake).

## v1.6.1: Compact in-game mod description
- Tightened the in-game mod-panel description: shorter per-line text, fewer redundant bullets, credits line at the end, and the right-click gun toggle (Add Glow / Remove Glow) called out so it is discoverable without reading the Workshop page.
- Wrapped the description body in a TMP line-height=40% tag so prose lines no longer take a full vanilla line of vertical space; the Workshop Description page is unchanged.
- No gameplay change, no multiplayer protocol change.
- REQUIRES: All players on a server must run 1.6.1 (matching-version handshake).

## v1.6.0: Clean uninstall for saves with glow paint
- Glow state now persists in a side-car file (sprayplus-glow.xml) inside the save ZIP, instead of a custom ThingSaveData subclass inside world.xml.
- Removing the mod from a save is now safe: the side-car is silently ignored on load, glow is lost but the save still opens.
- Before v1.6.0, a save that used glow paint could not be loaded without the mod installed; the XmlSerializer refused the unknown xsi:type and the whole world.xml failed to deserialize.
- Migration is automatic: loading a pre-v1.6.0 save reads the old GlowThingSaveData entries for back-compat; the next save writes the new side-car format and rewrites world.xml without the custom xsi:type.
- No gameplay change, no multiplayer protocol change.
- REQUIRES: All players on a server must run 1.6.0 (matching-version handshake).

## v1.5.2: Sync release alongside the repo-wide About.xml size-cap rule
- No code or content changes since v1.5.1.
- Workshop Description verified at 7962/8000 characters under the newly-documented repo rule.
- REQUIRES: All players on a server must run 1.5.2 (matching-version handshake).

## v1.5.1: Fix Workshop description layout broken by oversized Settings preamble
- The v1.5.0 Settings section added a long explanatory preamble paragraph that pushed the Workshop description past Steam's character limit and broke the layout on the Workshop page.
- Dropped the preamble; the four Scope - Topic headers keep the same grouping and ordering as v1.5.0 (Client - Preferences, Server - Consumables, Server - Glow Paint, Server - Network Painting).
- No gameplay change, no multiplayer protocol change.
- REQUIRES: All players on a server must run 1.5.1 (matching-version handshake).

## v1.5.0: Reorganize in-game settings panel into `Scope - <Topic>` groups
- The twelve settings now live in four collapsible groups: Client - Preferences (Paint Single Item By Default, Invert Color Scroll Direction), Server - Consumables (Unlimited Spray Paint Uses, Suppress Spray Paint Pollution), Server - Glow Paint (Enable Glow Paint), Server - Network Painting (Enable Network Painting, Network Paint Pipes / Cables / Chutes / Walls / Rails / Large Structures).
- Entries within each group use a deliberate order instead of alphabetical (master toggle first, utility networks before structural networks, primary tweaks before secondary).
- Existing player configuration values reset to defaults on first launch after updating. The section names changed, so BepInEx treats the old stored values as orphaned and re-seeds with defaults.
- No gameplay change, no multiplayer protocol change.
- REQUIRES: All players on a server must run 1.5.0 (matching-version handshake).

## v1.4.0: Glow paint gun
- NEW: The Spray Paint Gun is now a self-contained glow applicator. Fire at a painted target and it gains a glow halo (emissive material, visible as a bloom halo in unlit rooms); the target's existing color is preserved.
- NEW: Right-click the gun to switch between Add Glow and Remove Glow modes (repurposes the vanilla on/off toggle, with the HUD label rebranded).
- CHANGE: The gun no longer accepts spray cans. The gun has no ammo requirement and its can slot is hidden in the inventory UI.
- Color and glow are orthogonal: a can paint only changes color, a gun fire only changes glow.
- Shift (single target) and Ctrl (checkered pattern) modifiers apply to gun-paint.
- Glow state persists across save/load and syncs correctly in multiplayer; works on every vanilla paint color.
- NEW: Server toggle "Enable Glow Paint" (default On); when off, the gun reverts to accepting cans and painting their color.
- REQUIRES: All players on a server must run 1.4.0 (matching-version handshake).

## v1.3.0: Paint whole robotic arm rail assemblies
- NEW: Painting any rail piece, junction, bypass, or dock paints every member of the same robotic arm assembly in one stroke.
- NEW: Server toggle "Network Paint Rails" (default On).
- Shift still restricts to the single targeted piece; Ctrl still produces a checkered pattern.
- REQUIRES: All players on a server must run 1.3.0 (matching-version handshake).

## v1.2.5: Repoint Reporting Issues and Source Code URLs at the StationeersPlus monorepo
- The Reporting Issues link in the About description now points at github.com/SixFive7/StationeersPlus/issues.
- The Source Code link now points at github.com/SixFive7/StationeersPlus/tree/master/Mods/SprayPaintPlus.
- No gameplay changes.
- REQUIRES: All players on a server must run 1.2.5 (matching-version handshake).

## v1.2.4: Patch release
- REQUIRES: All players on a server must run 1.2.4 (matching-version handshake).

## v1.2.3: Refreshed Steam Workshop preview art
- Replaced the preview and thumbnail images with a new 16:9 key art that showcases the mod's features (color cycling, infinite paint, network painting, Shift/Ctrl modifiers, multiplayer).
- No gameplay changes.
- REQUIRES: All players on a server must run 1.2.3 (matching-version handshake).

## v1.2.2: Infinite spray fix for single-player
- FIX: Infinite spray paint now works in solo play. The server-only guard was also catching single-player (which the game reports as neither server nor client), so the infinite/suppress logic was silently skipped outside multiplayer.
- REQUIRES: All players on a server must run 1.2.2 (matching-version handshake).

## v1.2.1: Ctrl-checkered fix for walls and large structures
- FIX: Ctrl now produces a proper alternating pattern on walls and large structures. Previously every candidate landed on the same cell-parity, so the checker filter painted the whole set.
- REQUIRES: All players on a server must run 1.2.1 (matching-version handshake).

## v1.2.0: Paint whole rooms and whole frame grids
- NEW: Painting a wall now paints all same-type walls bounding the same room.
- NEW: Painting a frame, girder, or other large structure paints all orthogonally-connected structures of the exact same type.
- NEW: Server toggles "Network Paint Walls" and "Network Paint Large Structures" (both default on).
- Shift still restricts to the single targeted item; Ctrl still produces a checkered pattern.

## v1.1.1: Modifier keys for clients
- FIX: Shift / Ctrl modifiers now apply correctly when remote clients paint. Previously the server read the host's keyboard for every client's paint action, so clients' modifiers were ignored (always behaved as if nothing was held).
- Skip redundant client-side network-paint prefix for non-spray paint paths (cosmetic-only, did not propagate).
- REQUIRES: All players on a server must run 1.1.1 (wire format change in the modifier message).

## v1.1.0: LaunchPadBooster Networking V2
- CHANGE: Migrated to the new Networking V2 API (dedicated channel, automatic compression, multi-packet splitting).
- CHANGE: Explicitly enforce matching mod versions during the multiplayer handshake.
- REQUIRES: StationeersLaunchPad with LaunchPadBooster v0.2.0 or newer.

## v1.0.0: Initial release
- Combines Color Cycler, Network Painter, and Infinite Spray Paint into one multiplayer-safe mod.
- Scroll to cycle spray can colors; hold Shift to paint a single item; hold Ctrl for checkered pattern.
- Network painting for pipes, cables, and chutes with per-type toggles.
- Infinite spray paint uses and pollution suppression (server-side toggles).
- Conflict detection against the three original mods.
