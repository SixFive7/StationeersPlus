using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using HarmonyLib;
using TerrainSystem;
using UnityEngine;

namespace TestRig
{
    /// <summary>
    ///     Cursor and screenshot routes. The modal and mod-settings routes are dispatched inline in
    ///     <see cref="Router.Handle"/> because they are one-liners over <see cref="Modal"/> and
    ///     <see cref="ModSettingsPanel"/>.
    /// </summary>
    internal static partial class Router
    {
        /// <summary>
        ///     Pins the cursor target. <c>CursorManager.SetCursorTarget</c> rebuilds
        ///     <c>FoundThing</c> from a raycast every frame, so a forced target only survives via
        ///     <see cref="CursorForcePatch"/>, which re-applies it in a postfix.
        ///
        ///     A forced target is only safe when it carries a collider, and this refuses a target it
        ///     cannot give one to. The reasoning is in <see cref="CursorForcePatch"/>; the summary is
        ///     that the cursor is a tuple, the pair {FoundThing = X, CursorTargetCollider = null} is
        ///     a state the game itself can never produce, and reaching it wedges the client
        ///     permanently rather than merely misbehaving.
        /// </summary>
        private static HttpResponse ForceCursor(IDictionary body)
        {
            if (Json.GetBool(body, "clear", false))
            {
                bool wasWedged = CursorForcePatch.Release();
                return HttpResponse.Json(new Json.Obj()
                    .Bit("ok", true).Bit("cleared", true)
                    .Bit("stateReset", wasWedged)
                    .Str("note", "FoundThing, CursorTargetCollider and FoundTerrain were written " +
                                 "directly; clearing the pin alone does not recover a stale cursor")
                    .ToString());
            }

            long id = Json.GetLong(body, "targetId", 0);
            if (id == 0) return HttpResponse.Error("missing 'targetId' (or pass clear=true)", 400);
            var thing = Thing.Find(id);
            if (thing == null) return Fail("no Thing with reference id " + id);

            var collider = CursorForcePatch.FindCollider(thing);
            if (collider == null)
                return Fail("Thing " + id + " (" + thing.PrefabName + ") exposes no collider, so a forced " +
                            "cursor on it would leave CursorTargetCollider null. That is the state that " +
                            "wedges GameManager.Update permanently (see Research/GameSystems/CursorManager.md). " +
                            "Refusing. Prefer /player/use with a targetId, or the server-side give-item scenario.");

            CursorForcePatch.Apply(thing, collider);
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Int("targetId", id).Str("prefabName", thing.PrefabName)
                .Str("collider", collider.name)
                .Str("colliderType", collider.GetType().Name)
                .Bit("isSlotCollider", CursorForcePatch.IsSlotCollider(thing, collider))
                .ToString());
        }

        private static HttpResponse TakeScreenshot(IDictionary body)
        {
            int superSize = Math.Max(1, Json.GetInt(body, "supersize", 1));
            int maxWidth = Json.GetInt(body, "maxWidth", 1920);
            int timeoutMs = Json.GetInt(body, "timeoutMs", 30000);
            string path = Json.GetStr(body, "path");
            bool inline = Json.GetBool(body, "inline", string.IsNullOrEmpty(path));

            string error;
            int w, h;
            var png = Screenshot.CapturePng(superSize, maxWidth, timeoutMs, out error, out w, out h);
            if (png == null) return HttpResponse.Error(error ?? "screenshot produced no bytes");

            string written = null;
            if (!string.IsNullOrEmpty(path))
            {
                try { written = Screenshot.WriteToDisk(png, path); }
                catch (Exception ex) { return HttpResponse.Error("wrote no file: " + ex.Message); }
            }

            if (inline) return HttpResponse.Png(png);

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Str("instance", InstanceManifest.Name)
                .Str("path", written).Int("bytes", png.Length)
                .Int("width", w).Int("height", h).ToString());
        }
    }

    /// <summary>
    ///     Re-applies a forced cursor target after the game's own raycast has run.
    ///     <c>CursorManager.SetCursorTarget</c> overwrites <c>FoundThing</c> every frame from
    ///     <c>ManagerUpdate</c>, so nothing short of a postfix can hold a pin.
    ///
    ///     The cursor is a tuple and this pins all of it. An earlier version wrote only
    ///     <c>FoundThing</c> and left <c>CursorTargetCollider</c> at whatever the raycast had just
    ///     produced, which is null on a miss and null whenever the console is open. The pair
    ///     {FoundThing = X, CursorTargetCollider = null} is a state the game itself can never
    ///     produce, and <c>PlantAnalyserCartridge.GetScannedPlant</c> walks straight into it:
    ///     <c>Thing.GetSlot(null)</c> hits <c>Dictionary.TryGetValue(null)</c> and throws.
    ///
    ///     That throw is unrecoverable, which is the part worth understanding before touching this
    ///     class. The cartridge runs from <c>GameManager.Update</c> (the
    ///     <c>OcclusionManager.UpdatingThings.ForEach</c> pass) and
    ///     <c>CursorManager.ManagerUpdate</c>, the only caller of <c>SetCursorTarget</c>, runs later
    ///     in the same method with no try/catch between them. So the exception aborts the frame
    ///     before the cursor can be rebuilt, the stale FoundThing survives, and it throws again next
    ///     frame, forever. <c>NetworkManager.ManagerUpdate</c> is in the same loop, so a wedged
    ///     client also stops processing network packets. Measured at 100 exceptions per 6 seconds;
    ///     only leaving the world recovered it.
    ///
    ///     Two consequences shape the code below:
    ///
    ///       1. Never pin without a collider. <see cref="FindCollider"/> prefers a collider that is
    ///          actually a key in the target's slot lookup, so <c>GetSlot</c> returns a real Slot
    ///          instead of merely not throwing. The route refuses the request when nothing can be
    ///          found.
    ///       2. Clearing has to write the game's fields itself. Setting <c>Forced</c> to null only
    ///          stops the postfix re-applying; it cannot help when the reason the cursor is stale is
    ///          that <c>SetCursorTarget</c> is no longer reachable. <see cref="Release"/> therefore
    ///          assigns FoundThing, CursorTargetCollider and FoundTerrain directly. That still lands
    ///          while wedged, because this plugin's pump is a separate MonoBehaviour plus an
    ///          <c>ImGuiManager.LateUpdate</c> postfix, neither of which is downstream of the
    ///          aborted <c>GameManager.Update</c>.
    ///
    ///     <c>FoundTerrain</c> is pinned to Invalid deliberately:
    ///     <c>CursorManager.GetCurrentVoxelWorld</c> hard-casts <c>CursorTargetCollider</c> to
    ///     BoxCollider and is guarded only by <c>CursorTerrain.IsValid</c>, so a valid terrain paired
    ///     with a non-box collider is a second way to throw out of the same loop.
    ///
    ///     Prefer not to need any of this. <c>/player/use</c> with a targetId, and the server-side
    ///     give-item scenario, both do their job without involving the cursor.
    /// </summary>
    [HarmonyPatch]
    internal static class CursorForcePatch
    {
        internal static Thing Forced;
        private static Collider _forcedCollider;

        internal static MethodBase TargetMethod() => AccessTools.Method(typeof(CursorManager), "SetCursorTarget");

        internal static bool Prepare() => Plugin.ClientOnlyPatches && TargetMethod() != null;

        internal static void Postfix(CursorManager __instance)
        {
            var forced = Forced;
            if (forced == null) return;
            try
            {
                var collider = _forcedCollider;

                // The target can be destroyed while pinned (painting a thing that then gets
                // deconstructed, an item consumed). Unpin rather than hold a dead reference, and
                // let the vanilla state stand.
                if (collider == null || !forced)
                {
                    Release();
                    return;
                }

                __instance.FoundThing = forced;
                __instance.CursorTargetCollider = collider;
                __instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch
            {
                // A throw here would land inside the same GameManager.Update loop this whole class
                // exists to keep alive, so give up the pin instead.
                Release();
            }
        }

        /// <summary>Pins a target and its collider together. Both or neither.</summary>
        internal static void Apply(Thing thing, Collider collider)
        {
            _forcedCollider = collider;
            Forced = thing;

            // Write once immediately so a caller that never reaches another SetCursorTarget still
            // sees a consistent tuple.
            var instance = CursorManager.Instance;
            if (instance == null) return;
            try
            {
                instance.FoundThing = thing;
                instance.CursorTargetCollider = collider;
                instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch { }
        }

        /// <summary>
        ///     Drops the pin and puts the game's own cursor fields back to the empty state, so this
        ///     recovers a client whose SetCursorTarget is unreachable. Returns true when there was
        ///     something to reset.
        /// </summary>
        internal static bool Release()
        {
            bool had = Forced != null;
            Forced = null;
            _forcedCollider = null;

            var instance = CursorManager.Instance;
            if (instance == null) return had;
            try
            {
                if (instance.FoundThing != null) had = true;
                instance.FoundThing = null;
                instance.CursorTargetCollider = null;
                instance.FoundTerrain = CursorTerrain.Invalid;
            }
            catch { }
            return had;
        }

        /// <summary>
        ///     Best collider to report for a Thing, most faithful first.
        ///
        ///     A Slot collider is the only kind that is a key in the target's slot lookup, so it is
        ///     the only one that makes <c>GetSlot(collider)</c> return something rather than merely
        ///     not throw. Everything after it is a structurally valid stand-in.
        /// </summary>
        internal static Collider FindCollider(Thing thing)
        {
            if (thing == null) return null;
            try
            {
                if (thing.Slots != null)
                    foreach (var slot in thing.Slots)
                        if (slot != null && slot.Collider != null && slot.IsInteractable)
                            return slot.Collider;

                var fromList = First(thing._selfColliders) ?? First(thing._staticColliders) ?? First(thing._dynamicColliders);
                if (fromList != null) return fromList;

                return thing.GetComponentInChildren<Collider>();
            }
            catch { return null; }
        }

        internal static bool IsSlotCollider(Thing thing, Collider collider)
        {
            try
            {
                if (thing?.Slots == null || collider == null) return false;
                foreach (var slot in thing.Slots)
                    if (slot != null && ReferenceEquals(slot.Collider, collider)) return true;
            }
            catch { }
            return false;
        }

        private static Collider First(List<Collider> list)
        {
            if (list == null) return null;
            foreach (var c in list)
                if (c != null) return c;
            return null;
        }
    }
}
