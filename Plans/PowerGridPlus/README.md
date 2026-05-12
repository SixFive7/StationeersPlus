# Power Grid Plus

Work in progress. Not built, not on the Workshop yet. Design brief: see `PLAN.md` in this folder.

Reworks the Stationeers power simulation and turns the three cable tiers into three separate transmission voltages. A pure-patch mod: no new craftable devices, no asset bundle.

## What it does

Inherited from Sukasa's Re-Volt (MIT licensed), trimmed to the simulation half:

- Replaces the cable-network power tick. Electrical load is shared proportionally across every power source on a network, and split proportionally across every load.
- Cables burn out gradually and probabilistically when overloaded, based on a short rolling average of throughput, instead of instantly. Fuses still blow instantly and always blow before a cable does. Weakest fuse / weakest cable fails first.
- Recursive and looped power networks are allowed (the vanilla check that force-burns cables in such layouts is off by default, re-enable via settings).
- Stationary batteries are charge- and discharge-rate limited, with an optional charge-efficiency setting. Batteries expose their max charge/discharge rates as logic values.
- Transformers no longer leak free power and charge their own quiescent draw upstream; they expose current throughput as a logic value.
- APCs no longer leak a small amount of power and no longer slowly drain their battery when nothing is connected downstream.

Added by this mod:

- Super-heavy cable never burns out. It is the long-haul backbone. (Normal and heavy cable keep their ratings.)
- Super-heavy cable costs twice as much to build (configurable; set the multiplier to 1.0 for vanilla cost).
- The three cable tiers (normal, heavy, super-heavy) are three separate voltages and cannot be wired together directly. The only legal bridge between tiers is a transformer; wire two tiers together and a cable burns. (Design in progress, including how high-draw devices and the small/medium/large transformer tiers map onto this; see `PLAN.md`.)

## Requires

StationeersLaunchPad (with BepInEx). Server-authoritative: in multiplayer the host and every client need it.

## Credits

Built on the power-simulation work in [Re-Volt](https://github.com/sukasa/revolt) by Sukasa, used under the MIT License. The Re-Volt-derived portions retain their MIT notice; see `PLAN.md` section 8.

## License

This mod is Apache 2.0; see the repository `LICENSE` and `NOTICE`. The portions derived from Re-Volt remain under their original MIT License (Copyright (c) 2025 Sukasa).
