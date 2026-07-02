using Assets.Scripts.Objects.Electrical;

namespace PowerTransmitterPlus
{
    // Public cross-mod API. Everything another mod needs to cooperate with
    // Power Transmitter Plus lives here. Resolve the type by name
    // ("PowerTransmitterPlus.ModApi, PowerTransmitterPlus") and call the
    // static members via reflection, or reference the assembly directly.
    //
    // Stability contract: members are only added, never renamed, retyped, or
    // removed, and Version is bumped on every addition so callers can gate
    // features on a minimum level instead of probing individual members.
    // Version 1 is the initial surface (mod v1.9.0).
    //
    // Threading: all members are safe to call from any thread. The billing
    // patches read BillingOwner on the power-tick ThreadPool worker, so the
    // owner field is volatile and claim / release serialize on a lock. A
    // claim is valid at any time, including before this plugin's own Awake
    // has run (plugin load order between mods is nondeterministic); the
    // billing patches check ownership per call, so a late claim simply takes
    // effect on the next power tick.
    public static class ModApi
    {
        // Bumped on every addition to this surface.
        public const int Version = 1;

        private static readonly object OwnershipLock = new object();
        private static volatile string _billingOwner;

        // Identity of the mod currently holding wireless billing ownership,
        // or null when Power Transmitter Plus's own debt billing is active.
        public static string BillingOwner => _billingOwner;

        // Effective per-transmitter delivery cap in watts. 0 = unlimited.
        // Host-authoritative: on a multiplayer client this returns the value
        // the host pushed, matching what the simulation actually enforces.
        public static float EffectiveMaxCapacity() =>
            MaxCapacityConfigSync.GetEffectiveMaxCapacity();

        // True, with the live link distance in meters, when the transmitter
        // is currently linked to a receiver; false (and 0) when the
        // transmitter is null or unlinked. Never surfaces the stale distance
        // a transmitter keeps cached after its link drops.
        public static bool TryGetLink(PowerTransmitter transmitter, out float distanceMeters)
        {
            distanceMeters = 0f;
            if (transmitter == null || transmitter.LinkedReceiver == null) return false;
            var field = DistanceCostShared.LinkedDistanceField;
            if (field == null) return false;
            distanceMeters = (float)field.GetValue(transmitter);
            return true;
        }

        // Source-draw multiplier m for a transmitter: the source pays m watts
        // per watt delivered, m = 1 + k * distance_m / 1000 (k is the
        // host-authoritative Cost Factor). Returns exactly 1 when the
        // transmitter is null or unlinked, so the stale cached distance left
        // behind by a dropped link never leaks into the multiplier.
        public static float SourceDrawMultiplier(PowerTransmitter transmitter)
        {
            if (transmitter == null || transmitter.LinkedReceiver == null) return 1f;
            return DistanceCostShared.GetMultiplier(transmitter);
        }

        // Read the private _powerProvided transfer-debt accumulator of either
        // half of a wireless link (PowerTransmitter or PowerReceiver; the two
        // classes each declare their own field). Returns 0 for null or
        // unsupported instances.
        public static float GetTransferDebt(WirelessPower half)
        {
            var field = DistanceCostShared.PowerProvidedFieldFor(half);
            if (field == null) return 0f;
            return (float)field.GetValue(half);
        }

        // Overwrite the private _powerProvided transfer-debt accumulator of
        // either half of a wireless link. No-op for null or unsupported
        // instances. Intended for allocator mods that settle the ledger
        // themselves while holding billing ownership.
        public static void SetTransferDebt(WirelessPower half, float value)
        {
            var field = DistanceCostShared.PowerProvidedFieldFor(half);
            if (field == null) return;
            field.SetValue(half, value);
        }

        // Claim exclusive wireless billing ownership. While a claim is held,
        // Power Transmitter Plus's own debt billing (the UsePower debt
        // inflation and the GetUsedPower cap lift) and its standalone debt
        // ceiling stand down; the capacity advertise, the receiver drain-cap
        // lift, beam visuals, link handling, and logic readouts stay active.
        // Returns true when ownerId now holds the claim (re-claiming the same
        // id is idempotent) and false when a different owner already holds
        // it. ownerId must be a non-empty stable identifier, for example the
        // claiming plugin's GUID.
        public static bool ClaimBillingOwnership(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return false;
            lock (OwnershipLock)
            {
                if (_billingOwner != null && _billingOwner != ownerId)
                {
                    PowerTransmitterPlusPlugin.Log?.LogWarning(
                        $"PowerTransmitterPlus: billing ownership claim by '{ownerId}' rejected; '{_billingOwner}' already holds it");
                    return false;
                }
                if (_billingOwner == ownerId) return true;
                _billingOwner = ownerId;
                PowerTransmitterPlusPlugin.Log?.LogInfo(
                    $"PowerTransmitterPlus: billing ownership claimed by '{ownerId}'; native wireless debt billing is dormant while the claim is held");
                return true;
            }
        }

        // Release a claim previously taken with ClaimBillingOwnership. Only
        // the current owner can release; any other id is a no-op.
        public static void ReleaseBillingOwnership(string ownerId)
        {
            if (string.IsNullOrEmpty(ownerId)) return;
            lock (OwnershipLock)
            {
                if (_billingOwner != ownerId) return;
                _billingOwner = null;
                PowerTransmitterPlusPlugin.Log?.LogInfo(
                    $"PowerTransmitterPlus: billing ownership released by '{ownerId}'; native wireless debt billing re-enabled");
            }
        }
    }
}
