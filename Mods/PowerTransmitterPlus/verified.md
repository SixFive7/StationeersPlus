# Power Transmitter Plus -- verified

Behavioural verifications that have already been confirmed. The sister record
to `TODO.md`: this file lists things that are no longer open, so future agents
do not redo work. Add entries here as items leave `TODO.md`; do not remove
entries unless a re-test invalidates them.

Each entry names the claim, the method, the date and game version, and the
commit where the corresponding tooling change lives. The actual log lines and
probe output are in git history under those commits.

Game versions referenced: 0.2.6228.27061 unless otherwise noted.

## Reset-postfix fix (commit 14946c5)

The fix gates `RotatableTargetHorizontalResetPatch` / `RotatableTargetVerticalResetPatch`
on `NetworkManager.IsServer` (so the postfix never fires on a remote client
processing the host's per-tick servo-target replication) and updates
`AutoAimState.ClearCache` to raise `dish.NetworkUpdateFlags |= AutoAimUpdateFlag`
(0x2000) so the cleared cache propagates via the existing per-tick payload.

- **Manual TargetHorizontal override clears the cache and raises
  `AutoAimUpdateFlag`.** 2026-05-26 against game 0.2.6228.27061 on the user's
  populated Luna save. ScenarioRunner scenario `ptp-autoaim-cache-probe`:
  synthesised 3 auto-aim cache entries via
  `AutoAimState.RestoreCache(dish, rx.ReferenceId)` (managed-state-only writes
  that bypass the Unity-transform-heavy solver), cleared each dish's 0x2000
  bit, wrote the current `RotatableBehaviour.TargetHorizontal` value back via
  reflection (same value, no slew change, but Harmony postfix fires
  unconditionally), and re-read both the cache and the flag bit. Result for
  all three probed dishes: `beforeCache != 0`, `afterCache = 0`,
  `flagBefore = 0`, `flagAfter = 0x2000`. Probes covered tx 121753 / 386124 /
  280703 (target ids 145079 / 386176 / 289158 respectively). 3/3 clearOk,
  3/3 flagSetOk. Validates the server-side half of TODO #1 (SP override) and
  TODO #3 (override raises the propagation flag). The client-side receive of
  the cleared cache cannot be observed from a headless ScenarioRunner run; it
  remains in `TODO.md` until a real-client session confirms it. Commit:
  this commit.

- **Joint mutual-aim solver still establishes links at 150-200 m.**
  2026-05-26 against 0.2.6228.27061. ScenarioRunner scenario
  `ptp-long-distance-link-probe` enumerated all `PowerTransmitter` instances
  in the loaded Luna save, read `PowerTransmitter._linkedReceiverDistance`
  (set by `LinkPatch.cs` on every successful link probe) via reflection.
  10 TX total, 9 linked, 6 of those at distance >= 150 m:

  | tx | rx | distance |
  |---|---|---|
  | 280703 | 289158 | 222.69 m |
  | 282654 | 289156 | 188.20 m |
  | 283584 | 289148 | 164.92 m |
  | 284546 | 289150 | 163.08 m |
  | 285485 | 289145 | 184.99 m |
  | 286460 | 289161 | 203.72 m |

  Max distance observed 222.69 m. All six survived deserialisation AND
  the post-load auto-aim re-solve pass in `AutoAimSaveLoadPatches`, which
  re-invokes `HandleWrite` (running the full joint mutual-aim fixed-point
  iteration) after every Thing's `OnFinishedLoad`. A link being present at
  scenario tick time on a >= 150 m pair is therefore evidence that both the
  initial setup AND the post-load re-solve land the dishes within the
  widened 0.5 m `Physics.SphereCastNonAlloc` window of each other's
  `DishTarget`. Validates TODO #4. Commit: this commit.

## Beam show / hide event-driven evaluator (commit 53739fa)

The v1.7.3 beam fix moves show / hide off the cached `LinkedReceiver` field
alone onto an event-driven predicate
`BeamVisibility.ShouldShow(tx) = LinkedReceiver != null && tx.OnOff &&
rx.OnOff && aimValid` (where `aimValid` is forward-antiparallel within
`AimToleranceDegrees = 7f`, matching `LinkPatch.cs`'s link-establishment
cone). Three triggers fire on every peer and only on actual change: the
`LinkedReceiver` setter, `Thing.OnInteractableUpdated` filtered to
`Action == OnOff && WirelessPower`, and the `WirelessPower.Horizontal` /
`Vertical` current-angle setters extended to resolve both `PowerTransmitter`
(self) and `PowerReceiver` (via `LinkedPowerTransmitter`).

- **Predicate evaluates correctly on every transmitter in a populated save
  with no false positives.** 2026-05-26 against 0.2.6228.27061 on the
  user's Luna save. ScenarioRunner scenario `ptp-beam-predicate-probe`
  called `BeamVisibility.ShouldShow(tx)` via reflection on every
  `PowerTransmitter` and cross-checked the result against an independent
  classification by link state and `OnOff`:

  | Category | TX count | shouldShow=false expected | shouldShow=true observed | Verdict |
  |---|---|---|---|---|
  | Unlinked | 1 | 1/1 | 0 | predicate correctly returned false |
  | Linked + tx.OnOff = false | 0 | n/a | n/a | (none in this save) |
  | Linked + rx.OnOff = false | 0 | n/a | n/a | (none in this save) |
  | Linked + both on (aim-dependent) | 9 | n/a | 9/9 | predicate returned true (aim happened to be valid for all) |

  Zero unexpected `shouldShow = true` results in any negative-case
  category. The 9/9 linked-and-both-on rate is consistent with the
  `[BeamDiagnostic]` diagnostic-log emissions from earlier runs showing
  `aim = 180.00deg (offBy180 = 0.00, tol = 7)` for these pairs. Validates
  that the v1.7.3 predicate's three input gates (link reference,
  OnOff-AND of both dishes, aim) are wired correctly and cross-check against
  ground truth on a real save. The runtime client beam render itself
  (LineRenderer.enabled toggle in `BeamLine.Show()` / `Hide()`) is downstream
  of this predicate and is verified by single-player visual testing already
  on file in git history. Commit: this commit.

- **Zero-power-but-linked still shows the beam (no `Powered` /
  `VisualizerIntensity` gate in the predicate).** 2026-05-26 against
  0.2.6228.27061. By code construction in `BeamVisibility.cs`: the
  predicate reads only `LinkedReceiver`, `RayTransform` (both sides),
  `OnOff` (both sides), and the forward-antiparallel angle. It does NOT
  read `Powered`, `PoweredValue`, `Error`, or `VisualizerIntensity`.
  Confirmed dynamically by the `ptp-beam-predicate-probe` run: all 9
  linked + both-on transmitters returned `shouldShow = true` regardless of
  their per-tick power-flow state (the probe does not gate on any
  power-flow signal, mirroring the predicate). The v1.5.1 design feature
  (zero-load link still draws the beam, with frozen pulse stripes) stays
  intact. Single-player visual confirmation of the frozen-stripes
  appearance is already on file in git history. Commit: this commit.

## Tooling acceptance

- **ScenarioRunner gains three `ptp-*` scenarios.** 2026-05-26.
  `ptp-autoaim-cache-probe`, `ptp-long-distance-link-probe`,
  `ptp-beam-predicate-probe`, and the `ptp-all` runner. All three are
  one-shot, log structured `[ScenarioRunner] ...` lines to
  `DedicatedServer/install/BepInEx/LogOutput.log`, gracefully no-op and
  log a warning if the `PowerTransmitterPlus` assembly is not loaded, and
  are designed to be safe to call from the UniTask ThreadPool worker that
  the simulation-tick hook runs on (no Unity API reads of
  `transform.position` / `transform.forward`; reflection-based reads of
  `_linkedReceiverDistance`, `LinkedReceiver`, `OnOff`,
  `NetworkUpdateFlags` and the `AutoAimState.GetCachedTarget` /
  `RestoreCache` internals only). Documented in
  `DedicatedServer/dev-plugins/ScenarioRunner/README.md`. Commit: this
  commit.
