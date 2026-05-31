# Changelog

Full version history for KeypadMod Fix. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.0.0: Initial release
- FIX: KeypadMod no longer throws a `MissingMethodException` (`UniTask.Delay`) when a number button is pressed; the keypress pulse works again on the current game build.
- FIX: Entering a value on the keypad screen now applies on multiplayer clients and dedicated servers (it previously kept showing the old value); single-player was already working.
- Patches the installed KeypadMod by WIKUS at runtime; does not modify or redistribute it, and does nothing if KeypadMod is not installed.
- REQUIRES: BepInEx, StationeersLaunchPad, and KeypadMod by WIKUS. All players on a server need the same setup.
