# Power Transmitter Plus Playtests

Implemented changes awaiting in-game confirmation. An agent records a playtest here, not in `TODO.md`, when it has changed code whose behavior can only be confirmed by running the game: single-player, a hosted multiplayer session, or the dedicated server under `DedicatedServer/`. This keeps `TODO.md` a list of work still to do, and gives one place to check whether a change already has a pending test before adding another.

Rules:
- Add an entry when code is implemented but its in-game behavior is unconfirmed. Write down everything a tester needs: what changed (commit if there is one), single-player vs multiplayer / dedicated-server, the save or world to set up, the exact in-game steps, what to watch (InspectorPlus request files, specific log lines, on-screen behavior, IC10 reads), and the expected result. Point at any staged `.work/<date>-<slug>/` request files or playbook.
- Check first. Before adding, scan the entries below so a change already covered by a pending test is not duplicated; extend the existing entry instead.
- Remove an entry when one of these happens: a run confirms it works; a run shows it broken (then add a fresh `TODO.md` item for the fix, or keep working on it now); or the player says the playtest is done. Entries are plain bullets, not checkboxes: like `TODO.md`, finished items are removed, not ticked off. Outcomes live in git history.

The first two entries below gate cutting the v1.7.3 release. The server-side mechanism is already verified by ScenarioRunner (see `verified.md`): the reset-postfix clears the cache and raises `AutoAimUpdateFlag` on the host (commit `14946c5`), the `BeamVisibility.ShouldShow` predicate returns correctly for linked/unlinked/off pairs (commit `53739fa`), and a zero-power-but-linked pair still shows the beam. What remains needs a connected client, which a headless ScenarioRunner run cannot observe.

- **v1.9.0 billing set: debt ceiling, receiver drain-cap lift, ModApi billing-ownership handshake.** Code landed (uncommitted at time of writing): public `ModApi` surface, standalone debt ceiling in the advertise prefix, always-on `PowerReceiver.GetUsedPower` drain-cap lift, unlink gate on the source-draw multiplier. Test plan detail lives in `.work/2026-07-02-power-rearchitecture/DESIGN.md` section 5.5 (ScenarioRunner scenario `ptp-standalone-suite`, to be written by the test task).
  - Mode: dedicated server, Luna save, sun-noon scenario, with the PowerGridPlus folder stashed out of `data/mods/` so Power Transmitter Plus runs standalone (restore afterwards).
  - (a) Steady links conserve: TX and RX `_powerProvided` return to ~0 each tick (InspectorPlus before/after pairs on `PowerTransmitter` / `PowerReceiver`, field `_powerProvided`).
  - (b) Deliveries above 5 kW bill the source: batteries pull 50 kW total across the 7 wireless links; each receiver's debt drains instead of growing without bound, and each source net is billed delivered x m (exercises the drain-cap lift with `Max Transfer Capacity` = 0).
  - (c) Insufficient source: starve one solar transformer (save-edit its Setting low); that TX's debt climbs to ceiling = (cap > 0 ? cap : 5000) x m x 4, the advertise pauses, exactly one `reached the safety ceiling` LogWarning fires per episode, debt stays bounded over a 120-tick sweep, and co-located rigid loads are not browned out persistently.
  - (d) OnOff cycle mid-flow: debt stays bounded across the off period and the lump bill on re-enable; link resumes cleanly.
  - (e) Handshake (needs PowerGridPlus active; belongs to the Stage 2b test): log shows `billing ownership claimed by 'net.powergridplus'` once, PTP debt patches dormant while claimed, `released by` line on release.

- **Multiplayer auto-aim cache stability and propagation, observed by a client.** Host (dedicated server or single-machine host) on the v1.7.3 build with the reset-postfix fix from commit `14946c5`; a real client on the same build connected; a TX-RX pair on auto-aim.
  - Mode: multiplayer, host + one real client.
  - (a) IC10 read of `MicrowaveAutoAimTarget` on the client side does not flicker to 0 while the host slews the dish. (Pre-fix bug: the client's `RotatableTargetHorizontalResetPatch` fired on every replicated servo-target write and wiped the client's cache.)
  - (b) When either peer writes `RotatableBehaviour.TargetHorizontal` manually (tablet, IC10 `s d0 Horizontal`, in-world R-key), both peers' IC10 reads of `MicrowaveAutoAimTarget` drop to 0. (Verifies `ClearCache`'s `AutoAimUpdateFlag` propagation lands on the client and triggers `ApplyDeltaUpdate(0)`.)

- **Multiplayer beam render observed by a client.** Host on v1.7.3, real client connected, a linked TX-RX pair.
  - Mode: multiplayer, host + one real client.
  - (a) Toggling either dish on or off makes the beam hide / re-show on the client within one frame. Both directions, both dishes.
  - (b) Commanding the TX to slew away makes the beam vanish on the client at roughly 7 degrees of off-axis movement (not at slew completion) and re-show symmetrically on slew-back. Validates the predicate's aim check runs against client-local `RayTransform.forward` and that the new `WirelessPower.Horizontal` / `Vertical` setter triggers fire on the client per the per-tick servo-target replication.
