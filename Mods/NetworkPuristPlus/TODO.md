# Network Purist Plus TODO

No open items. Shipped at v1.1.2. The deliberate non-goals below are kept so they are not re-litigated.

## Won't do (decided against)

- **Carry chute items in transit over to the rebuilt single-tile chutes.** An item physically inside a long chute segment at the moment it is rebuilt is deleted, by design. The README states this.
- **Preserve a rebuilt pipe run's gas.** The gas inside a long pipe run is deleted when the run is rebuilt from single tiles, by design (the game's atmospherics-event system loses a pipe network's gas when its pipes are replaced this way; capture-then-`Atmosphere.Add` was tried and did not stick). The README states this. Re-pressurise rebuilt pipe runs.
- **Full corner-seam fix for cables.** The v1.1 per-axis canonical roll mates with the plurality of corners (~36% on both legs) and makes every straight-to-straight seam flush; a straight-to-corner seam can still show a band jump. Fully fixing it means re-rolling each corner-adjacent straight to its specific corner (and a straight between two disagreeing corners can match one end only), and probably normalising the corners themselves. Re-rolling a corner is connectivity-relevant: it changes `ConnectedCables` iteration order, the exact server-vs-client divergence vanilla `CableNetwork.Merge` desyncs on (see commit 14946c5 and `Research/Patterns/MultiplayerStateMutation.md`). Accepted as-is; the cosmetic gain is not worth the complexity or the multiplayer risk. Full analysis in `Research/GameSystems/PlacementOrientation.md` ("Why straight-cable roll normalisation does not fix the band-seam at a corner cable").
