---
title: ISprayer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.ISprayer
related:
  - ./SprayCan.md
  - ./SprayGun.md
  - ./OnServer.md
  - ./Thing.md
tags: [prefab]
---

# ISprayer

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla game interface at `Assets.Scripts.Objects.Items.ISprayer`. Marker interface for tools that apply a `ColorSwatch` to a `Thing`. Hosts a static `DoSpray` helper that is the convergence point for every paint application from an `ISprayer` tool.

## Implementors

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Two vanilla classes implement `ISprayer`:

- `SprayCan : Consumable, ISprayer, IUsedAmount, IUsed` (see `SprayCan.md`).
- `SprayGun : Tool, ISprayer, IUsedAmount, IUsed` (see `SprayGun.md`).

## DoSpray convergence point

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The static method `ISprayer.DoSpray(Thing thing, ISprayer sprayer, bool doAction)` (decompile line 334659-334715 in the monolithic dump; per-class decompile at `Assets/Scripts/Objects/Items/ISprayer.cs`) is where every paint application from every `ISprayer` tool converges. It extracts the color index from the sprayer's active `ColorSwatch` and calls:

```
OnServer.SetCustomColor(thing, colorSwatch.Index);
```

at decompile line 334711. The signature returns `Thing.DelayedActionInstance` which the cursor UI reads to decide red (null / fail) vs. green (success) cursor color.

Both `SprayCan.OnUseItem` (line 334636-334641) and `SprayGun.OnUseItem` (line 334806-334812) reach `DoSpray`. `DoSpray` is also called directly from `Thing.AttackWith` at decompile line 302593 when the target is `IsPaintable` and the sourceItem is `ISprayer` and `!HasColorState`.

## DoSpray validity gates

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Full body of `ISprayer.DoSpray` captured from the per-class decompile. Eight sequential gates, any of which can reject the paint:

```
static Thing.DelayedActionInstance DoSpray(Thing thing, ISprayer sprayer, bool doAction)
{
    var instance = new Thing.DelayedActionInstance
    {
        Duration = sprayer.TimeToUse(),
        ActionMessage = ActionStrings.Paint
    };

    // Gate 1
    if (sprayer.GetPaintMaterial() == null)
        return instance.Fail(GameStrings.NotEnoughSprayPaint, sprayer.ToTooltip());

    // Gate 2: same-color check
    if ((thing.CustomColor.Normal != null && thing.CustomColor.Normal != sprayer.GetPaintMaterial())
        || (thing.CustomColor.Normal == null && thing.PaintableMaterial != sprayer.GetPaintMaterial()))
    {
        ColorSwatch colorSwatch = GameManager.GetColorSwatch(sprayer.GetPaintMaterial());

        // Gate 3: tool off
        if (sprayer is Tool { OnOff: false } tool)
            return instance.Fail(GameStrings.CannotUseWhenNotOn, tool.ToTooltip());

        // Gate 4: empty
        if (sprayer.IsEmpty)
            return instance.Fail(GameStrings.NotEnoughSprayPaint, sprayer.ToTooltip());

        // Extended message (preview tooltip)
        var sb = new StringBuilder();
        sb.AppendLine(GameStrings.ThingWillBeSprayed.AsString(thing.ToTooltip(), colorSwatch.ToTooltip()));
        if (sprayer is Consumable c)
            sb.AppendLine(GameStrings.ItemInSlotValue.AsString(c.ToTooltip(), c.GetQuantityText()));
        else if (sprayer is SprayGun { SprayCan: not null } gun)
            sb.AppendLine(GameStrings.ItemInSlotValue.AsString(gun.SprayCan.ToTooltip(), gun.SprayCan.GetQuantityText()));
        instance.ExtendedMessage = sb.ToString();

        if (!doAction) return instance;

        if (GameManager.RunSimulation)
        {
            if (!sprayer.OnUseItem(sprayer.GetUseAmount(), thing))
                return instance;
            if (sprayer.TimeToUse() <= float.Epsilon && sprayer is Thing parent)
                AudioEvent.Create(parent, SprayPaintSoundHash);
            OnServer.SetCustomColor(thing, colorSwatch.Index);
        }
        return instance;
    }

    // Gate 5: same colour -> fail with CantPaintSameColour
    ColorSwatch colorSwatch2 = GameManager.GetColorSwatch(sprayer.GetPaintMaterial());
    return instance.Fail(GameStrings.CantPaintSameColour, thing.ToTooltip(), colorSwatch2.ToTooltip());
}
```

The relevant game-string citations:

- `GameStrings.NotEnoughSprayPaint` ("Not enough paint in {LOCAL:SprayCanister}") at `Assets/Scripts/Localization2/GameStrings.cs` line 837. Used by gates 1 and 4.
- `GameStrings.CannotUseWhenNotOn` — used by gate 3 when the sprayer is a `Tool` whose `OnOff` is false.
- `GameStrings.CantPaintSameColour` ("The {LOCAL:Thing} is already painted {LOCAL:Color}") at line 839. Used by gate 5.
- `GameStrings.ThingWillBeSprayed` at line 841. Preview tooltip for the green-cursor state.

`sprayer.OnUseItem(sprayer.GetUseAmount(), thing)` is called at the innermost point; if it returns false the paint aborts without calling `OnServer.SetCustomColor`. For `SprayGun`, `OnUseItem` delegates to the loaded `SprayCan.OnUseItem`. A `SprayGun` with no loaded can fails gate 1 (null `GetPaintMaterial`) before ever reaching this line.

**Implication for mods that want an ammo-less `SprayGun`**: a `SprayGun.OnUseItem` Harmony patch is not sufficient, because gates 1-4 reject the paint inside `DoSpray` before `OnUseItem` is called. The cursor-level mod must intercept at `ISprayer.DoSpray` itself (prefix on the static interface method) OR at `Thing.AttackWith` upstream. `SprayPaintPlus` v1.4.0 uses the `ISprayer.DoSpray` prefix approach.

## Tool-identity visibility

## Tool-identity visibility

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Inside `DoSpray`, the concrete tool type is available via the `sprayer` parameter (`sprayer is SprayGun`, `sprayer is SprayCan`, or `sprayer.GetType()`).

By the time execution reaches `OnServer.SetCustomColor`, the sprayer reference is no longer on the argument list; only `(Thing, int)` are passed. Any downstream logic that needs to know "which tool applied this paint?" must either hook `DoSpray` upstream, or capture the tool identity via a side-channel before `SetCustomColor` runs.

The `SprayPaintPlus` mod does not hook `DoSpray` as of version 1.x. Its `PaintAttackerTracker_Local` / `PaintAttackerTracker_Remote` patches (on `OnServer.AttackWith` / `AttackWithMessage.Process`) capture the painting `Human.ReferenceId` only; tool identity is not captured. See `../../Mods/SprayPaintPlus/RESEARCH.md` section 3.5.

## Tool-discrimination hook point

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Harmony patches that need to differentiate "paint from the gun" from "paint from the bare can" should hook the static `ISprayer.DoSpray` rather than `OnServer.SetCustomColor`.

A prefix that sets a static flag based on `sprayer is SprayGun` and a postfix that clears it scopes the flag to the duration of one `DoSpray` call. Any re-entrant `SetCustomColor` calls inside that event (for example, flood-fills applied by `SprayPaintPlus.NetworkPainterPatch`) see the flag as still set, because they run inside the same outer `DoSpray` frame on the same thread.

## Verification history

- 2026-04-21: page created. Findings sourced from Assembly-CSharp.dll decompile (DoSpray at line 334659-334715, SetCustomColor call at line 334711, SprayCan.OnUseItem at line 334636-334641, SprayGun.OnUseItem at line 334806-334812).
- 2026-04-21: added "DoSpray validity gates" section with the full body of `ISprayer.DoSpray` captured from a per-class decompile pass. Documents all 8 sequential reject paths (null PaintMaterial, same-colour block, Tool-off, IsEmpty, and the OnUseItem abort at the innermost point), the game-string citations for each error, and the key consequence for mods: `SprayGun.OnUseItem` alone is an insufficient hook because gates 1-4 in `DoSpray` reject the paint before `OnUseItem` is ever called. Additive; no prior claim changed.

## Open questions

- Do any non-tool code paths (debug console, save-restore, Stationpedia preview, mod-created UI) reach `OnServer.SetCustomColor` directly, bypassing `DoSpray`? If so, a tool-discrimination flag driven from `DoSpray` defaults to "no tool" on those paths, which may or may not match intended behavior.
