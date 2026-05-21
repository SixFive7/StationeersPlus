# PowerTransmitterPlus TODO

This file tracks open issues only. When an item is done, remove it rather than marking it done. Completed work lives in git history.

## Playtest before cutting a release with the reset-postfix fix

The reset-postfix fix (commit 14946c5: gate the `RotatableBehaviour.TargetHorizontal/Vertical` reset postfixes on `NetworkManager.IsServer`, and have `ClearCache` raise `AutoAimUpdateFlag`) is committed but NOT yet released -- the latest tag v1.7.2 predates it. Before cutting the release that ships it, verify in a real session.

- [ ] **Single-player.** Auto-aim a TX-RX pair, link establishes, power flows. Manual TargetHorizontal override clears the cache (IC10 reads `MicrowaveAutoAimTarget` as 0).
- [ ] **Multiplayer, host + client.** Host sets the target; client IC10 reads back the target id without flickering to 0 as the dish slews. This is the specific bug the fix addresses.
- [ ] **Multiplayer manual override.** Either peer overrides TargetHorizontal; both peers read `MicrowaveAutoAimTarget` as 0 (the `ClearCache` flag-raise propagates the clear).
- [ ] **Long-distance (150-200m) joint solve** still establishes the link.
