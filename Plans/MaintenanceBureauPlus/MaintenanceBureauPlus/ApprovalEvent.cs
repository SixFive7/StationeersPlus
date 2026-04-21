using System;
using System.Reflection;
using System.Text;
using Assets.Scripts.Objects;
using UnityEngine;

namespace MaintenanceBureauPlus
{
    // Orchestrates the post-approval sequence:
    //   1. Broadcast the approval reply (done by caller before Start() is called)
    //   2. Full blackout: stun every connected Human to the blackout value
    //   3. Repair sweep
    //   4. Telemetry
    //   5. Closing-message LLM call (async; returns to main thread)
    //   6. Per-player: spawn LanderCapsule + teleport, then write Stun=80 once
    //   7. Super-summary LLM call; persist to PersonaMemoryStore; reset conversation
    //
    // After step 6 the mod never touches stun again; the game's natural stun decay
    // wakes each player during the 13.5 s capsule descent.
    public static class ApprovalEvent
    {
        public static bool IsRunning { get; private set; }

        public static void Start()
        {
            if (IsRunning)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("ApprovalEvent.Start called while already running; ignoring.");
                return;
            }
            IsRunning = true;

            try
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] phase 2: full blackout stun=" + MaintenanceBureauPlusPlugin.StunBlackout);
                SetStunForAllHumans(MaintenanceBureauPlusPlugin.StunBlackout);

                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] phase 3: repair sweep");
                int repaired = RepairSweep.Run();
                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] sweep touched " + repaired + " things");

                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] phase 4: telemetry");
                var telemetry = TelemetryCollector.Collect();
                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] corpses=" + telemetry.CorpseCount + " wreckage=" + telemetry.WreckageCount);

                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] phase 5: closing message");
                EnqueueClosingMessage(telemetry);
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("ApprovalEvent early phase failed: " + ex);
                RunLatePhasesFallback();
            }
        }

        // Closing message is an async single-shot LLM call. When it returns (on the
        // worker thread), the callback queues the broadcast + late phases onto the
        // main thread.
        private static void EnqueueClosingMessage(Telemetry telemetry)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv == null || conv.Officer == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("No active persona for closing message; using fallback");
                BroadcastClosingFallback();
                RunLatePhases();
                return;
            }

            var prompt = BuildClosingPrompt(conv.Officer, telemetry);
            MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, MaintenanceBureauPlusPlugin.MaxTokensForClosing, reply =>
            {
                MainThreadQueue.Enqueue(() =>
                {
                    try
                    {
                        var text = reply;
                        // Strip any stray approval tag the model may have emitted
                        // even though the closing prompt tells it not to.
                        var parsed = ApprovalTagParser.Parse(reply);
                        if (parsed.Tag != ApprovalTag.None) text = parsed.StrippedText;

                        ChatPatch.BroadcastAsOfficer(conv.Officer.Name, text);
                    }
                    catch (Exception ex)
                    {
                        MaintenanceBureauPlusPlugin.Log.LogError("Closing broadcast failed: " + ex.Message);
                    }
                    RunLatePhases();
                });
            });
        }

        private static string BuildClosingPrompt(OfficerPersona officer, Telemetry t)
        {
            var personaBlock = officer.ToPersonaBlock();
            var body = SystemPrompts.ClosingMessageTemplate
                .Replace("{PREAMBLE}", SystemPrompts.GlobalBureauPreamble)
                .Replace("{PERSONA_BLOCK}", personaBlock)
                .Replace("{CORPSE_COUNT}", t.CorpseCount.ToString())
                .Replace("{WRECKAGE_BRIEF}", t.RenderWreckageBrief());

            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(body);
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        private static void BroadcastClosingFallback()
        {
            var officer = MaintenanceBureauPlusPlugin.Conversation != null
                ? MaintenanceBureauPlusPlugin.Conversation.Officer
                : null;
            var name = officer != null ? officer.Name : "Bureau Officer";
            ChatPatch.BroadcastAsOfficer(name,
                "The Bureau has processed your request. I am leaving the desk now. A different officer will handle your next inquiry.");
        }

        private static void RunLatePhases()
        {
            try
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] phase 6: per-player capsule spawn + stun=" + MaintenanceBureauPlusPlugin.StunWakeDuringDescent);
                SpawnCapsulesAndSetWakeStun();
                MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] hands off to game stun decay + capsule descent");
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("ApprovalEvent late phase failed: " + ex.Message);
            }
            finally
            {
                FinalizeAndReset();
            }
        }

        private static void RunLatePhasesFallback()
        {
            try { SpawnCapsulesAndSetWakeStun(); } catch { }
            FinalizeAndReset();
        }

        private static void FinalizeAndReset()
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv != null && conv.Officer != null)
            {
                var officer = conv.Officer;
                var prompt = BuildSuperSummaryPrompt(officer);
                MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, MaintenanceBureauPlusPlugin.MaxTokensForSuperSummary, reply =>
                {
                    MainThreadQueue.Enqueue(() =>
                    {
                        var trimmed = (reply ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(trimmed))
                        {
                            MaintenanceBureauPlusPlugin.Memory?.Append(trimmed);
                            MaintenanceBureauPlusPlugin.Log.LogInfo("[ApprovalEvent] super-summary saved: " + trimmed);
                        }
                        conv.Reset();
                        IsRunning = false;
                    });
                });
            }
            else
            {
                conv?.Reset();
                IsRunning = false;
            }
        }

        private static string BuildSuperSummaryPrompt(OfficerPersona officer)
        {
            var body = SystemPrompts.SuperSummaryTemplate
                .Replace("{NAME}", officer.Name ?? "")
                .Replace("{DEPARTMENT}", officer.Department ?? "");
            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(body);
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        // ---- Game-API wrappers ----

        // Enumerate connected players. Uses reflection on a likely static
        // collection to stay robust against namespace drift. The expected
        // shape is Assets.Scripts.Objects.Entities.Human with a static
        // AllHumans-like collection.
        private static System.Collections.Generic.IEnumerable<Thing> AllConnectedHumans()
        {
            // Prefer a 'Human' type with an 'AllHumans' static collection.
            // Fall back to scanning Thing.AllThings for things whose type name ends in "Human".
            var results = new System.Collections.Generic.List<Thing>();
            try
            {
                var all = Thing.AllThings;
                if (all == null) return results;
                foreach (var thing in all)
                {
                    if (thing == null) continue;
                    var typeName = thing.GetType().Name;
                    if (typeName == "Human" || typeName.EndsWith("Human"))
                        results.Add(thing);
                }
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("AllConnectedHumans failed: " + ex.Message);
            }
            return results;
        }

        // Set stun on every connected Human by calling the DamageState.Damage
        // method. The exact method signature comes from Research/Workflows/
        // KnockPlayerUnconscious.md:
        //   human.DamageState.Damage(ChangeDamageType.Set, value, DamageUpdateType.Stun)
        // We use reflection to stay robust against exact namespace paths.
        private static void SetStunForAllHumans(int value)
        {
            foreach (var human in AllConnectedHumans())
            {
                try
                {
                    SetStun(human, value);
                }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("Stun write failed: " + ex.Message);
                }
            }
        }

        private static void SetStun(Thing human, int value)
        {
            var damageStateProp = human.GetType().GetProperty("DamageState");
            if (damageStateProp == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("Human has no DamageState property; cannot set stun.");
                return;
            }
            var ds = damageStateProp.GetValue(human, null);
            if (ds == null) return;

            // First try direct field/property write to Stun.
            var stunProp = ds.GetType().GetProperty("Stun");
            if (stunProp != null && stunProp.CanWrite && stunProp.PropertyType == typeof(float))
            {
                stunProp.SetValue(ds, (float)value, null);
                return;
            }
            var stunField = ds.GetType().GetField("Stun");
            if (stunField != null && stunField.FieldType == typeof(float))
            {
                stunField.SetValue(ds, (float)value);
                return;
            }

            // Fall back to Damage(Set, value, channel) method invocation.
            var methods = ds.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
            foreach (var m in methods)
            {
                if (m.Name != "Damage") continue;
                var parms = m.GetParameters();
                if (parms.Length != 3) continue;
                try
                {
                    // Resolve ChangeDamageType.Set and DamageUpdateType.Stun by reflection.
                    var setValue = ResolveEnumValue(parms[0].ParameterType, "Set");
                    var stunValue = ResolveEnumValue(parms[2].ParameterType, "Stun");
                    if (setValue == null || stunValue == null) continue;
                    m.Invoke(ds, new object[] { setValue, (float)value, stunValue });
                    return;
                }
                catch { }
            }

            MaintenanceBureauPlusPlugin.Log.LogWarning("Could not set stun on " + human.GetType().Name + "; no matching API found.");
        }

        private static object ResolveEnumValue(Type enumType, string name)
        {
            if (enumType == null || !enumType.IsEnum) return null;
            try { return Enum.Parse(enumType, name, true); }
            catch { return null; }
        }

        // Per-player capsule spawn + one-time post-teleport Stun = 80.
        // Research recipe (Research/Workflows/TriggerLanderCapsule.md):
        //   var capsule = OnServer.Create<LanderCapsule>(Prefab.Find<LanderCapsule>(), pos, rot);
        //   OnServer.MoveToSlot(human, capsule.Slots[1]);
        //   OnServer.Interact(capsule.InteractMode, 1);
        private static void SpawnCapsulesAndSetWakeStun()
        {
            foreach (var human in AllConnectedHumans())
            {
                try
                {
                    SpawnCapsuleFor(human);
                    // After the teleport, write stun once. Do not touch it again.
                    SetStun(human, MaintenanceBureauPlusPlugin.StunWakeDuringDescent);
                }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("Capsule spawn / post-stun failed: " + ex.Message);
                }
            }
        }

        // Reflection-based invocation of the three-call capsule recipe.
        // TODO(verify-api): once the game's exact types are confirmed, swap the
        // reflection calls for direct type references for clarity and performance.
        private static void SpawnCapsuleFor(Thing human)
        {
            // Position + rotation of the player.
            var thingTransformProp = human.GetType().GetProperty("ThingTransform");
            if (thingTransformProp == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("Human has no ThingTransform property; cannot spawn capsule.");
                return;
            }
            var transform = thingTransformProp.GetValue(human, null) as Transform;
            if (transform == null) return;
            var pos = transform.position;
            var rot = transform.rotation;

            // Look up the LanderCapsule type (namespace varies).
            var landerType = FindTypeByName("LanderCapsule");
            if (landerType == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("LanderCapsule type not found via reflection.");
                return;
            }

            // Prefab.Find<LanderCapsule>() or similar.
            var prefabType = FindTypeByName("Prefab");
            if (prefabType == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("Prefab type not found.");
                return;
            }
            object prefabInstance = InvokeGenericStatic(prefabType, "Find", landerType, null);
            if (prefabInstance == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("Prefab.Find<LanderCapsule>() returned null.");
                return;
            }

            // OnServer.Create<LanderCapsule>(prefab, pos, rot).
            var onServerType = FindTypeByName("OnServer");
            if (onServerType == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("OnServer type not found.");
                return;
            }

            object capsule = InvokeGenericStatic(onServerType, "Create", landerType, new object[] { prefabInstance, pos, rot });
            if (capsule == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("OnServer.Create<LanderCapsule> returned null.");
                return;
            }

            // capsule.Slots[1] for the seated slot.
            var slotsProp = capsule.GetType().GetProperty("Slots");
            if (slotsProp == null) return;
            var slotsVal = slotsProp.GetValue(capsule, null) as System.Collections.IList;
            if (slotsVal == null || slotsVal.Count < 2) return;
            var seatSlot = slotsVal[1];

            // OnServer.MoveToSlot(human, slot)
            var moveToSlot = onServerType.GetMethod("MoveToSlot", new Type[] { human.GetType().BaseType ?? typeof(object), seatSlot.GetType() })
                ?? FindMethodByName(onServerType, "MoveToSlot");
            if (moveToSlot != null)
            {
                try { moveToSlot.Invoke(null, new object[] { human, seatSlot }); }
                catch (Exception ex) { MaintenanceBureauPlusPlugin.Log.LogError("MoveToSlot invoke failed: " + ex.Message); }
            }

            // OnServer.Interact(capsule.InteractMode, 1)
            var interactModeProp = capsule.GetType().GetProperty("InteractMode");
            if (interactModeProp == null) return;
            var interactMode = interactModeProp.GetValue(capsule, null);
            var interactMethod = FindMethodByName(onServerType, "Interact");
            if (interactMethod != null)
            {
                try { interactMethod.Invoke(null, new object[] { interactMode, 1 }); }
                catch (Exception ex) { MaintenanceBureauPlusPlugin.Log.LogError("Interact invoke failed: " + ex.Message); }
            }
        }

        private static Type FindTypeByName(string simpleName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == simpleName) return t;
                    }
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo FindMethodByName(Type type, string name)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == name) return m;
            }
            return null;
        }

        private static object InvokeGenericStatic(Type type, string methodName, Type typeArg, object[] args)
        {
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != methodName) continue;
                if (!m.IsGenericMethodDefinition) continue;
                try
                {
                    var closed = m.MakeGenericMethod(typeArg);
                    return closed.Invoke(null, args);
                }
                catch { }
            }
            return null;
        }
    }
}
