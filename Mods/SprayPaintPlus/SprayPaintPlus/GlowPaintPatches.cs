using System;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace SprayPaintPlus
{
    // Glow paint is a paired setting, so every gate below merges both halves
    // instead of reading a bare entry. WHICH merge depends on whose behavior is
    // being decided, and there are two answers:
    //
    //   SettingsMerge.EffectiveGlowPaint  this machine's own client half AND the
    //                                     session's server half. Right for the
    //                                     local UI: operability, slot visibility,
    //                                     the contextual name.
    //   SettingsMerge.ServerAllows(...)   the server half AND the ACTING player's
    //                                     client half, looked up by Human
    //                                     ReferenceId. Right for the apply in
    //                                     ThingAttackWithGunPatch, which runs on
    //                                     the authority on behalf of whoever swung
    //                                     the gun, and that is usually not us.
    //
    // The dividing line: a client half governs what you can DO, never what you
    // SEE, and it governs it only for its own owner. Everything that decides
    // whether the gun works, shows its slot, or labels its action is a "do" about
    // the local player and takes the first form. The emissive re-apply in
    // ThingSetCustomColorGlowPatch is a "see" and is not gated at all; the comment
    // on that class explains the defect that made it one.

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
            if (!SettingsMerge.EffectiveGlowPaint) return true;
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
    // Idempotent. Defensive: if the slot is already occupied (any existing
    // save that had a can loaded when the mod was added, whether a vanilla
    // save or a pre-block one), leave it visible so the player can still
    // remove the can. Once the can is removed and the world reloaded, the
    // now-empty slot is blocked like any other. No auto-eject by design; see
    // Research/Patterns/SlotInsertionBlock.md "Legacy-state handling".
    //
    // Awake fires per gun, so a gun that awoke before the join payload landed
    // decided its slot from a server half that was still a guess.
    // GlowPaintPatches.ReapplySlotState walks the already-spawned guns once the
    // real value is known; the body itself lives there so both entry points
    // apply exactly the same rules.
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
            GlowPaintPatches.ApplySlotState(gun, SettingsMerge.EffectiveGlowPaint);
        }
    }

    /// <summary>
    /// Slot-state upkeep for the spray gun's can slot.
    ///
    /// The block is applied at Awake, but the merged glow value can change after
    /// that: a remote client resolves the server half from its own entry until
    /// the join payload arrives, and the payload can flip the answer either way.
    /// So the state has to be reversible, not one-way, and something has to
    /// revisit the guns that awoke too early.
    /// </summary>
    internal static class GlowPaintPatches
    {
        // The vanilla slot definition, captured from the first gun seen before
        // anything touched it. Every SprayGun ships the same slot, so one capture
        // restores any of them, and ApplySlotState captures before it blocks, so
        // a value is always on hand by the time an unblock is possible.
        private static bool _vanillaCaptured;
        private static Slot.Class _vanillaType;
        private static bool _vanillaInteractable;
        private static Sprite _vanillaIcon;

        /// <summary>
        /// Brings one gun's can slot in line with the merged glow value. Both
        /// directions early-return when the slot already matches, so calling this
        /// repeatedly costs nothing and never double-applies.
        /// </summary>
        internal static void ApplySlotState(SprayGun gun, bool glowEnabled)
        {
            if (gun == null) return;
            if (gun.Slots == null || gun.Slots.Count == 0) return;
            var slot = gun.Slots[0];
            if (slot == null) return;

            if (!_vanillaCaptured && slot.Type != Slot.Class.Blocked)
            {
                _vanillaType = slot.Type;
                _vanillaInteractable = slot.IsInteractable;
                _vanillaIcon = slot.SlotTypeIcon;
                _vanillaCaptured = true;
            }

            if (glowEnabled)
            {
                if (slot.Type == Slot.Class.Blocked) return;   // already hidden
                if (slot.Get() != null) return;                // occupied: see the class comment
                slot.Type = Slot.Class.Blocked;
                slot.IsInteractable = false;
                var icon = Slot.GetSlotTypeSprite(Slot.Class.Blocked);
                if (icon != null) slot.SlotTypeIcon = icon;
                return;
            }

            // Glow is off for this session: give the slot back. Without this the
            // block stayed permanent for the rest of the session, so a player who
            // joined a server with glow disabled was left holding a gun that could
            // neither glow nor accept a can.
            if (slot.Type != Slot.Class.Blocked) return;       // already vanilla
            if (!_vanillaCaptured)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    "Spray gun slot restore skipped: the vanilla slot definition was never captured.");
                return;
            }
            slot.Type = _vanillaType;
            slot.IsInteractable = _vanillaInteractable;
            if (_vanillaIcon != null) slot.SlotTypeIcon = _vanillaIcon;
        }

        /// <summary>
        /// Re-runs the slot decision over every spray gun already in the world.
        /// Call it whenever the merged glow value may have moved, in particular
        /// once the join payload has landed. Safe at any point: with no world
        /// loaded the pool is empty, and ApplySlotState is idempotent.
        ///
        /// OcclusionManager.AllThings is the full live set (the same one the save
        /// serializer walks), so guns sitting in a backpack or a locker are
        /// included; FindObjectsOfType would skip them once their GameObject is
        /// deactivated inside a container.
        /// </summary>
        internal static void ReapplySlotState()
        {
            bool glowEnabled = SettingsMerge.EffectiveGlowPaint;
            int count = 0;
            try
            {
                OcclusionManager.AllThings.ForEach(thing =>
                {
                    if (!(thing is SprayGun gun)) return;
                    ApplySlotState(gun, glowEnabled);
                    count++;
                });
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogWarning($"Spray gun slot fixup failed: {e.Message}");
                return;
            }
            SprayPaintPlusPlugin.Log.LogInfo(
                $"Spray gun slot fixup: {count} gun(s) reconciled, glow paint {(glowEnabled ? "on" : "off")}.");
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
            // Two questions with two different answers, and only one of them is about
            // the machine running this code.
            //
            // Everything short of the apply is local UI: the cursor tooltip and the
            // preview describe what the player at THIS keyboard may do, so they merge
            // this machine's own halves through EffectiveGlowPaint.
            //
            // The apply is not. It runs on the authority on behalf of whichever player
            // swung the gun, so it merges the ACTING player's client half instead (see
            // the branch further down). Reading EffectiveGlowPaint there was a defect:
            // a host who switched their own "Client - Glow Paint" off switched glow
            // paint off for every player in the session, which is precisely what a
            // client half must never do.
            bool applying = doAction && GameManager.RunSimulation;
            if (!applying && !SettingsMerge.EffectiveGlowPaint) return true;
            if (attack.SourceItem == null) return true;
            if (!(attack.SourceItem is SprayGun gun)) return true;
            if (__instance == null) return true;
            if (!__instance.IsPaintable) return true;
            if (__instance.CustomColor == null) return true; // unpainted: let vanilla handle

            bool currentlyGlowing = GlowPaintHelpers.IsGlowing(__instance);
            bool wantGlowing = gun.OnOff;

            if (applying)
            {
                // The acting player, parked here by PaintAttackerTracker_Local /
                // _Remote before Thing.AttackWith runs. Read but deliberately not
                // reset: NetworkPainterPatch does the read-and-reset a moment later,
                // inside the SetCustomColor call below. It stays at -1 for any paint
                // that did not come from a player attack, and PlayerPrefs.Has reads an
                // unknown player as permissive.
                long humanId = SprayPaintHelpers.CurrentPaintingHumanId;

                if (!SettingsMerge.ServerAllows(
                        SprayPaintPlusPlugin.ServerGlowPaint,
                        SettingsMerge.SyncedGlowPaint,
                        humanId,
                        SettingsMerge.PlayerPrefs.GlowPaint))
                {
                    // Tell the player only when the SERVER half is what stopped them
                    // AND the stroke would really have changed something. One who
                    // switched their own copy off asked for a gun that does nothing and
                    // got it, and a stroke whose requested state already matches the
                    // target was a no-op with or without the setting.
                    //
                    // A remote client never reaches this: OnServer.AttackWith passes
                    // doAction: runSimulation, which is false there, so both its hover
                    // and its click pass land on the preview branch below.
                    if (currentlyGlowing != wantGlowing
                        && humanId >= 0
                        && SettingsMerge.PlayerPrefs.Has(humanId, SettingsMerge.PlayerPrefs.GlowPaint))
                    {
                        SettingBlockedNotice.NotifyBlocked(humanId, WarningNotifier.Functions.GlowPaint);
                    }

                    // Hand the whole stroke back to vanilla, tooltips included, so a
                    // blocked player sees exactly what a session without glow paint
                    // does with an ammo-less gun: DoSpray fails on its own gates.
                    return true;
                }
            }

            var instance = new Thing.DelayedActionInstance
            {
                Duration = 0.2f,
                ActionMessage = ActionStrings.Paint,
            };

            // Same-state check: if the gun's mode matches the target's
            // current glow state, the click would be a no-op. Fail with a
            // descriptive tooltip so the cursor paints red and the player
            // sees why, mirroring vanilla's "already painted <colour>" UX.
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

            if (applying)
            {
                var previousMode = GlowPaintHelpers.CurrentMode;
                GlowPaintHelpers.CurrentMode = wantGlowing
                    ? GlowApplyMode.AddGlow
                    : GlowApplyMode.RemoveGlow;
                try
                {
                    OnServer.SetCustomColor(__instance, __instance.CustomColor.Index);
                }
                catch (Exception e)
                {
                    SprayPaintPlusPlugin.Log.LogError($"Glow paint failed: {e}");
                }
                finally
                {
                    GlowPaintHelpers.CurrentMode = previousMode;
                }
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
            if (!SettingsMerge.EffectiveGlowPaint) return;
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
            // NO SETTINGS READ HERE, DELIBERATELY. The mode gate below is the whole
            // gate: CurrentMode leaves Idle only inside ThingAttackWithGunPatch's
            // apply branch, which has already merged the acting player's half, so by
            // the time this runs the answer is known to have been yes.
            //
            // An EffectiveGlowPaint read used to sit above as belt-and-braces, and it
            // was worse than redundant. This body runs on the authority during a flood,
            // where the acting player is usually somebody else, so a host with their
            // own client half off would have skipped the rewrite and let a remote
            // player's glow stroke repaint the whole network to the seed's color.
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
    //
    // NO SETTINGS GATE, DELIBERATELY. The two jobs sit on opposite sides of the
    // "a client half governs what you can DO, never what you SEE" rule, and only
    // one of them is a do.
    //
    // Job 1 is already gated by CurrentMode: the mode leaves Idle only inside
    // ThingAttackWithGunPatch, which merges the acting player's glow half before
    // it sets it. Reading a setting again here would decide nothing, and reading
    // the local machine's half would decide it for the wrong player.
    //
    // Job 2 is a see path and must run everywhere. Gating it was a real defect,
    // found in the v1.11.0 settings audit: a player with glow switched off
    // stopped re-applying the emissive material, so any glowing object that
    // anyone recoloured with a bare can silently went matte for that one player
    // while everybody else still saw it glowing. GlowingThingIds is
    // host-authoritative and synced (ThingGlowSyncPatches), so the re-skin is
    // deterministic and correct on every machine no matter how that machine has
    // its own glow paint configured.
    //
    // SERVER-SAFE WITHOUT AN EXPLICIT IsServer GATE. Job 1's GlowingThingIds
    // mutation is mode-gated, and CurrentMode is set only inside
    // ThingAttackWithGunPatch.Prefix's RunSimulation-gated branch -- it stays
    // Idle on clients, so job 1's branches never fire on a client. Job 2 is
    // a deterministic re-skin keyed on the per-side GlowingThingIds, which
    // is host-authoritative (synced via ThingGlowSyncPatches' 0x2000 bit and
    // join sync), so both sides see the same IsGlowing value and apply the
    // same emissive re-skin. This patch is part of the multiplayer-state-
    // mutation audit in Research/Patterns/MultiplayerStateMutation.md.
    [HarmonyPatch(typeof(Thing), nameof(Thing.SetCustomColor),
        new[] { typeof(int), typeof(bool) })]
    public class ThingSetCustomColorGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, int index, bool emissive)
        {
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
