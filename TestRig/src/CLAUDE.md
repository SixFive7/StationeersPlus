# TestRig source

The rig is one AOT-compiled C# binary at `TestRig/testrig.exe`, built from this tree.

**The binary is build output and it IS committed to git.** That is deliberate, and it
comes with a guard, explained below. Read that section before you touch anything here.

Rig rules apply in full: `TestRig/CLAUDE.md` (the lock, the save tiers, never taking
the developer's foreground). This file is about the source tree only.

## Rebuild

```
dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64
```

That is the whole procedure. It builds, publishes AOT, and installs the result at
`TestRig/testrig.exe` via the `InstallBinary` target. There is no script.

```
dotnet test TestRig/src/TestRig.slnx
```

**Commit `testrig.exe` in the same commit as the source change that produced it.**
A source commit without a matching binary commit leaves the next agent holding a
binary that refuses to run.

## Why a committed binary, and why it refuses to run when stale

A consumer of the rig should never need a `dotnet build` step. Committing the binary
buys that, and costs one risk: the committed artifact drifting from the source.

Staleness of an on-disk artifact has already cost this project two whole sessions,
once on stale mods and once on a stale game version. In both cases the evidence was
present and scrolled past. So this is a refusal, not a warning:

- The binary embeds a SHA-256 digest of every `.cs`, `.csproj`, `.props`, `.sln` and
  `.slnx` under `TestRig/src/`, **and over every `Mods|Plans/<Mod>/playtests/` tree**,
  which are compiled into the binary as well. The scope was `src/` alone, so a check
  change did not invalidate the artifact at all: an agent could change what a check
  measures, forget the rebuild, and the guard whose whole job is catching that said
  nothing.
- At startup it recomputes that digest over the tree beside it. On a mismatch it
  prints both digests and the rebuild command, and exits 7 without doing anything.
- If `TestRig/src/` is absent, the binary has been copied somewhere else entirely,
  which is not the staleness case this guards. It runs.

### A rebuild always dirties testrig.exe, and that is not a change

Rebuilding from unchanged sources produces a different binary. Measured on
2026-08-15: two builds of an identical tree differ by **129 bytes out of
16,947,712**, in exactly two places. Two bytes at offset `0x109`, which is the PE
header's `TimeDateStamp`, and about 127 contiguous bytes at `0x83aae6`, which is
the debug directory's embedded-PDB content id. Nothing in the code sections moves,
so this is not nondeterministic codegen: it is two stamps ILC writes fresh each
time, on top of a managed compile that is already deterministic.

The embedded source digest is unaffected, because it is computed over the SOURCES,
not over the binary. So a dirty `testrig.exe` after a no-op rebuild is cosmetic:

    git checkout -- TestRig/testrig.exe

Chasing the last 129 bytes means fighting ILC's debug-directory emission for
tidiness, and a silent regression there would be worse than knowing the two
offsets. Only commit the binary when the SOURCES changed.

The digest is computed once, by one piece of code. `TestRig.BuildTool` calls
`SourceHash.Compute` at build time; the binary calls the same method at startup. An
earlier design had a PowerShell script compute it at build time and C# recompute it at
run time, which meant two implementations that had to agree byte for byte forever. One
implementation cannot disagree with itself. The tree LIST is derived the same way, by
`SourceRoots.For`, from the one path both callers have.

Reproducibility rules inside `SourceHash.Compute`, all of which exist so a different
clone produces the same digest: ordinal path sort, CRLF normalised to LF, UTF-8 BOM
stripped, relative path hashed alongside content so a rename counts, `bin/` and `obj/`
excluded.

Expect the binary to be stale while you are actively editing. That is the guard
working, not a problem to route around.

## Layout

| Project | Target | What it is |
|---|---|---|
| `TestRig.Contracts` | `netstandard2.0` + `net10.0` | The wire contract shared with the in-game plugin. Types only. |
| `TestRig.Core` | `net10.0` | The rig: seams, infrastructure, session state, both halves. |
| `TestRig.Playtest` | `net10.0` | The playtest engine. Mod-agnostic. |
| `TestRig.Playtests` | `net10.0` | The checks, globbed from `Mods/*/playtests/`. |
| `TestRig.Cli` | `net10.0`, AOT | Entry point. Parse, dispatch, format. Thin. |
| `TestRig.BuildTool` | `net10.0` | Build-time helper. Not shipped. |
| `TestRig.Tests` | `net10.0` | xUnit. |

`TestRig.Contracts` multi-targets because the BepInEx plugin runs on the game's Mono
runtime and targets `net472`, which consumes `netstandard2.0`. The `net10.0` target is
where the JSON source generator lives, which AOT requires. **Do not change either
target framework**: dropping `netstandard2.0` silently breaks the plugin, and dropping
`net10.0` silently drops AOT serialisation.

## Rules that are not style preferences

**Everything reaches the outside world through an interface in
`TestRig.Core/Abstractions`.** No direct `File`, `Process`, `DateTime.UtcNow`,
`Thread.Sleep` or `HttpClient` outside `Infrastructure/`. This is what lets the suite
exercise the real seam rather than a shim. The PowerShell suites could not, and three
blocking defects hid in exactly that seam.

**An assertion that still passes when the code under test is replaced by a no-op is a
bug.** The PowerShell suite contained assertions of exactly that shape: its check for
the line that prints the session owner id was a grep of the launcher's *source text*, so
it stayed green for the entire life of a feature that never once executed. Assert on
observed behaviour. Never assert on source text.

**No PowerShell.** The old rig is deleted. Doc comments here cite its files by name
(`rig-lock.ps1`, `rig-reset.tests.ps1`, `lib/common.ps1`, `testrig.tests.ps1`) as the
provenance of a ported behaviour or a magic number; those files are in git history and
are deliberately not on disk. Nothing here needs them to build, test or run.

**Eight Win32 APIs are forbidden anywhere in this tree**: `SwitchDesktop`,
`SetForegroundWindow`, `ShowWindow`, `SetWindowPos`, `AttachThreadInput`,
`BringWindowToTop`, `SetActiveWindow`, `SetThreadDesktop`. Instances run on a Win32
desktop that is created and never switched to. Measured: 40 focus steals out of 40
samples without the mechanism, 0 out of 55 with it. `ForbiddenPInvokeGuardTests`
enforces this and has been proven to fail by planting each violation in turn.

One trap that guard cannot catch, measured on this machine 2026-08-14:
**`CreateProcessW` does not fail when `lpDesktop` names a desktop that does not
exist.** It returns success and silently lands the process on the caller's desktop.
`DesktopProcessLauncher.Start` therefore ensures the desktop immediately before every
launch. Do not separate those two steps.

## Adding a playtest check

Checks are C# under `Mods/<Mod>/playtests/`, compiled into `TestRig.Playtests`. They
cannot be discovered on disk at runtime: an AOT binary cannot load managed assemblies,
so adding a check means a rebuild.

**Add the file AND a line in `TestRig.Playtests.Playtests.All`.** That list names every
check type directly, and the direct reference is the trimmer root. Self-registration from
a `[ModuleInitializer]` left nothing statically referencing a check class, so under
`PublishAot` with `TrimMode=full` ILC removed all eight from the shipped binary:
`testrig playtest --list-checks` printed an empty list and exited 0 while `dotnet run`
over the same sources listed them all. Three guards missed it, and the pattern matters
more than the bug: the digest covered `src/` only, `dotnet test` runs on CoreCLR where
module initializers DO run, and an empty listing exited 0 so it read as a clean answer.
All three are closed, and `ShippedBinaryChecksTests` runs the SHIPPED binary, because
trimming is a property of the artifact and cannot be observed any other way.

Attestation derives from a check's own location via `[CallerFilePath]`. The compiler
supplies that value, so a check cannot lie about which mod it tests. Do not add a
declaration field that re-states something derivable. The DEPLOYED path comes from Core's
`LaunchPadMods.DeployedRelativeDll`, the same helper `deploy` writes through; it was
derived independently here, disagreed, and made every check on a correctly deployed
instance answer `binary-not-deployed`.

## Mods under test, and why an instance has to be told

An instance records an explicit set:

    testrig create --target hostie --role host --under-test SprayPaintPlus

A mod in that set is **not** seeded from the developer's own mods folder and gets no
`modconfig.xml` entry. It reaches the instance only through `deploy`, as
`Local_<Mod>`. Every OTHER mod is seeded exactly as before, at whatever version the
developer is running.

That asymmetry is the whole point, and the obvious simplification is wrong. Do not
make `create` skip every mod this repository builds: the rig normally tests ONE mod,
and the others must stay at their published state, because this repository carries
work in progress for them and an unrelated half-finished mod silently changing the
result is exactly the kind of confidently-wrong answer the harness exists to prevent.

Without the set, `create` seeded the developer's copy as `<Mod>` and `deploy` added
`Local_<Mod>` beside it, both landed in `modconfig.xml`, and the game loaded the mod
twice, applying every Harmony patch twice. A cap of three notices would print six.
The two DLLs were both exactly 96,768 bytes, so the length-only attestation the
PowerShell harness used would have attested that instance cleanly and run all eight
checks against a double-patched process.

Three guards keep it that way, and all three should stay:

- `deploy` refuses a mod the instance does not record, naming the command that adds
  it. Deploying beside a seeded copy is how the double load happened.
- Attestation separates `under-test-not-deployed` from `binary-not-deployed`, so a
  mod that is in the set but was never deployed fails loudly instead of a check
  running against a mod that is not there.
- The harness compares each check's own mod, from `[CallerFilePath]`, against the
  instance's set **before bring-up**, and refuses `mod-not-under-test-here`. That is
  what makes it impossible for a check to run against the developer's workshop copy
  while believing it tested the build.
