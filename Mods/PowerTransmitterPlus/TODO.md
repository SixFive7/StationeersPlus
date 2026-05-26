# PowerTransmitterPlus TODO

This file tracks open issues only. When an item is done, remove it rather than marking it done. Completed work lives in git history.

## Real-client multiplayer playtests before cutting v1.7.3

ScenarioRunner has verified the server-side mechanism for every TODO item that could be reached without a connected client (see `verified.md`):

- Reset-postfix manual-override clears the cache and raises `AutoAimUpdateFlag` on the host (commit 14946c5).
- Long-distance (>= 150 m) joint mutual-aim solver still establishes the link.
- `BeamVisibility.ShouldShow` predicate returns false for every unlinked / off transmitter and true for linked + both-on + aimed pairs (commit 53739fa).
- Zero-power-but-linked still shows the beam (by code construction; no `Powered` / `VisualizerIntensity` gate in the predicate).

What remains is what fundamentally needs a connected client: the client-side cache state and the client-side beam render cannot be observed from a headless ScenarioRunner run. Before cutting v1.7.3, run a single host + client session and confirm the two below.

- [ ] **MP auto-aim cache stability and propagation, observed by a client.** Host (dedicated server or single-machine host) is on the v1.7.3 build with the reset-postfix fix from commit 14946c5. A real client is on the same build and connected. With a TX-RX pair on auto-aim:
  - (a) IC10 read of `MicrowaveAutoAimTarget` on the **client** side does not flicker to 0 while the host slews the dish. (Pre-fix bug: client's `RotatableTargetHorizontalResetPatch` fired on every replicated servo-target write and wiped the client's cache.)
  - (b) When either peer writes `RotatableBehaviour.TargetHorizontal` manually (tablet, IC10 `s d0 Horizontal`, in-world R-key), **both** peers' IC10 reads of `MicrowaveAutoAimTarget` drop to 0. (Verifies `ClearCache`'s `AutoAimUpdateFlag` propagation lands on the client and triggers `ApplyDeltaUpdate(0)`.)

- [ ] **MP beam render observed by a client.** Host on v1.7.3, real client connected. With a linked TX-RX pair:
  - (a) Toggling either dish on or off makes the beam hide / re-show on the **client** within one frame. Both directions, both dishes.
  - (b) Commanding the TX to slew away makes the beam vanish on the client at roughly 7 degrees of off-axis movement (not at slew completion) and re-show symmetrically on slew-back. Validates the predicate's aim check runs against client-local `RayTransform.forward` and the new `WirelessPower.Horizontal` / `Vertical` setter triggers fire on the client per the per-tick servo-target replication.
