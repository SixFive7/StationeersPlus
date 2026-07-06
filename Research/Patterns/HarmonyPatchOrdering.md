---
title: Harmony patch ordering and pipeline semantics (HarmonyX)
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-06
sources:
  - .work/decomp/0.2.6403.27689/0Harmony.decompiled.cs :: HarmonyLib.Priority (4648-4676), PatchInfoSerialization.PriorityComparer (3465-3475), Patch constructors (3721-3792, -1 to 400 normalization 3729/3753/3779), HarmonyMethod.priority default (3004), PatchSorter (1236+), PatchFunctions.GetSortedPatchMethods (861-864), MethodPatcher WritePrefixes (9816-9882), MakeReturnLabel (9944-9959), WriteImpl state locals (9993-10000), WritePostfixes (10206-10214 head)
  - $(StationeersPath)\BepInEx\core\0Harmony.dll (assembly identity read from the decompile header and the DLL's VersionInfo)
related:
  - ./HarmonyPatchTypes.md
  - ./HarmonyPrefixReturnBool.md
  - ./ILRepackPerModCopy.md
  - ../GameSystems/ThirdPartyModIdentities.md
tags: [harmony]
---

# Harmony patch ordering and pipeline semantics (HarmonyX)

How HarmonyX orders multiple patches on one method and what "skip the original" actually skips. The load-bearing facts: patches sort by priority descending with ties broken by insertion order; every prefix runs even when an earlier prefix voted to skip; postfixes run even when the original was skipped; `__state` is one shared local per declaring patch class. Together these make a `Priority.First` prefix + `Priority.Last` postfix pair from one patch class a reliable bracket around the entire remaining patch pipeline on a method.

## Assembly identity and version scope
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Everything on this page was read from `.work/decomp/0.2.6403.27689/0Harmony.decompiled.cs`, the decompile of the `0Harmony.dll` in the Stationeers BepInEx install. That DLL is NOT pardeike Harmony: it is **HarmonyX**, the BepInEx fork. Assembly attributes in the decompile (lines 44-53):

```csharp
[assembly: AssemblyCompany("BepInEx")]
[assembly: AssemblyFileVersion("2.9.0.0")]
[assembly: AssemblyInformationalVersion("2.9.0")]
[assembly: AssemblyProduct("HarmonyX")]
[assembly: AssemblyTitle("0Harmony")]
[assembly: AssemblyVersion("2.9.0.0")]
```

Cross-checked against the live install: `$(StationeersPath)\BepInEx\core\0Harmony.dll` reports ProductName `HarmonyX`, ProductVersion `2.9.0`, alongside `BepInEx.dll` FileVersion `5.4.23.5`. So the semantics here are keyed to **HarmonyX 2.9.0 (shipped with BepInEx 5.4.23.5)**, not to the game version; the game version in the frontmatter and stamps records which decompile snapshot was read. HarmonyX rewrites the original method body in place via MonoMod ILHook manipulation (there is no separate "replacement method" as in classic Harmony docs); re-verify this page when the BepInEx/HarmonyX pairing changes, not when the game updates.

## Priority values
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`HarmonyLib.Priority` (lines 4648-4676, XML doc comments elided):

```csharp
public static class Priority
{
    public const int Last = 0;
    public const int VeryLow = 100;
    public const int Low = 200;
    public const int LowerThanNormal = 300;
    public const int Normal = 400;
    public const int HigherThanNormal = 500;
    public const int High = 600;
    public const int VeryHigh = 700;
    public const int First = 800;
}
```

A patch that sets no priority does not sort as -1: `HarmonyMethod.priority` defaults to `-1` (line 3004) and every `Patch` constructor normalizes it, `this.priority = ((priority == -1) ? 400 : priority);` (lines 3729 / 3753 / 3779). An attribute-less patch therefore runs at `Priority.Normal` (400).

## Sort order: priority descending, ties by insertion order
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`PatchInfoSerialization.PriorityComparer` (lines 3465-3475) is the comparison every sort path funnels into:

```csharp
internal static int PriorityComparer(object obj, int index, int priority)
{
    Traverse traverse = Traverse.Create(obj);
    int value = traverse.Field("priority").GetValue<int>();
    int value2 = traverse.Field("index").GetValue<int>();
    if (priority != value)
    {
        return -priority.CompareTo(value);
    }
    return index.CompareTo(value2);
}
```

The negated priority comparison sorts HIGHER priority values EARLIER (`First` = 800 before `Normal` = 400 before `Last` = 0). Equal priorities fall through to `index`, the registration counter, ascending: whoever patched first runs first among equals. Since each BepInEx plugin's `PatchAll` runs at plugin load, insertion order across mods is mod load order.

The comparer is consumed through `PatchFunctions.GetSortedPatchMethods` (861-864) -> `new PatchSorter(patches, debug).Sort(original)`. `PatchSorter.PatchSortingWrapper.CompareTo` (1260-1263) delegates to `PriorityComparer`, and the wrapper additionally carries `before` / `after` `HashSet`s populated from `[HarmonyBefore("owner")]` / `[HarmonyAfter("owner")]` (via `AddBeforeDependency` / `AddAfterDependency`), so explicit owner-id dependencies can override the pure priority order between specific mods. The sorted list is what the method patcher iterates (`GetSortedPatchMethodsAsPatches` consumed at line 10315). Prefixes, postfixes, transpilers, and finalizers are each sorted independently by the same rule.

## Every prefix runs; skipping is one branch AFTER the prefix block
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`MethodPatcher.WritePrefixes(ILEmitter.Label returnLabel)` (lines 9816-9882), trimmed to the load-bearing lines (`// ...` marks elision):

```csharp
private bool WritePrefixes(ILEmitter.Label returnLabel)
{
    if (prefixes.Count == 0)
    {
        return false;
    }
    // ...
    il.emitBefore = il.IL.Body.Instructions[0];          // prefix block goes to the TOP of the body
    // ... (__result local declared if the original returns a value; null for void)
    bool flag = prefixes.Any((PatchContext p) => p.method.ReturnType == typeof(bool)
        || p.method.GetParameters().Any((ParameterInfo pp) =>
            pp.Name == RunOriginalParam && pp.ParameterType.OpenRefType() == typeof(bool)));
    variableDefinition = (variables[RunOriginalParam] = il.DeclareVariable(typeof(bool)));
    VariableDefinition varDef = variableDefinition;
    il.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);            // __runOriginal starts true
    il.Emit(Mono.Cecil.Cil.OpCodes.Stloc, varDef);
    ILEmitter.Label label = ((value != null) ? il.DeclareLabel() : returnLabel);
    foreach (PatchContext prefix in prefixes)            // sorted order; NO branch inside the loop
    {
        MethodInfo method = prefix.method;
        // ...
        il.Emit(Mono.Cecil.Cil.OpCodes.Call, method);    // every prefix is invoked unconditionally
        // ...
        if (!AccessTools.IsVoid(method.ReturnType))
        {
            // ... (non-bool return type throws InvalidHarmonyPatchArgumentException)
            if (flag)
            {
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, varDef);
                il.Emit(Mono.Cecil.Cil.OpCodes.And);     // __runOriginal &= <this prefix's bool>
                il.Emit(Mono.Cecil.Cil.OpCodes.Stloc, varDef);
            }
        }
        // ...
    }
    if (!flag)
    {
        return false;
    }
    il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, varDef);       // the ONLY skip branch,
    il.Emit(Mono.Cecil.Cil.OpCodes.Brfalse, label);      // emitted AFTER all prefixes
    if (value == null)
    {
        return true;                                     // void original: skip target IS returnLabel
    }
    il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];
    il.MarkLabel(label);                                 // non-void: skip lands here,
    il.Emit(Mono.Cecil.Cil.OpCodes.Ldloc, value);        // loads __result, falls into the postfix block
    return true;
}
```

Consequences:

- A `bool` prefix returning `false` does NOT stop later prefixes. Its return value is only ANDed into the shared `__runOriginal` local; the loop emits a plain `Call` per prefix with no intervening branch. All prefixes from all mods run every call, in sorted order.
- The original-body skip is decided ONCE, by the single `Brfalse` after the whole prefix block, on the AND of every bool prefix's vote. One `false` from any prefix skips the original regardless of what the others returned (matches the cross-mod conflict analysis on [PowerGridPlusCrossModCompat](../Unsorted/PowerGridPlusCrossModCompat.md): "both prefixes WILL run regardless of return value").
- A later prefix can observe the accumulated vote by declaring a `bool __runOriginal` parameter (that is the second half of the `flag` predicate), but it cannot prevent earlier prefixes from having run.

## Postfixes always run, even when a prefix skipped the original
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Two pieces combine. First, `MakeReturnLabel` (lines 9944-9959, verbatim) rewrites every `ret` in the (possibly transpiled) original body into a branch to one shared label and appends the real `ret` at the end:

```csharp
private ILEmitter.Label MakeReturnLabel()
{
    if (ctx.IL.Body.Instructions.Count == 0)
    {
        il.Emit(Mono.Cecil.Cil.OpCodes.Nop);
    }
    ILEmitter.Label label = il.DeclareLabel();
    label.emitted = false;
    bool flag = false;
    foreach (Instruction item in il.IL.Body.Instructions.Where((Instruction ins) => ins.MatchRet()))
    {
        flag = true;
        item.OpCode = Mono.Cecil.Cil.OpCodes.Br;
        item.Operand = label.instruction;
        label.targets.Add(item);
    }
    label.instruction = Instruction.Create(flag ? Mono.Cecil.Cil.OpCodes.Ret : Mono.Cecil.Cil.OpCodes.Nop);
    il.IL.Append(label.instruction);
    return label;
}
```

Second, `WritePostfixes` re-anchors that same label at the START of the postfix block (head of the method, lines 10206-10214; `// ...` marks elision):

```csharp
private void WritePostfixes(ILEmitter.Label returnLabel)
{
    if (postfixes.Count == 0)
    {
        return;
    }
    // ...
    il.emitBefore = il.IL.Body.Instructions[il.IL.Body.Instructions.Count - 1];   // L10213
    il.MarkLabel(returnLabel);                                                     // L10214
    // ... (stores the in-flight return value into __result for a non-void original,
    //      then calls every void postfix in sorted order, then chains the
    //      passthrough postfixes whose return type equals their first parameter)
}
```

So every path that "returns from the original" lands at the start of the postfix block: each rewritten original `ret` branches there, and the prefix skip branch reaches it too (for a void original the skip target IS `returnLabel`, per the `(value != null) ? il.DeclareLabel() : returnLabel` line in `WritePrefixes`; for a non-void original the skip lands on the `Ldloc __result` emitted at the same end-of-body position and falls through into the postfix block, which stores that value back into `__result`). **Skipping the original never skips the postfixes.** A postfix that must distinguish "original ran" from "original skipped" declares a `bool __runOriginal` parameter; nothing else in the pipeline tells it.

Exception paths are the one thing that can break through: an unhandled exception thrown by the original or by a patch unwinds past the postfix block (finalizers, written by `WriteFinalizers`, are the layer that intercepts that; out of scope here).

## __state: one shared local per declaring patch class per patched method
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`WriteImpl` declares the `__state` locals up front, keyed by the patch method's DECLARING TYPE full name (lines 9993-10000 within the 9982+ loop over `prefixes.Union(postfixes).Union(finalizers)`):

```csharp
if (item.method.DeclaringType?.FullName == null || variables.ContainsKey(item.method.DeclaringType.FullName))
{
    continue;
}
foreach (ParameterInfo item2 in parameters.Where((ParameterInfo patchParam) => patchParam.Name == StateVar))
{
    variables[item.method.DeclaringType.FullName] = il.DeclareVariable(item2.ParameterType.OpenRefType());
}
```

One IL local per patch class per patched method, typed from the first `__state` parameter found for that class. A prefix `out T __state` and a postfix `T __state` in the SAME class share the local (that is the designed handoff); patches in DIFFERENT classes get independent locals and can never see each other's `__state`; the local is scoped to the patched method's body, so two different patched methods never share state through it.

## Consequence: a First/Last pair brackets the whole pipeline
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Combining the four facts: a `[HarmonyPriority(Priority.First)]` prefix runs before every other prefix (800 sorts first), a `[HarmonyPriority(Priority.Last)]` postfix runs after every other postfix (0 sorts last among postfixes), all prefixes always execute, and postfixes execute even when the original is skipped. So a single patch class contributing that pair observes the method call strictly before AND strictly after everything else patched onto it, including skip-original (`return false`) prefixes from other patch classes, with `__state` carrying data between the two ends privately. PowerGridPlus's ledger audit brackets the vanilla power-settle methods exactly this way to measure what the rest of the patch pipeline did to `_powerProvided`.

Limits of the bracket:

- Another patch at the same priority registered EARLIER wins the tie (insertion order): `First` beats everything below 800, not another `First` from a mod that loaded first.
- `[HarmonyBefore]` / `[HarmonyAfter]` dependency edges in the `PatchSorter` can reorder specific owners past a pure priority comparison.
- An unhandled exception anywhere in the pipeline bypasses the closing postfix (see the exception note above).
- Transpilers rewrite the original body itself; the rewritten body still sits between the prefix and postfix blocks, so it stays inside the bracket.

## Verification history

- 2026-07-06: page created. All excerpts read directly from `.work/decomp/0.2.6403.27689/0Harmony.decompiled.cs` (HarmonyX 2.9.0 per assembly attributes at lines 44-53; DLL identity cross-checked against `$(StationeersPath)\BepInEx\core\0Harmony.dll` VersionInfo, ProductName HarmonyX 2.9.0, next to BepInEx.dll 5.4.23.5): `Priority` constants (4648-4676), `HarmonyMethod.priority` default -1 (3004) with `Patch` constructor normalization to 400 (3729/3753/3779), `PriorityComparer` descending-priority insertion-order-tie sort (3465-3475) consumed via `PatchSorter` (1236+, CompareTo at 1260-1263) and `GetSortedPatchMethods` (861-864), `WritePrefixes` unconditional-call loop + single trailing `Brfalse` + void-original skip target = returnLabel (9816-9882), `MakeReturnLabel` ret-rewriting (9944-9959), `WritePostfixes` re-anchoring the return label at the postfix block start (10206-10214), and the `__state` per-declaring-class local declaration (9993-10000). No prior central page covered patch ordering; the partial statements on [ILRepackPerModCopy](./ILRepackPerModCopy.md) ("multiple postfixes run in sequence") and [PowerGridPlusCrossModCompat](../Unsorted/PowerGridPlusCrossModCompat.md) ("both prefixes WILL run regardless of return value") are consistent with and now grounded by this page.

## Open questions

None at creation.
