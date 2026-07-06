# StationeersPlus Workshop Metrics

Adoption and engagement numbers for the published mods, snapshotted by hand over time. One row per check per mod. Never edit an existing row; append a new one so the trend stays visible in the table and in git history.

Steam does not expose a public subscriber-over-time graph and the shared Playwright profile is logged out (no author dashboard), so the only way to see whether a mod is still growing or has plateaued is to compare snapshots taken on different dates. That is what this file is for.

## Updating

Re-check each mod's Steam Workshop page through the Playwright browser (see "steamcommunity.com lookups must go through Playwright" in the repo `CLAUDE.md`), then append a row to that mod's table with today's date:

- **Subscribers / Visitors / Favorites**: the three rows in the stats box on the right of the page ("huidige abonnees" / "unieke bezoekers" / "huidige favorieten" if the page renders in Dutch).
- **Collections**: the "N collections" link next to the author byline ("N verzamelingen ... bekijken").
- **Comments**: the count on the "Comments" tab ("Opmerkingen N").
- **Rating**: the star line under the title. Stays "insufficient ratings" ("Onvoldoende beoordelingen") until enough up/down votes accumulate for a score.
- **Latest version**: the version at the top of the mod's in-game `<ChangeLog>` in `About.xml`, plus the Workshop "Updated" date ("Bijgewerkt op").

Workshop conversion (subscribers / visitors) is a useful derived figure for judging how many visitors actually keep the mod; compute it when reading rather than storing it. Roughly 20-40% is a healthy range for a useful mod.

When a mod from `Mods/` or `Plans/` goes live on the Workshop, add a new `##` section for it with its Workshop URL and publish date, and move it out of "Not yet published" below.

## Spray Paint Plus

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3702940349
- Published: 2026-04-09

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-05-12 | 360 | 972 | 40 | 26 | 15 | insufficient ratings | 1.7.1 (Workshop updated 2026-04-28) |
| 2026-05-21 | 383 | 1033 | 43 | 31 | 15 | insufficient ratings | 1.7.1 (Workshop updated 2026-04-28) |
| 2026-05-21 | 383 | 1033 | 43 | 31 | 15 | insufficient ratings | 1.8.0 (Workshop updated 2026-05-21) |
| 2026-05-21 | 385 | 1039 | 43 | 32 | 15 | insufficient ratings | 1.8.0 (Workshop updated 2026-05-21) |
| 2026-05-31 | 414 | 1102 | 44 | 37 | 15 | insufficient ratings | 1.8.0 (Workshop updated 2026-05-21) |
| 2026-06-01 | 421 | 1111 | 44 | 40 | 16 | insufficient ratings | 1.8.0 (Workshop updated 2026-05-21) |
| 2026-06-20 | 463 | 1210 | 45 | 47 | 16 | insufficient ratings | 1.9.0 (Workshop updated 2026-06-20) |
| 2026-06-20 | 466 | 1216 | 45 | 48 | 16 | insufficient ratings | 1.10.0 (Workshop updated 2026-06-20) |
| 2026-07-06 | 513 | 1327 | 46 | 60 | 16 | insufficient ratings | 1.10.0 (Workshop updated 2026-06-20) |

## Power Transmitter Plus

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512
- Published: 2026-04-16

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-05-12 | 161 | 653 | 27 | 18 | 4 | insufficient ratings | 1.7.2 (Workshop updated 2026-05-06) |
| 2026-05-21 | 187 | 710 | 27 | 20 | 4 | insufficient ratings | 1.7.2 (Workshop updated 2026-05-06) |
| 2026-05-21 | 188 | 712 | 27 | 20 | 4 | insufficient ratings | 1.7.2 (Workshop updated 2026-05-06) |
| 2026-05-31 | 206 | 774 | 29 | 21 | 4 | insufficient ratings | 1.7.2 (Workshop updated 2026-05-06) |
| 2026-06-01 | 213 | 794 | 29 | 22 | 6 | insufficient ratings | 1.8.0 (Workshop updated 2026-06-01) |
| 2026-06-20 | 252 | 956 | 35 | 28 | 6 | insufficient ratings | 1.8.0 (Workshop updated 2026-06-01) |
| 2026-07-06 | 268 | 1050 | 36 | 40 | 10 | insufficient ratings | 1.8.0 (Workshop updated 2026-06-01) |

## Network Purist Plus

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3724874914
- Published: 2026-05-12

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-05-12 | 2 | 8 | 0 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-12) |
| 2026-05-21 | 20 | 223 | 0 | 0 | 2 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-12) |
| 2026-05-21 | 22 | 231 | 0 | 0 | 2 | insufficient ratings | 1.1.2 (Workshop updated 2026-05-21) |
| 2026-05-31 | 32 | 447 | 3 | 0 | 2 | insufficient ratings | 1.1.3 (Workshop updated 2026-05-31) |
| 2026-06-01 | 35 | 542 | 4 | 0 | 2 | insufficient ratings | 1.1.3 (Workshop updated 2026-05-31) |
| 2026-06-20 | 54 | 802 | 6 | 2 | 2 | insufficient ratings | 1.1.3 (Workshop updated 2026-05-31) |
| 2026-07-06 | 65 | 967 | 8 | 5 | 2 | insufficient ratings | 1.1.3 (Workshop updated 2026-05-31) |

## Inspector Plus

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3730349036
- Published: 2026-05-21

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-05-21 | 1 | 1 | 0 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-21) |
| 2026-05-31 | 6 | 71 | 0 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-21) |
| 2026-06-01 | 6 | 112 | 0 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-21) |
| 2026-06-20 | 9 | 204 | 2 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-21) |
| 2026-07-06 | 9 | 256 | 3 | 0 | 0 | insufficient ratings | 1.1.0 (Workshop updated 2026-05-21) |

## KeypadMod Fix

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3737027789
- Published: 2026-06-01

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-06-01 | 2 | 3 | 0 | 0 | 0 | insufficient ratings | 1.0.0 (Workshop updated 2026-06-01) |
| 2026-06-20 | 103 | 382 | 5 | 13 | 0 | insufficient ratings | 1.0.0 (Workshop updated 2026-06-01) |
| 2026-07-06 | 163 | 470 | 7 | 31 | 0 | insufficient ratings | 1.0.0 (Workshop updated 2026-06-01) |

## Marky's Suit Drink System Fix

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3759105192
- Published: 2026-07-06

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-07-06 | 1 | 10 | 0 | 0 | 0 | insufficient ratings | 1.0.1 (Workshop updated 2026-07-06) |

## Force Field Door Mod Fix

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3759105090
- Published: 2026-07-06

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-07-06 | 0 | 9 | 0 | 0 | 0 | insufficient ratings | 1.0.1 (Workshop updated 2026-07-06) |

## Not yet published

These live under `Mods/` or `Plans/` but are not on the Steam Workshop yet. Promote each to its own section above when it goes live.

- Equipment Plus
