# PowerTransmitterPlus TODO

## Multiplayer tests pending

These code paths are decompile-verified but cannot be exercised in single-player. Run when a second machine or remote tester is available. All assume both peers are on a matching mod version with `Enable Auto-Aim = true` (unless stated otherwise).

- [ ] **v1.6.1 `IJoinSuffixSerializer` join-time sync.** Fresh client joining a host with at least one auto-aimed dish should read the correct `MicrowaveAutoAimTarget` ReferenceId via IC10 immediately on connect. Steps: host has dish auto-aimed at receiver R; client joins; client IC10 reads `MicrowaveAutoAimTarget`; value should equal R's ReferenceId. Look for `[Info   :PowerTransmitterPlus] Restored auto-aim cache from host join: N applied, 0 dishes not found locally` in the client's `BepInEx/LogOutput.log`. Code: `Plugin.cs:SerializeJoinSuffix/DeserializeJoinSuffix`. Background: `Research/Protocols/PlayerConnectedThingFindTiming.md`.

- [ ] **v1.6.2 per-tick delta-state sync (mid-session target change).** While a client is connected, host writes `s d0 MicrowaveAutoAimTarget X` from IC10. Within one game tick (~500 ms), client's IC10 read should return X. Repeat with a second id Y to confirm subsequent updates flow. Code: `WirelessPowerBuildUpdateAutoAimPatch` / `WirelessPowerProcessUpdateAutoAimPatch` postfixes in `AutoAimPatches.cs`, and the `AutoAimUpdateFlag = 0x2000` reservation on `NetworkUpdateFlags`. Background: `Research/Protocols/PlayerConnectedThingFindTiming.md` "Cache survival post-join" section, `Research/Protocols/EquipmentPlusNetworking.md` for the canonical pattern.

- [ ] **v1.6.2 explicit-clear delta sync.** While a client is connected, host writes `s d0 MicrowaveAutoAimTarget 0`. Client IC10 read should return 0 within one tick. This case is distinct from the geometry-driven clear path because `HandleWrite(dish, 0L)` does not fire any `RotatableBehaviour.Target*` setter (no servo movement on zero target), so without the explicit `NetworkUpdateFlags |= AutoAimUpdateFlag` inside `SetCache` the clear would not propagate to clients. Code: `AutoAimState.SetCache` in `AutoAimPatches.cs`.

- [ ] **MP host save/load with connected client.** Host with at least one connected client saves the world while a dish is auto-aimed, host quits, host reloads the same save and is rejoined by the same client. Client IC10 should still read the correct cached target after the rejoin. Exercises the side-car path (`AutoAimSideCar.cs`, `AutoAimSaveLoadPatches.cs`) under MP host conditions where `SaveHelper.Save` runs server-only per the `!NetworkManager.IsClient` guard in vanilla `AutoSaveNow` (`Research/GameSystems/SaveZipExtension.md`).

- [ ] **`Enable Auto-Aim` handshake rejection.** Two peers with mismatched `Enable Auto-Aim` boot-time toggles; the client should be rejected at join with the message defined in `Plugin.cs:ProcessJoinValidate`. Test once for "host on, client off" and once for "host off, client on" to cover both error-message branches. Verifies the `IJoinValidator` plumbing added in v1.4.0.

- [ ] **`Allow Non-Floor Placement` handshake rejection.** Same shape as above for the `NonFloorPlacementPatched` branch of the join validator. Test both directions of the mismatch to cover both error-message branches.
