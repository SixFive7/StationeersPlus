using System;
using System.Collections;
using System.Collections.Generic;

namespace ClientDriver
{
    /// <summary>
    ///     The per-process DLC entitlement override. Three routes, and none of them can grant.
    ///
    ///     <list type="bullet">
    ///       <item><c>GET /dlc</c> reports what this process owns, what the session pool holds, what
    ///       has been removed, and the ordering a removal has to be sequenced into.</item>
    ///       <item><c>POST /dlc/remove</c> takes entitlement away.</item>
    ///       <item><c>POST /dlc/restore</c> puts back the baseline this process held before its
    ///       first removal.</item>
    ///     </list>
    ///
    ///     The removal-only guarantee is structural and lives in <see cref="DlcEntitlement"/>, which
    ///     spells out the five things that make it a property of the code. The route layer adds the
    ///     sixth: a request carrying an add-shaped field is REFUSED rather than ignored, so a caller
    ///     who assumed the endpoint was symmetric is told so instead of silently getting a removal
    ///     they did not ask for, or nothing at all.
    ///
    ///     What this unlocks: one Steam account can stage a DLC owner and a non-owner, which three
    ///     documents in this repository currently record as impossible. It is not; see
    ///     <c>Research/GameSystems/DLCGating.md</c>. Sequencing matters and the endpoint carries it,
    ///     because a removal applied after world entry is silently undone by the game's own
    ///     re-seeding and looks exactly like one that worked.
    /// </summary>
    internal static partial class Router
    {
        /// <summary>
        ///     Request fields that would only make sense on an endpoint that could grant. Present in
        ///     a request, they mean the caller expected a symmetric API; answering 400 with the
        ///     reason is the honest response, and it is what makes the removal-only intent visible
        ///     at the moment somebody assumes otherwise.
        /// </summary>
        private static readonly string[] GrantShapedFields =
        {
            "add", "grant", "give", "set", "own", "owned", "enable", "unlock", "value",
        };

        private static HttpResponse DlcRemoveRoute(IDictionary body)
        {
            foreach (string forbidden in GrantShapedFields)
            {
                if (!Json.Has(body, forbidden)) continue;
                return HttpResponse.Error(
                    "this endpoint is REMOVAL ONLY and '" + forbidden + "' is not a field it has. It " +
                    "cannot add entitlement to this process by any route, parameter or value: the only " +
                    "write it performs clears bits out of the value that is already there. Name what to " +
                    "take away in 'dlc' (a DLCType name, several comma-separated, 'all', or a numeric " +
                    "mask) and optionally 'scope' (owned | shared | both). Nothing was changed.", 400);
            }

            string spec = Json.GetStr(body, "dlc");
            if (string.IsNullOrEmpty(spec)) spec = Json.GetStr(body, "remove");
            if (string.IsNullOrEmpty(spec))
                return HttpResponse.Error(
                    "missing 'dlc': name the entitlement to REMOVE from this process. Accepts a DLCType " +
                    "name, several comma-separated, 'all', or a numeric mask. GET /dlc lists the names " +
                    "this game version knows.", 400);

            int mask;
            string parseError;
            if (!DlcEntitlement.TryParseMask(spec, out mask, out parseError))
                return HttpResponse.Error(parseError, 400);

            string scope = (Json.GetStr(body, "scope", "both") ?? "both").Trim().ToLowerInvariant();
            bool doOwned, doShared;
            switch (scope)
            {
                case "both": doOwned = true; doShared = true; break;
                case "owned": case "local": case "own": doOwned = true; doShared = false; break;
                case "shared": case "pool": case "session": doOwned = false; doShared = true; break;
                default:
                    return HttpResponse.Error(
                        "scope '" + scope + "' is not one of owned, shared or both. 'owned' is this " +
                        "process's own DLCManager entitlement; 'shared' is SharedDLCManager's " +
                        "session-wide union. Nothing was changed.", 400);
            }

            bool ok;
            string payload = DlcEntitlement.Remove(mask, doOwned, doShared, out ok);
            return HttpResponse.Json(payload, ok ? 200 : 409);
        }

        private static HttpResponse DlcRestoreRoute(IDictionary body)
        {
            foreach (string forbidden in GrantShapedFields)
            {
                if (!Json.Has(body, forbidden)) continue;
                return HttpResponse.Error(
                    "'" + forbidden + "' is not a field this endpoint has. Restore takes no arguments: " +
                    "it puts back the baseline captured from this process's own live state before its " +
                    "first removal, and there is no value a caller can name that it would write. " +
                    "Nothing was changed.", 400);
            }

            bool ok;
            string payload = DlcEntitlement.Restore(out ok);
            return HttpResponse.Json(payload, ok ? 200 : 409);
        }
    }
}
