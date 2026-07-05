# Marky's Suit Drink System Fix Research

Durable internals for Marky's Suit Drink System Fix, a temporary compatibility patch for the third-party Marky's Suit Drink System (by Marky, Workshop item 3644610659; source at https://github.com/Marky-S/MarkysSuitDrinkSystem).

## Why this mod exists

The Sanitation update (game version 0.2.6403.27689) changed `Assets.Scripts.Objects.Entity.Hydrate`. The old `void Hydrate(float)` overload was removed and replaced by `void Hydrate(Mole)` (Entity plus a `Human` override that also feeds the stomach atmosphere). Marky's Suit Drink System, built against an older version, calls `((Entity)ParentEntity).Hydrate(litres * 5f)` from its `Suit.InteractWith` prefix, so that call no longer resolves and throws `MissingMethodException: Method not found: void Assets.Scripts.Objects.Entity.Hydrate(single)`.

Because a missing-method reference is resolved when the containing method is JIT-compiled, the exception fires on every invocation of Marky's prefix, not only on an actual drink. The inventory UI calls `Suit.InteractWith(..., doAction: false)` every frame through `Interactable.UpdateDisplay` to refresh the interaction text, so an open suit inventory floods the log with the exception. The full hydration API change is documented in `Research/GameSystems/EntityHydrationAndNeeds.md`.

## Architecture

A single BepInEx plugin (`Plugin.cs`) with one patch class (`SuitDrinkPatches.cs`). It hard-depends on StationeersLaunchPad and soft-depends on Marky's Suit Drink System (resolved by its Harmony patch, not a compile-time reference). It adds no prefabs, assets, settings, or network messages.

## File walkthrough

- `Plugin.cs`: BepInEx entry point. In `Awake` it subscribes to `Prefab.OnPrefabsLoaded`. Marky's mod applies its Harmony patches in its own `Awake`, and StationeersLaunchPad's plugin-load ordering does not honor `[BepInDependency]` (see below), so the fix is deferred to `OnPrefabsLoaded`, which fires once on the main thread after every plugin `Awake` has run. `SuitDrinkPatches.Apply` runs there inside a try/catch so a failure logs instead of breaking load.
- `SuitDrinkPatches.cs`: confirms Marky's `Suit.InteractWith` prefix is present (by Harmony owner id), removes it, and installs the corrected prefix imperatively (the target is the game's `Suit.InteractWith`, resolved with `AccessTools.Method`). If the prefix is absent, it logs and returns.

## Patch catalog

### Suit.InteractWith prefix (replaces Marky's broken drink handler)

`Assets.Scripts.Objects.Clothing.Suit.InteractWith(Interactable, Interaction, bool)` returns a `DelayedActionInstance`. Marky patched it with a prefix that handles his custom "Drink" interactable (InteractableType numeric value 35, assigned by his `Thing.Awake` patch) and calls the removed `Hydrate(float)`.

The fix does two things in `Apply`:

1. `harmony.Unpatch(interactWith, HarmonyPatchType.Prefix, "MarkysSuitDrinkSystem")` removes only Marky's prefix, identified by the Harmony instance id he passes to `new Harmony("MarkysSuitDrinkSystem")`. His `GetContextualName`, `Language`, and `Thing.Awake` patches are left in place; they add the Water Tank slot, the "Drink" name, and the slot label, and none call the removed method.
2. `harmony.Patch(interactWith, prefix: InteractWithPrefix)` installs the corrected prefix.

`InteractWithPrefix` reproduces Marky's logic exactly except the hydration call. It reads the `GasCanister` in the suit's `WaterTank` slot (`Animator.StringToHash("WaterTank")`), computes the litres to drink as `Min((GetHydrationStorage() - Hydration) / 5, availableLitres)`, then builds the water `Mole` it drains and calls `Human.Hydrate(Mole)`. Hydrating with the drained moles reproduces the old effect exactly: `Entity.Hydrate(Mole)` adds `moles * HydrationBase.HydrationPerMole`, and `55.5556 mol/litre * 0.09 = 5` hydration per litre, which is what `Hydrate(litres * 5)` did. It removes the same `MoleQuantity` of `Chemistry.GasType.Water` from the tank. The `doAction == false` preview path (the per-frame call that spammed the exception) returns success without consuming anything.

## Decompiled game internals

- `Entity.Hydrate(Mole)` and the `Human` override, the `Hydration` / `GetHydrationStorage` members, the `HydrationBase` mole/hydration conversion constants, and the vanilla drink flows: `Research/GameSystems/EntityHydrationAndNeeds.md`.
- `Prefab.OnPrefabsLoaded` is `public static event Action`, invoked once at the end of prefab loading after all mods are loaded: the canonical "all mods loaded" main-thread hook. `Research/GameSystems/ModLoadSequence.md`.
- StationeersLaunchPad load model: SLP instantiates a Workshop/data mod's BepInEx plugin with `parent.AddComponent(type)` (in its `BepInExEntrypoint`), which triggers `Awake` synchronously and bypasses the BepInEx Chainloader. Consequence: `[BepInDependency]` does not order two SLP-loaded plugins relative to each other. SLP's own load order is driven by the `<OrderAfter>` / `<OrderBefore>` / `<DependsOn>` elements in `About.xml` via its `OrderGraph`, but the sort is gated behind the `AutoSort` config (default on) and is best-effort under the parallel load strategy. `Research/Workflows/StationeersLaunchPadModLoading.md`.

## Load-order enforcement

Two mechanisms, belt and suspenders:

- `About.xml` carries `<OrderAfter WorkshopHandle="3644610659" />`, which places Marky's mod before this one in StationeersLaunchPad's load graph (matched by Workshop handle, which needs no runtime verification unlike his `<ModID>`).
- The unpatch/repatch is deferred to `Prefab.OnPrefabsLoaded`, which fires after every plugin `Awake` regardless of the sort. This is the hard guarantee: even if a user disables AutoSort or the parallel strategy reorders instantiation, Marky's `PatchAll` has already run by the time `OnPrefabsLoaded` fires. `Suit.InteractWith` is only invoked during gameplay, far later than `OnPrefabsLoaded`, so the corrected prefix is active well before any call.

## Pitfalls

- Patch timing: unpatching in BepInEx `Awake` can miss Marky's prefix if his plugin has not run yet. Defer to `Prefab.OnPrefabsLoaded`.
- The target is a third-party patch identified by Harmony id string `"MarkysSuitDrinkSystem"` (his `new Harmony(...)` argument), which is not guaranteed to equal his BepInPlugin GUID. It was read from his decompiled `Plugin.Awake`. Confirm the id if his mod is rebuilt.
- Resolve the exact `Suit.InteractWith(Interactable, Interaction, bool)` overload so the unpatched method is the same one he patched.
- Do not call the removed `Hydrate(float)`; build a `Mole<Water>` and call `Hydrate(Mole)` (see `Research/GameSystems/EntityHydrationAndNeeds.md`).

## Design decisions

- Unpatch + reimplement (fix type A), rather than transpiling Marky's prefix or patching his prefix method: removing his prefix by Harmony id and installing a clean `Suit.InteractWith` prefix keeps the reimplementation readable and carries no compile-time or runtime dependency on his assembly (only the Harmony id string and the "WaterTank" slot name). His broken IL is never invoked once his prefix is gone, so it never JIT-throws.
- Soft dependency by runtime detection with a graceful no-op keeps this mod safe to ship even to users who do not have Marky's mod, and keeps it from erroring if his mod's shape changes.
- Hydrate with the drained moles (not a converted hydration-unit float) to mirror the vanilla drinking-fountain flow and preserve the stomach-atmosphere side effect of `Human.Hydrate`.
