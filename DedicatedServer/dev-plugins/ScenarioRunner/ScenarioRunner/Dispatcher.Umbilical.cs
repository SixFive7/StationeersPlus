using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using UnityEngine;

namespace ScenarioRunner
{
    // Headless verification of the PowerGridPlus rocket-umbilical LOGIC PASSTHROUGH feature
    // ("two umbilical halves share one LogicPassthroughMode and bridge each other's networks").
    //
    // Scenario ids:
    //   pgp-umbilical-passthrough-probe  -- tests 1-6 (default mode, real SetLogicValue mirror,
    //                                        merged DataDeviceList, undock breaks bridge, master
    //                                        toggle gate, MP wire-format round-trip).
    //   pgp-umbilical-saveload-set       -- test 7 phase 1: set a non-default mode (0) on a docked
    //                                        umbilical, log it, so a save captures it.
    //   pgp-umbilical-saveload-verify    -- test 7 phase 2: after reload, confirm the umbilical mode
    //                                        restored to 0, then set it back to the default (1).
    //
    // Every check emits "[ScenarioRunner] <TAG> P<n> PASS|FAIL ..." plus a final
    // "<TAG> END pass=X fail=Y total=Z". Reflection reaches PGP internals (internal static);
    // BindingFlags below are Static|Public|NonPublic. Requires PowerGridPlus loaded.
    //
    // Threading note (test 2): the real Device.SetLogicValue path runs PGP's SetLogicValuePatch,
    // which after SetMode calls DirtyBridgeNetworks (managed CableNetwork flag writes, thread-safe),
    // ScheduleCascadeForDevice (ConcurrentQueue.Enqueue, thread-safe), and PassthroughModeMessage.
    // SendAll(0L). On this headless dedi with ZERO clients connected, SendAll has no peers and is a
    // no-op, so the whole synchronous mirror path is safe to call from the ElectricityTick worker.
    // The deferred consumer cascade (MainThreadDispatcher.Update) does not run headless, but the
    // synchronous partner mirror -- the "two things, one mode" requirement under test -- completes
    // inside the patch and is what GetMode reads back. The scenario verifies zero clients first.
    internal static partial class Dispatcher
    {
        // ============================================================
        // Scenario: pgp-umbilical-passthrough-probe (tests 1-6)
        // ============================================================
        private static bool _upbFired;

        private static void Scenario_PgpUmbilicalPassthroughProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-umbilical-passthrough-probe")) return;
            if (_upbFired) return;
            _upbFired = true;

            int total = 0, pass = 0, fail = 0;

            try
            {
                _log?.LogInfo("[ScenarioRunner] UPB START umbilical-passthrough-probe");

                var asm = GetModAssembly(PGP_ASSEMBLY);
                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

                var storeT = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var topoT = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");
                var syncT = asm.GetType("PowerGridPlus.PassthroughSettingsSync");
                var settingsT = asm.GetType("PowerGridPlus.Settings");
                var regT = asm.GetType("PowerGridPlus.LogicTypeRegistry");

                var getMode = storeT?.GetMethod("GetMode", SF, null, new[] { typeof(Thing) }, null);
                var setMode = storeT?.GetMethod("SetMode", SF, null, new[] { typeof(Thing), typeof(int) }, null);
                var getDefaultMode = storeT?.GetMethod("GetDefaultMode", SF, null, new[] { typeof(Thing) }, null);
                var isEnabledBridge = topoT?.GetMethod("IsEnabledBridge", SF, null, new[] { typeof(Device) }, null);
                var gather = topoT?.GetMethod("GatherReachable", SF);
                var getPartner = topoT?.GetMethod("GetUmbilicalPartner", SF, null, new[] { typeof(ElectricalInputOutput) }, null);
                var dirtyBridge = topoT?.GetMethod("DirtyBridgeNetworks", SF, null, new[] { typeof(Device) }, null);

                // LogicType enum value for LogicPassthroughMode (internal static readonly LogicType).
                var lpmField = regT?.GetField("LogicPassthroughMode", SF);
                LogicType lpm = lpmField != null ? (LogicType)lpmField.GetValue(null) : default;

                _log?.LogInfo(
                    $"[ScenarioRunner] UPB reflection: getMode={getMode != null} setMode={setMode != null} " +
                    $"getDefaultMode={getDefaultMode != null} isEnabledBridge={isEnabledBridge != null} gather={gather != null} " +
                    $"getPartner={getPartner != null} dirtyBridge={dirtyBridge != null} lpm={(int)lpm} syncT={syncT != null} settingsT={settingsT != null}");

                // Locate a docked male/female umbilical pair.
                Objects.Rockets.RocketPowerUmbilicalMale male = null;
                ElectricalInputOutput female = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (male != null || t == null) return;
                    if (t is Objects.Rockets.RocketPowerUmbilicalMale m)
                    {
                        var p = getPartner?.Invoke(null, new object[] { (ElectricalInputOutput)(Device)m }) as ElectricalInputOutput;
                        if (p == null) p = UpbReadPartnerField(m) as ElectricalInputOutput;
                        if (p != null) { male = m; female = p; }
                    }
                });

                if (male == null || female == null)
                {
                    _log?.LogWarning("[ScenarioRunner] UPB COULD-NOT-RUN: no docked umbilical pair in save (need male with non-null partner). Tests 1-5 skipped; running test 6 (MP wire-format) only.");
                }
                else
                {
                    _log?.LogInfo($"[ScenarioRunner] UPB docked pair: male ref={male.ReferenceId} female ref={female.ReferenceId}");

                    var maleDev = (Device)male;
                    var femaleDev = (Device)female;
                    var maleEio = (ElectricalInputOutput)maleDev;

                    // Save the original modes so we can restore the save state at the end.
                    int origMale = (int)getMode.Invoke(null, new object[] { (Thing)maleDev });
                    int origFemale = (int)getMode.Invoke(null, new object[] { (Thing)femaleDev });

                    // ---- TEST 1: DEFAULT MODE ----
                    // A never-written docked umbilical reads mode 1 (GetMode falls through to
                    // GetDefaultMode==1 for both halves). We clear any stored value by reflecting the
                    // backing dictionary, then read GetMode -- it must fall through to the default.
                    UpbClearStoredMode(storeT, male.ReferenceId);
                    UpbClearStoredMode(storeT, female.ReferenceId);
                    int defM = getDefaultMode != null ? (int)getDefaultMode.Invoke(null, new object[] { (Thing)maleDev }) : -1;
                    int defF = getDefaultMode != null ? (int)getDefaultMode.Invoke(null, new object[] { (Thing)femaleDev }) : -1;
                    int readM = (int)getMode.Invoke(null, new object[] { (Thing)maleDev });
                    int readF = (int)getMode.Invoke(null, new object[] { (Thing)femaleDev });
                    total++;
                    if (defM == 1 && defF == 1 && readM == 1 && readF == 1)
                    { _log?.LogInfo($"[ScenarioRunner] UPB P1 PASS: unwritten docked umbilical reads mode 1 (default). GetDefaultMode male={defM} female={defF}; GetMode male={readM} female={readF}."); pass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] UPB P1 FAIL: GetDefaultMode male={defM} female={defF}; GetMode male={readM} female={readF} (expected all 1)."); fail++; }

                    // ---- TEST 2: REAL WRITE + PARTNER MIRROR ----
                    // Invoke the real game method male.SetLogicValue(LogicPassthroughMode, 0.0) so PGP's
                    // SetLogicValuePatch + partner mirror run, then read GetMode on BOTH halves: expect 0.
                    // Then female.SetLogicValue(..., 1.0) and expect BOTH 1. Verify zero clients first.
                    int clients = UpbConnectedClientCount();
                    _log?.LogInfo($"[ScenarioRunner] UPB P2 pre: connectedClients={clients} (SendAll is a no-op at 0; mirror path is worker-safe).");
                    bool wrote0Ok = false, wrote1Ok = false;
                    Exception setEx = null;
                    try
                    {
                        // Write 0 on the MALE.
                        maleDev.SetLogicValue(lpm, 0.0);
                        int m0 = (int)getMode.Invoke(null, new object[] { (Thing)maleDev });
                        int f0 = (int)getMode.Invoke(null, new object[] { (Thing)femaleDev });
                        wrote0Ok = (m0 == 0 && f0 == 0);
                        _log?.LogInfo($"[ScenarioRunner] UPB P2a male.SetLogicValue(LPM,0) -> GetMode male={m0} female={f0} (expect 0/0).");

                        // Write 1 on the FEMALE.
                        femaleDev.SetLogicValue(lpm, 1.0);
                        int m1 = (int)getMode.Invoke(null, new object[] { (Thing)maleDev });
                        int f1 = (int)getMode.Invoke(null, new object[] { (Thing)femaleDev });
                        wrote1Ok = (m1 == 1 && f1 == 1);
                        _log?.LogInfo($"[ScenarioRunner] UPB P2b female.SetLogicValue(LPM,1) -> GetMode male={m1} female={f1} (expect 1/1).");
                    }
                    catch (Exception e) { setEx = e; }
                    total++;
                    if (setEx != null)
                    { _log?.LogError($"[ScenarioRunner] UPB P2 FAIL: SetLogicValue threw: {setEx.GetBaseException().Message}"); fail++; }
                    else if (wrote0Ok && wrote1Ok)
                    { _log?.LogInfo("[ScenarioRunner] UPB P2 PASS: real SetLogicValue on one half mirrors to the docked partner both ways (0->0/0, 1->1/1). The 'two things, one mode' requirement holds."); pass++; }
                    else
                    { _log?.LogError($"[ScenarioRunner] UPB P2 FAIL: mirror incomplete. write0Ok={wrote0Ok} write1Ok={wrote1Ok}."); fail++; }

                    // ---- TEST 3: MERGED DataDeviceList ----
                    // With the pair docked + mode 1 (set in P2b), the Female's OutputNetwork merged
                    // data-device list must CONTAIN a device that physically lives on the Male's
                    // InputNetwork, and vice versa. This is what a motherboard dropdown / IC10 sees.
                    setMode.Invoke(null, new object[] { (Thing)maleDev, 1 });
                    setMode.Invoke(null, new object[] { (Thing)femaleDev, 1 });
                    dirtyBridge?.Invoke(null, new object[] { maleDev });
                    dirtyBridge?.Invoke(null, new object[] { femaleDev });

                    var maleInNet = maleEio.InputNetwork;
                    var femaleOutNet = female.OutputNetwork;
                    total++;
                    if (maleInNet == null || femaleOutNet == null)
                    { _log?.LogWarning($"[ScenarioRunner] UPB P3 COULD-NOT-RUN: maleInNet={(maleInNet != null)} femaleOutNet={(femaleOutNet != null)} (a side has no network)."); }
                    else
                    {
                        // Pick a witness device that lives on the male's input net (not the umbilicals themselves).
                        Device witnessOnMaleIn = UpbPickWitness(maleInNet, maleDev, femaleDev);
                        Device witnessOnFemaleOut = UpbPickWitness(femaleOutNet, maleDev, femaleDev);

                        // Force a rebuild of the merged data-device lists (DataDeviceList getter runs
                        // RefreshPowerAndDataDeviceLists -> PGP's LogicPassthroughPatches merge postfix).
                        var femaleOutData = femaleOutNet.DataDeviceList;   // should now include male-side devices
                        var maleInData = maleInNet.DataDeviceList;         // should now include female-side devices

                        bool maleDevSeenOnFemaleSide = witnessOnMaleIn != null && femaleOutData != null && femaleOutData.Contains(witnessOnMaleIn);
                        bool femaleDevSeenOnMaleSide = witnessOnFemaleOut != null && maleInData != null && maleInData.Contains(witnessOnFemaleOut);

                        _log?.LogInfo(
                            $"[ScenarioRunner] UPB P3 maleInNet={maleInNet.ReferenceId}(devs={maleInNet.DeviceList.Count},data={maleInData?.Count}) " +
                            $"femaleOutNet={femaleOutNet.ReferenceId}(devs={femaleOutNet.DeviceList.Count},data={femaleOutData?.Count}) | " +
                            $"witnessOnMaleIn={(witnessOnMaleIn != null ? witnessOnMaleIn.GetType().Name + "#" + witnessOnMaleIn.ReferenceId : "none")} seenOnFemaleData={maleDevSeenOnFemaleSide} | " +
                            $"witnessOnFemaleOut={(witnessOnFemaleOut != null ? witnessOnFemaleOut.GetType().Name + "#" + witnessOnFemaleOut.ReferenceId : "none")} seenOnMaleData={femaleDevSeenOnMaleSide}");

                        // PASS if at least one direction is demonstrably bridged (a witness from the
                        // far side shows up in the near side's merged data list). Both witnesses may be
                        // absent only if each side has nothing but the umbilical on it (degenerate).
                        bool anyWitness = witnessOnMaleIn != null || witnessOnFemaleOut != null;
                        bool bridged = maleDevSeenOnFemaleSide || femaleDevSeenOnMaleSide;
                        if (!anyWitness)
                        { _log?.LogWarning("[ScenarioRunner] UPB P3 COULD-NOT-RUN: neither side carries a non-umbilical witness device to detect in the merged list."); }
                        else if (bridged)
                        { _log?.LogInfo("[ScenarioRunner] UPB P3 PASS: a far-side device physically on the partner's network appears in the near side's MERGED DataDeviceList (real consumer-visible bridge)."); pass++; }
                        else
                        { _log?.LogError("[ScenarioRunner] UPB P3 FAIL: a witness device exists but does NOT appear in the merged DataDeviceList across the docked pair."); fail++; }
                    }

                    // ---- TEST 4: UNDOCK BREAKS THE BRIDGE ----
                    // Null _partnerUmbilical on BOTH halves (save originals), confirm IsEnabledBridge ->
                    // false and GatherReachable(maleInNet) no longer folds in the female's output net;
                    // then restore the partner refs and confirm it bridges again.
                    object savedMalePartner = UpbReadPartnerField(male);
                    object savedFemalePartner = UpbReadPartnerField(female);
                    long femaleOutId = femaleOutNet?.ReferenceId ?? -1;
                    long maleInId = maleInNet?.ReferenceId ?? -1;
                    total++;
                    try
                    {
                        // Ensure mode 1 so only docking gates the bridge.
                        setMode.Invoke(null, new object[] { (Thing)maleDev, 1 });
                        setMode.Invoke(null, new object[] { (Thing)femaleDev, 1 });

                        bool bridgeBeforeUndock = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });
                        var reachBeforeUndock = UpbReach(gather, maleInNet);
                        bool femaleReachableBefore = femaleOutId >= 0 && reachBeforeUndock.Contains(femaleOutId);

                        // UNDOCK: sever both partner refs.
                        UpbWritePartnerField(male, null);
                        UpbWritePartnerField(female, null);

                        bool bridgeAfterUndock = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });
                        var reachAfterUndock = UpbReach(gather, maleInNet);
                        bool femaleReachableAfter = femaleOutId >= 0 && reachAfterUndock.Contains(femaleOutId);

                        // RE-DOCK: restore originals.
                        UpbWritePartnerField(male, savedMalePartner);
                        UpbWritePartnerField(female, savedFemalePartner);

                        bool bridgeAfterRedock = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });
                        var reachAfterRedock = UpbReach(gather, maleInNet);
                        bool femaleReachableRedock = femaleOutId >= 0 && reachAfterRedock.Contains(femaleOutId);

                        _log?.LogInfo(
                            $"[ScenarioRunner] UPB P4 docked: bridge={bridgeBeforeUndock} femaleNetReachable={femaleReachableBefore} | " +
                            $"undocked: bridge={bridgeAfterUndock} femaleNetReachable={femaleReachableAfter} | " +
                            $"redocked: bridge={bridgeAfterRedock} femaleNetReachable={femaleReachableRedock} (femaleOutNet={femaleOutId})");

                        bool ok = bridgeBeforeUndock && !bridgeAfterUndock && bridgeAfterRedock
                                  && !femaleReachableAfter && (femaleReachableBefore || femaleReachableRedock);
                        if (ok)
                        { _log?.LogInfo("[ScenarioRunner] UPB P4 PASS: undock (null partner) flips IsEnabledBridge to false and drops the partner net from GatherReachable; re-dock restores both."); pass++; }
                        else
                        { _log?.LogError($"[ScenarioRunner] UPB P4 FAIL: bridgeBefore={bridgeBeforeUndock} bridgeUndock={bridgeAfterUndock} bridgeRedock={bridgeAfterRedock} reachBefore={femaleReachableBefore} reachUndock={femaleReachableAfter} reachRedock={femaleReachableRedock}."); fail++; }
                    }
                    catch (Exception e)
                    {
                        // Best-effort restore on any failure path.
                        try { UpbWritePartnerField(male, savedMalePartner); UpbWritePartnerField(female, savedFemalePartner); } catch { }
                        _log?.LogError($"[ScenarioRunner] UPB P4 threw (partner refs restored): {e.GetBaseException().Message}");
                        fail++;
                    }

                    // ---- TEST 5: MASTER TOGGLE ----
                    // Force EnableUmbilicalLogicPassthrough.Value=false AND the synced EffectiveUmbilical
                    // override to false, confirm IsEnabledBridge=false even when docked + mode 1; restore
                    // true and confirm the bridge returns.
                    total++;
                    var enableField = settingsT?.GetField("EnableUmbilicalLogicPassthrough", SF);
                    var effUmbProp = syncT?.GetProperty("EffectiveUmbilical", SF);
                    var setSynced = syncT?.GetMethod("SetSyncedValues", SF, null,
                        new[] { typeof(bool), typeof(bool), typeof(bool), typeof(bool), typeof(bool) }, null);
                    var syncedUmbField = syncT?.GetField("_umbilical", SF); // bool? backing the synced override
                    object savedEnableVal = null, savedSyncedUmb = null;
                    var configEntry = enableField?.GetValue(null);
                    var valueProp = configEntry?.GetType().GetProperty("Value");
                    try
                    {
                        // Ensure mode 1 + docked (already restored above) so only the toggle gates it.
                        setMode.Invoke(null, new object[] { (Thing)maleDev, 1 });

                        savedEnableVal = valueProp?.GetValue(configEntry);
                        savedSyncedUmb = syncedUmbField?.GetValue(null);

                        bool effBefore = effUmbProp != null && (bool)effUmbProp.GetValue(null);
                        bool bridgeBefore = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });

                        // Turn OFF: set config false and the synced override false (EffectiveUmbilical
                        // returns the synced value when present, else the config value).
                        valueProp?.SetValue(configEntry, false);
                        if (syncedUmbField != null) syncedUmbField.SetValue(null, (bool?)false);
                        bool effOff = effUmbProp != null && (bool)effUmbProp.GetValue(null);
                        bool bridgeOff = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });

                        // Turn back ON.
                        valueProp?.SetValue(configEntry, savedEnableVal);
                        if (syncedUmbField != null) syncedUmbField.SetValue(null, savedSyncedUmb);
                        bool effOn = effUmbProp != null && (bool)effUmbProp.GetValue(null);
                        bool bridgeOn = (bool)isEnabledBridge.Invoke(null, new object[] { maleDev });

                        _log?.LogInfo(
                            $"[ScenarioRunner] UPB P5 effUmbilical before={effBefore}/bridge={bridgeBefore} | " +
                            $"off={effOff}/bridge={bridgeOff} | restored={effOn}/bridge={bridgeOn}");

                        bool ok = bridgeBefore && !bridgeOff && bridgeOn && !effOff && effOn;
                        if (ok)
                        { _log?.LogInfo("[ScenarioRunner] UPB P5 PASS: master toggle off forces IsEnabledBridge=false even when docked + mode 1; restoring true returns the bridge."); pass++; }
                        else
                        { _log?.LogError($"[ScenarioRunner] UPB P5 FAIL: bridgeBefore={bridgeBefore} bridgeOff={bridgeOff} bridgeOn={bridgeOn} effOff={effOff} effOn={effOn}."); fail++; }
                    }
                    catch (Exception e)
                    {
                        // Restore toggle on any failure.
                        try { if (valueProp != null && savedEnableVal != null) valueProp.SetValue(configEntry, savedEnableVal); if (syncedUmbField != null) syncedUmbField.SetValue(null, savedSyncedUmb); } catch { }
                        _log?.LogError($"[ScenarioRunner] UPB P5 threw (toggle restored): {e.GetBaseException().Message}");
                        fail++;
                    }

                    // Restore the umbilical's original modes so the save state is left as found.
                    setMode.Invoke(null, new object[] { (Thing)maleDev, origMale });
                    setMode.Invoke(null, new object[] { (Thing)femaleDev, origFemale });
                    _log?.LogInfo($"[ScenarioRunner] UPB restored original modes: male={origMale} female={origFemale}.");
                }

                // ---- TEST 6: MULTIPLAYER join-suffix + PassthroughModeMessage wire-format round-trip ----
                UpbJoinSuffixUmbilicalRoundTrip(asm, ref total, ref pass, ref fail);
                UpbPassthroughModeMessageRoundTrip(asm, ref total, ref pass, ref fail);

                _log?.LogInfo($"[ScenarioRunner] UPB END pass={pass} fail={fail} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] UPB threw: {e}");
            }
        }

        // Test 6a: full SerializeJoinSuffix -> DeserializeJoinSuffix round-trip focused on the umbilical
        // surface: seed a per-device umbilical mode entry in PassthroughModeStore, flip the 5th boolean
        // (EnableUmbilicalLogicPassthrough), serialize, mutate both, deserialize, and confirm BOTH the
        // per-device mode entry AND the umbilical boolean survived in the correct field order. A
        // misaligned field desyncs every field after it, so this is the key MP-format check.
        private static void UpbJoinSuffixUmbilicalRoundTrip(Assembly asm, ref int total, ref int pass, ref int fail)
        {
            try
            {
                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var pluginType = asm.GetType("PowerGridPlus.Plugin");
                var storeT = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var syncT = asm.GetType("PowerGridPlus.PassthroughSettingsSync");
                var settingsT = asm.GetType("PowerGridPlus.Settings");

                object pluginInstance = UpbFindPluginInstance();
                if (pluginInstance == null)
                { _log?.LogError("[ScenarioRunner] UPB P6 FAIL: could not locate live Plugin instance via Chainloader."); fail++; total++; return; }

                var serialize = pluginType?.GetMethod("SerializeJoinSuffix", BindingFlags.Public | BindingFlags.Instance);
                var deserialize = pluginType?.GetMethod("DeserializeJoinSuffix", BindingFlags.Public | BindingFlags.Instance);
                var setModeByRef = storeT?.GetMethod("SetModeByReference", SF, null, new[] { typeof(long), typeof(int) }, null);
                var effUmbProp = syncT?.GetProperty("EffectiveUmbilical", SF);
                var enableField = settingsT?.GetField("EnableUmbilicalLogicPassthrough", SF);
                var configEntry = enableField?.GetValue(null);
                var valueProp = configEntry?.GetType().GetProperty("Value");
                var syncedUmbField = syncT?.GetField("_umbilical", SF);

                if (serialize == null || deserialize == null || setModeByRef == null || valueProp == null)
                { _log?.LogError($"[ScenarioRunner] UPB P6 FAIL: plumbing missing (serialize={serialize != null} deserialize={deserialize != null} setModeByRef={setModeByRef != null} valueProp={valueProp != null})."); fail++; total++; return; }

                // Resolve RocketBinaryWriter/Reader plumbing (same pattern as the priority MP probe).
                Type rbwType = null, rbrType = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types; try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (rbwType == null && t.Name == "RocketBinaryWriter") rbwType = t;
                        if (rbrType == null && t.Name == "RocketBinaryReader") rbrType = t;
                    }
                    if (rbwType != null && rbrType != null) break;
                }
                var rbwCtor = rbwType?.GetConstructor(new[] { typeof(int) });
                var rbrCtor = rbrType?.GetConstructor(new[] { typeof(Stream) });
                var rbwBuf = rbwType?.GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance);
                var rbwLen = rbwType?.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                if (rbwCtor == null || rbrCtor == null || rbwBuf == null || rbwLen == null)
                { _log?.LogError($"[ScenarioRunner] UPB P6 FAIL: RocketBinary plumbing not found (rbw={rbwType?.FullName} rbr={rbrType?.FullName})."); fail++; total++; return; }

                // Seed a known umbilical-mode entry + a known toggle value.
                long seedRef = 919191919191L;
                int seedMode = 1;
                setModeByRef.Invoke(null, new object[] { seedRef, seedMode });
                object savedToggle = valueProp.GetValue(configEntry);
                object savedSyncedUmb = syncedUmbField?.GetValue(null);
                bool seedToggle = true;
                valueProp.SetValue(configEntry, seedToggle);

                // Serialize.
                object writer = rbwCtor.Invoke(new object[] { 65536 });
                serialize.Invoke(pluginInstance, new object[] { writer });
                var raw = (byte[])rbwBuf.GetValue(writer);
                int len = (int)rbwLen.GetValue(writer);
                byte[] payload = new byte[len];
                Buffer.BlockCopy(raw, 0, payload, 0, len);
                _log?.LogInfo($"[ScenarioRunner] UPB P6 join-suffix payload bytes={payload.Length}");

                // Mutate both so deserialize must restore them.
                setModeByRef.Invoke(null, new object[] { seedRef, 0 });
                valueProp.SetValue(configEntry, false);
                if (syncedUmbField != null) syncedUmbField.SetValue(null, (bool?)false);

                // Deserialize.
                var ms = new MemoryStream(payload);
                object reader = rbrCtor.Invoke(new object[] { ms });
                deserialize.Invoke(pluginInstance, new object[] { reader });

                int restoredMode = UpbReadStoredMode(storeT, seedRef, -1);
                // DeserializeJoinSuffix routes the 5 booleans into PassthroughSettingsSync.SetSyncedValues,
                // which writes the synced backing field _umbilical (a bool?). Read THAT directly: it is the
                // value that crossed the wire. EffectiveUmbilical cannot be used here because on a host
                // (NetworkManager.IsServer=true) it returns the LOCAL config value and ignores the synced
                // field (PassthroughSettingsSync.Effective short-circuits for the server), so it would
                // report the host's config, not the deserialized wire value.
                bool? restoredSyncedUmb = syncedUmbField?.GetValue(null) as bool?;

                total++;
                bool modeOk = restoredMode == seedMode;
                bool boolOk = restoredSyncedUmb == seedToggle;
                if (modeOk && boolOk)
                { _log?.LogInfo($"[ScenarioRunner] UPB P6 PASS: join-suffix round-trip preserved umbilical per-device mode (ref {seedRef}: {restoredMode}) AND the 5th (umbilical) boolean (synced _umbilical={restoredSyncedUmb}) in correct field order. (Read via the synced field, not host-biased EffectiveUmbilical.)"); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] UPB P6 FAIL: round-trip mismatch. perDeviceMode expected={seedMode} got={restoredMode} (ok={modeOk}); umbilicalBool expected={seedToggle} got synced _umbilical={restoredSyncedUmb} (ok={boolOk}). A FAIL here means a field-order desync in the join suffix."); fail++; }

                // Restore the real toggle + synced override; drop the synthetic store entry.
                valueProp.SetValue(configEntry, savedToggle);
                if (syncedUmbField != null) syncedUmbField.SetValue(null, savedSyncedUmb);
                UpbClearStoredMode(storeT, seedRef);
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] UPB P6 threw: {e}");
                fail++; total++;
            }
        }

        // Test 6b: standalone PassthroughModeMessage serialize -> deserialize; DeviceId + Mode survive.
        private static void UpbPassthroughModeMessageRoundTrip(Assembly asm, ref int total, ref int pass, ref int fail)
        {
            try
            {
                var msgType = asm.GetType("PowerGridPlus.PassthroughModeMessage");
                if (msgType == null)
                { _log?.LogError("[ScenarioRunner] UPB P7 FAIL: PassthroughModeMessage type not found."); fail++; total++; return; }

                Type rbwType = null, rbrType = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type[] types; try { types = a.GetTypes(); } catch { continue; }
                    foreach (var t in types)
                    {
                        if (rbwType == null && t.Name == "RocketBinaryWriter") rbwType = t;
                        if (rbrType == null && t.Name == "RocketBinaryReader") rbrType = t;
                    }
                    if (rbwType != null && rbrType != null) break;
                }
                var rbwCtor = rbwType?.GetConstructor(new[] { typeof(int) });
                var rbrCtor = rbrType?.GetConstructor(new[] { typeof(Stream) });
                var rbwBuf = rbwType?.GetField("_buffer", BindingFlags.NonPublic | BindingFlags.Instance);
                var rbwLen = rbwType?.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                var serialize = msgType.GetMethod("Serialize");
                var deserialize = msgType.GetMethod("Deserialize");
                var deviceIdField = msgType.GetField("DeviceId");
                var modeField = msgType.GetField("Mode");
                if (rbwCtor == null || rbrCtor == null || rbwBuf == null || rbwLen == null
                    || serialize == null || deserialize == null || deviceIdField == null || modeField == null)
                { _log?.LogError("[ScenarioRunner] UPB P7 FAIL: PassthroughModeMessage plumbing missing."); fail++; total++; return; }

                long seedId = 424242424242L;
                int seedMode = 1;
                var outMsg = Activator.CreateInstance(msgType);
                deviceIdField.SetValue(outMsg, seedId);
                modeField.SetValue(outMsg, seedMode);

                object writer = rbwCtor.Invoke(new object[] { 4096 });
                serialize.Invoke(outMsg, new object[] { writer });
                var raw = (byte[])rbwBuf.GetValue(writer);
                int len = (int)rbwLen.GetValue(writer);
                byte[] payload = new byte[len];
                Buffer.BlockCopy(raw, 0, payload, 0, len);

                var inMsg = Activator.CreateInstance(msgType);
                var ms = new MemoryStream(payload);
                object reader = rbrCtor.Invoke(new object[] { ms });
                deserialize.Invoke(inMsg, new object[] { reader });

                long gotId = (long)deviceIdField.GetValue(inMsg);
                int gotMode = (int)modeField.GetValue(inMsg);

                total++;
                if (gotId == seedId && gotMode == seedMode && len == 12)
                { _log?.LogInfo($"[ScenarioRunner] UPB P7 PASS: PassthroughModeMessage round-trip preserved DeviceId={gotId} Mode={gotMode} (payload {len} bytes = int64+int32)."); pass++; }
                else if (gotId == seedId && gotMode == seedMode)
                { _log?.LogInfo($"[ScenarioRunner] UPB P7 PASS: PassthroughModeMessage round-trip preserved DeviceId={gotId} Mode={gotMode} (payload {len} bytes)."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] UPB P7 FAIL: DeviceId expected={seedId} got={gotId}; Mode expected={seedMode} got={gotMode}; bytes={len}."); fail++; }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] UPB P7 threw: {e}");
                fail++; total++;
            }
        }

        // ============================================================
        // Scenario: pgp-umbilical-saveload-set (test 7 phase 1)
        // ------------------------------------------------------------
        // Set a non-default LogicPassthroughMode (0) on a docked umbilical
        // (both halves) so a subsequent save captures it via the side-car.
        // ============================================================
        private static bool _uslSetFired;

        private static void Scenario_PgpUmbilicalSaveLoadSet()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-umbilical-saveload-set")) return;
            if (_uslSetFired) return;
            _uslSetFired = true;

            try
            {
                _log?.LogInfo("[ScenarioRunner] USL-SET START");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var storeT = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var topoT = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");
                var setMode = storeT?.GetMethod("SetMode", SF, null, new[] { typeof(Thing), typeof(int) }, null);
                var getMode = storeT?.GetMethod("GetMode", SF, null, new[] { typeof(Thing) }, null);
                var getPartner = topoT?.GetMethod("GetUmbilicalPartner", SF, null, new[] { typeof(ElectricalInputOutput) }, null);

                Objects.Rockets.RocketPowerUmbilicalMale male = null;
                ElectricalInputOutput female = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (male != null || t == null) return;
                    if (t is Objects.Rockets.RocketPowerUmbilicalMale m)
                    {
                        var p = getPartner?.Invoke(null, new object[] { (ElectricalInputOutput)(Device)m }) as ElectricalInputOutput;
                        if (p == null) p = UpbReadPartnerField(m) as ElectricalInputOutput;
                        if (p != null) { male = m; female = p; }
                    }
                });

                if (male == null)
                {
                    _log?.LogWarning("[ScenarioRunner] USL-SET COULD-NOT-RUN: no docked umbilical pair in save.");
                    return;
                }

                setMode.Invoke(null, new object[] { (Thing)(Device)male, 0 });
                setMode.Invoke(null, new object[] { (Thing)female, 0 });
                int m = (int)getMode.Invoke(null, new object[] { (Thing)(Device)male });
                int f = (int)getMode.Invoke(null, new object[] { (Thing)female });
                _log?.LogInfo($"[ScenarioRunner] USL-SET set umbilical mode 0: male ref={male.ReferenceId}={m} female ref={female.ReferenceId}={f}. Save now to persist (side-car PassthroughSaveLoadPatches).");
                _log?.LogInfo("[ScenarioRunner] USL-SET END");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] USL-SET threw: {e}");
            }
        }

        // ============================================================
        // Scenario: pgp-umbilical-saveload-verify (test 7 phase 2)
        // ------------------------------------------------------------
        // After reload, confirm the umbilical mode restored to 0 (the
        // PassthroughSaveLoadPatches umbilical cases + side-car read), then
        // set it back to the default (1) so the save is left as found.
        // ============================================================
        private static bool _uslVerifyFired;

        private static void Scenario_PgpUmbilicalSaveLoadVerify()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-umbilical-saveload-verify")) return;
            if (_uslVerifyFired) return;
            _uslVerifyFired = true;

            int total = 0, pass = 0, fail = 0;
            try
            {
                _log?.LogInfo("[ScenarioRunner] USL-VERIFY START");
                var asm = GetModAssembly(PGP_ASSEMBLY);
                const BindingFlags SF = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                var storeT = asm.GetType("PowerGridPlus.PassthroughModeStore");
                var topoT = asm.GetType("PowerGridPlus.Patches.PassthroughTopology");
                var sideCarT = asm.GetType("PowerGridPlus.PassthroughSideCar");
                var setMode = storeT?.GetMethod("SetMode", SF, null, new[] { typeof(Thing), typeof(int) }, null);
                var getMode = storeT?.GetMethod("GetMode", SF, null, new[] { typeof(Thing) }, null);
                var getPartner = topoT?.GetMethod("GetUmbilicalPartner", SF, null, new[] { typeof(ElectricalInputOutput) }, null);

                // P0: report whether the load side-car was populated (proof the load postfix fired).
                var loadedField = sideCarT?.GetField("LoadedModes", SF);
                object loaded = loadedField?.GetValue(null);
                int loadedCount = (loaded as IDictionary)?.Count ?? -1;
                _log?.LogInfo($"[ScenarioRunner] USL-VERIFY P0: PassthroughSideCar.LoadedModes count={loadedCount} ({(loaded != null ? "non-null -> load postfix fired" : "null -> no side-car or postfix did not fire")}).");

                Objects.Rockets.RocketPowerUmbilicalMale male = null;
                ElectricalInputOutput female = null;
                OcclusionManager.AllThings.ForEach(t =>
                {
                    if (male != null || t == null) return;
                    if (t is Objects.Rockets.RocketPowerUmbilicalMale m)
                    {
                        var p = getPartner?.Invoke(null, new object[] { (ElectricalInputOutput)(Device)m }) as ElectricalInputOutput;
                        if (p == null) p = UpbReadPartnerField(m) as ElectricalInputOutput;
                        if (p != null) { male = m; female = p; }
                    }
                });

                if (male == null)
                {
                    _log?.LogWarning("[ScenarioRunner] USL-VERIFY COULD-NOT-RUN: no docked umbilical pair in save.");
                    return;
                }

                int m = (int)getMode.Invoke(null, new object[] { (Thing)(Device)male });
                int f = (int)getMode.Invoke(null, new object[] { (Thing)female });
                total++;
                if (m == 0 && f == 0)
                { _log?.LogInfo($"[ScenarioRunner] USL-VERIFY P1 PASS: umbilical mode restored to 0 after reload (male ref={male.ReferenceId}={m} female ref={female.ReferenceId}={f}). Side-car persistence works."); pass++; }
                else
                { _log?.LogError($"[ScenarioRunner] USL-VERIFY P1 FAIL: expected mode 0/0 after reload, got male={m} female={f}. (If both are 1, the set phase did not persist -- check the save actually captured the side-car.)"); fail++; }

                // Restore default (1) so the save is left as found.
                setMode.Invoke(null, new object[] { (Thing)(Device)male, 1 });
                setMode.Invoke(null, new object[] { (Thing)female, 1 });
                _log?.LogInfo("[ScenarioRunner] USL-VERIFY restored umbilical mode to default (1).");
                _log?.LogInfo($"[ScenarioRunner] USL-VERIFY END pass={pass} fail={fail} total={total}");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] USL-VERIFY threw: {e}");
            }
        }

        // ---- Umbilical probe helpers ----

        // Count clients currently connected (server-side). Used to confirm SendAll(0L) is a no-op so
        // the real SetLogicValue mirror path is safe to drive from the worker thread.
        private static int UpbConnectedClientCount()
        {
            try
            {
                var nm = typeof(NetworkManager);
                // Try a few likely client-collection members across game versions.
                foreach (var name in new[] { "ServerClients", "Clients", "ConnectedClients" })
                {
                    var p = nm.GetProperty(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    var v = p?.GetValue(null) as ICollection;
                    if (v != null) return v.Count;
                    var fld = nm.GetField(name, BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    var fv = fld?.GetValue(null) as ICollection;
                    if (fv != null) return fv.Count;
                }
            }
            catch { }
            return 0; // headless dedi with nobody joined
        }

        // Clear a stored per-device mode so GetMode falls through to GetDefaultMode. The store keys by
        // ReferenceId in a ConcurrentDictionary<long,int> field (_byReference). Remove the key directly.
        private static void UpbClearStoredMode(Type storeT, long referenceId)
        {
            try
            {
                var dict = storeT.GetField("_byReference", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (dict == null) return;
                // ConcurrentDictionary<long,int>.TryRemove(long, out int)
                var tryRemove = dict.GetType().GetMethod("TryRemove", new[] { typeof(long), typeof(int).MakeByRefType() });
                if (tryRemove != null)
                    tryRemove.Invoke(dict, new object[] { referenceId, 0 });
            }
            catch { }
        }

        // Read a stored per-device mode directly from the _byReference dictionary. Returns the stored
        // value, or `missing` if no entry exists (PassthroughModeStore exposes no GetMode(long)).
        private static int UpbReadStoredMode(Type storeT, long referenceId, int missing)
        {
            try
            {
                var dict = storeT.GetField("_byReference", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
                if (dict == null) return missing;
                var tryGet = dict.GetType().GetMethod("TryGetValue", new[] { typeof(long), typeof(int).MakeByRefType() });
                if (tryGet == null) return missing;
                object[] args = new object[] { referenceId, 0 };
                bool found = (bool)tryGet.Invoke(dict, args);
                return found ? (int)args[1] : missing;
            }
            catch { return missing; }
        }

        private static object UpbReadPartnerField(object umbilical)
        {
            if (umbilical == null) return null;
            try
            {
                var f = umbilical.GetType().GetField("_partnerUmbilical", BindingFlags.NonPublic | BindingFlags.Instance);
                return f?.GetValue(umbilical);
            }
            catch { return null; }
        }

        private static void UpbWritePartnerField(object umbilical, object value)
        {
            if (umbilical == null) return;
            var f = umbilical.GetType().GetField("_partnerUmbilical", BindingFlags.NonPublic | BindingFlags.Instance);
            f?.SetValue(umbilical, value);
        }

        private static List<long> UpbReach(MethodInfo gather, CableNetwork net)
        {
            var ids = new List<long>();
            if (net == null || gather == null) return ids;
            try
            {
                var r = gather.Invoke(null, new object[] { net }) as IEnumerable;
                if (r == null) return ids;
                foreach (var x in r) if (x is CableNetwork cn) ids.Add(cn.ReferenceId);
            }
            catch { }
            return ids;
        }

        // Pick a witness device that physically lives on `net` and is not one of the umbilical halves,
        // so its presence in the OTHER side's merged DataDeviceList proves the bridge.
        private static Device UpbPickWitness(CableNetwork net, Device excludeA, Device excludeB)
        {
            if (net == null) return null;
            try
            {
                var list = net.DeviceList;
                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];
                    if (d == null) continue;
                    if (d == excludeA || d == excludeB) continue;
                    return d;
                }
            }
            catch { }
            return null;
        }

        // Locate the live PowerGridPlus.Plugin instance (which implements the join-suffix serializer).
        // Primary path: PGP exposes `internal static readonly Mod MOD` and wires
        // `MOD.Networking.JoinSuffixSerializer = this` in Awake, so the live Plugin IS that serializer.
        // This is worker-safe (static field + property reads) and works regardless of whether the mod
        // loaded via BepInEx Chainloader or StationeersLaunchPad's LocalModSource. Chainloader.PluginInfos
        // is a fallback only (it does NOT list StationeersLaunchPad-loaded mods, which is why the priority
        // mp-probe's Chainloader lookup fails on this dedi).
        private static object UpbFindPluginInstance()
        {
            // Primary: Plugin.MOD.Networking.JoinSuffixSerializer
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var pluginType = asm?.GetType("PowerGridPlus.Plugin");
                var modField = pluginType?.GetField("MOD", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                var mod = modField?.GetValue(null);
                if (mod != null)
                {
                    var netObj = mod.GetType().GetProperty("Networking", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mod)
                                 ?? mod.GetType().GetField("Networking", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mod);
                    if (netObj != null)
                    {
                        var ser = netObj.GetType().GetProperty("JoinSuffixSerializer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(netObj)
                                  ?? netObj.GetType().GetField("JoinSuffixSerializer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(netObj);
                        // The serializer IS the Plugin instance (Plugin : BaseUnityPlugin, IJoinSuffixSerializer).
                        if (ser != null && pluginType.IsInstanceOfType(ser)) return ser;
                        if (ser != null) return ser; // still usable: the interface methods live on it
                    }
                }
            }
            catch { }

            // Fallback: BepInEx Chainloader (works only for Chainloader-loaded mods).
            try
            {
                var chainloaderType = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                    .FirstOrDefault(t => t.FullName == "BepInEx.Bootstrap.Chainloader");
                var pluginInfosProp = chainloaderType?.GetProperty("PluginInfos", BindingFlags.Public | BindingFlags.Static);
                var pluginInfos = pluginInfosProp?.GetValue(null) as IDictionary;
                object info = null;
                if (pluginInfos != null && pluginInfos.Contains("net.powergridplus"))
                    info = pluginInfos["net.powergridplus"];
                return info?.GetType().GetProperty("Instance")?.GetValue(info);
            }
            catch { return null; }
        }
    }
}
