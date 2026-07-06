# Changelog

Full version history for Marky's Suit Drink System Fix. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.0.1: Label the preview art as temporary
- Preview image now reads "Temporary compatibility patch" so the Workshop art states the mod is a temporary patch. The patch behavior is unchanged.

## v1.0.0: Initial release
- FIX: In-suit drinking no longer throws a `MissingMethodException` on `Entity.Hydrate` on the Sanitation update; the exception previously spammed the log every frame the suit inventory was open and left the Drink action doing nothing. Drinking water from the suit's water tank works again.
- Removes Marky's Suit Drink System's outdated `Suit.InteractWith` prefix at runtime (by its Harmony ID) and replaces it with one built against the current game; his other patches are untouched. Does not modify or redistribute the original mod, and does nothing if it is not installed.
- REQUIRES: BepInEx, StationeersLaunchPad, and Marky's Suit Drink System by Marky. All players on a server need the same setup.
