using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    // Force SprayGun.IsOperable to return its OnOff state regardless of
    // IsEmpty (loaded-can presence). Vanilla returns `IsEmpty ? false : OnOff`
    // which would otherwise colour the targeting cursor red on an empty gun.
    // Under glow paint the gun runs ammo-less, so the empty-gate must go.
    [HarmonyPatch(typeof(SprayGun), nameof(SprayGun.IsOperable), MethodType.Getter)]
    public class SprayGunIsOperablePatch
    {
        [UsedImplicitly]
        public static bool Prefix(SprayGun __instance, ref bool __result)
        {
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return true;
            __result = __instance.OnOff;
            return false;
        }
    }

    // Hide the SprayGun's can-accepting slot by flipping its Type to Blocked
    // at instance Awake. Pattern mirrors Plans/EquipmentPlus/.../DynamicSlots.cs:
    // a Blocked slot with IsInteractable=false renders invisible in the
    // inventory UI and cannot be inserted into.
    //
    // Uses the TargetMethod pattern because SprayGun does not declare Awake
    // itself; Awake is inherited from a base. See
    // Research/Patterns/HarmonyInheritedMethodTrap.md.
    //
    // Idempotent. Defensive: if the slot is already occupied (legacy save
    // from before v1.4.0 with a can loaded), leave it visible so the player
    // can still remove the can. Legacy-eject automation is on the v1.5.0 TODO.
    [HarmonyPatch]
    public class SprayGunSlotHiderPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SprayGun).GetMethod("Awake",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (!(__instance is SprayGun gun)) return;
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return;
            if (gun.Slots == null || gun.Slots.Count == 0) return;
            var slot = gun.Slots[0];
            if (slot == null) return;
            if (slot.Type == Slot.Class.Blocked) return;
            if (slot.Get() != null) return;
            slot.Type = Slot.Class.Blocked;
            slot.IsInteractable = false;
            var icon = Slot.GetSlotTypeSprite(Slot.Class.Blocked);
            if (icon != null) slot.SlotTypeIcon = icon;
        }
    }

    // Intercept Thing.AttackWith when the source is a SprayGun. Patching
    // here instead of ISprayer.DoSpray because Harmony cannot patch static
    // methods on interfaces ("Owner can't be an array or an interface").
    // Thing.AttackWith is the ONLY caller of ISprayer.DoSpray in the
    // decompile (Thing.cs line 5003), so patching AttackWith covers every
    // paint path without touching the interface.
    //
    // Vanilla DoSpray runs through a chain of validity gates that all
    // fail for our ammo-less gun: null GetPaintMaterial (the "Not enough
    // paint" error visible on the cursor), same-colour block, Tool-off,
    // IsEmpty. We bypass AttackWith's DoSpray branch entirely for the
    // SprayGun+painted case: read the gun's OnOff to pick add vs remove
    // glow, set CurrentMode for the downstream per-Thing patches, and
    // enter via OnServer.SetCustomColor so NetworkPainterPatch (flood /
    // single / checkered) still runs.
    //
    // Non-matching attacks (can, authoring tool, anything else) pass
    // through to vanilla by returning true from the prefix.
    [HarmonyPatch(typeof(Thing), nameof(Thing.AttackWith))]
    public class ThingAttackWithGunPatch
    {
        // Custom game strings for cursor tooltips. Cached statics so
        // GameString.Create runs once per mod load, not per hover. Template
        // placeholders mirror the vanilla `CantPaintSameColour` pattern.
        private static readonly Assets.Scripts.Localization2.GameString GlowAlreadyApplied =
            Assets.Scripts.Localization2.GameString.Create(
                "SprayPaintPlus.GlowAlreadyApplied",
                "The {LOCAL:Thing} is already glowing",
                "Thing");

        private static readonly Assets.Scripts.Localization2.GameString NoGlowToRemove =
            Assets.Scripts.Localization2.GameString.Create(
                "SprayPaintPlus.NoGlowToRemove",
                "The {LOCAL:Thing} has no glow to remove",
                "Thing");

        private static readonly Assets.Scripts.Localization2.GameString GlowWillBeAdded =
            Assets.Scripts.Localization2.GameString.Create(
                "SprayPaintPlus.GlowWillBeAdded",
                "The {LOCAL:Thing} will glow",
                "Thing");

        private static readonly Assets.Scripts.Localization2.GameString GlowWillBeRemoved =
            Assets.Scripts.Localization2.GameString.Create(
                "SprayPaintPlus.GlowWillBeRemoved",
                "Glow will be removed from the {LOCAL:Thing}",
                "Thing");

        [HarmonyPrefix]
        [UsedImplicitly]
        public static bool Prefix(Thing __instance, Attack attack, bool doAction, ref Thing.DelayedActionInstance __result)
        {
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return true;
            if (attack.SourceItem == null) return true;
            if (!(attack.SourceItem is SprayGun gun)) return true;
            if (__instance == null) return true;
            if (!__instance.IsPaintable) return true;
            if (__instance.CustomColor == null) return true; // unpainted: let vanilla handle

            var instance = new Thing.DelayedActionInstance
            {
                Duration = 0.2f,
                ActionMessage = ActionStrings.Paint,
            };

            // Same-state check: if the gun's mode matches the target's
            // current glow state, the click would be a no-op. Fail with a
            // descriptive tooltip so the cursor paints red and the player
            // sees why, mirroring vanilla's "already painted <colour>" UX.
            bool currentlyGlowing = GlowPaintHelpers.IsGlowing(__instance);
            bool wantGlowing = gun.OnOff;
            if (currentlyGlowing == wantGlowing)
            {
                __result = instance.Fail(
                    wantGlowing ? GlowAlreadyApplied : NoGlowToRemove,
                    __instance.ToTooltip());
                return false;
            }

            // Valid action: set a preview tooltip so the cursor shows what
            // will happen on click, matching vanilla's "The Pipe will be
            // painted Red" pattern.
            instance.ExtendedMessage = (wantGlowing ? GlowWillBeAdded : GlowWillBeRemoved)
                .AsString(__instance.ToTooltip());

            if (!doAction)
            {
                // Preview: valid instance -> cursor green.
                __result = instance;
                return false;
            }

            var previousMode = GlowPaintHelpers.CurrentMode;
            GlowPaintHelpers.CurrentMode = wantGlowing
                ? GlowApplyMode.AddGlow
                : GlowApplyMode.RemoveGlow;
            try
            {
                if (GameManager.RunSimulation)
                {
                    OnServer.SetCustomColor(__instance, __instance.CustomColor.Index);
                }
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogError($"Glow paint failed: {e}");
            }
            finally
            {
                GlowPaintHelpers.CurrentMode = previousMode;
            }

            __result = instance;
            return false;
        }
    }

    // Relabel the gun's right-click on/off label from "On" / "Off" to
    // "Add Glow" / "Remove Glow". Runs on the generic Thing.GetContextualName
    // getter; filters by `__instance is SprayGun` and
    // `interactable.Action == InteractableType.OnOff` so other on/off-
    // togglable items keep their vanilla labels.
    //
    // Vanilla's label semantic is "the action the click WILL do":
    //   - OnOff=true  -> vanilla "Off"  -> ours "Remove Glow"
    //   - OnOff=false -> vanilla "On"   -> ours "Add Glow"
    [HarmonyPatch(typeof(Thing), nameof(Thing.GetContextualName))]
    public class SprayGunContextualNamePatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, Interactable interactable, ref string __result)
        {
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return;
            if (!(__instance is SprayGun gun)) return;
            if (interactable == null) return;
            if (interactable.Action != InteractableType.OnOff) return;
            __result = gun.OnOff ? "Remove Glow" : "Add Glow";
        }
    }

    // Prefix on Thing.SetCustomColor. During a gun paint event (CurrentMode
    // != Idle) with a painted target, rewrite the incoming colour index to
    // the target's existing colour. The gun never changes a Thing's colour;
    // only glow. Works per-Thing during flood-fill (each flooded item
    // preserves its own colour).
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor),
        new[] { typeof(int), typeof(bool) })]
    public class ThingSetCustomColorGunPreservePrefix
    {
        [UsedImplicitly]
        public static void Prefix(Thing __instance, ref int index)
        {
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return;
            if (GlowPaintHelpers.Reapplying) return;
            var mode = GlowPaintHelpers.CurrentMode;
            if (mode != GlowApplyMode.AddGlow && mode != GlowApplyMode.RemoveGlow) return;
            if (__instance == null || __instance.CustomColor == null) return;
            index = __instance.CustomColor.Index;
        }
    }

    // Postfix on Thing.SetCustomColor. Two jobs:
    //   1. Gun paint (CurrentMode == AddGlow or RemoveGlow): write the
    //      target's IsGlowing flag accordingly. Raise GlowNetworkFlag so
    //      state syncs via ThingGlowSyncPatches.
    //   2. Regardless of mode: if IsGlowing is true and the incoming call
    //      was non-emissive, re-invoke SetCustomColor(index, true) behind
    //      the Reapplying guard so the emissive material swap happens.
    //
    // Can paints (CurrentMode == Idle) leave IsGlowing untouched. Color and
    // glow are orthogonal: a can paint only changes colour; glow state
    // survives. If the target was glowing, the emissive re-apply from job 2
    // restores the emissive material on the new colour.
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor),
        new[] { typeof(int), typeof(bool) })]
    public class ThingSetCustomColorGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, int index, bool emissive)
        {
            if (!SprayPaintPlusPlugin.EnableGlowPaint.Value) return;
            if (GlowPaintHelpers.Reapplying) return;
            if (__instance == null || __instance.CustomColor == null) return;

            var mode = GlowPaintHelpers.CurrentMode;
            if (mode == GlowApplyMode.AddGlow)
            {
                GlowPaintHelpers.SetGlow(__instance, true);
                __instance.NetworkUpdateFlags |= GlowPaintHelpers.GlowNetworkFlag;
            }
            else if (mode == GlowApplyMode.RemoveGlow)
            {
                GlowPaintHelpers.SetGlow(__instance, false);
                __instance.NetworkUpdateFlags |= GlowPaintHelpers.GlowNetworkFlag;
            }

            if (GlowPaintHelpers.IsGlowing(__instance) && !emissive)
            {
                GlowPaintHelpers.ReapplyEmissive(__instance, true);
            }
        }
    }

    // Cleanup: remove destroyed Things from the glow dictionary.
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnDestroy))]
    public class ThingDestroyGlowCleanupPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (__instance != null)
                GlowPaintHelpers.GlowingThingIds.Remove(__instance.ReferenceId);
        }
    }
}
