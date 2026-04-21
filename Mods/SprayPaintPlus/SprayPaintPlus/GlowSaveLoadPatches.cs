using System.Reflection;
using Assets.Scripts.Objects;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    // Save/load patches for per-Thing glow state.
    //
    // SerializeSave: if the Thing is glowing, upgrade the vanilla
    // ThingSaveData to GlowThingSaveData so the flag persists. Non-glowing
    // Things skip the upgrade, so saves do not pay a byte per Thing in the
    // world.
    //
    // DeserializeSave: if the incoming saveData is GlowThingSaveData, restore
    // IsGlowing and re-apply emissive via SetCustomColor(index, true).
    //
    // Both target Thing.SerializeSave / DeserializeSave. Subclasses that
    // inherit these methods (every paintable structure, spray-can, etc.)
    // pass through the base postfix. Subclasses that override WITHOUT
    // calling base will miss this upgrade; for those, a TargetMethod
    // pattern per Research/Patterns/HarmonyInheritedMethodTrap.md is needed,
    // but every common paintable (Pipe, Wall, Cable, Frame, Structure) uses
    // the inherited path.
    //
    // See Research/Patterns/SaveDataIsinstInheritance.md for the inheritance
    // rule and Research/GameSystems/SaveDataRegistration.md for the
    // registration pattern used in Plugin.cs.

    [HarmonyPatch(typeof(Thing), nameof(Thing.SerializeSave))]
    public class ThingSerializeSaveGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, ref ThingSaveData __result)
        {
            if (__result == null) return;
            if (!GlowPaintHelpers.IsGlowing(__instance)) return;
            if (__result is GlowThingSaveData) return;

            // Vanilla returned a plain ThingSaveData (or a subclass). Copy
            // every inherited field into a GlowThingSaveData instance and
            // replace the result. The copy walks the type chain so
            // subclass-specific fields (DynamicThingSaveData, etc.) carry
            // over where possible; XML serialization matches on the final
            // runtime type, so the type hierarchy need not be preserved
            // exactly to round-trip.
            var upgraded = new GlowThingSaveData { IsGlowing = true };
            CopyFields(__result, upgraded);
            __result = upgraded;
        }

        private static void CopyFields(object src, object dst)
        {
            var t = src.GetType();
            while (t != null && t != typeof(object))
            {
                foreach (var fi in t.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (!fi.IsInitOnly)
                        fi.SetValue(dst, fi.GetValue(src));
                }
                t = t.BaseType;
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeserializeSave))]
    public class ThingDeserializeSaveGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, ThingSaveData saveData)
        {
            if (__instance == null) return;
            if (!(saveData is GlowThingSaveData sd)) return;
            if (!sd.IsGlowing) return;

            GlowPaintHelpers.SetGlow(__instance, true);

            // Re-apply emissive now. Vanilla DeserializeSave has already
            // called SetCustomColor(index) with emissive: false, so the
            // renderer is currently showing the non-emissive material.
            GlowPaintHelpers.ReapplyEmissive(__instance, true);
        }
    }
}
