---
title: IC10 device addressing (pin, alias, ReferenceId)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip (parser switch, _L_Operation, _LD_Operation, _S_Operation, _SD_Operation, _Operation._MakeDeviceVariable, _Operation_I, syntax-help formatter)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.CircuitHousing.GetLogicableFromId
related:
  - ./LogicType.md
  - ./IC10ExecutionTick.md
tags: [ic10, logic]
---

# IC10 device addressing

IC10 supports three forms for the "which device" operand of read/write instructions: a pin reference (`d0`..`d5`, `dr0`..`dr5`, `db`), an alias defined via `alias`, or a numeric ReferenceId (`$hex` literal, decimal literal, or a register holding the value). Some opcodes accept all three forms, others are restricted to ReferenceId only.

## Opcode list and accepted device-operand forms

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Source of truth: the syntax-help formatter in `ProgrammableChip` (the switch returning `MakeString(...)` for each `ScriptCommand`). It declares per-opcode token classes for each operand. `DEVICE_INDEX` covers `d0..d5`, `dr*`, and `db`; `REGISTER` covers `r0..r17` and aliases resolving to a register; `REF_ID` covers `$hex` / decimal / a register holding a ReferenceId. The set of token classes a slot accepts is the OR of those listed.

| Opcode | Device operand accepts | Other operands |
|---|---|---|
| `l`   | DEVICE_INDEX + REGISTER + REF_ID | register, LOGIC_TYPE |
| `ld`  | REGISTER + REF_ID                | register, LOGIC_TYPE |
| `s`   | DEVICE_INDEX + REGISTER + REF_ID | LOGIC_TYPE, register |
| `sd`  | REGISTER + REF_ID                | LOGIC_TYPE, register |
| `ls`  | DEVICE_INDEX + REGISTER + REF_ID | register, SLOT_INDEX, LOGIC_SLOT_TYPE |
| `ss`  | DEVICE_INDEX + REGISTER + REF_ID | SLOT_INDEX, LOGIC_SLOT_TYPE, register |
| `lr`  | DEVICE_INDEX + REGISTER + REF_ID | register, REAGENT_MODE, INTEGER |
| `get` | DEVICE_INDEX + REGISTER + REF_ID | register, address |
| `put` | DEVICE_INDEX + REGISTER + REF_ID | address, value |
| `getd`| REGISTER + REF_ID                | register, address |
| `putd`| REGISTER + REF_ID                | address, value |
| `clrd`| REGISTER + NUMBER                | (clears stack of device by id) |
| `lb`  | DEVICE_HASH (prefab hash)        | register, LOGIC_TYPE, BATCH_MODE |
| `lbn` | DEVICE_HASH + NAME_HASH          | register, LOGIC_TYPE, BATCH_MODE |
| `lbs` | DEVICE_HASH + SLOT_INDEX         | register, LOGIC_SLOT_TYPE, BATCH_MODE |
| `lbns`| DEVICE_HASH + NAME_HASH + SLOT_INDEX | register, LOGIC_SLOT_TYPE, BATCH_MODE |
| `sb`  | DEVICE_HASH                      | LOGIC_TYPE, register |
| `sbn` | DEVICE_HASH + NAME_HASH          | LOGIC_TYPE, register |
| `sbs` | DEVICE_HASH + SLOT_INDEX         | LOGIC_SLOT_TYPE, register |

Notable: `sbns` does NOT exist (the `lbns` slot-load-by-name has no slot-store-by-name twin). The `*d` variants (`ld`, `sd`, `getd`, `putd`, `clrd`) are the dedicated ReferenceId-only opcodes; the non-`d` variants (`l`, `s`, `ls`, `ss`, `lr`, `get`, `put`) accept all three operand forms including ReferenceId.

## How the ReferenceId form is parsed

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`ProgrammableChip._Operation._MakeDeviceVariable(chip, lineNumber, deviceCode)` is the dispatcher used by every opcode whose device operand is `DEVICE_INDEX + REGISTER + REF_ID`:

```csharp
if (deviceCode.Length > 0 && (deviceCode[0] == '$' || deviceCode[0] == '%' || char.IsDigit(deviceCode[0])))
    return new DirectDeviceVariable(chip, lineNumber, deviceCode, MaskDoubleValue | DeviceIndex | NetworkIndex, throwException: false);
if (deviceCode.Length > 1 && deviceCode[0] == 'r' && char.IsDigit(deviceCode[1]))
    return new DirectDeviceVariable(chip, lineNumber, deviceCode, MaskDoubleValue | DeviceIndex | NetworkIndex, throwException: false);
string[] array = deviceCode.Split(':');
if (array.Length != 0 && array[0].StartsWith('d')) {
    if (array[0] == "db")
        return new DeviceIndexVariable(chip, lineNumber, deviceCode, MaskDeviceIndex, throwException: false);
    if (Regex.IsMatch(array[0], "^(d[0-9]|dr*[r0-9][0-9])$"))
        return new DeviceIndexVariable(chip, lineNumber, deviceCode, MaskDeviceIndex, throwException: false);
}
return new DeviceAliasVariable(chip, lineNumber, deviceCode, MaskDoubleValue | DeviceIndex | NetworkIndex, throwException: false);
```

Recognised token shapes:

- `$AD4F`, `%1010`, or a leading digit → numeric literal (ReferenceId or numeric variant), wrapped as `DirectDeviceVariable`.
- `r0`..`r17` → register form, wrapped as `DirectDeviceVariable`. The register holds a numeric value treated as a ReferenceId at execute time.
- `db` → IC10 housing self-reference, `DeviceIndexVariable`.
- `d0`..`d5` and `dr*` (with optional network suffix `:N`) → pin form, `DeviceIndexVariable`.
- Anything else → `DeviceAliasVariable`, which resolves through the script's `alias` / `define` table.

For the dedicated `ld` / `sd` / `getd` / `putd` opcodes, the parser bypasses `_MakeDeviceVariable` and constructs `_Operation_I`:

```csharp
private abstract class _Operation_I : _Operation_1_0 {
    protected readonly IntValuedVariable _DeviceId;
    public _Operation_I(ProgrammableChip chip, int lineNumber, string registerStoreCode, string referenceId)
        : base(chip, lineNumber, registerStoreCode) {
        _DeviceId = new IntValuedVariable(chip, lineNumber, referenceId, MaskDoubleValue, throwException: false);
    }
}
```

The device operand is parsed straight as an int (no pin handling, no `db`, no alias-as-pin path). The accepted token classes therefore narrow to REGISTER + REF_ID per the help-string spec.

## Resolution at execute time: GetLogicableFromId

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`_LD_Operation.Execute` and `_SD_Operation.Execute` both resolve the device by calling `_Chip.CircuitHousing.GetLogicableFromId(int deviceId)`:

```csharp
public ILogicable GetLogicableFromId(int deviceId, int networkIndex = int.MinValue)
{
    if (deviceId == 0L) return null;
    Device device = Referencable.Find<Device>(deviceId);
    if (base.InputNetwork1 != null && !base.InputNetwork1.DataDeviceList.Contains(device))
        return null;
    if (networkIndex != int.MinValue)
        return ((IConnected)device)?.GetNetwork(networkIndex);
    return device;
}
```

Three constraints follow:

1. **The id is interpreted as a `Referencable.Find<Device>(int)` lookup.** This is the same global referencable registry that backs `Thing.ReferenceId` (LogicType 217). So yes, the ReferenceId you read off any device is what `ld` / `sd` accept.
2. **The device must be on the IC10 housing's data network.** If `InputNetwork1` is non-null and the resolved device is not in `InputNetwork1.DataDeviceList`, the method returns null. ReferenceId addressing does not bypass network reachability; it is not a global "any device anywhere" handle. The same data-network-only constraint that limits `lb` / `lbn` applies.
3. **The id is narrowed to `int` on the IC10 side.** `Referencable.ReferenceId` is a `long`, but `_DeviceId` is `IntValuedVariable` and `GetLogicableFromId` takes `int`. ReferenceIds in saves and runtime sequences fit in 32 bits in normal play, but a value with bit 31 set will sign-extend or truncate at the register/double boundary; the safe assumption is that ReferenceIds outside `int.MinValue..int.MaxValue` cannot be addressed by IC10. (Not separately verified against extreme-id behavior; flagged in Open Questions.)

`_S_Operation` (the non-`d` version) instead uses `_DeviceIndex.GetDevice(_Chip.CircuitHousing)` (an `IDeviceVariable`), which reaches `GetLogicableFromId` indirectly when the parsed form is the numeric/register variant. The pin and alias forms reach it via `GetLogicableFromIndex` instead.

## Read-side null guard: missing in _LD_Operation

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`_SD_Operation.Execute` checks for null before dereferencing:

```csharp
ILogicable logicableFromId = _Chip.CircuitHousing.GetLogicableFromId(variableValue);
if (logicableFromId == null)
    throw new ProgrammableChipException(ICExceptionType.DeviceNotFound, _LineNumber);
```

`_LD_Operation.Execute` does NOT:

```csharp
ILogicable logicableFromId = _Chip.CircuitHousing.GetLogicableFromId(variableValue);
LogicType variableValue2 = _LogicType.GetVariableValue(_AliasTarget.Register);
if (variableValue2 == LogicType.None)
    throw new ProgrammableChipException(ICExceptionType.LogicTypeIsNone, _LineNumber);
if (!logicableFromId.CanLogicRead(variableValue2))   // NRE if logicableFromId == null
    throw new ProgrammableChipException(ICExceptionType.IncorrectLogicType, _LineNumber);
```

If the supplied id resolves to `null` (id is 0, device does not exist, or device is not on the IC10's data network), `ld` will throw a NullReferenceException through `CanLogicRead`. The IC10 surfaces this as a generic chip error rather than the cleaner `DeviceNotFound` that `sd` raises. Mods that wrap or replace `_LD_Operation` should guard explicitly.

## ReferenceId is also addressable via the non-`d` opcodes

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Because `_MakeDeviceVariable` accepts `$hex` / decimal / register tokens, the regular `l`, `s`, `ls`, `ss`, `lr`, `get`, `put` instructions also accept a ReferenceId in the device slot. Example:

```ic10
alias pump $AD4F            # define alias for a known ReferenceId
l r0 pump Pressure          # regular `l` resolves the alias to the numeric form
ld r1 $AD4F Setting         # equivalent via the dedicated `ld` opcode
```

The pragmatic difference between `l <reg> <refid> <type>` and `ld <reg> <refid> <type>` is the parse path (and the `ld` null-guard gap above), not the addressing capability.

## Verification History

- 2026-04-26: Page created from decompiled `Assembly-CSharp.dll` (game version 0.2.6228.27061). Replaces an earlier draft of this page that cited a Steam Community forum post and contained a fabricated `sbns` opcode and an inaccurate description of `ld`/`sd` arity. Source for every claim is now the DLL paths in the frontmatter `sources` block.

## Open Questions

- Behaviour of `ld` / `sd` when the supplied numeric id has bit 31 set (long ReferenceId outside `int.MinValue..int.MaxValue`). Decompile shows `int` narrowing at `_DeviceId.GetVariableValue` and at `GetLogicableFromId(int)`, but the runtime sign-extension / overflow path was not exercised. ReferenceIds in normal play fit in 32 bits, so the case may never arise.
