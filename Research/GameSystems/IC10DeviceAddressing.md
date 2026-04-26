---
title: IC10 device addressing (pin, alias, ReferenceId)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip (parser switch, _L_Operation, _LD_Operation, _S_Operation, _SD_Operation, _Operation._MakeDeviceVariable, _Operation_I, syntax-help formatter)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip (_BRDSE_Operation, _BRDNS_Operation, _BDSE_Operation, _BDNS_Operation, _SDSE_Operation, _SDNS_Operation)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ProgrammableChip (_PUSH_Operation, _POP_Operation, _StackPointerIndex, RETURN_ADDRESS_STRING / STACK_POINTER_STRING auto-aliases)
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

## Existence-check opcodes for guarding ReferenceId addressing

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`ld` / `sd` (and `getd` / `putd` / `clrd`) throw `DeviceNotFound` (or NRE in `_LD_Operation`'s case) when the supplied id resolves to null. To guard against this, IC10 has six existence-check opcodes that all share the same null-check semantics as the underlying read/write path:

| Opcode | Form | Behavior |
|---|---|---|
| `bdse <device> <addr>` | absolute branch | Branch to `addr` if device exists. |
| `bdns <device> <addr>` | absolute branch | Branch to `addr` if device does NOT exist. |
| `brdse <device> <offset>` | relative branch | Same as `bdse` but `offset` is a signed PC delta. |
| `brdns <device> <offset>` | relative branch | Same as `bdns` but `offset` is a signed PC delta. |
| `bdseal <device> <addr>` / `bdnsal <device> <addr>` | branch + link | Variants that also write `ra` (link register) on the taken branch. |
| `sdse <register> <device>` | set register | Writes `1` to register if device exists, `0` if not. |
| `sdns <register> <device>` | set register | Writes `1` to register if device does NOT exist, `0` if so. |

All six accept the same device-operand forms that `_MakeDeviceVariable` produces (pin / register / `$hex` / alias), so a literal ReferenceId works directly:

```csharp
// _BRDSE_Operation.Execute (covers bdse/brdse via wrapper)
if (_DeviceIndex.GetDevice(_Chip.CircuitHousing) == null) {
    hasJumped = false;
    return index + 1;
}
hasJumped = true;
return index + offset + _JumpIndex.GetVariableValue(_AliasTarget.Register);
```

`_DeviceIndex.GetDevice` for the numeric/register form ultimately routes through `CircuitHousing.GetLogicableFromId`, which means **the existence check enforces the same data-network membership constraint as the addressing itself**: a device that exists in the world but is not on `InputNetwork1.DataDeviceList` reads as "not set" through `bdse` / `bdns` / `sdse` / `sdns`. This is the correct guard, not a partial check.

`sdse` / `sdns` are the right tool for non-branching one-shot guards (set up a flag in a register, use it later); `bdse` / `bdns` jump straight to a labeled handler when the existence question gates a code path.

## `define` names work for `sd` but NOT for `brdns` / `bdns` / `bdse` / `brdse` / `sdse` / `sdns`

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`define <name> <value>` (`_DEFINE_Operation`) writes the name into `ProgrammableChip._Defines` (a `Dictionary<string, double>`). `alias <name> <register-or-pin>` (`_ALIAS_Operation`) writes the name into `ProgrammableChip._Aliases` (a `Dictionary<string, _AliasValue>`). The two dictionaries are entirely separate, and per-opcode resolvers consult one or the other, not both.

The asymmetry that bites scripts:

- `sd <devCode> <type> <value>` constructs `_DeviceId = new IntValuedVariable(...)`. `IntValuedVariable.GetVariableValue` consults `_Defines` (visible at line 1958 / 2038 in `ProgrammableChip` decompile via the `InstructionInclude.Define` flag inside `MaskDoubleValue = 0x6F`). A `define`'d hex literal therefore resolves correctly: `define BasePowerTransmitter $39FA7` followed by `sd BasePowerTransmitter MicrowaveAutoAimTarget 0` works.
- `brdns <devCode> <offset>` (and `bdns`, `bdse`, `brdse`, `sdse`, `sdns`, `bdnsal`, `bdseal`) constructs `_DeviceIndex = _Operation._MakeDeviceVariable(...)`, whose fallback path is `DeviceAliasVariable`. `DeviceAliasVariable.GetDevice` calls `GetAliasType(_Alias)`:

  ```csharp
  protected _AliasTarget GetAliasType(string alias, bool throwException = true)
  {
      if (string.IsNullOrEmpty(_Alias) || !_Chip._Aliases.TryGetValue(alias, out var value))
      {
          if (throwException)
              throw new ProgrammableChipException(ICExceptionType.IncorrectVariable, _LineNumber);
          return _AliasTarget.None;
      }
      return value.Target;
  }
  ```

  This checks `_Aliases` only; `_Defines` is never consulted. A token resolved by `_MakeDeviceVariable` to a `DeviceAliasVariable` therefore throws `IncorrectVariable` on a `define`'d name even though the same name resolves correctly when the same opcode receives it via `IntValuedVariable`.

In practice: `define BasePowerTransmitter $39FA7` then `brdns BasePowerTransmitter 2` raises "incorrect variable" at the `brdns` line. Workarounds, in order of preference:

- Use the `$hex` literal directly: `brdns $39FA7 2`. Loses the `define` readability for the guard line but is otherwise free.
- Pre-load the id into a register: `move r0 BasePowerTransmitter` (where `IntValuedVariable` resolves the define) then `brdns r0 2`. The `r0` form routes through `DirectDeviceVariable`, which calls `GetLogicableFromId(int)` directly instead of `GetAliasType`.
- Replace the `define` with `alias`. `alias` only accepts a register or a pin (`r0..r17`, `d0..d5`, `dr*`, `db`) per the syntax-help formatter (`alias` row: `STRING, REGISTER + DEVICE_INDEX`); it does not accept a numeric ReferenceId. So this is only an option after the value is already in a register.

The asymmetry exists because `_MakeDeviceVariable` was written for the original device-operand triad (pin / register / alias-of-pin-or-register), and the ReferenceId form was bolted on later without extending the alias-resolution branch to consult `_Defines`. The dedicated `*d` opcodes (`ld`, `sd`, `getd`, `putd`, `clrd`) sidestep this by skipping `_MakeDeviceVariable` entirely.

## Stack-driven iteration over a list of ReferenceIds

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

For scripts that link N pairs of devices identically (the "auto-aim every transmitter to its receiver" pattern, repeated per-network with at most one pair physically present), the cleanest extensibility is to push every ReferenceId onto the IC10 stack at the top of the script, then pop two at a time and link them. Each pair becomes two `push` lines at the top with no further structural changes.

Relevant primitives, all in `ProgrammableChip`:

- `_StackPointerIndex = 16`. The chip reserves `r16` as the stack pointer; `r17` is the return-address register.
- `STACK_POINTER_STRING = "sp"` and `RETURN_ADDRESS_STRING = "ra"`. At chip startup `OnPrefabsLoaded` runs `new _ALIAS_Operation(this, 0, "sp", "r16").Execute(0)` and the equivalent for `ra`, so `sp` and `ra` are pre-registered aliases visible to every script.
- `_PUSH_Operation` writes the value to `_Stack[sp]` then increments `sp` by 1. `_POP_Operation` decrements `sp` by 1 then reads `_Stack[sp]`. Stack starts at `sp = 0`.
- `_Chip._Stack.Length` defaults to 512, so up to 512 push-without-pop is safe; overflow throws `StackOverFlow`.

Idiom (the extensibility win is that `push $hex # name` is the only line a future user adds per ReferenceId):

```ic10
push $39FA7   # BasePowerTransmitter
push $3A124   # BasePowerReceiver
push $1DB99   # SiliconPowerTransmitter
push $236B7   # SiliconPowerReceiver

link_loop:
beqz sp link_end
pop r1
pop r0
brdns r0 2
sd r0 MicrowaveAutoAimTarget r1
brdns r1 2
sd r1 MicrowaveAutoAimTarget r0
j link_loop
link_end:
```

Pair semantics: pushing `(TX1, RX1, TX2, RX2)` leaves the stack as `[TX1, RX1, TX2, RX2]`. LIFO popping gives `RX2, TX2, RX1, TX1`, so each two-pop window is a coherent (TX, RX) pair, processed in reverse insertion order. The `brdns rN 2` guard inside the loop preserves the data-network-membership semantics already documented for `sd`: pairs whose dishes are not on this IC10's network silently skip without throwing.

This pattern also resolves the `define`-vs-`alias` asymmetry from the previous section. `push <token>` constructs a `DoubleValueVariable`, which consults `_Defines`, so `push BasePowerTransmitter` works after a `define BasePowerTransmitter $39FA7` if the user prefers to keep a separate defines block. The trade-off is two parallel lists to maintain (defines and pushes) versus one annotated push list with names in comments.

## Batch-op safety for missing devices

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Note the asymmetry: batch instructions (`sb`, `sbn`, `sbs`, `lb`, `lbn`, `lbs`, `lbns`) are **silent no-ops on empty result sets**. `sb PowerTransmitters On 1` with zero matching transmitters on the network does not throw; it just iterates an empty list. Reads (`lb`, `lbn`) with no match return `0` rather than throwing, controlled by the `BATCH_MODE` operand (Average / Sum / Minimum / Maximum). So the only addressing path that needs runtime existence guards is the `*d` family and `l`/`s`/`ls`/`ss`/`lr`/`get`/`put` when fed a numeric/register form (which dispatches to `GetLogicableFromId` and throws `DeviceNotFound` on null).

## Verification History

- 2026-04-26: Page created from decompiled `Assembly-CSharp.dll` (game version 0.2.6228.27061). Replaces an earlier draft of this page that cited a Steam Community forum post and contained a fabricated `sbns` opcode and an inaccurate description of `ld`/`sd` arity. Source for every claim is now the DLL paths in the frontmatter `sources` block.
- 2026-04-26: Added "Existence-check opcodes for guarding ReferenceId addressing" and "Batch-op safety for missing devices" sections from `_BRDSE_Operation` / `_BRDNS_Operation` / `_BDSE_Operation` / `_BDNS_Operation` / `_SDSE_Operation` / `_SDNS_Operation` decompiles. Documents that all six existence checks route through `GetLogicableFromId` and therefore inherit the data-network membership constraint.
- 2026-04-26: Added "`define` names work for `sd` but NOT for `brdns` / `bdns` / `bdse` / `brdse` / `sdse` / `sdns`" section. Discovered while debugging a real `brdns BasePowerTransmitter 2` failure ("incorrect variable") in a user script that used `define` to name device IDs. Root cause: `_MakeDeviceVariable` falls through to `DeviceAliasVariable`, whose `GetDevice` calls `GetAliasType` which only consults `_Aliases`, never `_Defines`. The dedicated `*d` opcodes (`ld`/`sd`/`getd`/`putd`) bypass `_MakeDeviceVariable` and use `IntValuedVariable` directly, which does consult `_Defines`, so `define` works there.
- 2026-04-26: Added "Stack-driven iteration over a list of ReferenceIds" section from `_PUSH_Operation`, `_POP_Operation`, `_StackPointerIndex = 16`, and the `STACK_POINTER_STRING = "sp"` / `RETURN_ADDRESS_STRING = "ra"` constants plus the `_ALIAS_Operation(this, 0, "sp", "r16").Execute(0)` startup wiring. Documents the extensibility pattern (one `push $hex # name` line per ReferenceId at the top, fixed `pop`-loop below) for IC10 scripts that link N pairs of devices identically across networks.

## Open Questions

- Behaviour of `ld` / `sd` when the supplied numeric id has bit 31 set (long ReferenceId outside `int.MinValue..int.MaxValue`). Decompile shows `int` narrowing at `_DeviceId.GetVariableValue` and at `GetLogicableFromId(int)`, but the runtime sign-extension / overflow path was not exercised. ReferenceIds in normal play fit in 32 bits, so the case may never arise.
