using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using System;
using System.Text;

namespace MaintenanceBureauPlus
{
    // Harmony postfix on ChatMessage.Process. Reads every public chat line on the
    // server and routes qualifying lines into the ConversationManager-style flow
    // inlined in this class. Bureau replies go out via HumanId = -1 broadcasts;
    // the same value is used as the loop-guard on inbound messages.
    [HarmonyPatch(typeof(ChatMessage), nameof(ChatMessage.Process))]
    public static class ChatPatch
    {
        public static void Postfix(ChatMessage __instance)
        {
            if (__instance == null) return;
            if (!NetworkManager.IsServer) return;
            if (MaintenanceBureauPlusPlugin.Engine == null || !MaintenanceBureauPlusPlugin.Engine.IsLoaded) return;

            // Loop guard: bureau messages are sent with HumanId = -1. Skip them.
            if (__instance.HumanId == -1) return;

            var playerName = __instance.DisplayName;
            var message = __instance.ChatText;
            if (string.IsNullOrWhiteSpace(message)) return;

            // Debug hook: any chat message containing the literal [DEBUG-APPROVE]
            // token fires the approval event immediately. Unconditional while
            // the mod is in playtest so it works in both Debug and Release.
            // See TODO.md: remove this hook before v1 release.
            if (message.IndexOf("[DEBUG-APPROVE]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("Debug approval hook fired by " + playerName);
                ApprovalEvent.Start();
                return;
            }

            try
            {
                HandleIncoming(playerName, message);
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("HandleIncoming failed: " + ex.Message);
            }
        }

        // Entry point for every qualifying public chat line. If no cycle is active
        // we start one (which first asks the LLM to pick a persona). If a cycle is
        // active we append the message to the transcript and enqueue the next turn.
        private static void HandleIncoming(string playerName, string message)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv == null) return;
            if (ApprovalEvent.IsRunning) return;  // silence during the event

            if (!conv.IsActive)
            {
                StartNewCycle(playerName, message);
                return;
            }

            if (conv.IsAwaitingPersona) return;  // persona selection in flight; ignore further input briefly

            conv.AppendPlayer(playerName, message);
            conv.TurnCount++;

            var prompt = BuildTurnPrompt(conv, playerName, message);
            int maxTokens = MaintenanceBureauPlusPlugin.MaxTokensPerReply;

            MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, maxTokens, rawReply =>
            {
                MainThreadQueue.Enqueue(() => HandleReply(rawReply));
            });
        }

        private static void StartNewCycle(string playerName, string openingMessage)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            conv.Reset();
            conv.IsActive = true;
            conv.IsAwaitingPersona = true;
            conv.MinTurns = MaintenanceBureauPlusPlugin.MinTurns.Value;
            conv.MaxTurns = MaintenanceBureauPlusPlugin.MaxTurns.Value;
            conv.TurnCount = 1;
            conv.AppendPlayer(playerName, openingMessage);

            var prompt = BuildPersonaSelectionPrompt(openingMessage);
            int maxTokens = MaintenanceBureauPlusPlugin.MaxTokensForPersonaSelection;
            MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, maxTokens, rawReply =>
            {
                MainThreadQueue.Enqueue(() => OnPersonaSelected(rawReply, playerName, openingMessage));
            });
        }

        private static void OnPersonaSelected(string rawReply, string playerName, string openingMessage)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            var persona = ParsePersonaFromReply(rawReply) ?? FallbackRandomPersona();
            conv.Officer = persona;
            conv.IsAwaitingPersona = false;

            MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] persona selected: " + persona.Name +
                (string.IsNullOrEmpty(persona.Department) ? "" : " (" + persona.Department + ")"));

            // Immediately follow with the officer's first in-cycle reply.
            var prompt = BuildTurnPrompt(conv, playerName, openingMessage);
            MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, MaintenanceBureauPlusPlugin.MaxTokensPerReply, rawTurn =>
            {
                MainThreadQueue.Enqueue(() => HandleReply(rawTurn));
            });
        }

        private static OfficerPersona ParsePersonaFromReply(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            try
            {
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start < 0 || end <= start) return null;
                var json = raw.Substring(start, end - start + 1);
                var persona = UnityEngine.JsonUtility.FromJson<OfficerPersona>(json);
                if (persona == null || string.IsNullOrWhiteSpace(persona.Name)) return null;
                return persona;
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("Persona JSON parse failed: " + ex.Message);
                return null;
            }
        }

        private static OfficerPersona FallbackRandomPersona()
        {
            var pool = MaintenanceBureauPlusPlugin.Personas;
            if (pool != null && pool.Count > 0)
            {
                var all = pool.All;
                var pick = all[new Random().Next(all.Count)];
                MaintenanceBureauPlusPlugin.Log.LogWarning("Falling back to random persona: " + pick.Name);
                return pick;
            }
            return new OfficerPersona
            {
                Name = "Officer",
                Department = "General Services",
                Tic = "speaks in plain formal language",
                Voice = "clerical, dry",
                Backstory = "A career Bureau officer whose specific details have been redacted.",
                Summary = "Generic fallback officer."
            };
        }

        private static void HandleReply(string rawReply)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv == null || !conv.IsActive) return;

            var parsed = ApprovalTagParser.Parse(rawReply);
            var tag = parsed.Tag;
            var text = parsed.StrippedText;

            // Enforce bounds.
            if (tag == ApprovalTag.Approved && conv.TurnCount < conv.MinTurns)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("[APPROVED] before MinTurns (" +
                    conv.TurnCount + "/" + conv.MinTurns + "); demoting to [CONTINUE]");
                tag = ApprovalTag.Continue;
            }
            if (tag != ApprovalTag.Refused && tag != ApprovalTag.Approved && conv.TurnCount >= conv.MaxTurns)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning("MaxTurns hit (" +
                    conv.TurnCount + "/" + conv.MaxTurns + "); forcing [APPROVED]");
                tag = ApprovalTag.Approved;
            }

            var officerName = conv.Officer != null ? conv.Officer.Name : "Bureau";
            BroadcastAsOfficer(officerName, text);
            conv.AppendOfficer(text);

            switch (tag)
            {
                case ApprovalTag.Continue:
                    break;
                case ApprovalTag.Approved:
                    ApprovalEvent.Start();
                    break;
                case ApprovalTag.Refused:
                    MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] officer refused; ending cycle");
                    conv.Reset();
                    break;
                case ApprovalTag.None:
                    MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] reply had no approval tag; treating as [CONTINUE]");
                    break;
            }
        }

        // ---- Prompt assembly ----

        private static string BuildTurnPrompt(ConversationState conv, string playerName, string playerMessage)
        {
            var personaBlock = conv.Officer != null ? conv.Officer.ToPersonaBlock() : "(no persona yet)";
            var turnInfo = "TurnCount=" + conv.TurnCount + " MinTurns=" + conv.MinTurns + " MaxTurns=" + conv.MaxTurns;
            var body = SystemPrompts.InCycleTurnTemplate
                .Replace("{PREAMBLE}", SystemPrompts.GlobalBureauPreamble)
                .Replace("{PERSONA_BLOCK}", personaBlock)
                .Replace("{TURN_INFO}", turnInfo)
                .Replace("{APPROVAL_RULES}", SystemPrompts.ApprovalTagRules)
                .Replace("{TRANSCRIPT}", conv.RenderTranscript())
                .Replace("{PLAYER_MESSAGE}", "[" + playerName + "]: " + playerMessage);

            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(body);
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        private static string BuildPersonaSelectionPrompt(string openingMessage)
        {
            var pool = MaintenanceBureauPlusPlugin.Personas;
            var sb = new StringBuilder();
            if (pool != null)
            {
                var all = pool.All;
                for (int i = 0; i < all.Count; i++)
                {
                    sb.AppendLine(all[i].ToPoolSnippet());
                }
            }
            var memoryText = MaintenanceBureauPlusPlugin.Memory != null
                ? MaintenanceBureauPlusPlugin.Memory.RenderForPrompt(30)
                : "(empty)";

            var body = SystemPrompts.PersonaSelectionTemplate
                .Replace("{PREAMBLE}", SystemPrompts.GlobalBureauPreamble)
                .Replace("{POOL_SNIPPETS}", sb.ToString().TrimEnd())
                .Replace("{MEMORY_LIST}", memoryText);

            var prompt = new StringBuilder();
            prompt.Append("<|im_start|>system\n");
            prompt.Append(body);
            prompt.Append("\n<|im_end|>\n");
            prompt.Append("<|im_start|>user\n");
            prompt.Append("Opening message from player: ");
            prompt.Append(openingMessage);
            prompt.Append("\n<|im_end|>\n");
            prompt.Append("<|im_start|>assistant\n");
            return prompt.ToString();
        }

        // ---- Broadcast helper (main thread) ----

        public static void BroadcastAsOfficer(string officerName, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                var chatMessage = new ChatMessage
                {
                    ChatText = text,
                    DisplayName = officerName,
                    HumanId = -1
                };
                chatMessage.PrintToConsole();
                NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("BroadcastAsOfficer failed: " + ex.Message);
            }
        }
    }
}
