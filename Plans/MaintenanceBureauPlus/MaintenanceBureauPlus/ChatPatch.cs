using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using LaunchPadBooster.Networking;
using System;
using System.Text;
using System.Text.RegularExpressions;

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
            //
            // Two checks:
            //   Engine.IsBusy           - an inference is currently queued
            //                             or running on the worker thread.
            //   Conversation.PendingTurn - bridges the microsecond gap
            //                             between classifier completion and
            //                             reply enqueue, where IsBusy briefly
            //                             returns false. Set at the start of
            //                             each turn, cleared in HandleReply
            //                             after broadcast.
            var engine = MaintenanceBureauPlusPlugin.Engine;
            var convForBusy = MaintenanceBureauPlusPlugin.Conversation;
            if (engine != null && (engine.IsBusy || (convForBusy != null && convForBusy.PendingTurn)))
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

        // Entry point for every qualifying public chat line. If no cycle
        // is active, start one (single LLM call, no classifier needed).
        // If a cycle is active, run the two-step classifier-then-reply
        // pipeline: classify the player's message, decide a directive
        // from (turn, classification), then generate the reply with the
        // chosen directive embedded in the system block.
        private static void HandleIncoming(string playerName, string message)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv == null) return;
            if (ApprovalEvent.IsRunning) return;  // silence during the event

            if (!conv.IsActive)
            {
                StartNewCycle(playerName, message);
            }
            else
            {
                ClassifyThenReply(playerName, message);
            }
        }

        // Cycle start: pick officer locally, append the opening message,
        // skip the classifier (no prior turn to classify), default the
        // directive to StallSoft, fire one stateless reply call.
        private static void StartNewCycle(string playerName, string openingMessage)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            conv.Reset();
            conv.IsActive = true;
            conv.IsAwaitingPersona = false;
            conv.MinTurns = MaintenanceBureauPlusPlugin.MinTurns.Value;
            conv.MaxTurns = MaintenanceBureauPlusPlugin.MaxTurns.Value;
            conv.TurnCount = 1;
            conv.AppendPlayer(playerName, openingMessage);
            conv.Officer = PickRandomUnseenPersona();
            conv.PendingTurn = true;

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Cycle start: officer=" + (conv.Officer != null ? conv.Officer.Name : "?") +
                " dept=" + (conv.Officer != null ? conv.Officer.Department : "?") +
                " min=" + conv.MinTurns + " max=" + conv.MaxTurns);

            // Turn 1: model can't approve yet (turn < min), so directive is
            // always StallSoft. No classifier call needed; classification
            // would be "Fresh" with no prior context to evaluate.
            var directive = TurnDirective.StallSoft;
            var prompt = BuildReplyPrompt(conv, directive);

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Reply enqueue (cycle start): directive=" + directive +
                " promptChars=" + prompt.Length);

            MaintenanceBureauPlusPlugin.Engine.Enqueue(prompt, MaintenanceBureauPlusPlugin.MaxTokensPerReply, raw =>
            {
                MainThreadQueue.Enqueue(() => HandleReply(raw, directive));
            });
        }

        // Mid-cycle: ask the model to classify the player's MOST RECENT
        // message, parse the label, decide a directive, then enqueue the
        // reply. The append-and-bump happens BETWEEN the two calls so the
        // classifier sees the new message as separate context, while the
        // reply prompt sees it as the most-recent turn in History.
        private static void ClassifyThenReply(string playerName, string message)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            conv.PendingTurn = true;

            var classifierPrompt = BuildClassifierPrompt(conv, playerName, message);
            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Classifier enqueue: promptChars=" + classifierPrompt.Length);

            MaintenanceBureauPlusPlugin.Engine.Enqueue(classifierPrompt, MaintenanceBureauPlusPlugin.MaxTokensForClassification, classifierRaw =>
            {
                MainThreadQueue.Enqueue(() =>
                {
                    var classification = ParseClassification(classifierRaw);
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Classifier raw=" + Truncate(classifierRaw, 80) +
                        " parsed=" + classification);

                    conv.AppendPlayer(playerName, message);
                    conv.TurnCount++;

                    var directive = DecideDirective(
                        conv.TurnCount, conv.MinTurns, conv.MaxTurns, classification);

                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Turn " + conv.TurnCount + "/" + conv.MaxTurns +
                        " classification=" + classification + " directive=" + directive);

                    var replyPrompt = BuildReplyPrompt(conv, directive);
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] Reply enqueue: directive=" + directive +
                        " promptChars=" + replyPrompt.Length);

                    MaintenanceBureauPlusPlugin.Engine.Enqueue(replyPrompt, MaintenanceBureauPlusPlugin.MaxTokensPerReply, replyRaw =>
                    {
                        MainThreadQueue.Enqueue(() => HandleReply(replyRaw, directive));
                    });
                });
            });
        }

        // Five-state directive set chosen by decide_directive(turn, classification).
        // Each maps to a prose INSTRUCTION block in SystemPrompts that the
        // model reads silently and acts on; the system prompt forbids the
        // model from echoing the instruction back in its reply.
        internal enum TurnDirective { StallHard, StallSoft, ExtractDetail, MayAgree, MustAgreeClosing }

        // Classifier output. Throwaway per-turn signal used only to pick
        // the next directive; never persisted.
        internal enum PlayerClassification { Cooperative, Stalling, Hostile, Confused, OffTopic, Fresh }

        private static TurnDirective DecideDirective(int turn, int min, int max, PlayerClassification cls)
        {
            // Hard ceiling: the very last turn must approve, regardless of
            // how the player behaves on it. The LLM is told to spin an
            // in-character closing reason in this slot.
            if (turn >= max) return TurnDirective.MustAgreeClosing;

            // Eligible band: model may approve if the player has earned it,
            // or extract one more detail / continue stalling if not.
            if (turn >= min)
            {
                switch (cls)
                {
                    case PlayerClassification.Cooperative: return TurnDirective.MayAgree;
                    case PlayerClassification.Stalling:    return TurnDirective.ExtractDetail;
                    case PlayerClassification.Hostile:     return TurnDirective.StallHard;
                    case PlayerClassification.Confused:    return TurnDirective.StallSoft;
                    case PlayerClassification.OffTopic:    return TurnDirective.StallSoft;
                    default:                                return TurnDirective.StallSoft;
                }
            }

            // Early band (turn < min): no approval ever. Stalling vs hard-
            // stalling depends on the player's tone.
            switch (cls)
            {
                case PlayerClassification.Hostile:     return TurnDirective.StallHard;
                case PlayerClassification.Stalling:    return TurnDirective.ExtractDetail;
                default:                                return TurnDirective.StallSoft;
            }
        }

        // Match the first occurrence of any known label in the classifier's
        // output. The model is supposed to emit exactly one label, but a
        // tired model will sometimes preface it with a sentence; we tolerate
        // that. Order matters: HOSTILE before OFF_TOPIC (which contains "T")
        // is no problem since we match by IndexOf, not StartsWith. Default
        // to Cooperative on parse failure (benign, model will still get a
        // reasonable directive).
        private static PlayerClassification ParseClassification(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return PlayerClassification.Cooperative;
            var s = raw.ToUpperInvariant();
            if (s.IndexOf("HOSTILE", StringComparison.Ordinal) >= 0) return PlayerClassification.Hostile;
            if (s.IndexOf("STALLING", StringComparison.Ordinal) >= 0) return PlayerClassification.Stalling;
            if (s.IndexOf("CONFUSED", StringComparison.Ordinal) >= 0) return PlayerClassification.Confused;
            if (s.IndexOf("OFF_TOPIC", StringComparison.Ordinal) >= 0
                || s.IndexOf("OFF-TOPIC", StringComparison.Ordinal) >= 0
                || s.IndexOf("OFFTOPIC", StringComparison.Ordinal) >= 0)
                return PlayerClassification.OffTopic;
            if (s.IndexOf("COOPERATIVE", StringComparison.Ordinal) >= 0) return PlayerClassification.Cooperative;
            return PlayerClassification.Cooperative;
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

        private static void HandleReply(string rawReply, TurnDirective directive)
        {
            var conv = MaintenanceBureauPlusPlugin.Conversation;
            if (conv == null || !conv.IsActive)
            {
                if (conv != null) conv.PendingTurn = false;
                return;
            }

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] HandleReply: directive=" + directive +
                " rawChars=" + (rawReply != null ? rawReply.Length : 0) +
                " head=" + Truncate(rawReply, 160));

            var parsed = ApprovalTagParser.Parse(rawReply);
            var tag = parsed.Tag;
            var text = CleanReply(parsed.StrippedText);

            MaintenanceBureauPlusPlugin.Log.LogInfo(
                "[DIAG] Parsed: tag=" + tag + " cleanedChars=" + (text != null ? text.Length : 0) +
                " turn=" + conv.TurnCount + " min=" + conv.MinTurns + " max=" + conv.MaxTurns);

            // [REFUSED] is not a real tag in this design; demote to CONTINUE.
            if (tag == ApprovalTag.Refused)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] Officer attempted [REFUSED]; demoting to [CONTINUE].");
                tag = ApprovalTag.Continue;
            }

            // Defensive directive enforcement: the directive selects which
            // tags are legal this turn. The model is told this in the
            // INSTRUCTION block, but small models occasionally ignore the
            // directive. We enforce here as a backstop.
            if (directive == TurnDirective.MustAgreeClosing && tag != ApprovalTag.Approved)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] Directive=MustAgreeClosing but model emitted " + tag +
                    "; forcing [APPROVED].");
                tag = ApprovalTag.Approved;
            }
            if ((directive == TurnDirective.StallHard
                || directive == TurnDirective.StallSoft
                || directive == TurnDirective.ExtractDetail)
                && tag == ApprovalTag.Approved)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] Directive=" + directive + " (stall) but model emitted [APPROVED]; demoting to [CONTINUE].");
                tag = ApprovalTag.Continue;
            }

            // Hard bound: never approve before MinTurns regardless of directive.
            if (tag == ApprovalTag.Approved && conv.TurnCount < conv.MinTurns)
            {
                MaintenanceBureauPlusPlugin.Log.LogWarning(
                    "[DIAG] [APPROVED] before MinTurns (" + conv.TurnCount + "/" + conv.MinTurns +
                    "); demoting to [CONTINUE].");
                tag = ApprovalTag.Continue;
            }
            // Hard bound: at MaxTurns, force approval. Caught above for
            // MustAgreeClosing too, but defensive belt-and-suspenders.
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
            conv.PendingTurn = false;

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
                    ApprovalEvent.Start();
                    break;
                case ApprovalTag.None:
                    MaintenanceBureauPlusPlugin.Log.LogInfo(
                        "[DIAG] No approval tag detected; treating as [CONTINUE].");
                    break;
            }
        }

        // Post-process the model's reply: strip anything after a hallucinated
        // new-turn header (the model tends to role-play the next user turn
        // once it finishes its assistant turn), strip any stray bracketed
        // tokens that aren't [CONTINUE]/[APPROVED] (the model invents
        // things like [PHASE:FINAL], [INSTRUCTION], [STALL_HARD] when it
        // pattern-matches our internal labels), and trim trailing BPE
        // artifacts. Ċ (U+010A) is the tokenizer's rendering of the newline
        // byte and Ġ (U+0120) the space byte; either can leak when
        // generation stops mid-decode on a stop-word match.
        private static string CleanReply(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            int cut = FirstHallucinationIndex(text);
            if (cut >= 0) text = text.Substring(0, cut);
            text = StrayBracketRegex.Replace(text, string.Empty);
            return text.TrimEnd('Ċ', 'Ġ', '\n', '\r', ' ', '\t');
        }

        // Match any `[ALL_CAPS_TOKEN]` or `[ALL_CAPS_TOKEN:anything]` that
        // is NOT [CONTINUE] or [APPROVED]. ApprovalTagParser already strips
        // the legitimate tags before CleanReply runs, so by the time we
        // hit this regex the only acceptable bracketed content is in-prose
        // flavour like [REDACTED], which is mixed-case and won't match.
        // The negative lookahead exempts CONTINUE/APPROVED for safety in
        // case the parser missed one. Compiled once at type init.
        private static readonly Regex StrayBracketRegex = new Regex(
            @"\[(?!CONTINUE\]|APPROVED\])[A-Z_][A-Z0-9_]*(?::[^\]]*)?\]",
            RegexOptions.Compiled);

        private static int FirstHallucinationIndex(string text)
        {
            int best = -1;
            string[] markers = { "\n**[Turn", "\n[Turn", "\n<|im_start|>", "<|im_start|>" };
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

        // ---- Prompt assembly (stateless) ----

        // Full reply prompt rebuilt from scratch every turn. Layout:
        //
        //   <|im_start|>system
        //   {GlobalBureauPreamble}        (lore + few-shot + anti-reintro)
        //   ACTIVE OFFICER: {persona}
        //   {ApprovalTagRules}            ([CONTINUE]/[APPROVED] only)
        //   INSTRUCTION FOR THIS RESPONSE:
        //   {directive prose}             (one of the 5 directives)
        //   Reply as the officer. ...
        //   <|im_end|>
        //   <|im_start|>user
        //   {playerName}: {turn 1 player text}
        //   <|im_end|>
        //   <|im_start|>assistant
        //   {turn 1 officer text}
        //   <|im_end|>
        //   ...
        //   <|im_start|>user
        //   {playerName}: {turn N player text}    <- most recent
        //   <|im_end|>
        //   <|im_start|>assistant
        //   ^^ model continues from here.
        //
        // Renders the full conversation transcript as proper chat-template
        // user/assistant blocks; the model sees its own prior replies as
        // structured assistant turns (improvement 5 from the prompt list).
        // No KV cache reuse; every turn is re-tokenized in full.
        private static string BuildReplyPrompt(ConversationState conv, TurnDirective directive)
        {
            var personaBlock = conv.Officer != null ? conv.Officer.ToPersonaBlock() : "(no persona)";

            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(SystemPrompts.GlobalBureauPreamble);
            sb.Append("\n\nACTIVE OFFICER:\n");
            sb.Append(personaBlock);
            sb.Append("\n\n");
            sb.Append(SystemPrompts.ApprovalTagRules);
            sb.Append("\n\nINSTRUCTION FOR THIS RESPONSE:\n");
            sb.Append(DirectiveText(directive));
            sb.Append("\n\nReply as the officer. Keep replies at 3-5 short paragraphs. End every reply with exactly one tag from the approval rules.");
            sb.Append("\n<|im_end|>\n");

            foreach (var entry in conv.Transcript)
            {
                bool isPlayer = entry.Speaker != null
                    && entry.Speaker.StartsWith("Player ", StringComparison.Ordinal);
                if (isPlayer)
                {
                    sb.Append("<|im_start|>user\n");
                    sb.Append(ExtractPlayerName(entry.Speaker));
                    sb.Append(": ");
                    sb.Append(entry.Text ?? string.Empty);
                }
                else
                {
                    sb.Append("<|im_start|>assistant\n");
                    sb.Append(entry.Text ?? string.Empty);
                }
                sb.Append("\n<|im_end|>\n");
            }

            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        // Classifier prompt: small system block, full transcript so far
        // (prior to the new message), then the new player message as the
        // input to classify. Model is asked to emit one label.
        private static string BuildClassifierPrompt(ConversationState conv, string playerName, string newMessage)
        {
            var transcriptSb = new StringBuilder();
            if (conv.Transcript.Count == 0)
            {
                transcriptSb.Append("(no prior messages)");
            }
            else
            {
                foreach (var e in conv.Transcript)
                {
                    transcriptSb.Append(e.Speaker).Append(": ").Append(e.Text).Append('\n');
                }
            }

            var body = SystemPrompts.ClassifierTemplate
                .Replace("{TRANSCRIPT}", transcriptSb.ToString().TrimEnd())
                .Replace("{NEW_MESSAGE}", (playerName ?? "Player") + ": " + (newMessage ?? string.Empty));

            var sb = new StringBuilder();
            sb.Append("<|im_start|>system\n");
            sb.Append(body);
            sb.Append("\n<|im_end|>\n");
            sb.Append("<|im_start|>assistant\n");
            return sb.ToString();
        }

        // ConversationState.AppendPlayer formats Speaker as "Player (<name>)".
        // Strip the "Player (" / ")" wrapper so the user-block content shows
        // just "<name>: <text>" — matches the chat-template convention the
        // model expects.
        private static string ExtractPlayerName(string speaker)
        {
            const string prefix = "Player (";
            if (speaker == null) return "Player";
            if (speaker.StartsWith(prefix, StringComparison.Ordinal) && speaker.EndsWith(")", StringComparison.Ordinal))
                return speaker.Substring(prefix.Length, speaker.Length - prefix.Length - 1);
            return speaker;
        }

        private static string DirectiveText(TurnDirective d)
        {
            switch (d)
            {
                case TurnDirective.StallHard:        return SystemPrompts.DirectiveStallHard;
                case TurnDirective.StallSoft:        return SystemPrompts.DirectiveStallSoft;
                case TurnDirective.ExtractDetail:    return SystemPrompts.DirectiveExtractDetail;
                case TurnDirective.MayAgree:         return SystemPrompts.DirectiveMayAgree;
                case TurnDirective.MustAgreeClosing: return SystemPrompts.DirectiveMustAgreeClosing;
                default:                              return SystemPrompts.DirectiveStallSoft;
            }
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
