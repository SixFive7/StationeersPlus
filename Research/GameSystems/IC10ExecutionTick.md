---
title: IC10 Execution Tick Rate and Yield Semantics
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.CircuitHousing
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.CircuitHolders
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Networks.ElectricityManager
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager
related:
  - GameSystems/AtmosphericTick.md
  - GameClasses/ProgrammableChip.md
tags: [ic10, logic, power]
---

## Execution Invocation Chain

<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

IC10 code execution is driven by the following call stack, not by a MonoBehaviour Update/FixedUpdate:

1. **GameManager.cs:748** calls `ElectricityManager.ElectricityTick()`
2. **ElectricityManager.cs:111** calls `CircuitHolders.Execute()`
3. **CircuitHolders.cs:33-35** iterates all `ICircuitHolder` instances and calls their `Execute()` method
4. **CircuitHousing.cs:308-314** defines `Execute()` which calls `ProgrammableChip.Execute(128)` if conditions are met

Verbatim from CircuitHolders.cs:

```csharp
public static void Execute()
{
    AllCircuitHolders.ForEach(CircuitHolderAction);
}

private static readonly Action<ICircuitHolder> CircuitHolderAction = delegate(ICircuitHolder iCircuitHolder)
{
    iCircuitHolder?.Execute();
};
```

Verbatim from CircuitHousing.cs:

```csharp
public void Execute()
{
    if (GameManager.RunSimulation && !IsCursor && OnOff && Powered && GameManager.GameState == GameState.Running && !WorldManager.IsGamePaused && GameManager.GameState != GameState.None && !(ProgrammableChip == null) && !ProgrammableChip.CompilationError)
    {
        ProgrammableChip.Execute(128);
    }
}
```

## Tick Period and Frequency

<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The game tick loop is defined in **GameManager.cs:685**:

```csharp
await UniTask.Delay(GameTickSpeedMs, DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update, cancellationToken);
```

The tick speed is a constant defined at **GameManager.cs:148**:

```csharp
private static readonly int DefaultTickSpeedMs = 500;
```

Therefore:
- **Wall-clock period: 500 milliseconds per game tick**
- **Frequency: 2 Hz (0.5 seconds per tick)**
- **Not synchronized to atmospheric ticks (which run at 20 Hz / 50 ms)**

The ElectricityTick (and thus IC10 execution) is called once every game tick at line 748 within the main GameTick loop. This occurs at a **different frequency** than the 20 Hz atmospheric tick (which is documented as 50 ms).

## Instructions Executed Per Tick

<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

**CircuitHousing.RUN_COUNT constant** (CircuitHousing.cs:21):

```csharp
public const int RUN_COUNT = 128;
```

This constant is passed directly to the Execute() method call (CircuitHousing.cs:312):

```csharp
ProgrammableChip.Execute(128);
```

The `Execute(int runCount)` method in **ProgrammableChip.cs:6072-6119** processes a loop that executes up to `runCount` instructions per invocation:

```csharp
public void Execute(int runCount)
{
    if (_NextAddr < 0 || _NextAddr >= _LinesOfCode.Count || _LinesOfCode.Count == 0)
    {
        return;
    }
    int nextAddr = _NextAddr;
    int num = runCount;
    while (num-- > 0 && _NextAddr >= 0 && _NextAddr < _LinesOfCode.Count)
    {
        nextAddr = _NextAddr;
        try
        {
            _Operation operation = _LinesOfCode[_NextAddr].Operation;
            _NextAddr = operation.Execute(_NextAddr);
        }
        catch (ProgrammableChipException ex)
        {
            CircuitHousing?.RaiseError(1);
            _ErrorLineNumber = ex.LineNumber;
            _ErrorType = ex.ExceptionType;
            _NextAddr = nextAddr;
            break;
        }
        catch (Exception)
        {
            if (CircuitHousing != null)
            {
                CircuitHousing.RaiseError(1);
            }
            _ErrorLineNumber = (ushort)nextAddr;
            _ErrorType = ProgrammableChipException.ICExceptionType.Unknown;
            _NextAddr = nextAddr;
            break;
        }
        if (CircuitHousing != null)
        {
            _ErrorLineNumber = 0;
            _ErrorType = ProgrammableChipException.ICExceptionType.None;
            CircuitHousing.RaiseError(0);
        }
        if (_NextAddr < 0)
        {
            _NextAddr = -_NextAddr;
            break;
        }
    }
}
```

Therefore:
- **Instructions per tick: up to 128 per invocation**
- **This limit is enforced by the `while (num-- > 0)` loop counter**
- **Execution continues until either 128 instructions are consumed or a `yield` is encountered**

## Yield Instruction Implementation

<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The `yield` instruction is parsed as **ScriptCommand.yield** (ProgrammableChip.cs:1072-1078):

```csharp
case ScriptCommand.yield:
    if (array.Length != 1)
    {
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.IncorrectArgumentCount, lineNumber);
    }
    Operation = new _YIELD_Operation(chip, lineNumber);
    break;
```

The `_YIELD_Operation` class (ProgrammableChip.cs:4681-4692) implements the suspend semantics:

```csharp
private class _YIELD_Operation : _Operation
{
    public _YIELD_Operation(ProgrammableChip chip, int lineNumber)
        : base(chip, lineNumber)
    {
    }

    public override int Execute(int index)
    {
        return -index - 1;
    }
}
```

The return value `-index - 1` is a signal to the main Execute loop. In the ProgrammableChip.Execute() method (lines 6113-6117):

```csharp
if (_NextAddr < 0)
{
    _NextAddr = -_NextAddr;
    break;
}
```

When `Execute()` returns a negative value (from `yield`), the return value is negated to recover the original address, and the loop **breaks immediately**, suspending execution until the next tick.

Therefore:
- **`yield` returns control to the game tick immediately**
- **The next instruction after `yield` will execute in the next IC10 tick (500 ms later)**
- **No explicit countdown or sleep mechanism; suspension is implemented via return value and loop break**

## Relationship to Atmospheric Ticks

<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

IC10 execution and atmospheric ticks are **decoupled and asynchronous**:

- **Atmospheric tick rate: 20 Hz (50 ms)** (documented via `OnAtmosphericTick` callback)
- **IC10 tick rate: 2 Hz (500 ms)** (derived from GameManager.DefaultTickSpeedMs)
- **Ratio: 1 IC10 tick per 10 atmospheric ticks**

Both are called from the same **GameManager main game tick loop** (GameManager.cs:700-780), but they are independent operations:

- **Line 740**: `AtmosphericsManager.ThingAtmosphereTick()` (triggers OnAtmosphericTick on all Things)
- **Line 748**: `ElectricityManager.ElectricityTick()` (triggers IC10 execution via CircuitHolders.Execute)

The execution order within a single game tick is: atmospherics operations complete, then electricity (IC10) execution occurs. There is no synchronization lock or dependency between them.

## Verification History

<!-- append-only -->

- 2026-04-25: Page created. Decompiled Assembly-CSharp.dll v0.2.6228.27061 and traced full execution path from GameManager through CircuitHolders to ProgrammableChip.Execute(). Confirmed 128 instructions per tick, 500 ms period, and yield semantics via negative return value.

## Open Questions

None at present.
