using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using UnityEngine;

namespace MaintenanceBureauPlus
{
    // Orchestrates the post-approval sequence:
    //   1. Broadcast the approval reply (done by caller before Start())
    //   2. Full blackout: stun every connected Human to StunBlackout
    //   3. Repair sweep
    //   4. Telemetry
    //   5. Closing-message LLM call (async; returns to main thread)
    //   6. Per-player: spawn LanderCapsule + teleport, then write Stun=StunWakeDuringDescent once
    //   7. Super-summary LLM call; persist to PersonaMemoryStore; reset conversation
    //
    // After step 6 the mod never touches stun again; the game's natural stun decay
    // wakes each player during the 13.5 s capsule descent.
    //
    // Game APIs are concrete (not reflected) per Research pages verified at 0.2.6228.27061:
    //   Human enumeration           Research/GameClasses/Human.md
    //   Stun write                  Research/Workflows/KnockPlayerUnconscious.md
    //   Capsule spawn recipe        Research/Workflows/TriggerLanderCapsule.md
    //   LanderCapsule slots         Research/GameClasses/LanderCapsule.md
    // OnServer lives in the global namespace; referenced directly.
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

        // ---- Game-API wrappers (concrete calls, verified 0.2.6228.27061) ----

        // Research/GameClasses/Human.md documents the enumeration + online-filter pattern.
        private static IEnumerable<Human> AllConnectedHumans()
        {
            var results = new List<Human>();
            if (Human.AllHumans == null) return results;
            foreach (var h in Human.AllHumans)
            {
                if (h == null) continue;
                if (h.State != EntityState.Alive) continue;
                if (h.OrganBrain == null) continue;
                if (!h.OrganBrain.IsOnline) continue;
                results.Add(h);
            }
            return results;
        }

        // Research/Workflows/KnockPlayerUnconscious.md: EntityDamageState.Damage auto-forwards
        // Stun writes to the Brain organ, so we can call straight through human.DamageState
        // and do not need to poke human.OrganBrain.DamageState ourselves.
        private static void SetStunForAllHumans(int value)
        {
            foreach (var human in AllConnectedHumans())
            {
                try
                {
                    human.DamageState.Damage(ChangeDamageType.Set, (float)value, DamageUpdateType.Stun);
                }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("Stun write failed on " +
                        (human != null ? human.DisplayName : "<null>") + ": " + ex.Message);
                }
            }
        }

        // Research/Workflows/TriggerLanderCapsule.md three-call recipe:
        //   var prefab = Prefab.Find<LanderCapsule>("LanderCapsule");
        //   var capsule = OnServer.Create<LanderCapsule>(prefab, pos, rot);
        //   OnServer.MoveToSlot(human, capsule.Slots[1]);   // Slots[1] == seat per LanderCapsule.md
        //   OnServer.Interact(capsule.InteractMode, 1);     // LanderMode state 1 == Descending
        //
        // The one-time post-teleport Stun=StunWakeDuringDescent write per player follows
        // immediately, so natural stun decay (3 per life tick) wakes them during the
        // 13.5 s descent without any further mod intervention.
        private static void SpawnCapsulesAndSetWakeStun()
        {
            var prefab = Prefab.Find<LanderCapsule>("LanderCapsule");
            if (prefab == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("LanderCapsule prefab not found by name 'LanderCapsule'. Check the prefab name against a live game session.");
                return;
            }

            foreach (var human in AllConnectedHumans())
            {
                try
                {
                    SpawnCapsuleFor(human, prefab);
                    human.DamageState.Damage(
                        ChangeDamageType.Set,
                        (float)MaintenanceBureauPlusPlugin.StunWakeDuringDescent,
                        DamageUpdateType.Stun);
                }
                catch (Exception ex)
                {
                    MaintenanceBureauPlusPlugin.Log.LogError("Capsule spawn / post-stun failed for " +
                        (human != null ? human.DisplayName : "<null>") + ": " + ex.Message);
                }
            }
        }

        private static void SpawnCapsuleFor(Human human, LanderCapsule prefab)
        {
            var transform = human.ThingTransform;
            if (transform == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("Human has no ThingTransform; skipping capsule spawn.");
                return;
            }
            var pos = transform.position;
            var rot = transform.rotation;

            var capsule = OnServer.Create<LanderCapsule>(prefab, pos, rot);
            if (capsule == null)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("OnServer.Create<LanderCapsule> returned null for " + human.DisplayName);
                return;
            }

            if (capsule.Slots == null || capsule.Slots.Count < 2)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("LanderCapsule has fewer than 2 slots; cannot seat player " + human.DisplayName);
                return;
            }

            OnServer.MoveToSlot(human, capsule.Slots[1]);
            OnServer.Interact(capsule.InteractMode, 1);
        }
    }
}
