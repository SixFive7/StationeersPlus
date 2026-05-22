# PowerTransmitterPlus TODO

This file tracks open issues only. When an item is done, remove it rather than marking it done. Completed work lives in git history.

## Playtest before cutting a release with the reset-postfix fix

The reset-postfix fix (commit 14946c5: gate the `RotatableBehaviour.TargetHorizontal/Vertical` reset postfixes on `NetworkManager.IsServer`, and have `ClearCache` raise `AutoAimUpdateFlag`) is committed but NOT yet released -- the latest tag v1.7.2 predates it. Before cutting the release that ships it, verify in a real session.

- [ ] **Single-player.** Auto-aim a TX-RX pair, link establishes, power flows. Manual TargetHorizontal override clears the cache (IC10 reads `MicrowaveAutoAimTarget` as 0).
- [ ] **Multiplayer, host + client.** Host sets the target; client IC10 reads back the target id without flickering to 0 as the dish slews. This is the specific bug the fix addresses.
- [ ] **Multiplayer manual override.** Either peer overrides TargetHorizontal; both peers read `MicrowaveAutoAimTarget` as 0 (the `ClearCache` flag-raise propagates the clear).
- [ ] **Long-distance (150-200m) joint solve** still establishes the link.

## Playtest the v1.7.3 beam show / hide event-driven evaluator in multiplayer

The v1.7.3 beam fix (event-driven `BeamManager.ReevaluateVisibility` driven by three triggers: `LinkedReceiver` setter, `Thing.OnInteractableUpdated` filtered to `Action == OnOff`, `WirelessPower.Horizontal` / `Vertical` setters; predicate `LinkedReceiver != null && tx.OnOff && rx.OnOff && aimValid` with 7-degree aim tolerance matching link establishment) passed all single-player visual tests (toggle on / off, slew-away, slew-back, zero-power-but-on). The multiplayer trigger paths each fire on remote clients per the design (interactable-state replication for OnOff, per-tick servo-target sync for slew, `ProcessUpdate` for link), but the MP behaviour has not been confirmed in a real session. Before cutting v1.7.3, verify:

- [ ] **MP on / off propagation.** Host (or dedicated server) toggles a TX or RX on or off; remote client's beam hides / re-shows correctly within one frame. Both directions, both dishes.
- [ ] **MP slew propagation.** Host commands a TX to slew away from its linked RX; remote client sees the beam vanish at the same ~7-degree off-axis point as the host, and re-show on slew-back within tolerance.
- [ ] **MP zero-power preservation.** Both dishes on, linked, aimed; cut the source draw. Both peers see the beam still visible with frozen pulse stripes (the v1.5.1 zero-power feature stays intact in MP).
