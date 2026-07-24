---
title: SprayCan
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-25
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:94-98
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.SprayCan
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.ISprayer
  - $(StationeersPath)\rocketstation_Data\StreamingAssets\Data\paints.xml
related:
  - ./ColorSwatch.md
  - ./ISprayer.md
  - ../GameSystems/DLCGating.md
tags: [prefab]
---

# SprayCan

Vanilla game class representing a spray-paint can consumable. The game ships one `SprayCan` prefab per color.

## Declaration
<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

```
public class SprayCan : Consumable, ISprayer, IUsedAmount, IUsed
{
    [Header("Spray Can")]
    public Material PaintMaterial;

    private static readonly GasMixture PollutionMixture = new GasMixture(new Mole(Chemistry.GasType.Pollutant, new MoleQuantity(0.009999999776482582), new MoleEnergy(73.0)));

    public override int ConstructingSoundHash => Animator.StringToHash("SprayPaintLong");

    public override int FinishedConstructingSoundHash => Animator.StringToHash("SprayPaintFinished");

    public float TimeToUse() => 0.5f;

    public Material GetPaintMaterial() => PaintMaterial;

    public override bool UseDefaultUiUsingSounds() => false;

    public override bool OnUseItem(float quantity, Thing onUseThing)
    {
        base.Quantity -= quantity;
        AtmosphericEventInstance.CloneGlobalAddGasMix(base.WorldGrid, PollutionMixture);
        return true;
    }
}
```

`OnUseItem` is the whole of the can's per-use behavior: decrement quantity, emit one `PollutionMixture` (0.01 mol Pollutant at 73 J) into the can's `WorldGrid`. It always returns true.

## Fields
<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

Source: F0015, re-confirmed against 0.2.6403.27689.

- `PaintMaterial` / `PaintableMaterial`: The `Material` representing the can's current color. The game has one `SprayCan` prefab per color. `GameManager.GetColorSwatch(Material)` is the game's own reverse lookup from this material to the `ColorSwatch`, used by `ISprayer.DoSpray`.
- `Thumbnail`: The `Sprite` shown in the inventory slot. Tied to the prefab, so switching color requires updating it manually.
- `Quantity`: Decremented on each use. Setting it to 0 before vanilla runs effectively makes the can infinite.
- `DLCType` (inherited from `Thing`, `[SerializeField] private DLCType _dlcType` with a getter-only `public DLCType DLCType`): the entitlement required to obtain this can. `DLCType.None` on the twelve vanilla cans; `DLCType.MetallicPaints` on the four metallic cans. This is the ONLY place a paint color's DLC requirement is recorded, because `ColorSwatch` has no `DLCType` field. See `../GameSystems/DLCGating.md`.

## One prefab per color, and the metallic set
<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

`rocketstation_Data/StreamingAssets/Data/paints.xml` lists a Tool Manufactory recipe per can. Sixteen cans ship at 0.2.6403.27689: twelve vanilla plus four from the Metallic Paints DLC.

Vanilla twelve: `ItemSprayCanBlack`, `ItemSprayCanBlue`, `ItemSprayCanBrown`, `ItemSprayCanGreen`, `ItemSprayCanGrey`, `ItemSprayCanKhaki`, `ItemSprayCanOrange`, `ItemSprayCanPink`, `ItemSprayCanPurple`, `ItemSprayCanRed`, `ItemSprayCanWhite`, `ItemSprayCanYellow`.

Metallic four: `ItemSprayCanMetallicBronze`, `ItemSprayCanMetallicGold`, `ItemSprayCanMetallicObsidian`, `ItemSprayCanMetallicSilver`.

Every recipe, vanilla and metallic alike, is `Time 5`, `Energy 500`, `Iron 1`. The recipes ship to all players; the DLC gate in the fabricator path is what stops a non-owner producing the metallic four.

Note the prefab name uses `Grey` while the swatch name is `ColorGray` (see `./ColorSwatch.md`). Do not derive one from the other by string manipulation.

Runtime enumeration on 2026-07-25 in game version 0.2.6403.27689 confirmed all 16 prefabs are present in `Prefab.AllPrefabs` regardless of entitlement, each with a non-null `PaintMaterial`, each resolving to exactly one `CustomColors` swatch by material reference, with the twelve vanilla cans carrying `DLCType.None` and the four metallic cans `DLCType.MetallicPaints`. The full index map is on `./ColorSwatch.md` under "Metallic swatch addition"; the method and its threading caveats are recorded there too.

Because `SprayCan` prefabs are the only carrier of the color-to-DLC mapping, walking `Prefab.AllPrefabs` for `SprayCan` instances is how a mod resolves which `CustomColors` entries are entitlement-gated:

```
foreach (Thing thing in Prefab.AllPrefabs)
{
    if (thing is SprayCan prefabCan && prefabCan.PaintMaterial != null)
    {
        // prefabCan.PaintMaterial identifies the swatch, prefabCan.DLCType is the gate
    }
}
```

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-25 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0015. No conflicts.
- 2026-07-25: re-read against 0.2.6403.27689. Added the verbatim class declaration, the `PollutionMixture` constants, and the inherited `DLCType` field. Added "One prefab per color, and the metallic set" listing all sixteen prefab names from `paints.xml` and the identical recipe cost. Added `related` links and the cross-reference to the new `../GameSystems/DLCGating.md`. No existing claim was contradicted; all prior content was additive-confirmed.

## Open questions

None. The distinct-material question raised at page revision time was resolved the same day by runtime enumeration: all 16 cans map one-to-one onto 16 distinct swatch materials. See `./ColorSwatch.md`.
