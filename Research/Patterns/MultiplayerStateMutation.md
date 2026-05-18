---
title: MultiplayerStateMutation
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - ./ServerAuthoritativeSimulation.md
  - ./SinglePlayerNetworkRole.md
  - ../GameClasses/CableNetwork.md
  - ../Protocols/NetworkPuristPlusCableAlignment.md
related:
  - ./ServerAuthoritativeSimulation.md
  - ./SinglePlayerNetworkRole.md
  - ../GameClasses/CableNetwork.md
tags: [harmony, network]
---

# Multiplayer state-mutation patches: gate or explain

A Harmony patch (or non-Harmony code triggered by a game event) that mutates persistent game state during a lifecycle hook firing on both server AND client, without a proper server-only gate, where the mutation result can differ between the two sides, produces a stable multiplayer desync. This page is the audit lens for that bug class and the per-patch checklist for new code.

## The bug class
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

Four conjoint criteria. All four must hold for a patch to be in the class.

1. The patched method (or the event handler) fires on both server and client. Methods that vanilla only invokes server-side (e.g. anything reached only via `GameManager.RunSimulation` paths like `ElectricityTick`, `OnAtmosphericTick`, or IC10 chip evaluation) are out of scope -- the class is about patches that vanilla touches symmetrically.
2. The patch mutates persistent game state. "Persistent" means: a field or property whose value is observed by future ticks or future serialisation. Reading the same field, or writing to a local variable, does not qualify. Examples that qualify: `ThingTransform.rotation`, `RegisteredRotation`, `Direction`, network membership lists, dictionary entries keyed by `ReferenceId`, `RotatableBehaviour.TargetHorizontal / TargetVertical`, anything that ends up in a SerializeOnJoin / BuildUpdate / save file.
3. The mutation is not gated to `NetworkManager.IsServer` (or an equivalent host-only signal: `GameManager.RunSimulation`, or a thread-static flag that is itself only set inside a host-only branch). Gating only an enqueued `NetworkUpdateFlags |= N` write while leaving the field assignment unconditional does NOT count -- the field write is the desync.
4. The mutation result is not deterministic across the wire. The result depends on floating-point classification edges (`Mathf.Abs(forward.x) >= Mathf.Abs(forward.y)`), ordering-dependent lookups (the order vanilla returns adjacent cables in), timing of `base.X` chains, transient input state (which side last received a wire packet), or worker-thread scheduling.

Class-A bug -> visible symptoms include: stable id desyncs (server and client read different `ReferenceId` for the same logical object), visual or geometric mismatches (rotation, position, orientation), state-machine divergences (one side thinks a feature is active, the other does not), value drift on the same logical computation.

## Sibling class: the merge-non-authoritative pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

A second, vanilla-side class is structurally related: when a static `Merge(List<T>)` picks the survivor by `list[0]` AND both server and client invoke `Merge(ConnectedX(thing))` independently AND there is no "rebuild on merge" event analogous to `RebuildCableNetworkEvent` that carries the host's chosen survivor id across, ANY ordering perturbation upstream (from a class-A bug, from a third-party mod, from FP edges) latches into a stable id-level desync.

Confirmed instances in 0.2.6228.27061:

- `CableNetwork.Merge(List<CableNetwork>)` (decompile line 253998). Affects `CableNetwork` and the inheriting `WirelessNetwork`.
- `StructureNetwork.Merge(List<StructureNetwork>, out StructureNetwork)` (line 177412). Affects `RocketNetwork`, `RoboticArmNetwork`, `LandingPadNetwork`, and any other `StructureNetwork` subclass that overrides `INetworkedStructure.DeserializeOnJoin` to re-run a local merge.

Not affected: `PipeNetwork`, `ChuteNetwork`. They inherit the `Merge(List)` signature but their wire-deserialisation paths do not re-call it locally on the client -- they trust the server's id from the join packet.

Power Grid Plus closes the class-B pattern in `Patches/MergeDeterminismPatches.cs` by sorting the input list by `ReferenceId` ascending before vanilla picks `list[0]`. ReferenceIds are server-allocated and identical on every peer (see `Research/GameClasses/CableNetwork.md` "Lifecycle: three constructors"), so both sides converge on the same survivor regardless of upstream ordering disagreement.

## Audit checklist for a new patch
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

For every Harmony patch that touches mutable state, the author answers all six questions in a comment block above the patch class. The answers either justify the absence of an `IsServer` gate or motivate adding one.

1. **What method does this patch target?** Class name + member name.
2. **Does vanilla invoke that method on the client?** Yes / no. If "yes", continue. If "no" (the method is reached only via host-only paths like `GameManager.RunSimulation`-gated callers), document the host-only caller chain and stop -- the patch is safe by transitivity.
3. **What persistent state does this patch mutate?** List the fields / properties / dictionaries / lists / network flags.
4. **What gates the mutation?** One of:
   - `NetworkManager.IsServer` direct check.
   - `GameManager.RunSimulation` (host-only via the host-only simulation tick).
   - A thread-static flag that is only set inside a host-only branch (mode-gating).
   - Caller gating: the patch is targeted at a method that is itself host-only-called.
5. **Could the mutation result differ between server and client given identical inputs?** Yes / no. If "yes" and step 4 does not produce a host-only gate, the patch is in the class -- add an explicit `IsServer` check.
6. **What is the propagation mechanism?** When the host mutates the state, how does the change reach clients? One of:
   - Standard vanilla per-tick delta (`NetworkUpdateFlags |= N` writing into `BuildUpdate`).
   - Custom `NetworkUpdateFlags` bit + matching `BuildUpdate` / `ProcessUpdate` postfixes.
   - Custom `INetworkMessage` (per-event message).
   - Join-time sync (`SerializeOnJoin` / `DeserializeOnJoin` postfixes).
   - Save-load sidecar that replays on both sides at load time.

The check that step 4 actually closes step 5 is the load-bearing one. A patch that mutates non-deterministic state with no gate is the bug.

## Worked examples from this monorepo
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

### NetworkPuristPlus CableRoll.Normalise -- caller-gated, redundant inner gate added

Targets `Cable.OnRegistered` postfix (and an additional `World.OnLoadingFinished` postfix). Both callers gate on `!GameManager.RunSimulation`. The mutation (rotation + `RegisteredRotation` + `Direction` + `NetworkUpdateFlags`) is therefore reachable only on the host; clients pick up the rotation via the standard bit-1 transform delta. Added a defensive `if (!NetworkManager.IsServer) return false;` at the top of `Normalise` so the function is safe-by-construction even if a future caller forgets the outer gate. Step 5 answer is "yes" (the canonical rotation is computed from a floating-point axis-dominance classification that could disagree on rotations near the 45-degree edge), step 4 answer is "caller-gated `RunSimulation` plus inner `IsServer`", and step 6 answer is "standard bit-1 transform delta".

### PowerTransmitterPlus auto-aim reset postfix -- real class-A bug fixed

Targets `RotatableBehaviour.TargetHorizontal` / `TargetVertical` setters. Vanilla invokes the setter on the client during `WirelessPower.ProcessUpdate`'s application of the host's bit-256 servo-target delta, so the patch fires on the client. The reset postfix called `ClearCache(dish)` unconditionally -- which on the client cleared the dish's auto-aim target id whenever the host slewed the servo, regardless of whether the host had actually cleared its own cache. If the same tick did NOT also carry the custom `AutoAimUpdateFlag = 0x2000` payload (because the host did not change the target this tick, only stepped the servo toward the existing target), the client's cache stayed at zero indefinitely while the host's stayed at the real target id.

Fix: gate both reset postfixes on `NetworkManager.IsServer`, and modify `ClearCache` to set the `AutoAimUpdateFlag` so legitimate host-side clears still propagate to clients via the existing per-tick payload. Step 6 answer becomes "custom `0x2000` bit; client's `ApplyDeltaUpdate` writes the host's value verbatim, including id=0 for explicit clears".

### SprayPaintPlus ThingSetCustomColorGlowPatch -- mode-gated, comment added

Targets `Thing.SetCustomColor` postfix. Vanilla invokes `SetCustomColor` on the client during save load, during reception of an `OnServer.SetCustomColor` network message, and during `ColorCycler` animation steps -- so the postfix does fire on the client. But the postfix's `GlowingThingIds` mutation is gated by `GlowPaintHelpers.CurrentMode != Idle`, and `CurrentMode` is set off-Idle only inside `ThingAttackWithGunPatch.Prefix`'s `if (GameManager.RunSimulation)` branch. So on a client the mode stays Idle and the mutation branches never fire. The "re-skin emissive" branch IS unconditional but is a deterministic function of the per-side `GlowingThingIds[id]`, which is host-authoritative via the custom `0x2000` `GlowNetworkFlag` and join-time sync.

This patch is safe without an explicit `IsServer` gate. The 2026-05-18 audit added a comment block at the patch site documenting why, so a future reader does not re-litigate the call.

### PowerGridPlus passthrough SetLogicValue patches -- defense-in-depth gate added

Targets `Device.SetLogicValue` / `Transformer.SetLogicValue`. Vanilla invokes `SetLogicValue` only on the host (IC10 ticks on the host, tablet writes are network-routed to the host), so the patches are caller-gated in practice. But the patch did NOT have an explicit `IsServer` check, which leaves a future modded client-side logic writer free to desync the per-Thing `PassthroughModeStore`. Added `if (!NetworkManager.IsServer) return false;` after the `LogicType` filter. Zero observable impact today; protects against future contributors.

## Anti-patterns this rules out
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

- **Gating only the `NetworkUpdateFlags` write.** If the field assignment runs on the client but only the `|= N` write is `IsServer`-gated, the field is locally desynced and the host never sees a reason to re-send it. The field assignment must be inside the gate.
- **Trusting `Reapplying` / `SuppressReset` thread-statics across host/client mirroring.** These flags work for re-entrancy within a single side. They do not gate the cross-side mutation question -- a flag set by code that runs on the host does not protect a method that ALSO runs on the client.
- **Patching a setter without checking who invokes it.** Property setters are routine reflection targets for sync code (`ProcessUpdate` calling the setter to apply a wire-arriving value). A postfix on a property setter fires on every peer that applies the value, not just the originator.
- **Assuming "vanilla calls this method server-only" without grepping every caller in the decompile.** Some methods are called on the client during save load, during join sync, or during `ProcessUpdate`'s application of a delta. Run the audit checklist (step 2) against the decompile rather than guessing.

## Verification history

- 2026-05-18: page created. Sourced from the multiplayer cable-network-id desync investigation. Documents the class-A bug (client-side mutation without proper gating) and class-B bug (vanilla merge-non-authoritative pattern) plus the six-question audit checklist and four worked examples from the monorepo (NetworkPuristPlus, PowerTransmitterPlus, SprayPaintPlus, PowerGridPlus passthrough setters). Companion to the deterministic Merge sort resolution documented in `Research/GameClasses/CableNetwork.md`.

## Open questions

- Are there other vanilla static methods with the same "list[0] wins + each side decides independently + no rebuild event" shape beyond `CableNetwork.Merge` and `StructureNetwork.Merge`? A broader decompile sweep across the `Assets.Scripts.Networks` namespace would be needed to be sure. Candidates worth probing: anything else inheriting `StructureNetwork`, plus the various `Atmospherics*Network` types if their save-load and join-sync paths re-derive membership locally.
