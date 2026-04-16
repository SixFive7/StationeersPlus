# Changelog

## 1.1.0 (2026-04-16)

- Added `MicrowaveLinkedPartner` (LogicType 6576): read-only on both transmitter and receiver. Returns the ReferenceId of the currently linked partner dish, or 0 when unlinked. Enables closed-loop IC10 automation (aim at a target, confirm the link formed via LinkedPartner, fall back if not).
- Host beam visual settings (width, color, emission intensity, stripe wavelength, scroll speed) are now always synced to all clients in multiplayer. Clients see the host's beam appearance on connect and whenever the host changes a visual setting mid-game.

## 1.0.0

- Initial release.
- Visible laser beam between linked microwave transmitter and receiver dishes.
- Scrolling energy pulses along the beam, speed proportional to power throughput.
- Replaces vanilla distance-based capacity derate with a configurable source-draw overhead (k factor, server-authoritative).
- MicrowaveSourceDraw / MicrowaveDestinationDraw / MicrowaveTransmissionLoss / MicrowaveEfficiency logic readouts on both transmitter and receiver (LogicType values 6571-6574).
- Writable MicrowaveAutoAimTarget (6575): IC10 can aim the dish at any Thing by ReferenceId.
- IC10 name resolution for all five LogicTypes.
- Full multiplayer support with server-authoritative simulation and live config broadcast.
