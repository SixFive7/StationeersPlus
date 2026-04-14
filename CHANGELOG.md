# Changelog

All notable changes to Spray Paint Plus are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.1] - 2026-04-14

### Fixed
- Shift / Ctrl modifier keys are now honored when remote clients paint.
  The server previously tracked modifier state under the LaunchPadBooster
  connection id but identified the painter from `AttackWithMessage` — which
  does not carry that id on the server — so every client paint fell through
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
