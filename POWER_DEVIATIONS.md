# Power Grid Plus -- Implementation Deviations and Decisions (pass 2, 2026-06-10)

> Post-review update 2026-06-13 / 2026-06-14: during the deviation walkthrough the developer reworked five
> items. **P1** (wrong-tier burns) became the Option B + Option C design; **P2** (the §5.7 generator-overflow
> burn) became a deterministic 20-tick running average with `CableBurnFactor` removed (all randomness
> gone from the mod); **P3** (the NaN guard) gained per-device source clamping plus an in-game
> broken-device alert that names the culprit; **P4** (the OVERLOAD hover wording) became per-device-type,
> naming each device's actual constraint instead of a transformer-only line; **P5** (battery / elastic
> OVERLOAD) was kept but its re-arm was network-synced so jointly-sufficient storage banks always recover
> together. P1-P3 are implemented, built, and dedi-verified; P4 and P5 are implemented and built (in-game
> checks queued). See the rewritten **P1** through **P5** entries below for the full designs, the choices
> made, and the verification results. A sixth item surfaced from the P5 review: the adversarial
> PowerTransmitterPlus interaction audit found PowerGridPlus under-counted a microwave link's source-network
> draw (PowerTransmitterPlus bills the input `delivered * m` for distance, not a delivery derate); fixed and
> recorded as **P6.1**. The rest of this file is the original pass-2 record.

The full implementation pass executed every phase of POWER.md / POWERTODO.md under the locked
2026-06-10 decisions (D1=b single architecture, D2=b non-mutating cable caps, D3 = 5000/100000/0,
D4 distinct colours, D5 directed-SCC, D6 fault+zero with cable-burn fallback, D7 visuals everywhere,
D8 implement everything). This file records where the implementation deviated from the written spec,
plus the judgment calls made where the spec under-specified, so each one can be reviewed and, if
needed, revisited. Pass-1 deviations that the locked decisions resolved are gone; see git history
for the old file.

Status summary: every POWERTODO phase is implemented. Build is green at v0.2.0. Dedicated-server
verified on the Luna save (full baseline: no exceptions over 108+ ticks, zero false deprioritization / overload /
cycle faults across 168 segmenters, the expected vvf=108 producer-isolation set) plus a synthetic
forced-overload scenario (transformer Setting throttled to 100 W via save edit -> per-device hit-max
OVERLOAD fired, the subnet went dark cleanly with no partial power). Client-only visuals and
multiplayer sync are implemented but headless-untestable; they are queued in
`Mods/PowerGridPlus/PLAYTEST.md`.

## Deviations from the spec text

### P1. Wrong-tier burns: same-tick split via main-thread burning (RESOLVED 2026-06-13, B+C)

Original spec (POWER.md §4.3 steps 3/5): a full network re-enumeration between the wrong-tier burns
(1.5a) and cycle detection (1.5b), so every later phase sees the post-burn topology within the same
tick. Pass-2 could not do this: `Cable.Break()` self-marshals to the main thread while the tick runs
on the UniTask worker, the split lands AFTER the tick, and a fixed 4-tick per-network cooldown bridged
the marshal latency so the same junction was not burned twice. The consequence was one tick (0.5 s) of
cross-tier conduction and a magic-number cooldown.

Reviewed with the developer and reworked into **Option B + Option C** (the developer disliked the magic
number and wanted same-tick-correct power distribution plus a per-tick re-check for robustness). The
research that drove the design is curated in `Research/GameClasses/Cable.md` ("Network split on
destruction") and `Research/GameClasses/CableNetwork.md` (the `ConnectedCables` worker-thread note).

What changed:

- **B -- detection on the worker, burn on the main thread.** Tier detection (`VoltageTierEnforcer.DetectViolation`)
  reads only cached state (`Connection.GetCable` uses the cached `LocalGrid`, not `Transform.position`),
  so it is safe in Phase 1.5a on the worker. The burn itself (and the mixed-tier victim walk, which uses
  `ConnectedCables` -> `Transform.position`, main-thread only) is marshalled to the main thread via
  `UnityMainThreadDispatcher`. There `Cable.Break()` runs synchronously and the split lands at end of
  frame, before the next tick.
- **B -- `CableNetwork.OnNetworkChanged` is the immediate trigger.** This main-thread-only event fires on
  every membership mutation (placement, merge, split, load, device add), so a freshly created junction is
  detected and burned the instant it appears, before any tick conducts across it (zero-tick reaction for
  the common case). It is robust to future / modded build paths: anything that joins a network through the
  standard mutators trips it. The per-tick worker sweep (`VoltageTierEnforcer.Run`) is retained as the
  every-tick backstop for anything `OnNetworkChanged` does not cover (e.g. an in-place tier mutation with
  no membership change). Both funnel through the same detect/burn pair and respect the pending gate, so
  they never double-burn.
- **No magic number -- `SplitPendingRegistry` replaces the 4-tick cooldown.** A network with a burn in
  flight is gated until its split lands, detected by cable-count change (`Cable.OnDestroy` -> `Remove`
  drops the burned cable, so `CableList.Count` differs from the count captured at burn time). This is
  1 tick on a healthy server and self-extends only while the server is genuinely frame-starved; no fixed
  timer.
- **C -- the allocator defers durable decisions on burn-pending networks.** `PowerAllocator` skips
  committing new deprioritization / overload lockouts for any contributor whose input or output network is pending, so
  a 60 s lockout is never committed against a merged topology that is about to split. The §5.7
  generator-overflow burn marks its network pending too (and skips if a burn is already in flight), so the
  same deferral covers it from the tick after it fires.

Verified on the dedicated server (Luna, save-edited mixed-tier junction: one heavy straight cable on
network 429366 flipped to normal): the burn fired exactly once on the main thread, correctly picked the
normal (lower-tier) cable, the split landed and resolved the mismatch (no re-burn, no cascade), the fault
registry stayed `deprioritized=0 overload=0 cycle=0` on the affected network through the burn (Option C), and no
exceptions fired. Baseline (clean Luna) was an exact match to the pass-2 baseline (segmenters=168,
deprioritized=0 overload=0 cycle=0 currentMismatch=108), confirming no regression from the per-tick `OnNetworkChanged` /
worker-sweep / allocator-gate machinery.

Choices made, for review:

- **`OnNetworkChanged` as the immediate trigger** rather than hooking each `Cable.OnRegistered` /
  device-registration / `OnFinishedLoad` site. Chosen because it is the single main-thread chokepoint that
  every membership change passes through, including build paths a future game or mod might add. Cost: it is
  parameterless, so the handler coalesces and re-scans all networks (cheap: cached-tier reads over ~200
  networks). If you would rather hook specific sites, that is a smaller blast radius but loses the
  modded-path robustness you asked for.
- **§5.7 generator-overflow as a sole trigger is only covered transitively.** A generator overflowing a
  cable is almost always also a tier violation (generators are heavy-only; a generator overflowing a
  normal cable is a misplaced-device burn that fires first in Phase 1.5a and preempts §5.7). The §5.7
  pending-mark and `IsPending` guard were exercised by the tier test (the flipped network carried
  heavy-backbone load over a 5000 W cap, and §5.7 correctly skipped it because the tier burn had marked it
  pending). A §5.7 burn firing as the *only* trigger (a generator bank exceeding the 100 kW heavy cap with
  no tier mismatch) was not isolated headlessly; it shares all the same machinery and is left as a
  playtest item in `Mods/PowerGridPlus/PLAYTEST.md`.

### P2. Probabilistic burn replaced by a deterministic 20-tick running average (RESOLVED 2026-06-13)

Original pass-2 deviation: the deleted PowerGridTick burned cables on a 10-20 s rolling throughput
average; the rewrite put a per-tick probability ramp on `PowerTick.GetBreakableCables`
(burn chance = (generator_supply / cap - 1) x CableBurnFactor), which kept "0 disables" but lost the
smoothing memory and used an RNG.

Reviewed with the developer, who wanted ALL randomness removed (for reproducibility, and as a hedge
against any future client-side burn evaluation) with the smoothing restored as a deterministic running
average. Reworked into:

- **Deterministic 20-tick running average** (`CableBurnWindow`; no RNG, no settings). Each tick the
  prefix observes the generator power flowing on the network -- min(generator supply, real throughput),
  so an idle network or transformer-fed overflow does not count -- into a per-network 20-slot sliding
  ring. A cable burns when the 20-tick average exceeds the weakest cable's cap. 20 ticks = 10 s at
  2 Hz: both a grace floor (a fresh or just-burned network must fill a full window first) and the
  averaging window (a spike must be countered by an equivalent dip within 10 s or the average crosses
  the cap).
- **Burn victim = the top-producing generator's output cable.** The window also tracks each generator's
  20-tick production; the burn site is the cable at the output of the generator that produced the most
  over the window (ties by lowest ReferenceId), isolating the largest energy source. Falls back to the
  weakest cable if that producer's cable cannot be resolved.
- **Reset on burn.** The network's window is cleared on a burn so one sustained overload cannot burn a
  second cable before the split lands; the split-off network re-accumulates a fresh window. Composes
  with the P1 B+C pending gate (the §5.7 check skips an in-flight network).
- **`CableBurnFactor` removed.** No setting governs the burn; it is hardcoded. The per-tier cable caps
  (Normal / Heavy / Super-Heavy Max Watts) remain configurable -- those define the ratings, not the
  burn behaviour.

This was the only RNG in the mod. After removal the mod's decisions are fully deterministic; the only
remaining non-determinism is the client-side fault-countdown wall clock (`MonotonicClock`), which drives
a visual countdown only and is never a synced decision (see P15).

Verified on the dedicated server: a ScenarioRunner probe (`pgp-cable-burn-window-probe`) drove
`CableBurnWindow` directly (the under-cap Luna grid never triggers a real §5.7 burn) -- 5/5 PASS: a
partial window does not arm (grace floor), a full window of sustained overload arms with the average
over cap and ranks the correct top producer, a single 100 kW spike followed by idle ticks averages to
exactly the cap and does NOT arm, and reset clears the window. The clean-Luna baseline showed zero
spurious burns and zero exceptions from the per-tick observation across the ~200-network grid.

Behaviour note for review: the effective grace varies with overload magnitude (a natural consequence of
averaging). A network already near cap that spikes crosses the average sooner; a gross overload from
idle takes close to the full 20 ticks. The 10 s is the window length and the grace floor, not a fixed
"exactly 10 s then burn." A §5.7 burn firing as a sole trigger on a live grid (a >100 kW generator bank
on pure-heavy cable, with no tier mismatch to preempt it) is left as a playtest item -- the decision
logic is verified by the probe, the integration by the baseline.

### P3. Per-device NaN sanitization restored, with an in-game broken-device alert (RESOLVED 2026-06-13)

Original pass-2 deviation: PowerGridTick sanitized every device's reported watts; the D1=b rework
replaced that with a single per-network postfix on `PowerTick.CalculateState` that zeroed a non-finite
`Required` / `Potential` (one dark tick, per-device granularity lost, once-per-session warning).

Reviewed with the developer, who chose option 1 (per-device sanitization) and asked for a player-visible
in-game alert so a broken modded device is impossible to miss. Implemented:

- **Source clamp (`DeviceOutputSanitizer`).** The `GetGeneratedPower` / `GetUsedPower` postfixes on the
  device classes PowerGridPlus already patches (the 8 producers, Battery charge + discharge,
  AreaPowerControl, the rocket umbilicals) clamp a non-finite return to 0 at the source. The value is then
  clean for every reader (vanilla CalculateState, the §5.7 average, the allocator) and the network never
  darkens for those devices. (NaN <= 0 is false, so the clamp is applied before each postfix's own
  early-outs, which a NaN would otherwise slip past.)
- **Backstop + culprit naming (the enhanced CalculateState guard).** The per-network guard remains, but
  now when it detects a poisoned sum -- which means an UNKNOWN / modded class we do not patch produced the
  NaN, since a patched class would have been clamped at its source -- it zeroes the sum (one dark tick)
  AND scans the network to NAME the offending device. This is exactly the case a player needs to hear
  about.
- **Player-visible alert.** Every sanitization is logged to the BepInEx file log (developer detail). The
  in-game console (`ConsoleWindow.PrintError`, marshalled to the main thread because the tick runs on the
  worker) names each broken device ONCE PER WORLD SESSION. Decision for review: the developer asked for
  "every time"; `GetGeneratedPower` / `GetUsedPower` run 2-4x per tick, so literal every-time in-game
  would flood the console unusably, and a time-based throttle would be the kind of magic number removed
  elsewhere in this pass. So the file log is every time; the in-game console is once-per-device (loud,
  named, not spam). Trivially switchable to literal every-tick if preferred.

Host-only (the tick runs on the simulating peer; a dedicated server logs to its server console). Verified:
built green; clean-Luna baseline unchanged (segmenters=168, deprioritized=0 overload=0 cycle=0 currentMismatch=108) with zero
spurious sanitizations and zero exceptions -- the finite-checks are transparent for normal values. The
actual firing (a broken device gets clamped + named in-game) needs a deliberately-broken device and is
queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P4. Overload hover wording: per-device-type (RESOLVED 2026-06-14)

Original spec (§11.1): one OVERLOAD template, `(Overloaded: Downstream demand exceeds transformer limit!
{n}s)`, written when only transformers could overload. P5 extended OVERLOAD to elastic suppliers (batteries,
APCs, rocket umbilicals) and P6 to PT/PR pairs, so a transformer-specific line is wrong on those devices.

Reviewed with the developer, who chose per-device-type wording over a single generic line. The hover now
names the constraint the hovered device actually hit, switched on the C# type of the instance the player is
pointing at (`FaultHover.OverloadClause`):

- `Transformer`: `Downstream demand exceeds this transformer's limit!`
- `Battery` (and subclasses): `Downstream demand exceeds this battery's discharge rate!`
- `AreaPowerControl`: `Downstream demand exceeds this APC's output!`
- `RocketPowerUmbilicalMale` / `RocketPowerUmbilicalFemale`: `Downstream demand exceeds this umbilical's discharge rate!`
- `PowerTransmitter` / `PowerReceiver`: `This power link cannot carry the downstream demand!`
- any other overload-capable device: `Downstream demand exceeds this device's limit!` (generic fallback)

The hovered `Thing` is threaded into the hover renderer from both hover patches (body tooltip
`FaultHoverPatches`, button title box `FaultButtonTooltipPatches`); since the 2026-07-14 naming lock the
per-device titles (`Battery overloaded fault` etc.) carry the per-device wording and
`FaultHover.TryGetMergedBlock` is the single renderer (the clause set above is the historical wording). No new data crosses the
wire: the clause is chosen locally from the instance the client is hovering, while the fault kind and
countdown still come from the synced fault registries. §11.1 was updated to match, and the stale
transformer-only quote in the `Enable Transformer Overload Protection` setting description (`Settings.cs`)
was corrected to the new transformer clause. Build green at v0.2.0. Per-device in-game wording confirmation
is queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P5. Battery / elastic OVERLOAD on undersupplied networks (RESOLVED 2026-06-14, kept + network-synced re-arm)

§8.4's hit-max OVERLOAD trigger was specced for Setting-bearing devices only. The implementation extended it
to elastic suppliers (Battery, APC, RocketPowerUmbilical*) so storage-fed networks honour the no-partial-power
invariant: when a network's COMBINED elastic discharge still leaves rigid demand unmet, every elastic supplier
on it trips OVERLOAD and the subnet goes dark cleanly instead of partial-powering. Reviewed with the developer,
who kept this behaviour (the alternative, vanilla partial power, was explicitly rejected by the spec's central
claim) and confirmed the combined-supply math: two batteries that individually fall short but together cover
the load do NOT trip, because the allocator sums their effective discharge (`AvailableElastic`) before deciding.

One gap surfaced in review and was fixed. The per-device 60 s lockout could drift out of phase across the
elastic suppliers on one network (one toggled OFF then ON via the §10.3 reset, or one locked first by an
unrelated fault), after which they re-armed one at a time, each re-overloading alone because its siblings were
still locked, leaving a network they could JOINTLY power dark forever. Fixed by a network-level commit with
retry-before-reset: when a net's elastic cohort would overload, the allocator first RETRIES the whole
on-cohort (every elastic overloaded this tick or already overload-locked; all ON, since OFF-as-reset clears
off devices each tick). If their combined discharge would now cover the net's residual demand (the load
dropped, a device was toggled back on, or supply was added), it CLEARS the cohort's overload locks and
recovers the same tick, no timer reset. Only if the retry still falls short does it (re)stamp ONE shared
fresh expiry across the cohort, arming the 60 s lockout and keeping them phase-synced so an
individually-too-weak-but-jointly-sufficient bank always re-arms together instead of taking turns failing.
`OverloadRegistry` stays per-device (flash / hover / snapshot unchanged); the commit is self-resolving (after
it the net is all-recovered or all-locked). Spec'd in POWER.md §8.4.1. Build green at v0.2.0; a two-battery jointly-sufficient recovery test (including
the toggle-one-battery desync path) is queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P6. PT/PR pair OVERLOAD vs DEPRIORITIZED: now routed by the binding constraint (RESOLVED 2026-06-15, reworked)

The pair's Setting-equivalent is the live `PT.GetGeneratedPower(wireless)` read (§6.3), which is already
input-supply-limited. The deliverable cap therefore collapsed two different bottlenecks, the link's own
rated transfer cap and the input network's `PotentialLoad`, into one number, so a pair held down by a
starving input could trip OVERLOAD ("this link cannot carry the demand") where the root cause was upstream
and DEPRIORITIZED ("insufficient upstream supply") was the more useful diagnosis. A re-analysis against the current
code (adversarial agent + verification) confirmed the corner case was still live and that §8.4.2 (P6.1) did
NOT close it: P6.1's `delivered * m` modelling and `inCableCap / m` bound act only on the input-draw and
input-cable paths (they push long PowerTransmitterPlus links toward DEPRIORITIZED), but they never touch the overload
trigger's use of `liveCap`, and are inert in exactly the regime the corner case describes (input affordable
for `delivered * m`).

Reviewed and REWORKED (developer chose to distinguish the two bottlenecks). The allocator now carries a
`Seg.InputLimited` flag for a PT/PR pair, true when `liveCap >= InputNetwork.PotentialLoad - Eps` (the
deliverable equals the input potential, so the SOURCE binds, not the link's rating). In the §8.4 hit-max
pass, an input-limited pair routes its shortfall to DEPRIORITIZED (`seg.Deprioritized`, `DeprioritizedRegistry`) instead of
OVERLOAD; a pair bound by its own rated cap still OVERLOADS. Both take the pair offline (no-partial-power);
only the diagnosis / registry differ. No cross-mod rated-cap read was needed: PowerTransmitterPlus applies
no output-side loss, so `liveCap == PotentialLoad` exactly when input-limited (under an unlimited cap every
shortfall is a DEPRIORITIZED, the link is never the rating bottleneck), and a long vanilla link whose distance loss
pulls `liveCap` below `PotentialLoad` reads as link-limited (OVERLOAD), which is truthful since the loss is
the link's own. Spec'd in POWER.md §8.4 / §8.4.2. Build green at v0.2.0; queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P6.1 PowerTransmitterPlus distance draw: allocator now models the source-side overhead (RESOLVED 2026-06-14)

Found by the adversarial PowerTransmitterPlus x PowerGridPlus compatibility audit during the P5 review.
PowerTransmitterPlus does not derate a microwave link's delivered power for distance; it inflates the
TRANSMITTER's input-side draw to `delivered * m`, where `m = 1 + k * distance_km` (k default 5). The
allocator modelled the PT/PR pair's input-network pull as `throughput + ~20 W` quiescent, blind to `m`
(it read the quiescent `pt.UsedPower` field, not the inflated `pt.GetUsedPower(InputNetwork)` method). So a
long link feeding a constrained source network was under-counted by ~m: the allocator deprioritized nothing, vanilla
Phase 3 then computed `_powerRatio < 1` on the input network and brown-outs every rigid device on it, the
no-partial-power invariant defeated on the transmitter's input side. The input cable could also burn
unprotected (the cable check used `throughput`, not `throughput * m`).

Fix (POWER.md §6.3 / §8.4.2): the allocator reads `m` from PowerTransmitterPlus via reflection
(`PowerTransmitterPlusInterop.SourceDrawMultiplier` -> the new public `DistanceCostShared.SourceDrawMultiplier`,
a soft dependency returning 1 when PowerTransmitterPlus is absent), models the pair's input demand as
`throughput * m + quiescent`, and bounds the input cable at `input_cable_cap / m`. With vanilla (or no
PowerTransmitterPlus) `m = 1` and behaviour is unchanged. Also closed a related hardening gap (the PT/PR pair
is the one supplier class not run through DeviceOutputSanitizer): a non-finite `liveCap` read is now clamped
to 0 before it can poison the allocator sums. PowerTransmitterPlus gained one public accessor (no behaviour
change); both mods build green at v0.2.0. The cross-mod compatibility research page and PLAYTEST.md were
updated; durable `PowerReceiver` internals were curated to `Research/GameClasses/PowerReceiver.md`.

### P7. Dead-input deferral kept + no-upstream-supply cue (RESOLVED 2026-06-14)

A contributor whose input network has no supply at all (no generators, no storage, no inflow) idles instead
of cycling 60-second deprioritization lockouts forever. Reviewed and KEPT: it avoids a permanent orange strobe on unpowered
branches and gives instant recovery when the input is powered (no lockout to wait out), and DEPRIORITIZED semantically
means "lost a priority contest for scarce supply," which a zero-supply input is not.

Added per the review: a steady, neutral-grey `No upstream supply` info block (over `The input network carries no power`) on dead-input contributors that
are trying to pass power downstream, so the player gets a signal without the strobe or the recovery delay. It
is an INFO cue, not a fault: lowest hover precedence (a real fault on the same device wins), no flash, no 60 s
timer. Recomputed every tick from the converged allocator state (`DeadInputRegistry`), and mirrored to clients
via the per-tick fault snapshot (`KindDeadInput`) so it shows on every peer; cleared on world load. Applies to
transformers, APCs, and PT pairs (the pull-through contributors); batteries and umbilicals are pure suppliers
and do not receive it. Spec'd in POWER.md §8.3.1. Build green at v0.2.0; queued in `Mods/PowerGridPlus/PLAYTEST.md`.

2026-07-17, decision 33: the dead-input cue keeps its per-device semantics; the related NETWORK-level
DEAD_UNMET state gained its own face (Undersupplied, `UndersuppliedRegistry`, snapshot kind 6, same
keep-alive TTL model). The two cues are complementary: dead input means "my feed carries nothing",
Undersupplied means "my whole net cannot fund its demand"; both are info states, never lockouts.

### P8. Parallel-supplier demand split: priority-tiered, proportional within a tier (RESOLVED 2026-06-14)

§8 specified the deprioritization victim order and the §8.4 trigger but not how a met demand divides across parallel
suppliers (transformers / APCs / PT pairs) feeding one output network. Pass 1 implemented a flat greedy fill
by (priority DESC, RefId ASC), each supplier taking min(EffCap, remaining), so equal-priority suppliers did
NOT share (the lowest-RefId one carried the load to its cap first).

Reviewed and reworked to a HYBRID per the developer's choice: still greedy across priority TIERS (higher-
priority banks fill first, primary/backup), but PROPORTIONAL by EffCap WITHIN a tier, so equal-priority
suppliers share the load in proportion to capacity (a 50 kW and a 5 kW transformer at equal priority split
~10:1). Self-bounding (each share <= its EffCap), conserves the tier total, and the §8.4 overload behaviour
is unchanged (a tier overloads all-or-nothing; higher tiers trip before lower ones engage). Ordering stays
integer-keyed (MP-deterministic, §8.0.1); the within-tier division is float but bit-identical across peers on
the shared runtime. Spec'd in POWER.md §8.3.2. Build green at v0.2.0; queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P9. APC DischargeSpeed: now cell-only, consistent with battery / umbilical (RESOLVED 2026-06-14)

Vanilla `AreaPowerControl.GetGeneratedPower` returns passthrough + cell in one bundled number, so the supply
share PowerGridPlus writes for an APC (into `SoftSupplyShareCache`, which feeds that `GetGeneratedPower`) is
the bundled passthrough + soft-grant-through + cell. Pass 1 had the APC's `DischargeSpeed` read that bundled
value, so it reported total APC output rather than the cell-only rate, inconsistent with battery / umbilical
`DischargeSpeed` (cell-only, since those devices are pure elastic suppliers).

Reviewed and reworked to cell-only per the developer's choice: the allocator already computes the APC cell's
elastic share separately, so it now stamps that cell-only figure into a new `ApcCellDischargeCache` before the
`SoftSupplyShareCache` entry is overwritten with the bundled total, and the APC's `DischargeSpeed` read pulls
from `ApcCellDischargeCache`. `SoftSupplyShareCache` stays bundled (the `GetGeneratedPower` clamp needs it).
`DischargeSpeed` now means the cell's discharge rate on every storage device. The Stationpedia footer already
described cell behaviour, so it needed no change. Spec'd in POWER.md §7.3 / the LogicType table. Build green
at v0.2.0; queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P10. CurrentMismatchFault LogicType exposure: widened to all producers (except PowerConnector) + inheritance hardened (RESOLVED 2026-06-14)

Pass 1 exposed `CurrentMismatchFault` only on the flash-capable producers (PowerGeneratorPipe / GasFuelGenerator,
SolidFuelGenerator, StirlingEngine), per §8.7's narrow rationale, narrower than §19's table (which listed the
wider Solar / Wind / RTG / Coal / Gas / Stirling set) and narrower than the current-mismatch DETECTION set. It also had a
silent-break risk: the patch sat on PowerGeneratorPipe and covered GasFuelGenerator only by inheritance.

Reviewed and reworked (options 2 + 3 per the developer). Exposure WIDENED to every `ProducerClassifier.IsProducer`
device that has a DEVICE-SPECIFIC logic surface: solar, both wind turbines, the small turbine, and gas /
solid-fuel / stirling. Excluded are the two producers that declare no logic surface and so cannot be logic-read:
`PowerConnector` (a dynamic-generator dock) and `RadioscopicThermalGenerator` (RTG, a rocket-internal component
with no normal data port). A decompile pass during review confirmed which producers override `CanLogicRead`
(curated to `Research/GameSystems/PowerProducerLogicReadability.md`): wind and the small turbine expose
`PowerGeneration`, solar its tracking logic; RTG and PowerConnector declare none. (An earlier draft of this
rework listed RTG as exposed; corrected here, RTG can ENTER CURRENT_MISMATCH_FAULT but is hover-only since it cannot be logic-read.)
And
HARDENED: each producer's actual runtime `CanLogicRead` / `GetLogicValue` is resolved via `AccessTools`, so a
future game version that gives e.g. GasFuelGenerator its own override is followed instead of silently dropped;
a per-instance filter (`ProducerClassifier.IsProducer` minus PowerConnector) keeps the read off non-producers
when the resolved target is a shared base method. §8.7 and the §19 table were updated. Build green at v0.2.0;
queued in `Mods/PowerGridPlus/PLAYTEST.md`.

### P11. TurbineGenerator and PowerConnector added to the producer list (RESOLVED 2026-06-14, kept)

The decompile shows both override `GetGeneratedPower` (they put power on the network) yet neither was in §8.5's
producer list. Both are classified as known producers (hover-only): leaving them unclassified would be worse
under D6, the unknown-producer fallback would BURN a cable next to them, whereas a known producer gets the clean
fault+zero.

Reviewed and KEPT: producer-isolation applies uniformly, so the classic early-game "portable generator on a
power connector wired straight to machines" setup trips CURRENT_MISMATCH_FAULT until a transformer is added,
consistent with the mod's central mechanic (the alternative, exempting PowerConnector, would punch a hole in
the voltage-tier model by letting an unregulated producer reach consumers directly). The fault only fires when
something is actually docked and generating; an empty connector does nothing. Documented per the review: §8.5's
producer list reconciled to include both (it still correctly excludes the DIFFERENT vestigial `PowerConnection`
class), the README current-mismatch note now names the portable generator and small turbine, and a new producer-isolation
Stationpedia footer (auto-attached to every producer prefab discovered via `ProducerClassifier.IsProducer`)
explains the rule and the portable-generator-needs-a-transformer point. Build green at v0.2.0; queued in
`Mods/PowerGridPlus/PLAYTEST.md`.

### P12. Producer-isolation OFF-as-reset reworked: active-source gate + network-level retry (RESOLVED 2026-06-15)

Pass 1's `OffAsResetSweep` cleared every lockout on any locked device reporting `OnOff == false`, and
the original note claimed buttonless producers "report OnOff true in practice." A decompile pass plus
adversarial agents (two opposing advocates + an independent judge) overturned that premise: `Thing.OnOff`
is false whenever the prefab carries no `InteractableType.OnOff` interactable (`HasOnOffState`, set from
the `Interactables` list in `Thing.CacheStates`), so every buttonless producer reports `OnOff == false`
permanently and was swept every tick, freezing its current-mismatch countdown at ~60 s.

A first rework gated the sweep on `HasOnOffState && !OnOff`. A re-review then found a deeper hole: the
producer-isolation detector decided "is this a producer" by CLASS (`ProducerClassifier.IsProducer`), not
by whether anything is actually generating. Because the detector runs the same tick AFTER the sweep and
re-notes by class, OFF-as-reset was silently broken for EVERY buttoned producer (gas / solid / stirling),
not just the connector, and an empty or switched-off Power Connector faulted spuriously (contradicting the
P11 "empty connector must not fault" expectation). The connector is also a transparent proxy: it forwards a
docked `DynamicGenerator`'s power (the generator, a `DraggableThing`, is never on the cable network), so the
real source is the generator.

Reworked (developer chose the strict overload-pattern). Two pieces:

1. **Active-source gate.** The detector faults only ACTIVE producers (`IsActiveProducer`): a `PowerConnector`
counts while its docked generator is delivering (read the generator's raw `PowerGenerated`, NOT the
connector's enforcement-zeroed `GetGeneratedPower`, which would oscillate); a buttoned producer counts while
`OnOff`; a buttonless producer always (it cannot be switched off). An inactive producer is transparent to the
rule, like a `Transformer`. This fixes the empty/off-connector false fault and makes per-device OFF-as-reset
work for the buttoned producers.

2. **Network-level commit (mirrors the elastic-overload retry, §8.4.1).** CURRENT_MISMATCH_FAULT stays per-producer in the
registry but is committed per network with RETRY before RESET and a shared synced expiry. A net is a candidate
when an active producer NEWLY violates this tick, or a toggle requested a retry (`OffAsResetSweep` flags the net
via `CurrentMismatchFaultDetector.RequestRetry` when it clears a producer's lock). Recover (no longer violating)
clears the whole producer cohort; reset (still violating) stamps every active producer to one shared fresh
expiry. A stable all-locked net is not a candidate, so its synced timer counts down (the frozen-countdown fix).
This gives the buttonless producers a manual retry they otherwise lack: toggling any buttoned producer on the
net clears the buttonless ones too, which then resolve (if the wiring is now fixed) or re-fault on a fresh
synced timer. Like overload there is no free auto-recovery: a fix with no interaction clears at the 60 s expiry.

Files: `ProducerClassifier` (`IsActiveProducer` / `ConnectorIsDelivering`), `CurrentMismatchFaultDetector` (the
commit + `RequestRetry`), `OffAsResetSweep` (connector delivery-gate + flag-the-net on clear). Spec'd in POWER.md
§8.5 + §10.3. Game internals curated to `Research/GameSystems/PowerProducerOnOffState.md` (per-producer OnOff,
the PowerConnector pass-through) and `Research/GameClasses/Device.md` (`Has*State` from the Interactables list,
not the animator). Build green at v0.2.0; queued in `Mods/PowerGridPlus/PLAYTEST.md`.

Epistemic caveat: `Interactables` is serialized prefab data the decompile cannot read, so it can prove an OnOff
interactable PRESENT (a deref that would crash if absent) but never ABSENT. Only the gas generator is decompile-
proven; the other two fuel-generator "has OnOff" calls and the six "buttonless" calls are strong inferences. The
connector active-gate sidesteps this entirely by reading `PowerGenerated` (a buttonless generator could never
report > 0 anyway). The queued playtest includes an InspectorPlus probe of `HasOnOffState` on each producer and a
docked `DynamicGenerator` to confirm the table in one load.

### P13. Non-default transformer Setting is honoured + surfaced with a hover warning (RESOLVED 2026-06-15)

With formulas on `Setting` and the knob writing Priority, a transformer saved with `Setting != OutputMaximum`
(the `Setting = 0` case is the extreme: a dark subnet) has no in-world lever to raise it, the dial writes
Priority, so only IC10 or a rebuild changes Setting. The Luna save has two Setting=0 transformers (343183,
343182, logged at load; their subnets dark). Per §5.3 a saved Setting is deliberate state.

Reviewed and reworked (developer chose keep-and-surface, not migrate). Three pieces:

1. `OutputMaximum` stays the descriptive IC10-readable rating (kept, the developer liked the non-overloaded name).
2. Every newly built transformer defaults `Setting = OutputMaximum` (already implemented, `TransformerSettingInitPatch`
on `Thing.OnRegistered`, before `DeserializeSave`, so loaded transformers keep their saved value).
3. Any transformer with `Setting != OutputMaximum` (generalised from the original `Setting = 0` case) shows a
neutral info block on BOTH the case and the on/off button title box: an amber `Throttled` title over
`Limited to {set} of {max} by the IC10 Setting value` and `The dial sets priority instead of power`
(`TransformerThrottleHover` feeding the merged block, no flash / no countdown; since the 2026-07-14
naming lock the info block is suppressed while any fault is active). The saved
Setting is still honoured (NOT migrated, so a deliberate `Setting = 0` disable persists); the warning just makes
the throttle discoverable and the IC10/rebuild fix obvious. Spec'd in POWER.md §5.3. Build green at v0.2.0;
queued in `Mods/PowerGridPlus/PLAYTEST.md`.

Migration (`Setting 0 -> OutputMaximum` on load) was considered and rejected: it would quietly undo a deliberate
`Setting = 0` disable (a §5.3-endorsed mechanism) and make it session-only across save / load.

### P14. ApcRateApplier / CableMaxApplier deleted rather than kept (RESOLVED 2026-06-15, kept)

Pass-1 rewrote `AreaPowerControl.BatteryChargeRate` per instance (and cable caps via `CableMaxApplier`);
both mutations are gone under locked decision D2 (per-instance vanilla serialized fields stay vanilla in
the save; PowerGridPlus reads its config setting at the point of use instead). The PowerGridPlus prefixes
read the settings directly, and with `Enable APC Power Fix` off, true vanilla (including its 1000 W field
default) is what runs. Saves touched by pass-1 builds keep whatever values pass-1 baked in until a vanilla
save cycle (dedicated-server test saves only; no released build ever mutated).

Reviewed and KEPT (the deletion is the correct D2-consistent design). The code is already non-mutating:
`AreaPowerControlPatches.ComputeChargeCap` reads `Settings.ApcBatteryChargeRate` each tick, the discharge
ceiling reads `Settings.ApcBatteryDischargeRate` via `ApcDischargeRateRegistry` (PGP-owned, vanilla has no
discharge-rate field), and `CableMax.For` reads the cable-cap setting, none touch the serialized fields.
The only action this review needed was reconciling three stale POWER.md lines that still described the
deleted pass-1 field-patch behaviour: the cable-cap paragraph (§4.x, claimed PGP "rewrites each Cable
prefab's MaxVoltage at mod load", now the non-mutating `CableMax` read), the `ApcBatteryChargeRate`
settings-table row (claimed it was "patched into the vanilla field at mod load") and the §7.3 discharge
note (claimed the discharge ceiling "reuses the same vanilla `BatteryChargeRate` field ... no separate
setting yet"). All three fixed. No mod-code change; the test-save residue is harmless and self-clears on
a vanilla save cycle.

### P15. Client fault mirrors are wall-clock based, not until-tick (RESOLVED 2026-06-15, kept)

`ElectricityTickCounter` only advances on the simulating peer (host / dedicated server), so §13's
original until-tick client mirror could never expire correctly on a client (its tick counter is frozen).
Implemented and KEPT: the per-tick `FaultRegistrySnapshotMessage` carries REMAINING ticks per entry; the
client converts to an expiry against a monotonic wall clock (`MonotonicClock.NowMs + remaining * 500`,
500 ms = one 2 Hz tick) and decides "still faulted" by `ExpiryMs > NowMs`. The host re-sends a full
snapshot every tick, so client drift self-heals each tick; an empty snapshot is sent exactly once on a
registry's non-empty -> empty transition so OFF-as-reset (and the §8.5 network-retry recover) clears
client visuals immediately. The host stays purely tick-based (MP-deterministic); only the client mirror
is wall-clock, the same per-peer model §11.2 already uses for the cosmetic countdown.

Reviewed and kept: the until-tick model is unusable on a client, and syncing the tick counter in lockstep
would add traffic and a desync failure mode for no gain. Caveat (flagged in the registry comments): the
`remaining * 500 ms` conversion and `LockoutDurationTicks = 120` hard-code the 2 Hz electricity tick;
revisit if the game ever changes its tick rate. POWER.md §13 still describes the superseded diff-message +
client-dict model; it is reconciled together with P16 (the message-format deviation), since §13 conflates
both.

### P16. One snapshot message replaced the four legacy diff messages (RESOLVED 2026-06-15, kept)

The original §13 kept `ShedStateMessage` / `OverloadStateMessage` (and siblings) as transition signals
next to the new full snapshots. Implemented and KEPT: one `FaultRegistrySnapshotMessage` with a `Kind`
byte replaces all four per-transition diff messages outright; transitions are implicit in the snapshot
deltas. One sync model, one source of truth, no diff-vs-snapshot divergence risk.

Reviewed and kept; the only action was reconciling §13 (it still described the deleted diff path, the
`_clientShedding` dicts, "four registries", a stale registry name, and claimed the empty
packet was suppressed entirely and the legacy diff path remained). Rewrote §13, the §8 phase-output line,
and the two `LogicType.DeprioritizedFault` / `DeviceOverloadedFault` table rows (they said "replicated via ShedStateMessage /
OverloadStateMessage").

Folded-in check (per the developer's request, confirming the just-extended current-mismatch fault is synced): the snapshot
at that date carried FIVE kinds, deprioritized (0), device-overload (1), cycle (2), current-mismatch (3), dead-input (4);
the census is SEVEN since cable-overload (5, decision 29's split) and undersupplied (6, decision 33's face,
network-keyed with the dead-input-style keep-alive TTL) joined. The current-mismatch
network commit (§8.5) rides `KindCurrentMismatch` with violator names; because the commit stamps a cohort
to ONE shared expiry, every member carries the same remaining-tick count, so clients render the cohort's
countdowns ticking down together (no per-client desync) with no new sync mechanism. The dead-input cue
(P7) rides `KindDeadInput` as a 2-tick keep-alive TTL on the per-tick heartbeat; it is intentionally NOT
in the mid-cooldown join handshake (no countdown to resume; the first heartbeat refreshes it within a
tick). The join handshake covers the four countdown registries only. No mod-code change was needed, the
current-mismatch rework was already fully and correctly folded into the unified sync.

### P17. Producer isolation aligned to the strict-literal spec (RESOLVED 2026-07-12)

A silent code-spec divergence, found during the 2026-07-12 audit round and closed on the developer's
"go full strict" decision. POWER.md §8.5 has always been strict-literal (a producer's network may
contain ONLY producers and `Transformer` instances; everything else, batteries and the other segmenter
classes included, is a violator; a Transformer's presence exempts nothing). The implementation lagged:
`CurrentMismatchFaultDetector.Run` exempted the whole net whenever ANY `Transformer` was present
(`violating = hasActiveProducer && hasRigid && !hasTransformer`) and only counted DRAWING rigid
consumers (`ProducerClassifier.IsRigidConsumer`, gated on `GetUsedPower > 0`) as violators, so
solar + battery + transformer on one bus passed, and an idle consumer on a producer bus passed.

Aligned to spec: the classification loop now sets `hasForeignDevice` for every device that is neither
a producer (class-based, so inactive producers stay transparent per P12) nor a `Transformer` nor an
unknown-producer-like (which keeps the cable-burn fallback), presence-based rather than draw-based;
`violating = hasActiveProducer && hasForeignDevice`. `IsRigidConsumer` was deleted (no remaining
caller). The hover line dropped the now-misleading "without transformer" tail in favour of the fixed
advisory "A producer connects only to producers and transformers." (FaultHover, §11.1 updated), and
the §8.5 Recover text no longer claims adding a transformer recovers a net. Consequence for existing
worlds: buses that passed under the lag (generator + battery sharing a net with a transformer) fault
at load until rewired; the Medium transformer's {heavy, heavy} tier map keeps
generators -> Medium transformer -> battery bank legal. CHANGELOG + README carry the player-facing
warning; the fault-on-load sweep is queued in PLAYTEST.md. The 108-producer Luna current-mismatch baseline recorded
in this file's status summary predates this change and will read higher on the next run.

## Verification gaps (implemented, not yet observed)

### 2026-06-16 headless campaign update

A headless ScenarioRunner campaign (new `pgp-pt-*` scenarios against the clean Downloads `Luna.save`;
full log `.work/2026-06-16-pgp-playtest/results.md`) closed most of the data/logic gaps. NOW VERIFIED
headlessly: fault hover line CONTENT + exact hex colors + precedence (P4 per-device overload clauses,
P7 dead-input cue, P13 throttle, deprioritized/cycle/current-mismatch strings); P9 APC cell-only DischargeSpeed; P10 current-mismatch
logic exposure (solar + solid generator) and non-exposure on transformers/connector; P12 OnOff table +
active-source gate + OFF-as-reset (driven via `OffAsResetSweep.Run` after OnOff=false); P11 isolation
fires on 108 of 1024 solar with 916 regulated-and-not-faulted; P3 `DeviceOutputSanitizer` NaN/Inf clamp;
P6.1 `PowerTransmitterPlusInterop.SourceDrawMultiplier` cross-mod read (m up to 2.545); flash component
attach + color constants; the custom LogicType reads. P5/P6/P8 decision LOGIC is code-verified and the
OUTCOME is observable (P6 routes to Deprioritized-vs-Overload registry; P5 stamps a shared synced expiry; P8
`PowerActual` reflects per-transformer delivered watts), but the FIRING on built topology stays a client
check.

**Bug found + FIXED (2026-06-16):** the burn-reason "Burned:" hover never attached (`pgp-pt-burnreason`
reported `attachedCount=0`). Root cause was NOT timing/cell drift as first suspected: `BurnReasonPatches`
was missing its class-level `[HarmonyPatch]`, so `PatchAll` silently skipped all three of its patches
(the `CableRuptured.OnRegistered` consume, the `Structure.GetPassiveTooltip` "Burned:" line, the Tooltip
re-apply) -- the same trap that once hid `FaultButtonTooltipPatches`. Added the attribute; re-verified
on the load-time tier burn: the wreckage registers at the burned cell, `consumeHit=True`,
`attachedCount=1`, `GetAttached` returns "Wrong voltage...". This fixes both the live and load-time burn
paths. The on-screen wreckage hover + the BurnReasonSideCar round-trip remain client checks but are now
unblocked.

**Finding + FIXED (2026-06-16):** the flash component attached to Battery and APC but `DiscoverRenderers`
found 0 renderers, so the pulse was invisible on them. Extended the name-fallback to the APC `MasterLever`,
the nuclear battery `Indic0NoShadow`, and the stationary battery body (reached only when the on/off-button
path fails, so transformers are unaffected); `pgp-pt-flash-all` now reports renderers>0 on all three. The
eyes-on pulse confirm remains a client check. (Superseded 2026-07-18 for the APC only: the flash target
moved off the `MasterLever` onto the charge LED, resolved through the serialized
`_chargingLedMaterialChanger` renderer, with vanilla `ApcMaterialChanger.RefreshState` suppressed while
faulted and re-run on fault exit; `Lever` is gone from the name-fallback list. Battery and nuclear-battery
targeting are unchanged.)

### Irreducible client residue (in `Mods/PowerGridPlus/PLAYTEST.md`, grouped to minimize reloads)

- Visible flash pulse + countdown smoothness/locale (Update-driven; headless has no LateUpdate).
- The FIRING of P5 two-battery synced recovery, P6 PT deprioritized-vs-overload, P6.1 source-side no-dim, P8
  proportional split, and cycle-fault on built anti-parallel transformers.
- Client-side mirror, join handshake mid-fault, OFF-as-reset from a client, and all 2-peer sync
  (the in-process serializer/state-machine paths are verified; only the live transport is not).
- Live in-play wrong-tier burn + burn-reason hover (attach fixed + verified headlessly) + cursor rejects.
- BurnReasonSideCar round-trip (burn -> save -> reload -> hover intact; now unblocked) and mod-removal safety.
- Rocket umbilical rate caps + LogicTypes against a docked rocket.
- The unknown-producer cable-burn fallback (needs an unclassified modded producer).
- Emergency-light behavior (uninstall the third-party Battery Backup Light mod first) + prefab list.
- The §5.7 generator-overflow burn live sole-trigger (the 20-tick decision is verified 5/5; needs an
  overbuilt >100 kW pure-heavy generator bank for the live integration).
- P3 in-game console culprit naming (needs a deliberately-broken modded device).
- Stationpedia footer render; settings-panel toggles; passthrough dropdown refresh.

## Incidents during the pass (resolved)

- `AmbiguousMatchException` on the Device-base logic patches (slot-keyed overloads) took ALL
  PowerGridPlus patches down for two test runs; the failure mode is silent-vanilla plus a single
  `[Fatal]` BepInEx line. Fixed with explicit argument types; the log-audit grep pattern that missed
  it (`\[Fatal\]` instead of `\[Fatal`) is also why it went unnoticed for one cycle.
- The research catalogue had `Battery` in the wrong namespace (`Assets.Scripts.Objects.Pipes`
  instead of `Assets.Scripts.Objects.Electrical`); caught by the compiler, corrected in
  `Research/GameSystems/PowerSegmentingDevices.md`.
- `pgp-fault-state-probe`'s `cycleDetectNow` has always read -1 (HashSet is not a non-generic
  ICollection); the meaningful signal is the registry count. Probe quirk, not a mod bug.
- (2026-06-13, B+C testing) Deploying the freshly-built PowerGridPlus.dll with `-DeployMods` (which
  writes to `install/BepInEx/plugins/PowerGridPlus/`) while the dedi already had PowerGridPlus synced
  into `data/mods/Local_PowerGridPlus/` caused a duplicate load: both copies ran `Plugin.OnPrefabsLoaded`,
  `RegisterMessage<PassthroughModeMessage>` was called twice, and patch application aborted with
  `[Fatal :Power Grid Plus] ... An item with the same key has already been added.` -- so PGP ran pure
  vanilla. Fix for this dedi (PowerGridPlus is part of the synced mod set): deploy by overwriting
  `data/mods/Local_PowerGridPlus/PowerGridPlus.dll` directly, not via `-DeployMods`. This is the same
  duplicate-load class the DedicatedServer CLAUDE.md warns about for `-SyncMods` + `-DeployMods`.

## Spec bookkeeping not done on purpose

- POWERTODO Phase 9's full synthetic probe suite (dozens of pgp-* scenarios) is not built
  one-to-one; the Luna baseline + the save-edit overload scenario + the shortfall-net probe cover
  the core paths headlessly, and PLAYTEST.md covers the visual/client paths. Building the remaining
  synthetic scenario saves is mechanical follow-up work if wanted.
- POWERTODO 10.1's "consolidate POWER.md into RESEARCH.md and delete POWER.md" is deferred until
  after the developer's review of this pass (POWER.md is still the live review artifact).
- Phase 12.2 Steam Workshop publish is the developer's call (mod is at v0.2.0, unpublished).
