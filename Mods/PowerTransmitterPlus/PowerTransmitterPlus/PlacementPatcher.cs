using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Util;
using HarmonyLib;

namespace PowerTransmitterPlus
{
    // Lift the floor-only placement restriction on StructureMicrowavePowerTransmitter
    // and StructureMicrowavePowerTransmitterReceiver by mutating three prefab inspector
    // values to the shape vanilla SmartRotate expects for (Grid, All, All).
    //
    // Decompile findings (game version 0.2.6228.27061):
    //   - The floor-only restriction comes from prefab inspector values, not code.
    //     Structure declares AllowedRotations and RotationAxis with C# default `All`
    //     on Structure.cs:133-135, and ElectricalInputOutput declares OpenEndsPermutation
    //     and ConnectionType with C# defaults `int[6]{0,1,2,3,4,5}` and `Exhaustive`
    //     respectively on ElectricalInputOutput.cs:28-30. The floor-only / yaw-only
    //     dish behaviour comes from the prefab inspector overriding all four to a
    //     smaller-cycle configuration (AllowedRotations=Floor, RotationAxis=Y,
    //     OpenEndsPermutation=int[4]{0,1,2,3}, ConnectionType=FaceFloorZY-or-similar).
    //   - AllowedRotations gates two surfaces: InventoryManager.UpdatePlacement's
    //     surface auto-correct (line 1719-1739) and Structure.Rotate's multi-axis
    //     branch (line ~2199 special-cases the literal Floor and falls through to
    //     Y-only).
    //   - RotationAxis gates the cursor R-key handler at InventoryManager.cs:2443-
    //     2479 via three independent `(RotationAxis & X) != None` checks. With Y
    //     only, the player can yaw with Q/E but cannot pitch or roll.
    //   - OpenEndsPermutation + ConnectionType together drive vanilla SmartRotate
    //     under QuantityModifier (C-key autoplace + scroll cycle). The dispatcher
    //     keys off ConnectionType to pick a permutation cycle; the permutation
    //     indices reach up to 5 in the (Grid, All) path (RotZ cycle {0,2,5,4} +
    //     RotX cycle {0,1,5,3} + RotY cycle {1,2,3,4}), which IndexOutOfRanges on
    //     the dish's inspector-baked int[4] permutation array.
    //   - Quaternion is lossless through every persistence channel
    //     (ConstructionCreationMessage, per-tick WriteTransform, SerializeOnJoin,
    //     StructureSaveData). Custom placements survive multiplayer and save/load
    //     without further patching.
    //
    // Strategy: re-run the vanilla SmartRotate.AutomaticSetup function on the dish
    // after flipping AllowedRotations and RotationAxis. AutomaticSetup is the same
    // function the prefab editor would call at bake time; its (PlacementSnap.Grid,
    // RotationAxis.All) branch produces int[6] + ConnectionType.Exhaustive, exactly
    // the shape the (Grid, All) GetOpenEndLocationPermutation path expects. Vanilla
    // SmartRotate then handles autoplace and scroll cycling without any Harmony
    // patch. AutomaticSetup is `public static` and has zero callers in the rest of
    // the assembly, so the one-shot call is race-free.
    //
    // Three-target rationale (the same prefab exists at runtime in three places):
    //   - WorldManager.SourcePrefabs[i] (the SourcePrefab): the original prefab
    //     loaded by Unity into the scene. SetupConstructionCursors iterates this
    //     list to build the cursor preview clones, so mutating it makes the
    //     cursor preview reach the correct SmartRotate state.
    //   - Prefab._allPrefabs[hash] (the registered clone): made by Prefab.Register
    //     at game-load time via Object.Instantiate(sourcePrefab) and stored under
    //     PrefabsGameObject. Thing.Create<T>(prefab, ...) at Thing.cs:2330
    //     re-resolves via Prefab.Find(prefab.PrefabHash) and instantiates THIS
    //     clone for every placement and every save-load reconstruction. Empirically
    //     verified on 2026-04-26 by snapshotting cursor (RotationAxis=All) vs.
    //     placed dish (RotationAxis=Y) in the same session: only the registered
    //     clone reaches placement, and our SourcePrefab mutation does not propagate
    //     to it.
    //   - InventoryManager._constructionCursors[name] (the cursor clone): made by
    //     SetupConstructionCursors via Object.Instantiate(sourcePrefab) at
    //     ManagerAwake time. If our patch runs after ManagerAwake the cursor is
    //     already cloned and must be mutated in-place.
    //
    // Idempotent. Gated whole-class on PowerTransmitterPlusPlugin.NonFloorPlacementPatched
    // (captured at boot from the EnableNonFloorPlacement ConfigEntry); the validator
    // catches client/host mismatches at join time.
    internal static class PlacementPatcher
    {
        private static bool _applied;

        private static readonly FieldInfo ConstructionCursorsField =
            AccessTools.Field(typeof(InventoryManager), "_constructionCursors");

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;

            int sourcePrefabsPatched = MutateSourcePrefabs();
            int registeredPrefabsPatched = MutateRegisteredPrefabs();
            int cursorsPatched = MutateExistingCursors();

            PowerTransmitterPlusPlugin.Log.LogInfo(
                $"Non-floor placement enabled on {sourcePrefabsPatched} source prefab(s), " +
                $"{registeredPrefabsPatched} registered prefab(s), " +
                $"{cursorsPatched} construction cursor(s)");
        }

        // WorldManager.Instance.SourcePrefabs holds the original prefabs Unity loaded
        // into the scene. SetupConstructionCursors iterates this list to build the
        // construction-cursor preview clones, so mutating these source prefabs is
        // what makes the cursor show non-floor placement and reach the correct
        // SmartRotate cycle when the player is holding a kit.
        private static int MutateSourcePrefabs()
        {
            int n = 0;
            foreach (var prefab in WorldManager.Instance.SourcePrefabs)
            {
                if (!IsDishDevice(prefab)) continue;
                var structure = prefab as Structure;
                if (structure == null) continue;
                ApplyTo(structure);
                n++;
            }
            return n;
        }

        // Prefab.Register (Prefab.cs:118) clones each SourcePrefab via
        // UnityEngine.Object.Instantiate at game-load time and stores the clone in
        // Prefab._allPrefabs[prefabHash]. Thing.Create<T>(prefab, ...) at
        // Thing.cs:2330 ignores its `prefab` argument's identity and re-resolves
        // via Prefab.Find(prefab.PrefabHash), so all placements (and save loads)
        // come from the registered clone, NOT the SourcePrefab we mutated above.
        // Walk the registered clones too; otherwise placed dishes inherit the
        // inspector-baked Y / int[4] / FlatExhaustive shape and IndexOutOfRange
        // back into vanilla SmartRotate as soon as the cursor scroll-cycles.
        private static int MutateRegisteredPrefabs()
        {
            int n = 0;
            foreach (var prefab in WorldManager.Instance.SourcePrefabs)
            {
                if (!IsDishDevice(prefab)) continue;
                var registered = Prefab.Find<Structure>(prefab.PrefabHash);
                if (registered == null) continue;
                if (registered == prefab) continue; // same instance, already covered
                ApplyTo(registered);
                n++;
            }
            return n;
        }

        private static int MutateExistingCursors()
        {
            if (ConstructionCursorsField == null)
            {
                PowerTransmitterPlusPlugin.Log.LogWarning(
                    "InventoryManager._constructionCursors field not found; cursor preview will pick up the new placement values only after the next SetupConstructionCursors call");
                return 0;
            }

            // _constructionCursors is private static readonly Dictionary<string, Structure>.
            // If ManagerAwake has not yet fired when this runs the dictionary is empty;
            // SetupConstructionCursors will later clone cursors from the (already-patched)
            // SourcePrefabs and the cursors inherit the new values naturally. A 0 here is
            // not an error.
            var dict = ConstructionCursorsField.GetValue(null) as IDictionary<string, Structure>;
            if (dict == null || dict.Count == 0) return 0;

            int n = 0;
            foreach (var kv in dict)
            {
                var cursor = kv.Value;
                if (!IsDishDevice(cursor)) continue;
                ApplyTo(cursor);
                n++;
            }
            return n;
        }

        // Apply the four-field shape change to a single rotatable Structure. Order
        // matters: AutomaticSetup branches on the GetRotationAxis() / GetAllowedRotations()
        // values, so the two flag writes must precede the call.
        private static void ApplyTo(Structure structure)
        {
            structure.AllowedRotations = AllowedRotations.All;
            structure.RotationAxis = RotationAxis.All;

            if (structure is ISmartRotatable rotatable)
            {
                // AutomaticSetup picks the vanilla (Grid, All) branch: allocates
                // int[6], runs _AutomaticInitial3DSetup (null-safe on the dish's
                // OpenEnds list whether empty or populated), tries _SetPermutation
                // against the OrientationLookup table, falls through to
                // ConnectionType.Exhaustive when no pre-defined pattern matches,
                // then writes back OpenEndsPermutation = {0,1,2,3,4,5} via the
                // post-switch normalization loop. End state matches the C# defaults
                // on ElectricalInputOutput.cs:28-30.
                SmartRotate.AutomaticSetup(rotatable);
            }
        }

        // PowerTransmitter and PowerReceiver are the directional dish pair; both
        // derive from WirelessPower. PowerTransmitterOmni is a sibling (derives
        // from Electrical, not WirelessPower) and is excluded.
        internal static bool IsDishDevice(Thing thing)
        {
            return thing is PowerTransmitter || thing is PowerReceiver;
        }
    }
}
