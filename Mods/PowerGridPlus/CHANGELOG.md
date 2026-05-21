# Changelog

Full version history for Power Grid Plus. The newest entry also appears in `About.xml` `<ChangeLog>`.

## v0.1.0: First build (work in progress)
- Reworked cable-network power tick from Re-Volt: proportional load sharing, gradual probabilistic cable burnout on a rolling throughput average, NaN-power guard.
- Stationary battery charge/discharge-rate limits, optional charge efficiency, and rate-limit logic values.
- Transformer free-power exploit fix, quiescent-draw restore, and Power Actual logic value.
- Area Power Controller power-leak / idle-drain fix.
- Recursive and looped networks allowed by default.
- Super-heavy cable never burns; the three cable tiers are separate transmission voltages (mixing them burns the lower-tier boundary cable and splits the network); devices belong on their tier (generators and batteries on heavy, high-draw machines on heavy or normal, super-heavy is cables-and-transformers only, everything else on normal) and a device on the wrong tier gets no power; super-heavy cable coil costs 2x to craft (configurable via the multiplier).
- Reactive tier enforcement: a wrong-tier cable burns on the next power tick if the network has actual power flow (an idle or off network destroys nothing). Wrong-tier cable placement is blocked at the cursor with a "Wrong voltage" tooltip; device placement is never blocked. Burned cables explain why they burned in their hover tooltip.
- Logic passthrough: a writable LogicPassthroughMode logic value (set via IC10 or a logic writer) makes power-network bridging devices logic-transparent, so a logic reader sees devices on the far side, transitively across a whole chain of bridged devices (cycle-safe, so looped networks are fine). Covers transformers, stationary batteries, linked power transmitter and receiver dishes, and Area Power Controllers. The per-device mode is server-authoritative, persists across save and load, and each device family has its own master server toggle.
- Deterministic network merge: when cable or structure networks merge, the survivor is chosen by lowest reference id so the host and all clients agree on the same network id (a multiplayer correctness fix).
