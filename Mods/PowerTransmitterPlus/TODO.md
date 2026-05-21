# PowerTransmitterPlus TODO

## Auto-aim write does not land on a dedicated server (unverified; may be an EquipmentPlus bug)

Setting `MicrowaveAutoAimTarget` from the in-game configuration tablet on a dedicated server is a no-op: the value reads back 0 and the dish does not slew. Reproduced 2026-05-18 against game 0.2.6228.27061 with the user as a remote client to a local dedicated server.

What is known:
- A diagnostic build logged every `WirelessPower.SetLogicValue` call. The tablet writes produced ZERO prefix fires, while the post-load auto-aim re-solve (which calls `HandleWrite` directly) logged normally. So the tablet write never reached `WirelessPower.SetLogicValue` at all -- the write is lost upstream of this mod's patch.
- The committed reset-postfix fix (gate the `RotatableBehaviour.TargetHorizontal/Vertical` reset postfixes on `NetworkManager.IsServer`, and have `ClearCache` raise `AutoAimUpdateFlag`, commit 14946c5) addresses a real client-side cache-clear bug, but is UNVERIFIED against the user-visible symptom because the write never arrives.

It could very well be a bug in EquipmentPlus rather than PowerTransmitterPlus. The tablet the user configured with is likely EquipmentPlus's AdvancedTablet, not the stock hand tablet. If EquipmentPlus's tablet write path does not route a logic-value edit through the vanilla `SetLogicValueMessage` -> `Thing.Find<ILogicable>(id).SetLogicValue(...)` flow (or mangles the value, e.g. a `float`-typed field quantizing a large target ReferenceId), the write would never reach any `SetLogicValue` patch, in which case the fault is in EquipmentPlus and PowerTransmitterPlus is a red herring. Confirm which tablet/cartridge the user used and trace its commit handler before assuming the bug is here.

Diagnostic next steps (from `Research/Protocols/LogicValueWriteMessages.md`):
- Prefix `SetLogicValueMessage.Process` on the dedicated server and log every inbound write. If the tablet edit produces no message, the tablet (EquipmentPlus AdvancedTablet?) is not sending `SetLogicValueMessage` -- trace its commit path. If it does send one but `SetLogicValue` still does not fire, the dispatch/patch attach is the problem.
- Check whether the relevant tablet UI control is `float`-typed (lossy for a 64-bit ReferenceId) vs `double`-typed.

Verify with an IC10 write (`s d0 MicrowaveAutoAimTarget <refId>`) as a control: that path calls `Device.SetLogicValue` server-side and should fire the patch, isolating whether the problem is the tablet write path or the auto-aim logic itself.
