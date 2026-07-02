using System;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;

namespace PowerGridPlus
{
    /// <summary>
    ///     Soft (reflection) bridge to PowerTransmitterPlus. PowerTransmitterPlus does NOT derate a
    ///     microwave link's delivered power for distance; instead it inflates the transmitter's
    ///     INPUT-side draw by a factor m = 1 + k * distance_km (POWER.md §6.3). The allocator must
    ///     know m to present the real input demand (delivered * m) on the source network and to
    ///     bound the input cable, or a long link on a constrained source network silently brown-outs
    ///     that network (POWER.md §8.4.2). It also needs the link's static delivery rating (the
    ///     configured Max Transfer Capacity) to size the PT-pair seg.
    ///
    ///     <para>Resolution tiers, probed ONCE at first use (everything cached; no per-tick
    ///     reflection). PowerGridPlus never hard-references PowerTransmitterPlus, so this stays an
    ///     optional dependency:</para>
    ///
    ///     <list type="number">
    ///       <item><b>ModApi</b> (PowerTransmitterPlus 1.9.0+): the public, versioned cross-mod
    ///       surface <c>PowerTransmitterPlus.ModApi</c> (requires <c>Version >= 1</c>). Binds
    ///       EffectiveMaxCapacity, TryGetLink, SourceDrawMultiplier, and ClaimBillingOwnership, then
    ///       claims wireless billing ownership as "net.powergridplus": while the claim is held,
    ///       PowerTransmitterPlus's own debt billing (UsePower debt inflation + GetUsedPower cap
    ///       lift + standalone debt ceiling) stands down and this mod's allocator is the single
    ///       billing authority; the capacity advertise, receiver drain-cap lift, beam visuals, and
    ///       link handling stay active on the PowerTransmitterPlus side. The claim is
    ///       per-call-checked over there, so resolving lazily (first GATHER that meets a linked
    ///       transmitter, always before that tick's ENFORCE can bill) is safe.</item>
    ///       <item><b>Legacy</b> (Workshop 1.8.0 line): <c>DistanceCostShared.SourceDrawMultiplier</c>
    ///       (public wrapper, added after the 1.8.0 tag) or the internal <c>GetMultiplier</c> (the
    ///       identical computation; the shipped Workshop build has only this), plus
    ///       <c>MaxCapacityConfigSync.GetEffectiveMaxCapacity</c> and the vanilla
    ///       <c>_linkedReceiverDistance</c> field. No ownership handshake exists at this tier;
    ///       PowerGridPlus's DeliveryGatePatch + fresh-Pull prefixes keep the ledgers bounded
    ///       instead, exactly as before the ModApi existed.</item>
    ///       <item><b>Absent</b>: vanilla wireless model. m = 1, and the caller falls back to the
    ///       vanilla transmission rating (MaxPowerTransmission minus the distance-delivery loss,
    ///       fed by <see cref="LinkedReceiverDistance"/>).</item>
    ///     </list>
    ///
    ///     <para>The two tiers agree by construction: ModApi.EffectiveMaxCapacity forwards to the
    ///     same MaxCapacityConfigSync method the legacy tier binds, and ModApi.SourceDrawMultiplier
    ///     forwards to the same DistanceCostShared.GetMultiplier for a linked transmitter (the
    ///     allocator only asks about linked pairs), so switching tiers never changes the allocator's
    ///     numbers for the same world state.</para>
    ///
    ///     <para>Threading: first use happens inside GATHER on the UniTask power worker, the only
    ///     caller of these methods, so the lazy one-shot resolve needs no lock. ModApi members are
    ///     documented thread-safe. Any failure at any step degrades one tier and never throws.</para>
    /// </summary>
    internal static class PowerTransmitterPlusInterop
    {
        /// <summary>Ownership identity presented to PowerTransmitterPlus (this plugin's GUID).</summary>
        private const string OwnerId = "net.powergridplus";

        private delegate bool TryGetLinkFn(PowerTransmitter transmitter, out float distanceMeters);

        private static bool _resolved;

        // ModApi tier (all bound together, or none).
        private static int _modApiVersion;
        private static Func<PowerTransmitter, float> _modMultiplier;
        private static Func<float> _modEffCap;
        private static TryGetLinkFn _modTryGetLink;

        // Legacy tier.
        private static Func<PowerTransmitter, float> _legacyMultiplier;
        private static string _legacyMultiplierName;
        private static Func<float> _legacyEffCap;
        private static FieldInfo _legacyDistField;

        /// <summary>
        ///     The factor by which a transmitter's input-network draw exceeds its delivered output
        ///     (>= 1). Returns 1 when PowerTransmitterPlus is absent or the link has no distance
        ///     overhead. Never throws.
        /// </summary>
        internal static float SourceDrawMultiplier(PowerTransmitter pt)
        {
            if (pt == null) return 1f;
            EnsureResolved();
            var fn = _modMultiplier ?? _legacyMultiplier;
            if (fn == null) return 1f;
            float m;
            try { m = fn(pt); }
            catch { return 1f; }
            if (float.IsNaN(m) || float.IsInfinity(m) || m < 1f) return 1f;
            return m;
        }

        /// <summary>
        ///     PowerTransmitterPlus's effective Max Transfer Capacity in watts (0 = unlimited), or a
        ///     negative sentinel (-1) when PowerTransmitterPlus is not loaded, so the caller falls back
        ///     to the vanilla transmission rating. Never throws.
        /// </summary>
        internal static float EffectiveMaxCapacityOrAbsent()
        {
            EnsureResolved();
            var fn = _modEffCap ?? _legacyEffCap;
            if (fn == null) return -1f;
            float c;
            try { c = fn(); }
            catch { return -1f; }
            if (float.IsNaN(c) || c < 0f) return 0f;   // garbage -> treat as unlimited (0)
            return c;
        }

        /// <summary>
        ///     The transmitter's linked-receiver distance in metres, used only for the vanilla
        ///     rating's distance derate when PowerTransmitterPlus is absent. Prefers ModApi.TryGetLink
        ///     when bound (0 for an unlinked transmitter: the stale cached distance a dropped link
        ///     leaves behind never surfaces); falls back to the vanilla private field
        ///     <c>_linkedReceiverDistance</c>. Returns 0 on any failure. Never throws.
        /// </summary>
        internal static float LinkedReceiverDistance(PowerTransmitter pt)
        {
            if (pt == null) return 0f;
            EnsureResolved();
            if (_modTryGetLink != null)
            {
                try
                {
                    if (_modTryGetLink(pt, out float d))
                        return !float.IsNaN(d) && d >= 0f ? d : 0f;
                    return 0f;
                }
                catch { return 0f; }
            }
            if (_legacyDistField == null) return 0f;
            try
            {
                var v = _legacyDistField.GetValue(pt);
                return v is float f && !float.IsNaN(f) && f >= 0f ? f : 0f;
            }
            catch { return 0f; }
        }

        // ------------------------------------------------------------------
        // One-shot resolution.
        // ------------------------------------------------------------------

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;   // resolve once; plugin load completes before any tick runs

            bool claimGranted = false;
            try { claimGranted = ResolveModApi(); }
            catch
            {
                _modApiVersion = 0;
                _modMultiplier = null;
                _modEffCap = null;
                _modTryGetLink = null;
            }
            if (_modMultiplier == null)
            {
                try { ResolveLegacy(); }
                catch
                {
                    _legacyMultiplier = null;
                    _legacyEffCap = null;
                    _legacyDistField = null;
                }
            }

            // Exactly one Info line stating which tier resolved (and, on the ModApi tier, the
            // outcome of the one-time billing ownership claim).
            if (_modMultiplier != null)
            {
                Plugin.Log?.LogInfo(
                    $"PowerTransmitterPlus interop: ModApi v{_modApiVersion} resolved; billing ownership claim ('{OwnerId}') "
                    + (claimGranted
                        ? "granted, PowerTransmitterPlus native wireless debt billing stands down."
                        : "REJECTED (another owner holds it); relying on the delivery-gate/fresh-pull patches instead."));
            }
            else if (_legacyMultiplier != null)
            {
                Plugin.Log?.LogInfo(
                    $"PowerTransmitterPlus interop: legacy tier via {_legacyMultiplierName} (pre-ModApi build, e.g. Workshop 1.8.0); "
                    + "modelling transmitter distance draw overhead, no ownership handshake.");
            }
            else
            {
                Plugin.Log?.LogInfo(
                    "PowerTransmitterPlus interop: absent; vanilla wireless model (m = 1, vanilla link rating).");
            }
        }

        /// <summary>
        ///     Bind the ModApi tier. Returns the billing-ownership claim outcome (only meaningful
        ///     when the tier bound; the tier is all-or-nothing, so a partially-present surface --
        ///     which the ModApi stability contract rules out anyway -- falls through to legacy).
        /// </summary>
        private static bool ResolveModApi()
        {
            var type = Type.GetType("PowerTransmitterPlus.ModApi, PowerTransmitterPlus", false);
            if (type == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType("PowerTransmitterPlus.ModApi", false);
                    if (type != null) break;
                }
            }
            if (type == null) return false;

            var versionField = type.GetField("Version", BindingFlags.Public | BindingFlags.Static);
            if (versionField == null) return false;
            object raw = versionField.IsLiteral ? versionField.GetRawConstantValue() : versionField.GetValue(null);
            if (!(raw is int version) || version < 1) return false;

            var effCap = type.GetMethod("EffectiveMaxCapacity",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            var tryGetLink = type.GetMethod("TryGetLink",
                BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(PowerTransmitter), typeof(float).MakeByRefType() }, null);
            var multiplier = type.GetMethod("SourceDrawMultiplier",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(PowerTransmitter) }, null);
            var claim = type.GetMethod("ClaimBillingOwnership",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (effCap == null || tryGetLink == null || multiplier == null || claim == null) return false;
            if (effCap.ReturnType != typeof(float) || multiplier.ReturnType != typeof(float)
                || tryGetLink.ReturnType != typeof(bool) || claim.ReturnType != typeof(bool)) return false;

            var boundEffCap = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), effCap);
            var boundTryGetLink = (TryGetLinkFn)Delegate.CreateDelegate(typeof(TryGetLinkFn), tryGetLink);
            var boundMultiplier = (Func<PowerTransmitter, float>)
                Delegate.CreateDelegate(typeof(Func<PowerTransmitter, float>), multiplier);
            var boundClaim = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), claim);

            // Commit the tier only after every member bound, then claim billing ownership exactly
            // once. A rejected claim (another mod holds it) still leaves the read surface bound.
            _modApiVersion = version;
            _modEffCap = boundEffCap;
            _modTryGetLink = boundTryGetLink;
            _modMultiplier = boundMultiplier;
            bool granted;
            try { granted = boundClaim(OwnerId); }
            catch { granted = false; }
            return granted;
        }

        /// <summary>
        ///     Bind the legacy tier, unchanged from the pre-ModApi interop so the shipped Workshop
        ///     PowerTransmitterPlus 1.8.0 keeps working exactly as before. Prefer the public
        ///     SourceDrawMultiplier wrapper (forward-compatible; added after the 1.8.0 tag), else the
        ///     internal GetMultiplier -- the identical computation. Without the GetMultiplier
        ///     fallback the reflection fails against the shipped build and the allocator silently
        ///     bills distance links at m = 1 while PowerTransmitterPlus inflates the _powerProvided
        ///     debt at the true m, running the source debt away on every long link.
        /// </summary>
        private static void ResolveLegacy()
        {
            Type shared = null;
            Type capSync = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (shared == null) shared = asm.GetType("PowerTransmitterPlus.DistanceCostShared", false);
                if (capSync == null) capSync = asm.GetType("PowerTransmitterPlus.MaxCapacityConfigSync", false);
                if (shared != null && capSync != null) break;
            }

            var mi = shared?.GetMethod(
                "SourceDrawMultiplier",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(PowerTransmitter) },
                null);
            if (mi == null)
                mi = shared?.GetMethod(
                    "GetMultiplier",
                    BindingFlags.NonPublic | BindingFlags.Static,
                    null,
                    new[] { typeof(PowerTransmitter) },
                    null);
            if (mi != null)
            {
                _legacyMultiplier = (Func<PowerTransmitter, float>)
                    Delegate.CreateDelegate(typeof(Func<PowerTransmitter, float>), mi);
                _legacyMultiplierName = mi.IsPublic ? "public SourceDrawMultiplier" : "internal GetMultiplier";
            }

            var capMi = capSync?.GetMethod(
                "GetEffectiveMaxCapacity",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                null,
                Type.EmptyTypes,
                null);
            if (capMi != null && capMi.ReturnType == typeof(float))
                _legacyEffCap = (Func<float>)Delegate.CreateDelegate(typeof(Func<float>), capMi);

            try
            {
                _legacyDistField = typeof(PowerTransmitter).GetField(
                    "_linkedReceiverDistance", BindingFlags.NonPublic | BindingFlags.Instance);
            }
            catch { _legacyDistField = null; }
        }
    }
}
