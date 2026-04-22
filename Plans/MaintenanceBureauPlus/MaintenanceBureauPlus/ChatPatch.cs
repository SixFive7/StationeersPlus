using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using LaunchPadBooster.Networking;
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
                "[DIAG] ChatPatch.Postfix: HumanId=" + __instance.HumanId +
                " Display=" + (__instance.DisplayName ?? "<null>") +
                " textChars=" + (__instance.ChatText != null ? __instance.ChatText.Length : 0) +
                " text=" + Truncate(__instance.ChatText, 200));

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

            // Linearity guard: if the LLM is still working on the prior
            // turn, do NOT enqueue this message. The model never sees it.
            // Instead broadcast a canned auto-reply from a non-officer
            // bureau entity (BusyResponses.md) so the player gets a clear,
            // in-lore signal that they were ignored, and they have to wait
            // and re-send. This enforces strict turn-taking between the
            // player and the model.
            var engine = MaintenanceBureauPlusPlugin.Engine;
            if (engine != null && engine.IsBusy)
            {
                var busy = MaintenanceBureauPlusPlugin.BusyResponses?.PickRandom();
                if (busy != null)
                {
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Engine busy; player message dropped, sending busy reply '" +
                        busy.Sender + "'.");
                    BroadcastAsOfficer(busy.Sender, busy.Text);
                }
                else
                {
                    MaintenanceBureauPlusPlugin.Log.LogWarning(
                        "[DIAG] Engine busy but BusyResponses pool is empty; player message silently dropped.");
                }
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

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Turn " + conv.TurnCount + "/" + conv.MaxTurns +
                " phase=" + PhaseFor(conv) +
                " officer=" + (conv.Officer != null ? conv.Officer.Name : "?") +
                " promptChars=" + prompt.Length +
                " promptHead=" + Truncate(prompt, 140));

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

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Cycle start: officer=" + (conv.Officer != null ? conv.Officer.Name : "?") +
                " dept=" + (conv.Officer != null ? conv.Officer.Department : "?") +
                " min=" + conv.MinTurns + " max=" + conv.MaxTurns +
                " phase=" + PhaseFor(conv) +
                " promptChars=" + prompt.Length);

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

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] HandleReply: rawChars=" + (rawReply != null ? rawReply.Length : 0) +
                " head=" + Truncate(rawReply, 160));

            var parsed = ApprovalTagParser.Parse(rawReply);
            var tag = parsed.Tag;
            var text = CleanReply(parsed.StrippedText);

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Parsed: tag=" + tag + " cleanedChars=" + (text != null ? text.Length : 0) +
                " turn=" + conv.TurnCount + " min=" + conv.MinTurns + " max=" + conv.MaxTurns);

            // The Bureau never refuses. If the model tries, we treat it as
            // continuing. The system prompt forbids it, but a stubborn model
            // occasionally emits the tag anyway.
            if (tag == ApprovalTag.Refused)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] Officer attempted [REFUSED] (not allowed); demoting to [CONTINUE].");
                tag = ApprovalTag.Continue;
            }

            // Bounds enforcement.
            if (tag == ApprovalTag.Approved && conv.TurnCount < conv.MinTurns)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] [APPROVED] before MinTurns (" + conv.TurnCount + "/" + conv.MinTurns +
                    "); demoting to [CONTINUE].");
                tag = ApprovalTag.Continue;
            }
            if (tag != ApprovalTag.Approved && conv.TurnCount >= conv.MaxTurns)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] MaxTurns reached (" + conv.TurnCount + "/" + conv.MaxTurns +
                    ") with tag=" + tag + "; forcing [APPROVED].");
                tag = ApprovalTag.Approved;
            }

            var officerName = conv.Officer != null ? conv.Officer.Name : "Bureau";
            BroadcastAsOfficer(officerName, text);
            conv.AppendOfficer(text);

            switch (tag)
            {
                case ApprovalTag.Continue:
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Continuing cycle; awaiting next player message (turn " +
                        conv.TurnCount + "/" + conv.MaxTurns + ").");
                    break;
                case ApprovalTag.Approved:
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] APPROVED at turn " + conv.TurnCount +
                        "/" + conv.MaxTurns + "; firing ApprovalEvent.");
                    MaintenanceBureauPlusPlugin.Engine.EndInteractiveCycle();
                    ApprovalEvent.Start();
                    break;
                case ApprovalTag.None:
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] No approval tag detected; treating as [CONTINUE].");
                    break;
            }
        }

        // Post-process the model's reply: strip anything after a hallucinated
        // new-turn header (the model tends to role-play the entire chat log
        // once it finishes its assistant turn), then trim trailing BPE
        // artifacts. Ċ (U+010A) is the tokenizer's rendering of the newline
        // byte, and Ġ (U+0120) is the space byte; either can leak when
        // generation stops mid-decode on a stop-word match.
        private static string CleanReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int cut = FirstHallucinationIndex(text);
            if (cut >= 0) text = text.Substring(0, cut);
            // Strip any leftover bracketed PHASE marker the model leaks
            // anywhere in the reply. AntiPrompts catch most of these but
            // miss leading-of-line variants and any that the executor's
            // stop-word check sees only after emitting them.
            text = StripBracketedPhase(text);
            return text.TrimEnd('Ċ', 'Ġ', '\n', '\r', ' ', '\t');
        }

        // Remove any '[PHASE:...]' / '[Phase:...]' substring no matter where
        // it lands. The model treats PHASE:FINAL etc. as a tag-like marker
        // and emits it at end-of-reply (or, occasionally, mid-reply). We
        // only ever want our own [CONTINUE]/[APPROVED] tags in user-visible
        // text, and ApprovalTagParser already strips those.
        private static string StripBracketedPhase(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int searchFrom = 0;
            while (true)
            {
                int open = text.IndexOf("[PHASE", searchFrom, StringComparison.OrdinalIgnoreCase);
                if (open < 0) break;
                int close = text.IndexOf(']', open);
                if (close < 0)
                {
                    // Unterminated — chop from the marker to end-of-string.
                    text = text.Substring(0, open);
                    break;
                }
                text = text.Remove(open, close - open + 1);
                searchFrom = open;
            }
            return text;
        }

        private static int FirstHallucinationIndex(string text)
        {
            int best = -1;
            string[] markers = { "\n**[Turn", "\n[Turn", "\nPHASE:", "\n<|im_start|>", "<|im_start|>" };
            foreach (var marker in markers)
            {
                var idx = text.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0 && (best < 0 || idx < best)) best = idx;
            }
            return best;
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "<null>";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
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
            // Compact PHASE directive tells the officer which tags are
            // permitted this turn without leaking turn numbers the model would
            // parrot back. The system prompt (SystemPrompts.ApprovalTagRules)
            // teaches the model what each phase means and to never reference
            // the directive by name. AntiPrompts include "\nPHASE:" as a
            // hallucination guard, so the model can't continue the pattern
            // into a fake next turn after its reply.
            //
            // The (STAY IN VOICE: ...) annotation is a per-turn voice + tic
            // refresh. The full persona block lives in the KV cache (sent
            // once per cycle), but a fresh single-line reminder fights model
            // drift toward generic helpful-assistant tone after a few turns.
            // The system prompt forbids the model from echoing or addressing
            // anything inside parentheses.
            var phase = PhaseFor(conv);
            var sb = new StringBuilder();
            sb.Append("PHASE:");
            sb.Append(phase);

            if (conv.Officer != null && (!string.IsNullOrEmpty(conv.Officer.Voice) || !string.IsNullOrEmpty(conv.Officer.Tic)))
            {
                sb.Append("\n(STAY IN VOICE: ");
                if (!string.IsNullOrEmpty(conv.Officer.Voice)) sb.Append(conv.Officer.Voice);
                if (!string.IsNullOrEmpty(conv.Officer.Voice) && !string.IsNullOrEmpty(conv.Officer.Tic)) sb.Append(". ");
                if (!string.IsNullOrEmpty(conv.Officer.Tic)) sb.Append(conv.Officer.Tic);
                sb.Append(".)");
            }

            if (phase == "FINAL")
            {
                sb.Append("\n(This is your final turn on this cycle. You MUST close with [APPROVED] and an in-character reason for suddenly signing off that fits ");
                sb.Append(conv.Officer != null && !string.IsNullOrEmpty(conv.Officer.Name)
                    ? conv.Officer.Name
                    : "this officer");
                sb.Append("'s voice and backstory.)");
            }
            sb.Append("\n");
            sb.Append(playerName ?? "Player");
            sb.Append(": ");
            sb.Append(playerMessage ?? string.Empty);
            return sb.ToString();
        }

        private static string PhaseFor(ConversationState conv)
        {
            if (conv.TurnCount < conv.MinTurns) return "EARLY";
            if (conv.TurnCount >= conv.MaxTurns) return "FINAL";
            return "ELIGIBLE";
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

        // Bureau popup display duration, scaled with text length so long
        // replies stay on screen long enough to read. Clamped to a sane
        // window so a short reply doesn't vanish and a huge one doesn't
        // block the screen forever.
        private const float PopupMinSeconds = 6f;
        private const float PopupMaxSeconds = 20f;
        private const float PopupSecondsPerChar = 0.035f;

        public static void BroadcastAsOfficer(string officerName, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try
            {
                MaintenanceBureauPlusPlugin.Log.LogInfo(
                    "[DIAG] Broadcasting: officer=" + officerName +
                    " chars=" + text.Length +
                    " preview=" + Truncate(text, 120));

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

                // Popup: shown locally on the server (SendToClients doesn't
                // echo back), broadcast to remote clients via
                // BureauReplyMessage.
                float duration = System.Math.Min(PopupMaxSeconds,
                    System.Math.Max(PopupMinSeconds, text.Length * PopupSecondsPerChar));
                BureauPopup.Show(officerName, text, duration);

                if (NetworkManager.IsServer)
                {
                    try
                    {
                        new BureauReplyMessage
                        {
                            OfficerName = officerName,
                            ReplyText = text,
                            DisplayDuration = duration,
                        }.SendAll(0L);
                        MaintenanceBureauPlusPlugin.Log.LogInfo(
                            "[DIAG] BureauReplyMessage sent to remote clients.");
                    }
                    catch (Exception ex)
                    {
                        MaintenanceBureauPlusPlugin.Log.LogError(
                            "[DIAG] BureauReplyMessage.SendAll failed: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                MaintenanceBureauPlusPlugin.Log.LogError("BroadcastAsOfficer failed: " + ex.Message);
            }
        }
    }
}
