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

## Power Transmitter Plus

- Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512
- Published: 2026-04-16

| Date | Subscribers | Visitors | Favorites | Collections | Comments | Rating | Latest version |
|---|---|---|---|---|---|---|---|
| 2026-05-12 | 161 | 653 | 27 | 18 | 4 | insufficient ratings | 1.7.2 (Workshop updated 2026-05-06) |

## Not yet published

These live under `Mods/` or `Plans/` but are not on the Steam Workshop yet. Promote each to its own section above when it goes live.

- Equipment Plus
- Network Purist Plus
- Inspector Plus (local-only developer tool; may never be published)
