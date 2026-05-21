# Changelog

Full version history for Inspector Plus. The newest entry also appears in `About.xml` `<ChangeLog>` and as the latest note on the Steam Workshop Change Notes tab.

## v1.1.0: Headless server snapshot toggle
- New optional Force Unpause Without Client setting (Server - Headless, off by default). On a headless dedicated server it runs the simulation with no client connected so request-file snapshots can be captured by automated tooling; no effect on a client or single-player.

## v1.0.0: First public release
- On-demand JSON snapshots of live scene objects: drop a request file in BepInEx/inspector/requests/ and read the result from BepInEx/inspector/snapshots/.
- F8 dumps every MonoBehaviour in the current scene in one pass.
- Captures private and public fields, computed properties, and Unity Transform positions, walking nested objects to a configurable depth with cycle detection.
- Hard caps on snapshot size with a visible truncation marker so a dump never runs away.

## v0.2.0: Generic walker fixes for any Unity game
- Inline-expand plain serializable classes instead of stringifying them as (TypeName); shared with the UnityEngine.Object path so the same filter, private-include, and skip rules apply.
- Null-guard FindObjectsOfType and fall back to a reflection scan over live MonoBehaviours and static members so non-UnityEngine.Object types are still reachable.
- Cycle detection via reference-equality set; emit (cycle) when a reference recurs on the same chain.
- Hard caps on total serialized bytes, top-level objects, and nested expansions; emit a visible _truncated marker when a cap is hit.
- Wrap name, gameObject, activeInHierarchy, transform.position, and other Unity reads in try-catch so destroyed Unity wrappers never throw out of the walker.
- Top-level try-catch surfaces unexpected walker errors as an error key in the snapshot JSON.
- New maxMonoBehaviours request field (default 10000) lets callers raise or lower the top-level cap.

## v0.1.0: Initial release
- On-demand snapshots via request JSON dropped in BepInEx/inspector/requests/.
- F8 full-scene MonoBehaviour dump.
- Field, property, and Unity position capture with configurable recursion depth and cycle detection.
