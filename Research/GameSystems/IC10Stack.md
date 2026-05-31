---
title: IC10 Data Stack, sp/ra Registers, and push/pop Semantics
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-31
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip (_PUSH_Operation, _POP_Operation, _PEEK_Operation, _JAL_Operation, _J_Operation, Execute)
related:
  - GameSystems/IC10ExecutionTick.md
  - GameSystems/IC10DeviceAddressing.md
tags: [ic10, logic]
---

The IC10 `ProgrammableChip` exposes a 512-entry data stack plus a stack-pointer register (`sp`) and a return-address register (`ra`). `push`/`pop`/`peek`/`poke`/`get`/`put` and the `jal` family all read and mutate these. This page records the exact array size, register indices, mutation order, and the out-of-range behavior, because a script that uses the stack for argument passing and for protecting `ra` across nested calls depends on every one of these details.

## Stack array, sp, and ra register layout

<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

The chip holds 18 registers and a 512-entry stack (ProgrammableChip fields):

```csharp
private readonly double[] _Registers = new double[18];   // line 393250
private readonly double[] _Stack = new double[512];      // line 393256
private readonly int _StackPointerIndex = 16;            // line 393252  -> sp == r16
private readonly int _ReturnAddressIndex = 17;           // line 393254  -> ra == r17
```

- The data stack is `double[512]`: valid indices `0` through `511`.
- `sp` is register `r16`. It is aliased to the name `sp` at compile time (`new _ALIAS_Operation(this, 0, "sp", $"r{_StackPointerIndex}")`, line 393704). `sp` holds the index where the NEXT `push` will write; it starts at 0.
- `ra` is register `r17`, aliased to `ra` (line 393705). `ra` is an ordinary register: `jal` overwrites it, and nothing auto-saves it across a nested `jal`. Protecting `ra` across a nested call is the script author's responsibility (`push ra` / `pop ra`).
- `GetStackSize()` returns `_Stack.Length`, i.e. 512 (line 394313-394316). This is also what the `StackSize` LogicType reads.
- On reset/recompile, `sp` is set to 0 (`_Registers[_StackPointerIndex] = 0.0;` at lines 393694 and 394214).

## push: write-then-increment

<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`_PUSH_Operation.Execute` (lines 392473-392492) reads `sp`, range-checks, writes `_Stack[sp]`, then increments `sp`:

```csharp
public override int Execute(int index)
{
    double variableValue = _Argument1.GetVariableValue(_AliasTarget.Register);
    int num = (int)Math.Round(_Chip._Registers[_Chip._StackPointerIndex]);
    if (num < 0)
    {
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackUnderFlow, _LineNumber);
    }
    if (num >= _Chip._Stack.Length)
    {
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackOverFlow, _LineNumber);
    }
    _Chip._Stack[num] = variableValue;
    _Chip._Registers[_Chip._StackPointerIndex] += 1.0;   // sp incremented AFTER the write
    ...
    return index + 1;
}
```

- The range check is on the PRE-increment `sp`. `push` with `sp == 512` throws `StackOverFlow` (index 512 is out of range). The last valid push is at `sp == 511`, leaving `sp == 512` afterward.
- `push` with `sp < 0` throws `StackUnderFlow` (only reachable if `sp` was already corrupted negative).
- `sp` is rounded (`Math.Round`) before use, so fractional `sp` values snap to the nearest integer index.

## pop: decrement-then-read

<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`_POP_Operation` derives from `_PEEK_Operation` but overrides `Execute` to decrement first (lines 392413-392431):

```csharp
public override int Execute(int index)
{
    _Chip._Registers[_Chip._StackPointerIndex] -= 1.0;   // sp decremented FIRST
    int variableIndex = _Store.GetVariableIndex(_AliasTarget.Register);
    int num = (int)Math.Round(_Chip._Registers[_Chip._StackPointerIndex]);
    if (num < 0)
    {
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackUnderFlow, _LineNumber);
    }
    if (num >= _Chip._Stack.Length)
    {
        throw new ProgrammableChipException(ProgrammableChipException.ICExceptionType.StackOverFlow, _LineNumber);
    }
    _Chip._Registers[variableIndex] = _Chip._Stack[num];
    ...
    return index + 1;
}
```

- `pop` decrements `sp` FIRST, then range-checks and reads `_Stack[sp]`.
- **`pop` on an empty stack (`sp == 0`) throws `StackUnderFlow`**: `sp` becomes `-1`, the `num < 0` check fires. The chip does NOT return 0 and does NOT wrap. Critically, `sp` has ALREADY been decremented to `-1` when the exception is thrown, so the corrupted value is visible if the chip is later resumed without a reset.
- `peek` (`_PEEK_Operation`, lines 392442-392460) reads `_Stack[sp - 1]` WITHOUT modifying `sp`; it throws `StackUnderFlow` when `sp == 0` (computed index `-1 < 0`).

## Out-of-range behavior: error-halt, not wrap or zero

<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

A stack range violation throws `ProgrammableChipException` with type `StackUnderFlow` or `StackOverFlow`. The chip's run loop (`ProgrammableChip.Execute(int runCount)`, lines 393771-393815) catches it:

```csharp
catch (ProgrammableChipException ex)
{
    CircuitHousing?.RaiseError(1);
    _ErrorLineNumber = ex.LineNumber;
    _ErrorType = ex.ExceptionType;
    _NextAddr = nextAddr;   // rewind to the faulting line; do NOT advance
    break;
}
```

Consequences:

- The housing's error flag is raised (`RaiseError(1)`), `_ErrorLineNumber`/`_ErrorType` record the fault, `_NextAddr` is rewound to the faulting instruction, and the run loop `break`s.
- The chip is now in an error state and stops executing. `CircuitHousing.Execute()` runs the chip only when `!ProgrammableChip.CompilationError` and the housing is powered/on; a raised runtime error halts useful progress until the chip is reset or re-flashed. It does NOT silently continue, does NOT return 0 for the bad pop, and does NOT wrap the index.
- The same three exception types are surfaced for the device-memory variants too: `put`/`putd` wrap `IMemoryWritable.WriteMemory` (lines 392581-392601, 392655-392675) and translate `StackUnderflowException`/`StackOverflowException` into the same `ICExceptionType.StackUnderFlow`/`StackOverFlow`. `ReadMemory`/`WriteMemory` themselves throw on `address < 0` or `address >= _Stack.Length` (lines 394279-394303).

Note there are two distinct exception families in the source: the chip-internal `ProgrammableChipException` (thrown directly by `push`/`pop`/`peek`) and the `StackUnderflowException`/`StackOverflowException` SystemExceptions (thrown by `ReadMemory`/`WriteMemory` and re-mapped by `put`/`putd`). Both end the run-loop iteration with an error.

## jal / j / branch-to-ra and how ra is clobbered

<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- `jal target` (`_JAL_Operation.Execute`, lines 391415-391419) sets `ra = index + 1` (the line after the `jal`) and then jumps:

```csharp
public override int Execute(int index)
{
    _Chip._Registers[_Chip._ReturnAddressIndex] = index + 1;
    return base.Execute(index);   // _J_Operation: jump to target
}
```

- `j target` (`_J_Operation`, lines 391201-391211) jumps without touching `ra`.
- A branch whose jump target is the register `ra` (e.g. `j ra`, `beq r0 1 ra`) jumps to whatever line number is currently in `ra`. This is the return mechanism.
- Because `jal` unconditionally overwrites `ra`, calling a second subroutine with `jal` from inside the first destroys the first's return address. A subroutine that calls another via `jal` and still needs to `j ra` afterward MUST bracket the inner call with `push ra` / `pop ra`. There is no hardware call stack; `ra` is a single shared register.
- The `*al` branch variants (`beqzal`, `bdseal`, etc.) set `ra = index + 1` only when the branch is taken (`if (hasJumped)`, e.g. `_BDSEAL_Operation`, lines 391429-391437).

## Verification History

<!-- append-only -->

- 2026-05-31: Page created. Decompiled Assembly-CSharp.dll v0.2.6228.27061. Confirmed stack is `double[512]` (indices 0-511), `sp == r16`, `ra == r17`, `_Registers` is `double[18]`. Confirmed push is write-then-increment with pre-increment range check (overflow at `sp == 512`), pop is decrement-then-read (underflow at `sp == 0`, with `sp` left at `-1`). Confirmed range violations throw `ProgrammableChipException` (StackUnderFlow / StackOverFlow), which the run loop catches, rewinds `_NextAddr`, raises the housing error, and breaks: error-halt, not return-0 or wrap. Confirmed `jal` unconditionally writes `ra = index+1` and `j`/branch-to-ra do not, so nested `jal` requires manual `push ra`/`pop ra`. Sourced while adversarially reviewing an airlock+forcefield IC10 script for stack-discipline defects.

## Open Questions

None at present.
