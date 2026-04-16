# Changelog

All notable changes to Spray Paint Plus are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.2.3] - 2026-04-15

### Changed
- Replaced the Steam Workshop preview image and in-game thumbnail
  with new 16:9 key art that illustrates the mod's features: color
  cycling, infinite spray paint, network painting, Shift and Ctrl
  modifier keys, and multiplayer cooperation. The prior images were
  uploaded at off-ratio sizes (1232x706 and 640x367) which Steam
  letterboxed to fit its 16:9 display frame; the new art is exact
  16:9 (1280x720 for `Preview.png`, 640x360 for `thumb.png`).

### Requires
- All players on a server must run 1.2.3 (matching-version handshake).

## [1.2.2] - 2026-04-14

### Fixed
- Infinite spray paint now works in single-player. The guard on
  `SprayCan.OnUseItem` short-circuited on `!NetworkManager.IsServer`,
  which is true for both multiplayer remote clients (correct, the
  server owns quantity) *and* single-player (`NetworkRole.None`,
  which the game itself treats as "not server, not client"). Updated
  the guard to `IsActive && !IsServer` so the infinite/suppress
  logic runs in solo play while still deferring to the server in
  multiplayer.

### Requires
- All players on a server must run 1.2.2 (matching-version handshake).

## [1.2.1] - 2026-04-14

### Fixed
- Ctrl-checkered pattern now actually alternates on walls and large
  structures. `Grid3` stores world coords scaled x10 and walls /
  `LargeStructure`s snap to a 2-world-unit cell grid, so every cell-
  aligned structure's GridPosition landed on the same parity and the
  checker filter accepted every candidate. Parity is now derived from
  the delta between the two GridPositions using the structure's own
  `GridSize`. The delta is always an exact multiple of cell size in
  Grid3 units, so integer division is exact and the grid offset falls
  out of the equation.

### Requires
- All players on a server must run 1.2.1 (matching-version handshake).

## [1.2.0] - 2026-04-14

### Added
- Wall room-fill: painting a wall now paints all same-type walls bounding
  the same `Room`. Cells inside `room.Grids` plus one layer of neighbors
  are scanned, and structures whose exact type matches and whose
  `GetRoom()` returns the same room are painted.
- Large structure grid flood-fill: painting a `LargeStructure` (frame,
  girder, ladder, etc.) flood-fills orthogonal 6-neighbor cells, painting
  every structure of the exact same type that is connected through a
  chain of cells containing that type. `Cell.NeighborCells` is filtered
  to orthogonal via a Grid3 axis-diff check because it includes 26
  neighbors (corners + diagonals).
- Server toggles `Network Paint Walls` and `Network Paint Large Structures`
  (both default on). Disabling walls short-circuits the wall branch
  without falling through to the large-structure path.

## [1.1.1] - 2026-04-14

### Fixed
- Shift / Ctrl modifier keys are now honored when remote clients paint.
  The server previously tracked modifier state under the LaunchPadBooster
  connection id but identified the painter from `AttackWithMessage`, which
  does not carry that id on the server, so every client paint fell through
  to reading the host's own keyboard. The painter is now identified by the
  Human ReferenceId from the attack payload, matching how vanilla already
  identifies actors.

### Changed
- Wire format of `PaintModifierMessage` now carries the sender's Human
  ReferenceId. Enforced by the existing matching-version handshake.
- `NetworkPainterPatch` prefix short-circuits on remote clients. The
  authoritative network paint runs on the server and is broadcast back;
  the client-side prefix never contributed for spray-paint flows and only
  produced purely-local visual artifacts on non-spray paths.

## [1.1.0] - 2026-04-12

### Changed
- Migrated to LaunchPadBooster Networking V2 API (PR #32).
  Messages now use a dedicated raknet channel with automatic compression
  and multi-packet splitting, replacing the piggybacked `ThingColorMessage` approach.
- Explicitly enforce matching mod versions during the multiplayer handshake
  via `Networking.Required = true`.

### Requires
- StationeersLaunchPad with LaunchPadBooster v0.2.0 or newer.

## [1.0.0] - 2026-04-09

### Added
- Initial release combining Color Cycler, Network Painter, and
  Infinite Spray Paint into a single multiplayer-safe mod.
- Scroll to cycle spray can colors; Shift to paint a single item;
  Ctrl for checkered pattern.
- Network painting for pipes, cables, and chutes with per-type toggles.
- Infinite spray paint and pollution suppression (server-side toggles).
- Conflict detection against the three original mods.
