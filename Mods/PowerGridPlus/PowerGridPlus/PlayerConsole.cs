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
    ///     Main thread only (the chat send touches Unity-side state, and the local
    ///     <c>PrintToConsole</c> shifts ConsoleWindow's unlocked 1024-entry static array while the
    ///     draw loop reads it). Every current caller already runs on the main thread: the two
    ///     VoltageTier burn paths and the stack-repair sweep (both marshalled via
    ///     UnityMainThreadDispatcher), the three WreckageCleanup sweep sites (dispatcher-enqueued),
    ///     and the WiringGuardPatches registration guard (a Cable.OnRegistered postfix, inherently
    ///     main-thread). There is no assertion enforcing this: a future worker-thread caller would
    ///     have its Unity exception swallowed by the catch below and surface only as a LogWarning.
    ///
    ///     No rate limiting. ConsoleWindow has none of its own and every print costs a full
    ///     1024-entry array shift, so capping is the caller's job. WreckageCleanup's AnnounceCap is
    ///     the pattern to copy.
    /// </summary>
    internal static class PlayerConsole
    {
        // Test seam: ScenarioRunner asserts the last broadcast text via reflection
        // (DedicatedServer/dev-plugins/ScenarioRunner, Dispatcher.MixedWireFixture.cs). Changing a
        // broadcast's text breaks those assertions. Written without synchronisation, which is fine
        // only because Broadcast is main-thread-only.
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
