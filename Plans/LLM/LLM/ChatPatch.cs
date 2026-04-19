using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using System;
using System.Collections.Concurrent;

namespace LLM
{
    /// <summary>
    /// Harmony Postfix on ChatMessage.Process(). When a player sends a message
    /// matching the trigger prefix, the text is forwarded to the LLM engine.
    /// The response arrives asynchronously and is dispatched to all clients
    /// from the main thread via DrainResponses().
    /// </summary>
    [HarmonyPatch(typeof(ChatMessage), nameof(ChatMessage.Process))]
    public static class ChatPatch
    {
        // Responses from the inference thread arrive here; Update() drains them.
        private static readonly ConcurrentQueue<string> PendingResponses =
            new ConcurrentQueue<string>();

        /// <summary>
        /// Fires after the game's own ChatMessage.Process() completes (console
        /// print + broadcast already done). We only act on the server.
        /// </summary>
        public static void Postfix(ChatMessage __instance)
        {
            // Only the server should trigger inference
            if (!NetworkManager.IsServer)
                return;

            if (LlmPlugin.Engine == null || !LlmPlugin.Engine.IsLoaded)
                return;

            var playerName = __instance.DisplayName;
            var message = __instance.ChatText;

            // Ignore messages from ourselves to prevent loops
            var botName = LlmPlugin.BotName.Value;
            if (playerName.Equals(botName, StringComparison.OrdinalIgnoreCase))
                return;

            var prefix = LlmPlugin.TriggerPrefix.Value;

            // If a prefix is configured, only respond to messages that start with it
            if (!string.IsNullOrEmpty(prefix))
            {
                if (!message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return;

                // Strip the prefix before sending to the model
                message = message.Substring(prefix.Length).Trim();
                if (string.IsNullOrEmpty(message))
                    return;
            }

            LlmPlugin.Log.LogInfo($"[{botName}] Processing request from {playerName}: {message}");

            // Fire-and-forget on the inference thread; result queued for main thread
            LlmPlugin.Engine.Enqueue(playerName, message, response =>
            {
                LlmPlugin.Log.LogInfo($"[{botName}] Response ready: {response}");
                PendingResponses.Enqueue(response);
            });
        }

        /// <summary>
        /// Called from Plugin.Update() on the main thread to dispatch queued
        /// LLM responses into the game's chat system.
        /// </summary>
        public static void DrainResponses()
        {
            while (PendingResponses.TryDequeue(out var response))
            {
                SendBotMessage(response);
            }
        }

        /// <summary>
        /// Sends a chat message as the bot. Must run on the main thread.
        /// Follows the same pattern as SayCommand.Say() for server/batch mode:
        /// construct a ChatMessage, print to server console, broadcast to clients.
        /// </summary>
        private static void SendBotMessage(string response)
        {
            var botName = LlmPlugin.BotName.Value;

            try
            {
                var chatMessage = new ChatMessage
                {
                    ChatText = response,
                    DisplayName = botName,
                    HumanId = -1 // no human entity; message appears in chat log only
                };

                // Print to the server's own console
                chatMessage.PrintToConsole();

                // Broadcast to all connected clients
                NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
            }
            catch (Exception e)
            {
                LlmPlugin.Log.LogError($"Failed to send bot message: {e.Message}");
            }
        }
    }
}
