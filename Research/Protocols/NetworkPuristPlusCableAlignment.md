---
title: NetworkPuristPlus Cable Alignment and Rotation
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-17
sources:
  - E:\Steam\steamapps\workshop\content\544550\3724874914\NetworkPuristPlus.dll
  - .work/decomp/0.2.6228.27061/NetworkPuristPlus.decompiled.cs
tags: [network, harmony, transforms]
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/CableNetwork.md
  - ../Patterns/ServerAuthoritativeSimulation.md
---

# NetworkPuristPlus Cable Alignment and Rotation

NetworkPuristPlus v1.1.0 is a server-authoritative mod (via BepInEx + Harmony + LaunchPad networking) that strips long-piece variants and optionally aligns straight cables to a canonical rotation orientation. The `AlignStraightCables` setting is the suspect in multiplayer cable rotation desync: it rotates cables post-placement without explicit server-only gating at the rotation point.

## Harmony patches
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

The mod registers five patches via `new Harmony("net.networkpuristplus").PatchAll()` (line 148):

1. **`[HarmonyPatch(typeof(World), "OnLoadingFinished")]` (line 659)**
   - Class: `ReplaceLongPiecesOnLoadPatch`
   - Type: `Postfix`
   - Purpose: Rebuilds placed long pieces into single-tile segments on world load.

2. **`[HarmonyPatch(typeof(World), "OnLoadingFinished")]` (line 873)**
   - Class: `NormalizeCableRollOnLoadPatch`
   - Type: `Postfix`
   - Purpose: Aligns all straight cables on world load when `Settings.CableAlignmentEnabled`.

3. **`[HarmonyPatch(typeof(Cable), "OnRegistered")]` (line 852)**
   - Class: `NormalizeCableRollOnRegisterPatch`
   - Type: `Postfix`
   - Purpose: Aligns freshly-placed cables immediately post-registration when `Settings.CableAlignmentEnabled`.

4. **`[HarmonyPatch(typeof(Constructor), "SpawnConstruct")]` (line 926)**
   - Class: `RewriteLongVariantOnConstructPatch`
   - Type: `Prefix` (returns bool, can skip base)
   - Purpose: Rewrites long-piece builds into single-tile spawns at build time.

5. **`[HarmonyPatch(typeof(SPDADataHandler), "HandleThingPageOverrides")]` (line 986)**
   - Class: `HideLongVariantsStationpediaPatch`
   - Type: `Postfix`
   - Purpose: Re-hides long-variant prefabs from the Stationpedia after page-override passes.

## The AlignStraightCables config setting
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

Config name: `AlignStraightCables`, bound via `config.Bind<bool>("Server - Cables", "Align Straight Cables", true, ...)` (line 316). Binding is in static `Settings` class, called once in `Awake()` at line 132.

Where it is read:

1. **`CableAlignmentEnabled` property** (lines 283-294): gated by master `Enabled` toggle.
2. **Join validation** (lines 180, 222): broadcasted and validated on client join.
3. **Patch guards** (lines 859, 878): checked before alignment.

## Cable rotation mechanics: CableRoll class
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

Rotation logic in `CableRoll` static utility class (lines 779-851):

### Canonical orientation mapping

```csharp
internal static Quaternion Canonical(Quaternion rotation)
{
    Vector3 val = rotation * Vector3.forward;
    float num = Mathf.Abs(val.x);
    float num2 = Mathf.Abs(val.y);
    float num3 = Mathf.Abs(val.z);
    if (num >= num2 && num >= num3)
        return CanonX;
    if (num2 >= num3)
        return CanonY;
    return CanonZ;
}
```

Deterministic: transforms forward vector, classifies by axis dominance.

### Normalization (lines 821-850)

```csharp
internal static bool Normalise(Cable c)
{
    if (!IsNormalisableStraight(c))
        return false;
    Quaternion thingTransformRotation = ((Thing)c).ThingTransformRotation;
    Quaternion val = Canonical(thingTransformRotation);
    if (Quaternion.Angle(thingTransformRotation, val) < 1f)
        return false;
    ((Thing)c).ThingTransform.rotation = val;               // line 842: ROTATION SET
    ((Thing)c).RegisteredRotation = val;                   // line 843
    ((Structure)c).Direction = val;                        // line 844
    if (NetworkManager.IsServer)                           // line 845: SERVER CHECK (too late)
    {
        ((Thing)c).NetworkUpdateFlags = (ushort)(((Thing)c).NetworkUpdateFlags | 1);
    }
    return true;
}
```

**Critical issue:** Rotation assignments (lines 842-844) execute BEFORE server-only check (line 845).

## The cable registration patch
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

```csharp
[HarmonyPatch(typeof(Cable), "OnRegistered")]
internal static class NormalizeCableRollOnRegisterPatch
{
    private static void Postfix(Cable __instance)
    {
        if (!Settings.CableAlignmentEnabled || !GameManager.RunSimulation || (int)GameManager.GameState == 4)
        {
            return;
        }
        try
        {
            CableRoll.Normalise(__instance);
        }
        catch (Exception arg)
        {
            NetworkPuristPlusPlugin.PlayerWarn($"could not align a just-built cable: {arg}");
        }
    }
}
```

**No explicit server-only guard.** `Cable.OnRegistered` fires on both server AND client.

## Verdict on Task 4
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

**YES.** NetworkPuristPlus mutates cable rotation after `Cable.OnRegistered` runs on BOTH server AND client without server-only gating at the rotation-setting point.

**Method:** `CableRoll.Normalise(Cable c)` (line 821)

**Rotation mutations:**
- Line 842: `((Thing)c).ThingTransform.rotation = val;`
- Line 843: `((Thing)c).RegisteredRotation = val;`
- Line 844: `((Structure)c).Direction = val;`

**Problem:** These execute unconditionally. The server check at line 845 only gates `NetworkUpdateFlags`, not the transform mutation.

**Called from:** `NormalizeCableRollOnRegisterPatch.Postfix` (line 865), which patches `Cable.OnRegistered` with no explicit server-only guard.

## Verification history

- 2026-05-17: page created. Decompiled NetworkPuristPlus.dll (Workshop 3724874914) via ilspycmd. Verdict: YES, cable rotation mutated on both sides without server-only gating at rotation point (lines 842-844 before server check at 845).

## Open questions

- Does `Cable.DeserializeOnJoin` invoke `OnRegistered` synchronously?
- Multi-frame delay between local rotation and network replication?
- Floating-point context near axis-dominance boundaries causing divergent canonical selection?

