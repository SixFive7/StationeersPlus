using System;
using Assets.Scripts;
using Assets.Scripts.Networking;

namespace PowerGridPlus
{
    /// <summary>
    ///     Player-visible console broadcast for enforcement events (wrong-tier burns, refused
    ///     placements). Uses the vanilla chat channel: a <see cref="ChatMessage"/> with
    ///     <c>DisplayName "Server"</c> and <c>HumanId -1</c> prints to the local console and, when a
    ///     network server is up, replicates to every client's console (HumanId -1 suppresses the
    ///     floating chat bubble). The F3 console renders through ImGui, not TextMeshPro: rich-text
    ///     color tags do NOT render there and a dedicated server DROPS lines containing "&lt;color="
    ///     from its system console, so messages MUST be plain text
    ///     (Research/GameSystems/ChatBroadcast.md).
    ///
    ///     Main thread only (the chat send touches Unity-side state); every current caller
    ///     (VoltageTier burns, the registration guard) already runs on the main thread.
    /// </summary>
    internal static class PlayerConsole
    {
        // Test seam: ScenarioRunner asserts the last broadcast text via reflection.
        internal static string LastBroadcast;

        internal static void Broadcast(string message)
        {
            LastBroadcast = message;
            Plugin.Log?.LogInfo($"[PowerGridPlus] {message}");
            try
            {
                var chatMessage = new ChatMessage
                {
                    ChatText = message,
                    DisplayName = "Server",
                    HumanId = -1
                };
                chatMessage.PrintToConsole();
                if (NetworkManager.IsServer)
                    NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[PowerGridPlus] Console broadcast failed: {ex.Message}");
            }
        }
    }
}
