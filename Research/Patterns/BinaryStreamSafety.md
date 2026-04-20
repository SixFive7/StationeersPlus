---
title: Binary stream safety: no try-catch around BuildUpdate / ProcessUpdate
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:72-76 (F0012, primary)
  - Mods/SprayPaintPlus/RESEARCH.md:197-199 (F0022)
  - Mods/SprayPaintPlus/SprayPaintPlus/ConsumableSyncPatch.cs:10-19 (F0323)
related:
  - ../Protocols/GameMessageFactory.md
tags: [network]
---

# Binary stream safety: no try-catch around BuildUpdate / ProcessUpdate

Custom postfixes that append fields to the per-tick update stream or the join snapshot MUST NOT wrap their read/write calls in `try`/`catch`. Catching a read/write exception leaves the underlying `RocketBinaryReader`/`RocketBinaryWriter` at the wrong position, silently corrupting every subsequent field for that object.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0012 (Mods/SprayPaintPlus/RESEARCH.md:72-76, primary):

> Appends one `Int32` (the color index) after the vanilla `Consumable` data in both the per-tick update stream (`BuildUpdate`/`ProcessUpdate`) and the join snapshot (`SerializeOnJoin`/`DeserializeOnJoin`). Uses `SprayPaintHelpers.PaintColorNetworkFlag` (bit 12, `GenericFlag2`) to gate the per-tick write/read.
>
> No try-catch wraps these calls. If the read/write throws, catching it would leave the binary stream at the wrong position, corrupting all subsequent data for that object. Letting it propagate is the safer choice.

F0022 (Mods/SprayPaintPlus/RESEARCH.md:197-199) restates the rationale for the complementary patch:

> No try-catch wraps the binary read/write. If one side writes an Int32 and the other side's read fails mid-stream, catching the exception would leave the `RocketBinaryReader` at the wrong position. Every subsequent field for that object (and potentially the entire update packet) would be misaligned. Letting the exception propagate allows the game's connection-reset logic to recover cleanly.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Write the patch body as straight-line code with no exception handling around the `WriteInt32`/`ReadInt32` (or analogous) call. Rely on the game's connection-reset logic to recover: a propagated exception aborts the current packet and forces a resync, which is loud but correct. A swallowed exception is silent and misaligns every downstream field indefinitely.

F0323 (code comment, `ConsumableSyncPatch.cs:10-19`) states the full recipe:

```text
    // No try-catch around BuildUpdate/ProcessUpdate read/write operations.
    // If WriteInt32 succeeds but ReadInt32 fails (or vice versa), catching
    // the exception would leave the RocketBinaryReader/Writer at the wrong
    // position, silently corrupting ALL subsequent data for that object.
    // Letting the exception propagate is safer; the game's own error
    // handling can reset the connection.
    //
    // The code inside each Postfix is a single WriteInt32/ReadInt32 call
    // behind two guard checks, so the chance of an exception here is
    // effectively zero under normal conditions.
```

Keep the patch body short (one or two primitive reads/writes, with guard checks that precede the read/write). That keeps the exception surface area small and matches the "effectively zero under normal conditions" invariant; the rule is absolute nonetheless.

Gate the per-tick write/read with a `NetworkUpdateFlags` bit so the custom field is only appended when the flag is set. See `../GameSystems/NetworkUpdateFlags.md`.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0012: primary statement of the rule in the context of `Consumable` color-index sync.
- F0022: restatement of the rule when the RESEARCH.md section covers the complementary paint patch; same reasoning.
- F0323: code comment deployed in the patch file, reiterating the rule with the additional "effectively zero under normal conditions" caveat.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0012 primary, F0022 and F0323 confirming.

## Open questions

None at creation.
