namespace MaintenanceBureauPlus
{
    // Single canned auto-reply the bureau's ancillary systems issue when
    // a player chats while the active officer is mid-deliberation.
    // Sender is intentionally never the active officer; it's a machine,
    // furniture, or anonymous bureau infrastructure that exists only
    // to enforce turn-taking. Loaded from Resources/BusyResponses.md by
    // BusyResponseRegistry.
    public class BusyResponse
    {
        public string Sender;
        public string Text;
    }
}
