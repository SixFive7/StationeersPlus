# Inspector Plus TODO

This file tracks open issues only. Entries are plain bullets, not `- [ ]` checkboxes; when an item is done, remove it rather than ticking it off. Completed work lives in git history.

Implemented changes still awaiting an in-game or dedicated-server test do not belong here; record those in `PLAYTEST.md` (same folder).

No open issues.
- Asset-typed requests (TMP_FontAsset and other ScriptableObject assets) return zero objects: ResolveInstances trusts an EMPTY FindObjectsOfType result for UnityEngine.Object-derived types and only falls back to the reflection scan on null, but FindObjectsOfType never sees assets. Route empty results for non-Component Unity types through FallbackLookup too (observed 2026-07-14 on both the dedicated server and a graphical client).
