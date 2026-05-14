// Centralised LogicType ushort values for every SixFive7 mod.
//
// This file is the single source of truth for the integer assigned to every
// custom LogicType this monorepo's mods register at runtime. The catalogue and
// reservation rules live in Patterns/Logic/README.md next to this file. Each
// mod's csproj links this file via <Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs"
// Link="Patterns\LogicTypeNumbers.cs" /> so the constants are compiled in once
// per mod.
//
// Rules (full text in Patterns/Logic/README.md):
//   1. Append at the next free slot below. Increments of 1, compact packing.
//   2. Never bump an existing value. Mods, savegames, and IC10 source code
//      rely on these values for identity; renumbering breaks every script and
//      every saved per-Thing override.
//   3. Before adding a new entry, scan for collisions with known third-party
//      mod bands (see Patterns/Logic/README.md).
//   4. Update Patterns/Logic/README.md's table in the same commit as any new
//      entry here.

namespace StationeersPlus.Shared
{
    public static class LogicTypeNumbers
    {
        // ---------------- PowerTransmitterPlus (6571-6576) ----------------
        public const ushort MicrowaveSourceDraw       = 6571;
        public const ushort MicrowaveDestinationDraw  = 6572;
        public const ushort MicrowaveTransmissionLoss = 6573;
        public const ushort MicrowaveEfficiency       = 6574;
        public const ushort MicrowaveAutoAimTarget    = 6575;
        public const ushort MicrowaveLinkedPartner    = 6576;

        // ---------------- PowerGridPlus (6577- ) --------------------------
        public const ushort LogicPassthroughMode      = 6577;

        // Next free slot: 6578.
    }
}
