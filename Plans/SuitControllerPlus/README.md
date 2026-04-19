# SuitControllerPlus

A complete IC10 hardsuit/harmsuit controller for Stationeers. Reads external atmosphere composition via the External Suit Reader mod for proper safety decisions, manages life support intelligently to save power and consumables, and respects manual override.

## Requirements

- **External Suit Reader** mod must be installed and active. Workshop ID 3071985478. Without it, `RatioOxygenOutput`, `RatioPollutantOutput`, `RatioVolatilesOutput` return invalid data and the safety logic falls apart.
- A hardsuit or harmsuit with a Programmable Chip in the IC slot.

## Installation

1. Subscribe to External Suit Reader on the Workshop.
2. Copy the contents of `SuitControllerPlus.ic10` into a Programmable Chip using the IC Editor.
3. Insert the chip into the IC slot of your suit.
4. (Optional, for storm warnings) Set up the companion script (see below).

## Files

| File | Purpose |
|---|---|
| `SuitControllerPlus.ic10` | Main suit script. 128 lines, fits exactly in one IC chip. |
| `SuitControllerPlus_WeatherStation.ic10` | Companion script for an IC housing at base. Sends storm countdown to the suit. |
| `Researched Scripts/` | 41 reference scripts from the Workshop, used as research input when designing this controller. |

## Quick Start

Load `SuitControllerPlus.ic10` into your suit's IC chip. The script starts running immediately and will:

- Auto-close the helmet when external conditions are unsafe.
- Auto-open the helmet when external conditions return to safe.
- Manage filtration, air release, and AC intelligently while the helmet is closed.
- Sound periodic alerts for low battery, low O2, full waste, hot waste, low filter, and incoming storm.
- Stop interfering if you lock the helmet or manually open/close it (see "Manual override" below).

## Feature Summary

### Helmet automation
- Auto-close on unsafe external pressure (default 20 to 200 kPa).
- Auto-close on unsafe external temperature (default 0C to 50C).
- Auto-close on toxic external atmosphere (pollutants + volatiles partial pressure > 0.5 kPa).
- Auto-close on low external O2 partial pressure (< 16 kPa).
- Auto-open when all four conditions return to safe.

### Life support (helmet closed)
- **Smart AC**: only runs when internal temp is below 0C or above 40C.
- **Smart air release**: only runs when internal pressure is below 30 or above 120 kPa.
- **Smart filtration**: only runs when internal O2 ratio drops below 0.45 or CO2 ratio exceeds 0.05. Saves filter consumable.
- **Auto-flush adulterants**: helmet Flush is enabled whenever internal volatiles, pollutants, or nitrous oxide are detected.
- **Waste fire emergency shutdown**: if waste tank temp exceeds 2500 K, filtration and air release are forced off to avoid feeding the fire.

### Life support (helmet open)
- AC, filtration, and air release all disabled automatically.

### Periodic alerts (every 60 ticks, single sound per cycle, highest priority wins)
- Low battery (charge < 20%).
- Low O2 tank pressure (< 1500 kPa).
- High waste tank pressure (> 3500 kPa).
- High waste tank temperature (> 2000 K).
- Low filter quantity (< 3 uses remaining).
- Storm incoming (within 5 minutes, requires companion script).

### Emergency
- **Unconscious detection**: when `EntityState > 0`, all life support turns on, alarm sounds, script loops until conscious.
- **Helmet not equipped**: suit systems are disabled and stay disabled until a helmet is fitted.

### Suit type
- **Auto-detects** hardsuit vs harmsuit for the filter slot (slot 4 vs slot 5) by checking the IC slot Class.
- Battery slot is hardcoded to slot 2 (hardsuit). Harmsuit users should change `2` to `3` on line 93.

## Manual Override System

The script never writes to `Helmet Lock`. Manual control is fully respected through three mechanisms.

**Lock the helmet to disable everything.** When `Helmet Lock` is 1, the script jumps straight back to the start of the loop without touching any suit system. AC, filtration, air release, and helmet state stay exactly where you left them. Unlock to resume automation.

**Open or close the helmet manually to pause automation.** When the helmet is unlocked and the script is active, manually changing the helmet state pauses the helmet automation. Filtration, AC, and air release continue to follow the actual helmet state, but the script will not reopen or reclose the helmet against your wishes.

**Resume on condition change.** A pause clears as soon as any of the four monitored safety conditions flips state (pressure ok/not ok, temp ok/not ok, O2 ok/not ok, toxins ok/not ok). The script then immediately applies the correct helmet state for the new conditions.

This gives you a clean workflow for entering an unsafe room: open the helmet manually, then lock it. The script will not fight you between opening and locking.

## Companion Weather Station Script

For storm warnings, set up a base IC housing with two devices:

- `d0`: Weather Station
- `d1`: Logic Transmitter, linked to the player's hardsuit with a screwdriver

Load `SuitControllerPlus_WeatherStation.ic10` into the housing. It writes the storm countdown (in seconds) to the suit's `Setting` register when a storm is incoming and within 5 minutes. The suit script reads this and triggers sound 18 (StormIncoming) on the periodic alert cycle.

The companion script is optional. Without it, the storm alert simply never fires. Everything else still works.

## Configuration

### Configurable defines (top of script, lines 3 to 8)

| Define | Default | Meaning |
|---|---:|---|
| `MINP` | 20 | External pressure minimum (kPa) |
| `MAXP` | 200 | External pressure maximum (kPa) |
| `MINT` | 273 | External temperature minimum (K, 0C) |
| `MAXT` | 323 | External temperature maximum (K, 50C) |
| `MINO2` | 16 | External O2 partial pressure minimum (kPa) |
| `MAXTOX` | 0.5 | External pollutant + volatiles partial pressure max (kPa) |

### Inline thresholds (edit the literal in the line shown)

| Value | Line | Meaning |
|---:|---:|---|
| 0.45 | 72 | Internal O2 ratio below which filtration activates |
| 0.05 | 74 | Internal CO2 ratio above which filtration activates |
| 273 / 313 | 61, 62 | Internal temp range for AC (K) |
| 30 / 120 | 66, 67 | Internal pressure range for air release (kPa) |
| 2500 | 59 | Waste tank temp at which filtration and air release are forced off (K) |
| 0.20 | 95 | Battery charge ratio for low battery alert |
| 1500 | 98 | O2 tank pressure for low O2 alert (kPa) |
| 3500 | 101 | Waste tank pressure for high waste alert (kPa) |
| 2000 | 104 | Waste tank temp for high temp alert (K) |
| 3 | 107 | Filter quantity for low filter alert |
| 300 | 111 | Storm warning window (seconds, 5 minutes) |
| 60 | 92 | Ticks between alert cycles |
| 2 | 116 | Sleep seconds while sound plays |

## Sound Alert Reference

| Sound ID | Trigger |
|---:|---|
| 6 | Unconscious |
| 18 | Storm incoming (within 5 minutes) |
| 20 | Filter low |
| 23 | Battery low |
| 39 | Waste tank pressure high |
| 40 | O2 tank pressure low |
| 41 | Waste tank temperature high |

When multiple alerts trigger in the same cycle, the one checked last wins. Priority order in the script: battery, O2, waste pressure, waste temp, filter, storm. Storm has the highest effective priority.

## Suit Slot Reference

| Slot | Hardsuit | Harmsuit |
|---:|---|---|
| 0 | O2 canister | O2 canister |
| 1 | Waste canister | Waste canister |
| 2 | Battery | (varies) |
| 3 | IC chip (Class 26) | Battery |
| 4 | Filter | (varies) |
| 5 | (varies) | Filter |

The script auto-detects hardsuit vs harmsuit via the IC slot Class and adjusts the filter slot accordingly. The battery slot is hardcoded to 2; change line 93 to `3` if running on a harmsuit.

## Why This Design

Most existing Workshop suit scripts cannot read external gas composition. Vanilla IC10 only exposes external pressure and temperature on the suit, so they decide safety by pressure alone. The External Suit Reader mod exposes external O2 and toxin ratios, which lets this script make safety decisions based on what is actually in the air, not just whether there is air at all. That distinction matters on Venus, in argon-filled rooms, and when atmospheres slowly poison without changing pressure.

The script uses every line of the 128-line IC10 budget. There is no room for new features without removing existing ones.

## Reporting Issues

Please file bug reports and feature requests on the GitHub issues page rather than as comments on the script post. Workshop comment notifications are unreliable and reports left there often go unseen.

## License

Apache License 2.0. See `LICENSE` and `NOTICE` in the parent directory.
