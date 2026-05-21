# Changelog

Full version history for Equipment Plus. The newest entry also appears in `About.xml` `<ChangeLog>`.

## v1.0.0: Initial release
- Dynamic multi-slot support for Sensor Lenses and the Advanced Tablet (slots grow as you fill them, shrink as you empty them; exactly one empty slot visible at all times).
- Multiplayer-safe Configuration Cartridge editing (scroll to select a line, left-click to copy read-only values or edit writable values; both LogicType and per-slot LogicSlotType writes work from any client via vanilla SetLogicFromClient and a custom SetLogicSlotFromClient message).
- Built-in logic slot display on the Config Cartridge screen (writable in green, read-only in grey), absorbing Slot Configuration Cartridge.
- Scroll-modifier equipment shortcuts: Ctrl + scroll cycles tablet cartridges (auto-equips a tablet from off-hand, toolbelt, backpack, or suit when the active hand is empty); LeftShift + scroll cycles worn sensor chips; Ctrl + LeftShift + scroll tightens/widens the worn helmet's spotlight (auto-brightness scales intensity and range; configurable via Client - Helmet Beam settings).
- Vanilla camera zoom auto-rebound from LeftShift to RightShift on first launch to free LeftShift for the lens cycle binding (custom bindings are respected; only the LeftShift default is replaced). Hold the RIGHT shift for camera zoom.
- Active sensor chip and active tablet cartridge persist across save/load and sync across peers in multiplayer.
- Per-character helmet beam preferences persist across save/load (each player's spot angle, intensity, and range survive a reload; on join, the host pushes the current beam map to the connecting client).
- Save persistence uses side-car files inside the save ZIP (no xsi:type extension to world.xml), so uninstalling Equipment Plus never breaks an existing save.
- Full multiplayer support; matching mod versions enforced during the connection handshake.
- Startup conflict detection for Better Advanced Tablet, ImprovedConfiguration, Slot Configuration Cartridge, and Better Headlamp; the mod refuses to load if any of those are enabled and logs an ongoing warning.
