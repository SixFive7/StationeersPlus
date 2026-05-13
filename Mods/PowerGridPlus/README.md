# Power Grid Plus

Reworks the Stationeers power simulation and turns the three cable tiers into three separate transmission voltages.

Work in progress. Not on the Workshop yet. A pure-patch mod: no new craftable devices, no asset bundle. The simulation half is derived from Sukasa's [Re-Volt](https://github.com/sukasa/revolt) (MIT); the three-tier voltage backbone is new. Design notes and the decision log live in `RESEARCH.md`; pending work lives in `TODO.md`.

## What it does

Inherited from Re-Volt, trimmed to the simulation half:

- Replaces the cable-network power tick. Electrical load is shared proportionally across every power source on a network, and split proportionally across every load.
- Cables burn out gradually and probabilistically when overloaded, based on a short rolling average of throughput, instead of instantly. Fuses still blow instantly and always blow before a cable does. The weakest fuse or weakest cable fails first.
- Recursive and looped power networks are allowed. The vanilla check that force-burns cables in such layouts is off by default; re-enable it in settings.
- Stationary batteries are charge- and discharge-rate limited, with an optional charge-efficiency setting. Batteries expose their max charge rate (Import Quantity) and max discharge rate (Export Quantity) as logic values.
- Transformers no longer leak free power and charge their own quiescent draw to the upstream network; they expose current throughput as the Power Actual logic value.
- Area Power Controllers no longer leak a small amount of power and no longer slowly drain their battery when nothing is connected downstream.

Re-Volt's circuit breakers, smart breakers, heavy breakers and the Load Center are not included; this is the pure-simulation subset, no new prefabs.

Added by this mod (the three-tier voltage backbone):

- **Super-heavy cable never burns out.** It is the long-haul backbone. Normal and heavy cable keep their ratings.
- **Super-heavy cable costs twice as much to craft** (configurable via the multiplier; 1.0 = vanilla cost).
- **The three cable tiers are three separate transmission voltages.** A cable network must be all one tier. Joining two tiers burns the lower-tier cable at the junction, which splits the network back apart. The only legal bridge between tiers is a transformer (or an Area Power Controller) -- its input and output sit on separate networks, so it's free to step between voltages.
- **Devices belong on the right tier.** Generators and stationary batteries go on heavy cable. The high-draw machines (Carbon Sequester, the furnaces, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, Deep Miner -- and any extra ones you list in the config) may use heavy or normal. Super-heavy is the long-haul backbone: only cables and transformers. Everything else runs on normal cable. A device on the wrong tier receives no power. (Transformers, Area Power Controllers and wireless power devices are exempt -- they work on whatever they're wired to.)

This is an early build, not yet play-tested. The cable-tier rule is fully enforced (the boundary cable burns reactively, and a best-effort build-time check rejects mixing them at the cursor). The device-tier rule is enforced at the simulation level (no power on the wrong tier); the build-time *cursor* rejection for misplaced devices isn't wired up yet (see `TODO.md`).

## Settings

All settings are server-authoritative: in multiplayer the host's values apply for everyone.

| Section | Setting | Default | Effect |
|---|---|---|---|
| Cable Simulation | Cable Burn Factor | 1.0 | Scales the per-tick cable burn chance. 0.0 disables gradual burnout. |
| Cable Simulation | Enable Unlimited Super-Heavy Cables | true | Super-heavy cable never burns. |
| Cable Simulation | Enable Recursive Network Limits | false | Restores the vanilla force-burn of looped grids. |
| Cable Costs | Super-Heavy Cable Cost Multiplier | 2.0 | Multiplies the super-heavy cable coil recipe cost. 1.0 = vanilla. |
| Voltage Tiers | Enable Voltage Tiers | true | The three cable tiers are separate voltages; mixing them burns the lower-tier cable; devices belong on their tier (no power otherwise). |
| Voltage Tiers | Extra Heavy-Cable Devices | (empty) | Comma-separated prefab names of extra devices allowed on heavy cable (for modded high-draw machines). |
| Batteries | Enable Battery Limits | true | Charge/discharge-rate limit stationary batteries. |
| Batteries | Max Battery Charge Rate | 0.002 | Max charge per tick, as a fraction of capacity. |
| Batteries | Max Battery Discharge Rate | 0.007 | Max discharge per tick, as a fraction of capacity. |
| Batteries | Battery Charge Efficiency | 1.0 | Fraction of incoming power stored. |
| Batteries | Enable Battery Logic Additions | true | Expose battery rate limits as logic values. |
| Transformers | Enable Transformer Exploit Mitigation | true | Close the transformer free-power exploit. |
| Transformers | Enable Transformer Logic Additions | true | Expose transformer throughput as Power Actual. |
| Area Power Control | Enable APC Power Fix | true | Stop the APC power leak and idle battery drain. |

## Compatibility

**Requires:** [BepInEx](https://docs.bepinex.dev/) and [StationeersLaunchPad](https://github.com/StationeersLaunchPad/StationeersLaunchPad).

All players on a server must have Power Grid Plus installed; the version is checked during the connection handshake. Dedicated servers need the same BepInEx + StationeersLaunchPad + Power Grid Plus setup.

Known interaction: [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) itself swaps the power tick per network the same way this mod does, so running both is not supported. See `TODO.md` for the cross-mod compatibility checklist.

## Reporting Issues

If you run into a bug or something behaves unexpectedly, please open an issue on [GitHub](https://github.com/SixFive7/StationeersPlus/issues). Please include the mod name in the title so reports can be triaged. Steam comment notifications don't always come through, so GitHub is the reliable way to make sure a report is seen.

## Credits

Built on the power-simulation work in [Re-Volt](https://github.com/sukasa/revolt) by Sukasa, used under the MIT License. The Re-Volt-derived portions retain their MIT notice; see the repository `NOTICE`.

## License

Licensed under the [Apache License 2.0](https://github.com/SixFive7/StationeersPlus/blob/master/LICENSE). Attribution required; see the LICENSE and NOTICE files at the repository root. The portions derived from Re-Volt remain under their original MIT License (Copyright (c) 2025 Sukasa).
