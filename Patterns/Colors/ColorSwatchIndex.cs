// Centralised vanilla ColorSwatch index values for every SixFive7 mod.
//
// This file is the single source of truth for the integer index the game
// assigns to each built-in color swatch. The catalogue and the rules for it
// live in Patterns/Colors/README.md next to this file. A mod that refers to a
// vanilla color by index links this file via
//   <Compile Include="..\..\..\Patterns\Colors\ColorSwatchIndex.cs" Link="Patterns\ColorSwatchIndex.cs" />
// so the constants are compiled in once per mod.
//
// The values are owned by the game, not by this repo: they are the indices into
// GameManager.CustomColors for the 12 vanilla swatches, verified against
// Assembly-CSharp in game version 0.2.6228.27061 (see
// Research/GameClasses/ColorSwatch.md). The game stores the index as an int.
//
// Rules (full text in Patterns/Colors/README.md):
//   1. These 12 are the vanilla set. Do not add mod-registered swatches here;
//      those occupy indices at and above 12 and are not stable across installs.
//   2. Never renumber. Savegames, IC10 reads, and the paint UI key off the
//      index; changing a value silently repaints existing things.
//   3. Re-verify against Research/GameClasses/ColorSwatch.md when the game
//      updates. If the game ever reorders the vanilla swatches, update the
//      constants and the Research page together.

namespace StationeersPlus.Shared
{
    public static class ColorSwatchIndex
    {
        public const int ColorBlue   = 0;
        public const int ColorGray   = 1;
        public const int ColorGreen  = 2;
        public const int ColorOrange = 3;
        public const int ColorRed    = 4;
        public const int ColorYellow = 5;
        public const int ColorWhite  = 6;
        public const int ColorBlack  = 7;
        public const int ColorBrown  = 8;
        public const int ColorKhaki  = 9;
        public const int ColorPink   = 10;
        public const int ColorPurple = 11;
    }
}
