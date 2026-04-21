---
title: PipeIgniter
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Objects.Pipes.PipeIgniter
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
  - rocketstation_Data/resources.assets :: GameObject StructurePipeIgniter (path_id 36224)
related:
  - ../GameClasses/Thing.md
tags: [power]
---

# PipeIgniter

## Class shape
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`Objects.Pipes.PipeIgniter` extends `Assets.Scripts.Objects.Pipes.DevicePipeMounted` (which extends `Device` which extends `SmallGrid`). The prefab is `StructurePipeIgniter`; the hand-held item form is `ItemPipeIgniter`.

Full decompiled class (verbatim, `Objects/Pipes/PipeIgniter.cs`):

```csharp
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.Localization2;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;

namespace Objects.Pipes;

public class PipeIgniter : DevicePipeMounted
{
    public override bool OnOff => true;

    public override void OnAtmosphericTick()
    {
        base.OnAtmosphericTick();
        if (base.NetworkAtmosphere != null && Powered && Error == 0 && Activate == 1)
        {
            base.NetworkAtmosphere.GasMixture.AddEnergy(new MoleEnergy(5.0));
            base.NetworkAtmosphere.Sparked = true;
        }
        OnServer.Interact(base.InteractActivate, 0);
    }

    public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
    {
        if (interactable.Action == InteractableType.Activate)
        {
            return HandleActivate(interactable, interaction, doAction);
        }
        return base.InteractWith(interactable, interaction, doAction);
    }

    private DelayedActionInstance HandleActivate(Interactable interactable, Interaction interaction, bool doAction)
    {
        DelayedActionInstance delayedActionInstance = new DelayedActionInstance
        {
            Duration = 0f,
            ActionMessage = interactable.ContextualName
        };
        if (!Powered)
        {
            return delayedActionInstance.Fail(GameStrings.DeviceNoPower);
        }
        if (Error == 1)
        {
            return delayedActionInstance.Fail(GameStrings.DeviceError);
        }
        if (!doAction)
        {
            return delayedActionInstance.Succeed();
        }
        OnServer.Interact(base.InteractActivate, 1);
        return delayedActionInstance.Succeed();
    }

    public override void CheckForPipe()
    {
        if (GameManager.RunSimulation && !IsValidPipe() && Error == 0)
        {
            OnServer.Interact(base.InteractError, 1);
        }
        else if (GameManager.RunSimulation && IsValidPipe() && Error == 1)
        {
            OnServer.Interact(base.InteractError, 0);
        }
    }
}
```

## Power draw
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`OnOff` is hardcoded to `true`. `Device.GetUsedPower(CableNetwork)` (Device.cs lines 1138-1149) returns the `UsedPower` SerializeField whenever `OnOff` is true and the structure is completed, so the igniter pays its full rated wattage continuously while it has a valid cable connection:

```csharp
public virtual float GetUsedPower(CableNetwork cableNetwork)
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork) return -1f;
    if (!OnOff || !base.IsStructureCompleted) return 0f;
    return UsedPower;
}
```

`Device.UsedPower` default is `10f` (Device.cs:37), overridden per-prefab via the Unity SerializeField. The actual rated wattage for `StructurePipeIgniter` lives in `resources.assets` on the `PipeIgniter` MonoBehaviour (path_id 100393 under GameObject path_id 36224). Stationpedia's "Power Use" value in-game is the authoritative display; confirm with InspectorPlus if needed. Request: `types=["PipeIgniter"], fields=["UsedPower","OnOff","Powered","Activate","Error"]`.

Key consequence: **the power draw does not change between the idle state and the active-sparking state.** The igniter draws `UsedPower` every simulation tick as long as it is cabled, whether or not anyone is pressing Activate.

## Ignition semantics
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`OnAtmosphericTick` reads `Activate == 1` as the trigger. When triggered and `Powered && Error == 0`:

1. Adds 5.0 `MoleEnergy` to the pipe network's `GasMixture` (`NetworkAtmosphere.GasMixture.AddEnergy(new MoleEnergy(5.0))`).
2. Sets `NetworkAtmosphere.Sparked = true` for the current tick.
3. Unconditionally calls `OnServer.Interact(base.InteractActivate, 0)` at the end of the tick, so the Activate flag resets to 0 after one attempt.

Because `Activate` auto-resets to 0 each atmospheric tick, the device is a one-shot sparker: one player-initiated `HandleActivate` produces exactly one ignition attempt on the next atmospheric tick. To ignite every tick, an external driver (IC10 script, logic writer, automated interaction) must set `Activate = 1` again between each tick.

Note the sequence inside `OnAtmosphericTick`: the reset call runs even when the ignition condition is false, so a write to `Activate` outside the "Powered, no error, valid pipe" window still consumes the flag with no effect.

## MoleEnergy cost
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Each successful ignition contributes `5.0` `MoleEnergy` to the pipe's gas mixture. This is a gas-mixture energy deposit (heats the gas) and is unrelated to the cable-network power draw; no additional electrical power is consumed per ignition.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- 2026-04-21: page created from decompiled `PipeIgniter.cs` and `Device.cs` (Stationeers 0.2.6228.27061). Exact `UsedPower` value for `StructurePipeIgniter` not read from the prefab typetree (UnityPy could not parse the MonoBehaviour structure); candidate raw-bytes float scan returned `30.0` at offset 248 among other plausible floats but is not verified.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- Exact `UsedPower` wattage of `StructurePipeIgniter`. Resolve by reading the Stationpedia in-game entry or by running InspectorPlus with `types=["PipeIgniter"], fields=["UsedPower"]` once the game is loaded.
