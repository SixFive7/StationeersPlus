using System;
using System.Collections;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using HarmonyLib;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    ///     Player routes. Everything here is a direct method call into the game rather than
    ///     synthetic input, which is why all of it kept working through the whole period when
    ///     <c>/input/*</c> was being discarded by the cursor gate. Prefer these when the test does
    ///     not specifically need to exercise the input path.
    /// </summary>
    internal static partial class Router
    {
        /// <summary>
        ///     Teleports the local player. Mirrors <c>Human.ForceSetPosition</c> but without its
        ///     <c>GameManager.RunSimulation</c> gate, which is false on a multiplayer client and
        ///     would make the call a silent no-op there.
        ///
        ///     Caveat worth knowing before relying on it: on a REMOTE client the server snaps the
        ///     body back within seconds. The local transform moves and the response is honest about
        ///     what it wrote, but the position does not stick. It rarely matters, because
        ///     <c>/player/use</c> addresses its target by reference id and
        ///     <c>OnServer.AttackWith</c> has no distance or line-of-sight gate: a stroke lands from
        ///     15 m away.
        /// </summary>
        private static HttpResponse Teleport(IDictionary body)
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            Vector3 current = human.ThingTransformPosition;
            Vector3 target = current;

            if (Json.Has(body, "position")) target = ReadVector(body, "position", current);
            else if (Json.Has(body, "x") || Json.Has(body, "y") || Json.Has(body, "z"))
                target = new Vector3(
                    Json.GetFloat(body, "x", current.x),
                    Json.GetFloat(body, "y", current.y),
                    Json.GetFloat(body, "z", current.z));
            if (Json.Has(body, "offset")) target = target + ReadVector(body, "offset", Vector3.zero);

            try
            {
                human.ThingTransformPosition = target;
                if (human.Transform != null) human.Transform.position = target;
                var rb = human.ActiveRigidbody;
                if (rb != null)
                {
                    rb.MovePosition(target);
                    if (!rb.isKinematic) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                }
                human.ResetInterpolation();
            }
            catch (Exception ex) { return HttpResponse.Error("teleport failed: " + ex.Message); }

            bool remoteClient = false;
            try { remoteClient = !GameManager.RunSimulation; } catch { }

            var o = new Json.Obj()
                .Bit("ok", true).Vec("from", current).Vec("to", human.ThingTransformPosition);
            if (remoteClient)
                o.Str("note", "this is a remote client, so the server will snap the body back within " +
                              "seconds. Address actions by targetId instead of relying on position.");
            return HttpResponse.Json(o.ToString());
        }

        /// <summary>
        ///     Sets the look direction. <c>CameraController.RotationX</c> is pitch with positive
        ///     meaning up, <c>RotationY</c> is yaw. <c>SetMouseLook</c> adds mouse delta to both
        ///     every LateUpdate, so a one-shot write holds only while the mouse is still: fine for
        ///     an unattended client, and exactly what <c>UnitTest_SetRotation</c> is for.
        /// </summary>
        private static HttpResponse Look(IDictionary body)
        {
            var cam = CameraController.Instance;
            if (cam == null) return Fail("no CameraController (not in a world)");

            float yaw = cam.RotationY;
            float pitch = cam.RotationX;

            if (Json.Has(body, "at"))
            {
                var human = Human.LocalHuman;
                if (human == null) return Fail("no local player");
                Vector3 at = ReadVector(body, "at", Vector3.zero);
                Vector3 origin = CameraController.CameraOrigin;
                Vector3 dir = (at - origin).normalized;
                yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            else
            {
                yaw = Json.GetFloat(body, "yaw", yaw);
                pitch = Json.GetFloat(body, "pitch", pitch);
            }

            pitch = Mathf.Clamp(pitch, -89f, 89f);

            try { cam.UnitTest_SetRotation(pitch, yaw); }
            catch (Exception ex) { return HttpResponse.Error("look failed: " + ex.Message); }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true).Flt("yaw", cam.RotationY).Flt("pitch", cam.RotationX).ToString());
        }

        /// <summary>
        ///     Uses the item in the active hand on a target Thing, through <c>OnServer.AttackWith</c>.
        ///     That is the same entry the game itself takes when the held item declares
        ///     <c>AttackWithEvent.Server</c>: predict locally, then send an <c>AttackWithMessage</c>.
        ///     It takes a reference id rather than requiring the cursor to be pointed at anything,
        ///     which is why it is the preferred way to drive an interaction: no aiming, no cursor
        ///     forcing, and no distance gate.
        /// </summary>
        private static HttpResponse UseOnTarget(IDictionary body)
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");

            var im = InventoryManager.Instance;
            if (im == null || im.ActiveHand == null || im.InactiveHand == null) return Fail("hands not initialised");

            Thing target = null;
            long targetId = Json.GetLong(body, "targetId", 0);
            if (targetId != 0) target = Thing.Find(targetId);
            else if (Json.GetBool(body, "cursor", true)) target = CursorManager.CursorThing;

            if (target == null) return Fail("no target: pass targetId, or aim the cursor at something first");

            float ratio = Json.GetFloat(body, "completedRatio", 1f);
            bool isDestroy = Json.GetBool(body, "destroy", false);
            bool isCopy = Json.GetBool(body, "copy", false);
            Vector3 point = Json.Has(body, "point")
                ? ReadVector(body, "point", target.ThingTransformPosition)
                : target.ThingTransformPosition;

            var held = InventoryManager.ActiveHandSlot?.Get();

            try
            {
                OnServer.AttackWith(
                    InventoryManager.Parent,
                    (byte)im.ActiveHand.SlotId,
                    (byte)im.InactiveHand.SlotId,
                    target.ReferenceId,
                    point,
                    ratio,
                    isDestroy,
                    isCopy);
            }
            catch (Exception ex)
            {
                return HttpResponse.Error("AttackWith failed: " + ex.Message);
            }

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("instance", InstanceManifest.Name)
                .Int("targetId", target.ReferenceId)
                .Str("targetPrefab", target.PrefabName)
                .Str("heldItem", held == null ? null : held.PrefabName)
                .Vec("point", point)
                .ToString());
        }

        private static HttpResponse SwapHands()
        {
            var human = Human.LocalHuman;
            if (human == null) return Fail("no local player");
            try
            {
                var behaviour = human.HumanHandsBehaviour;
                if (behaviour == null) return Fail("no HumanHandsBehaviour");
                var method = AccessTools.Method(behaviour.GetType(), "SwapHands");
                if (method == null) return Fail("SwapHands not found on " + behaviour.GetType().Name);
                method.Invoke(behaviour, null);
            }
            catch (Exception ex) { return HttpResponse.Error("swap hands failed: " + ex.Message); }
            return HttpResponse.Json(
                "{\"ok\":true,\"player\":" + StateReporter.PlayerJson() + "}");
        }
    }
}
