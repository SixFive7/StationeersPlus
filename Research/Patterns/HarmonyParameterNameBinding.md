---
title: Harmony parameter-name binding (a mismatch aborts the whole PatchAll)
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-18
sources:
  - Live incident 2026-07-18, PowerGridPlus CursorTooltipChokePatches vs Assets.Scripts.UI.Tooltip.HandleToolTipDisplay (client Player.log capture quoted below)
related:
  - ./HarmonyInheritedMethodTrap.md
  - ./PassiveTooltipPipelines.md
tags: [harmony]
---

# Harmony parameter-name binding (a mismatch aborts the whole PatchAll)

## The trap
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

A Harmony prefix or postfix that wants one of the original method's arguments binds it BY THE ORIGINAL'S EXACT PARAMETER NAME. Declaring the injected parameter under a different name is not a silent no-bind: HarmonyX fails at IL-compile time while the patch is being written, and the exception propagates out of `Harmony.PatchAll(assembly)`, ABORTING the remaining patch classes of the assembly. The mod then runs half-patched: every class processed before the bad one is live, everything after is missing.

Observed live 2026-07-18 (game 0.2.6403.27689): a prefix declared `ref PassiveTooltip passiveTooltip` against `Assets.Scripts.UI.Tooltip::HandleToolTipDisplay(PassiveTooltip cursorPassiveTooltip)`. Player.log verbatim:

```
[Error  :  HarmonyX] Failed to patch void Assets.Scripts.UI.Tooltip::HandleToolTipDisplay(Assets.Scripts.Objects.PassiveTooltip cursorPassiveTooltip): System.Exception: Parameter "passiveTooltip" not found in method void Assets.Scripts.UI.Tooltip::HandleToolTipDisplay(Assets.Scripts.Objects.PassiveTooltip cursorPassiveTooltip)
[Fatal  :Power Grid Plus] Power Grid Plus failed to apply patches: HarmonyLib.HarmonyException: IL Compile Error (unknown location) ---> ... Parameter "passiveTooltip" not found ...
```

The half-patched consequences in that incident: the mod's atomic-tick prefix (processed earlier in metadata order) ran, but a `[HarmonyReversePatch]` stub in a later class was never replaced, so its placeholder `NotImplementedException` fired on every tick of any world that walked that code path. Fixture worlds that never walked it stayed green, hiding the breakage.

## The rules this buys
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

- When injecting an original argument, copy the parameter name from the decompile VERBATIM (or use `[HarmonyArgument("originalName")]`, or index-based `__0`/`__1`, which do not depend on the name).
- After any build that adds or changes a patch class, verify the mod's own patches-complete log line in the target log before trusting any run. For PowerGridPlus that line is `Power Grid Plus patches applied`; its ABSENCE plus a `[Fatal :Power Grid Plus] failed to apply patches` line is the half-patched signature.
- Grep BepInEx logs for errors with open-bracket patterns (`\[Fatal` / `\[Error`), never `\[Fatal\]`: the real format is `[Fatal  :ModName]`, and the closed-bracket pattern silently matches nothing.
- A fixture battery run on a fresh world does not prove patching completed: reflection-driven fixtures bypass Harmony, and code paths gated on world content (a stationary battery, a dish pair) may never execute. Include one populated-save run and the patches-complete line check.

## Verification history

- 2026-07-18: page created from the live half-patched-mod incident; the failing binding, the log signature, and the fix (rename the injected parameter to the original's name) all verified in the same session.

## Open questions

- None.
