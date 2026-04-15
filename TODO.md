# PowerTransmitterPlus TODO

## Verify MicrowaveEfficiency in-game

`MicrowaveEfficiency` (LogicType 6574) was added after the last in-game test pass. The other three readouts (6571-6573) are confirmed working end-to-end (tablet dropdown + IC10 `lbn`), and efficiency uses the exact same injection plumbing, so this is expected to just work, but it hasn't been eyeballed yet.

Steps (quick, 2 min):

1. Restart Stationeers.
2. Point a configuration tablet at a linked transmitter or receiver. Confirm `MicrowaveEfficiency` appears in the dropdown between `MicrowaveTransmissionLoss` and the next alphabetic neighbour.
3. At ~119 m with `k = 5` and any load > 0: efficiency should read ~0.627 (formula: `1 / (1 + 5 * 0.119)`).
4. Turn the transmitter off or unlink: should drop to 0.
5. IC10 smoke test: add `lbn r3 trans name MicrowaveEfficiency 0` to the test chip, confirm it compiles and the register shows the expected value.

## Multiplayer sync test (deferred)

Only relevant if playing multiplayer. Verifies the server-authoritative `k` value reaches clients live.

Steps:

1. Host a multiplayer session.
2. Connect a second machine as client.
3. Build + link a Microwave Transmitter/Receiver pair on the client's side, put a load on it.
4. On the client: point a configuration tablet at either dish, note `MicrowaveSourceDraw` value (and the `MicrowaveTransmissionLoss`).
5. On the host: edit `E:\Steam\steamapps\common\Stationeers\BepInEx\config\net.powertransmitterplus.cfg` and change `Cost Factor (k) = 5` to a very different value (e.g. `20`). Save.
6. Confirm the client's tablet readouts shift within a second or two without requiring a rejoin.
7. Also verify new clients joining mid-session immediately receive the host's `k` value (this is handled by the `NetworkManager.PlayerConnected` Harmony postfix → `BroadcastIfHost`).

Reference: plan.md §10 for the sync protocol, §15 step 2.
