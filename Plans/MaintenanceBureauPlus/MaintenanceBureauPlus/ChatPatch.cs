using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using System;
using System.Text;

namespace MaintenanceBureauPlus
{
    // Harmony postfix on ChatMessage.PrintToConsole. Every chat line display,
    // host outgoing + client receiving, routes through this method (see
    // SayCommand.Say line 98484, ChatMessage.Process line 260722). Using
    // Process instead would miss the host's own messages, which never go
    // through Process locally. Bureau replies go out with HumanId = -1 so the
    // same field serves as the loop guard on inbound messages.
    [HarmonyPatch(typeof(ChatMessage), nameof(ChatMessage.PrintToConsole))]
    public static class ChatPatch
    {
        public static void Postfix(ChatMessage __instance)
        {
            if (__instance == null) return;

            // Emergency drain: if Plugin.Update and MainThreadTicker.Update
            // are both dead, this is the only main-thread code we ever reach.
            // Drain any pending LLM callbacks from previous turns before
            // processing the new message.
            try { MainThreadQueue.Drain(); } catch { }

            // Diagnostic: prove the Harmony patch is actually wired to
            // ChatMessage.PrintToConsole. If we never see this line in the log
            // after a player chats, the patch did not attach to the right method.
            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "ChatPatch.Postfix fired: HumanId=" + __instance.HumanId +
                " Display=" + (__instance.DisplayName ?? "<null>") +
                " Text=" + (__instance.ChatText ?? "<null>"));

            // Only the server / host runs LLM inference. Clients just see the
            // bureau's messages as normal chat via the regular broadcast.
            if (!NetworkManager.IsServer && !IsStandaloneSinglePlayer())
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo("ChatPatch: not server and not single-player, bailing.");
                return;
            }
            if (MaintenanceBureauPlusPlugin.Engine == null || !MaintenanceBureauPlusPlugin.Engine.IsLoaded)
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo("ChatPatch: engine not loaded yet, bailing.");
                return;
            }

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

            // Diagnostic hotpath: typing literally 'ping' gets an instant
            // 'pong' reply synthesized without touching the LLM. Proves the
            // chat intercept + officer broadcast pipeline works end-to-end
            // independent of inference. Remove before v1 release.
            if (string.Equals(message.Trim(), "ping", StringComparison.OrdinalIgnoreCase))
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] ping hotpath fired");
                BroadcastAsOfficer("Bureau PingBot", "pong");
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

        // Entry point for every qualifying public chat line. If no cycle is
        // active we start one (persona is picked locally, interactive LLM
        // session opens with the full system prompt). If a cycle is active
        // we append only the delta — one player message — to the ongoing
        // session, reusing the KV cache from previous turns.
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

            conv.AppendPlayer(playerName, message);
            conv.TurnCount++;

            var prompt = BuildTurnDeltaPrompt(conv, playerName, message);
            int maxTokens = MaintenanceBureauPlusPlugin.MaxTokensPerReply;

            MaintenanceBureauPlusPlugin.Engine.EnqueueInteractive(prompt, maxTokens, rawReply =>
            {
                MainThreadQueue.Enqueue(() => HandleReply(rawReply));
            });
        }

        private static void StartNewCycle(string playerName, string openingMessage)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            conv.Reset();
            conv.IsActive = true;
            conv.IsAwaitingPersona = false;   // resolved synchronously below
            conv.MinTurns = MaintenanceBureauPlusPlugin.MinTurns.Value;
            conv.MaxTurns = MaintenanceBureauPlusPlugin.MaxTurns.Value;
            conv.TurnCount = 1;
            conv.AppendPlayer(playerName, openingMessage);

            // Persona selection used to be an LLM call over a prompt listing
            // all 100 personas in full, which is ~14 kB and takes minutes on
            // a 1.5B CPU model to ingest before the first token is produced.
            // Pick deterministically in-process instead, biasing away from
            // recently-seen names. The LLM's job is to PLAY the character,
            // not pick one; the picking is cheap local logic.
            conv.Officer = PickRandomUnseenPersona();

            MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] persona selected (local): " +
                conv.Officer.Name +
                (string.IsNullOrEmpty(conv.Officer.Department) ? "" : " (" + conv.Officer.Department + ")"));

            // Open a fresh interactive LLM session. The preamble + persona +
            // approval rules are sent once here and stay in the KV cache for
            // the rest of the cycle; subsequent turns only send the delta.
            MaintenanceBureauPlusPlugin.Engine.BeginInteractiveCycle();

            var prompt = BuildCycleStartPrompt(conv, playerName, openingMessage);
            MaintenanceBureauPlusPlugin.Engine.EnqueueInteractive(prompt, MaintenanceBureauPlusPlugin.MaxTokensPerReply, rawTurn =>
            {
                MainThreadQueue.Enqueue(() => HandleReply(rawTurn));
            });
        }

        private static OfficerPersona PickRandomUnseenPersona()
        {
            var pool = MaintenanceBureauPlusPlugin.Personas;
            if (pool == null || pool.Count == 0)
                return FallbackRandomPersona();

            var memory = MaintenanceBureauPlusPlugin.Memory;
            var seenNames = new System.Collections.Generic.HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            if (memory != null)
            {
                // Parse names out of recent super-summary entries. Each entry
                // begins 'Officer <Name>, <Department>: ...'.
                foreach (var line in memory.Summaries)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    const string prefix = "Officer ";
                    int start = line.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (start < 0) continue;
                    int nameStart = start + prefix.Length;
                    int comma = line.IndexOf(',', nameStart);
                    if (comma < 0) continue;
                    var name = line.Substring(nameStart, comma - nameStart).Trim();
                    if (!string.IsNullOrEmpty(name)) seenNames.Add(name);
                }
            }

            var all = pool.All;
            var unseen = new System.Collections.Generic.List<OfficerPersona>();
            foreach (var p in all)
            {
                if (p == null) continue;
                if (!seenNames.Contains(p.Name ?? string.Empty))
                    unseen.Add(p);
            }
            var candidates = (unseen.Count > 0) ? (System.Collections.Generic.IList<OfficerPersona>)unseen : (System.Collections.Generic.IList<OfficerPersona>)all;
            return candidates[new Random().Next(candidates.Count)];
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
                    MaintenanceBureauPlusPlugin.Engine.EndInteractiveCycle();
                    ApprovalEvent.Start();
                    break;
                case ApprovalTag.Refused:
                    MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] officer refused; ending cycle");
                    MaintenanceBureauPlusPlugin.Engine.EndInteractiveCycle();
                    conv.Reset();
                    break;
                case ApprovalTag.None:
                    MaintenanceBureauPlusPlugin.Log.LogInfo("[Bureau] reply had no approval tag; treating as [CONTINUE]");
                    break;
            }
        }

        // ---- Prompt assembly ----

        // First prompt of an interactive cycle. Sends the full system block
        // (preamble + persona + approval rules + final instruction) plus the
        // first player message. The InteractiveExecutor will tokenize and
        // evaluate all of this, and everything except the user/assistant
        // pair will stay in the KV cache for every subsequent turn.
        private static string BuildCycleStartPrompt(ConversationState conv, string playerName, string playerMessage)
        {
            var personaBlock = conv.Officer != null ? conv.Officer.ToPersonaBlock() : "(no persona)";

            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(SystemPrompts.GlobalBureauPreamble);
            sb.Append("\n\nACTIVE OFFICER:\n");
            sb.Append(personaBlock);
            sb.Append("\n\n");
            sb.Append(SystemPrompts.ApprovalTagRules);
            sb.Append("\n\nReply as the officer. Keep replies at 3-5 short paragraphs. End every reply with exactly one tag from the approval rules.");
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>user\n");
            sb.Append(FormatUserLine(conv, playerName, playerMessage));
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        // Per-turn delta prompt for a cycle already in progress. The system
        // block is in the KV cache, so we only send the new user message and
        // the assistant-turn header. No transcript, no preamble, no rules.
        private static string BuildTurnDeltaPrompt(ConversationState conv, string playerName, string playerMessage)
        {
            var sb = new StringBuilder();
            sb.Append("<|im_start|>user\n");
            sb.Append(FormatUserLine(conv, playerName, playerMessage));
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        private static string FormatUserLine(ConversationState conv, string playerName, string playerMessage)
        {
            // Embed turn state in the user line since the KV-cached system
            // block can't be rewritten mid-cycle. The officer sees the turn
            // count in every player message.
            return "[Turn " + conv.TurnCount + " of " + conv.MaxTurns + "] [" +
                (playerName ?? "Player") + "]: " + (playerMessage ?? string.Empty);
        }

        // ---- Broadcast helper (main thread) ----

        // Stationeers' SayCommand blocks in pure single-player via
        // CommandBase.CannotInSinglePlayer, but some community mod flows still
        // let ChatMessage.PrintToConsole fire. Treat "neither client nor server"
        // as single-player and process bureau logic there too.
        private static bool IsStandaloneSinglePlayer()
        {
            return !NetworkManager.IsClient && !NetworkManager.IsServer;
        }

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
                // PrintToConsole also re-enters our postfix, but our HumanId == -1
                // loop guard filters it out before any processing.
                chatMessage.PrintToConsole();
                if (NetworkManager.IsServer)
                {
                    NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
                }
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("BroadcastAsOfficer failed: " + ex.Message);
            }
        }
    }
}
