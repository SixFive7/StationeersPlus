---
title: ThingColorMessage
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Networking.Messages.ThingColorMessage
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.SetCustomColor
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.OnServer.SetCustomColor
related:
  - ../GameClasses/Thing.md
  - ../GameClasses/OnServer.md
  - ../GameSystems/NetworkUpdateFlags.md
  - ../Protocols/LaunchPadBoosterNetworking.md
tags: [network, prefab]
---

# ThingColorMessage

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla `ProcessedMessage<ThingColorMessage>` at `Assets.Scripts.Networking.Messages.ThingColorMessage`. Server-to-client broadcast of a `Thing`'s custom-color change. Authoritative channel for custom-color sync in vanilla; `SprayPaintPlus` piggybacks a second channel on `Thing.NetworkUpdateFlags` bit 12 for its own per-can color-index sync rather than extending this message.

## Wire format

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

```
public class ThingColorMessage : ProcessedMessage<ThingColorMessage>
{
    public long ThingId;     // 8 bytes
    public int ColorIndex;   // 4 bytes
}
```

Class declaration at decompile line 259874. Serialization body at line 259884-259891:

```
Deserialize: ThingId = reader.ReadInt64(); ColorIndex = reader.ReadInt32();
Serialize:   writer.WriteInt64(ThingId); writer.WriteInt32(ColorIndex);
```

12 bytes plus the message header. No bit packing, no reserved bytes, no unused slots.

## Sender

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`OnServer.SetCustomColor(Thing thing, int colorIndex)` sends `ThingColorMessage` at decompile line 39453-39459 after applying the color change server-side. The message fans out to all connected clients.

Inside `Thing.SetCustomColor(int index, bool emissive = false)` at decompile line 302860, vanilla:

- Validates via `GameManager.IsValidColor(index)`.
- On valid, sets `CustomColor = GameManager.GetColorSwatch(index)` and raises `NetworkUpdateFlags |= 32` (bit 5) at decompile line 302873-302874.
- On invalid, returns early without setting the color and without raising an error (silent no-op).

## Receiver

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`ThingColorMessage.Process(long hostId)` at decompile line 259881 calls:

```
Thing.Find<Thing>(ThingId).SetCustomColor(ColorIndex);
```

Same `Thing.SetCustomColor` entry as the server uses. The silent-no-op-on-invalid-index behavior therefore applies on receive as well: attempts to piggyback extra bits inside `ColorIndex` are clamped out by `IsValidColor`.

## Backing field on Thing

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.CustomColor` at decompile line 297959 is typed `ColorSwatch` (object reference). The canonical int identifier is `CustomColor.Index`. `ColorSwatch` exposes `Normal` (Material), `Emissive` (Material), `Index` (int), `Light` (Color), `DisplayName` (string).

Save-side: `ThingSaveData.CustomColorIndex` at decompile line 304515 is an `[XmlElement] int` initialized to `-1`. `-1` means "no custom color; use prefab default". `Thing.LoadSimData` at decompile line 302262-302287 calls `SetCustomColor(saveData.CustomColorIndex)` only when the value is `>= 0`.

## Emissive parameter and its transience

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Thing.SetCustomColor(int index, bool emissive = false)` accepts an optional `emissive` flag that, when `true`, swaps the renderer's material to `ColorSwatch.Emissive` (if non-null) via `CustomColorMapping.SetEmissive` and writes `Color.white` to the `_EmissionColor` shader property on every `ThingRenderer`. The full body is documented in `../GameClasses/ColorSwatch.md`.

This wire format does NOT carry the `emissive` flag:

- `OnServer.SetCustomColor` (decompile line 39449-39463) applies the color via `thing.SetCustomColor(colorIndex)` without passing an emissive value, then sends `ThingColorMessage { ThingId, ColorIndex }`.
- `ThingColorMessage.Process` (line 259881-259883) calls `Thing.Find<Thing>(ThingId).SetCustomColor(ColorIndex)` on the receiving client, also without an emissive value.
- `ThingSaveData` persists only `CustomColorIndex`; on load, `Thing.LoadSimData` (line 302262-302287) calls `SetCustomColor(index)` defaulting to `emissive: false`.

Every re-entry therefore clears the emissive state. Vanilla callers work around this by re-applying `emissive: true` on every state change:

- `ChemLight.OnInteractableUpdated` at decompile line 322433.
- `RoadFlare.OnInteractableUpdated` at decompile line 334170.

No painted structure (wall, pipe, cable, frame) ever receives `emissive: true` in vanilla. Mods that want glow to persist and sync must store their own `IsGlowing` flag per Thing and re-apply emissive after every `SetCustomColor` entry. See `../GameSystems/RenderingPipelineAndGlow.md` for the full rendering approach and `../GameClasses/ColorSwatch.md` for swatches whose `Emissive` material is null.

## Side-channel options for extra paint metadata

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Because the wire format has no slack and `SetCustomColor` clamps invalid indices, mods that need to carry per-paint metadata (for example, a "glow" flag) to clients cannot piggyback inside `ColorIndex` or extend this message without breaking version compatibility. Two documented alternatives:

1. Claim a free bit in `Thing.NetworkUpdateFlags` (see `../GameSystems/NetworkUpdateFlags.md`) and append a field via postfix patches on `Thing.BuildUpdate` / `Thing.ProcessUpdate` / `Thing.SerializeOnJoin` / `Thing.DeserializeOnJoin`. Mirrors `SprayPaintPlus.ConsumableSyncPatch`, but applied to `Thing` so the field is carried by painted structures (walls, pipes, cables) rather than only by spray cans. See `../Patterns/BinaryStreamSafety.md` for the no-try-catch constraint.
2. Ship a dedicated LaunchPadBooster `INetworkMessage` (see `./LaunchPadBoosterNetworking.md`) that carries the metadata independently. Incurs a second round-trip per paint event.

## Verification history

- 2026-04-21: page created. Decompile findings sourced from Assembly-CSharp.dll (ThingColorMessage at line 259874-259891, OnServer.SetCustomColor sender at line 39453-39459, Thing.SetCustomColor receiver at line 302860 with NetworkUpdateFlags |= 32 at line 302873-302874, Thing.CustomColor field at line 297959, ThingSaveData.CustomColorIndex at line 304515, Thing.LoadSimData at line 302262-302287).
- 2026-04-21: added "Emissive parameter and its transience" section documenting that the `emissive` flag on `Thing.SetCustomColor` is not stored, saved, or synced, and pointing at the two vanilla callers (`ChemLight.OnInteractableUpdated` at line 322433 and `RoadFlare.OnInteractableUpdated` at line 334170). Additive; no prior claim changed.

## Open questions

- `Thing.SetCustomColor` raises `NetworkUpdateFlags |= 32` (bit 5). The full pipeline from bit 5 to the `ThingColorMessage` send has not been traced on this page; verify whether bit 5 is the exclusive trigger for a color-message send or whether it is a generic "dirty" marker consumed by multiple senders.
