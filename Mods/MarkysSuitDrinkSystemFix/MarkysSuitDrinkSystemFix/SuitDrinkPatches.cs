using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Clothing;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Sound;
using Assets.Scripts.Util;
using HarmonyLib;
using UnityEngine;

namespace MarkysSuitDrinkSystemFix
{
    // Runtime fix for Marky's Suit Drink System. The broken original patch is removed by Harmony ID and
    // replaced with a reimplementation compiled against the CURRENT game assemblies. Nothing here clones
    // or redistributes Marky's mod; it operates on the installed copy and no-ops if that copy is absent.
    //
    // Background on the hydration API change: Research/GameSystems/EntityHydrationAndNeeds.md.
    internal static class SuitDrinkPatches
    {
        // Marky's mod builds its Harmony instance as new Harmony("MarkysSuitDrinkSystem"), so its patches
        // are owned by that id. We remove only the Suit.InteractWith prefix with that owner.
        private const string MarkyHarmonyId = "MarkysSuitDrinkSystem";

        // The InteractableType value Marky's Thing.Awake patch assigns to the suit's "Drink" interactable.
        // Matched by numeric value to stay in lockstep with his still-active GetContextualName and
        // Thing.Awake patches, which also key off 35; we only replace the InteractWith handler.
        private const int DrinkAction = 35;

        // Marky names the added slot "WaterTank"; its StringHash is Animator.StringToHash of that string.
        private static readonly int WaterTankSlotHash = Animator.StringToHash("WaterTank");

        // 1 litre of water is 55.5556 moles. Hydrating with those moles reproduces the old
        // Hydrate(litres * 5) effect exactly: Entity.Hydrate(Mole) adds moles * HydrationBase.HydrationPerMole
        // and 55.5556 * 0.09 = 5 hydration per litre. See Research/GameSystems/EntityHydrationAndNeeds.md.
        private const double MolesPerLitreWater = 55.55555555555556;

        public static void Apply(Harmony harmony)
        {
            var interactWith = AccessTools.Method(
                typeof(Suit), "InteractWith",
                new[] { typeof(Interactable), typeof(Interaction), typeof(bool) });
            if (interactWith == null)
            {
                Plugin.Log.LogWarning(
                    "Suit.InteractWith could not be resolved; Marky's Suit Drink System Fix is inactive. " +
                    "The game may have changed.");
                return;
            }

            // Only act if Marky's broken prefix is actually present. If his mod is not installed (or was
            // already fixed at the source), leave everything alone.
            var patchInfo = Harmony.GetPatchInfo(interactWith);
            bool markyPrefixPresent = patchInfo?.Prefixes?.Any(p => p.owner == MarkyHarmonyId) ?? false;
            if (!markyPrefixPresent)
            {
                Plugin.Log.LogInfo(
                    "Marky's Suit Drink System InteractWith prefix was not found; Marky's Suit Drink System " +
                    "Fix is inactive (the mod is absent or already updated).");
                return;
            }

            // Remove only Marky's InteractWith prefix. His GetContextualName, Language, and Thing.Awake
            // patches stay: they add the Water Tank slot, the "Drink" name, and the slot label, and none
            // of them call the removed Hydrate(float).
            harmony.Unpatch(interactWith, HarmonyPatchType.Prefix, MarkyHarmonyId);
            harmony.Patch(interactWith, prefix: new HarmonyMethod(typeof(SuitDrinkPatches), nameof(InteractWithPrefix)));

            Plugin.Log.LogInfo(
                "Marky's Suit Drink System Fix active: replaced the broken Suit.InteractWith drink prefix " +
                "with a Hydrate(Mole) version.");
        }

        // Corrected reimplementation of Marky's Suit.InteractWith prefix. Identical to his logic except the
        // hydration call: his ((Entity)ParentEntity).Hydrate(litres * 5f) used the removed float overload;
        // we build the same water Mole we drain from the tank and call Hydrate(Mole), which reproduces the
        // same hydration gain and also feeds the stomach atmosphere via Human.Hydrate. Returns false (skip
        // the game's InteractWith) only for the drink action; every other action falls through unchanged.
        private static bool InteractWithPrefix(
            ref Thing.DelayedActionInstance __result, Suit __instance, Interactable interactable, bool doAction)
        {
            var action = new Thing.DelayedActionInstance
            {
                Duration = 0f,
                ActionMessage = interactable.ContextualName,
            };

            if ((int)interactable.Action != DrinkAction)
                return true; // not the drink action: run the game's InteractWith normally

            var tank = ((Thing)__instance).Slots
                .FirstOrDefault(s => s.StringHash == WaterTankSlotHash)?.Get<GasCanister>();
            if (tank == null)
            {
                __result = action.Fail();
                return false;
            }

            float availableLitres = ((Thing)tank).InternalAtmosphere.GasMixture.Water.Volume.ToFloat();
            if (availableLitres <= 0.01f)
            {
                __result = action.Fail();
                return false;
            }

            // Preview pass (the per-frame call that refreshes the interaction text): report success without
            // consuming anything. This is the call path that spammed the exception before the fix.
            if (!doAction)
            {
                __result = action.Succeed();
                return false;
            }

            Human wearer = __instance.ParentEntity;
            if (wearer == null)
            {
                __result = action.Fail();
                return false;
            }

            if (wearer.IsLocalPlayer)
            {
                Singleton<AudioManager>.Instance.PlayAudioClipsData(
                    ((Thing)wearer).ReferenceId, Item.DrinkingFinishedHash, Vector3.zero, (ChannelData)null, 1f, 1f);
            }

            float hydrationRoomLitres = (wearer.GetHydrationStorage() - wearer.Hydration) / 5f;
            float litresToDrink = Mathf.Min(hydrationRoomLitres, availableLitres);
            if (litresToDrink <= 0f)
            {
                // Already fully hydrated: the action succeeds but consumes nothing (also guards against a
                // negative amount if hydration ever exceeds storage).
                __result = action.Succeed();
                return false;
            }

            var drained = new MoleQuantity(MolesPerLitreWater * litresToDrink);
            var energy = IdealGas.Energy(
                Chemistry.Temperature.TwentyDegrees, Mole.SpecificHeat(Chemistry.GasType.Water), drained);
            wearer.Hydrate(new Mole(Chemistry.GasType.Water, drained, energy));
            ((Thing)tank).InternalAtmosphere.Remove(drained, Chemistry.GasType.Water);

            __result = action.Succeed();
            return false;
        }
    }
}
