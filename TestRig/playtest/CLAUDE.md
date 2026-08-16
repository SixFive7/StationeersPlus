# Playtest harness

**Asked to run a mod's in-game checks? Run the harness. Do not hand-roll the rig sequence.**

```
testrig playtest                                  # everything compiled into this binary
testrig playtest --only "the host*"               # a wildcard over check NAMES
testrig playtest --evidence-root <path>           # where the bundle lands
testrig playtest --wait-seconds 300               # queue budget when another session holds the rig
```

It runs a mod's checks against the client rig with nobody at the keyboard and reports **pass**, **fail** or **inconclusive** for each. Exit 0 is all-pass, 1 means a check read a value from the authority and found the wrong one, **8** means nothing failed but something could not be measured.

**`--only` selects checks; `--target` never does.** A check declares the instances it needs and brings them up itself, so naming half the rig could not change what runs, only what the report claimed to cover. `playtest --target server` is a refusal.

```
TestRig/src/TestRig.Playtest/    the engine: seams, runner, readers, flakes, evidence, attestation
TestRig/src/TestRig.Playtests/   the collection, globbing Mods/*/playtests/**/*.cs
Mods/<ModName>/playtests/*.cs    the checks themselves, next to the mod they test
```

**Checks are C# compiled into the binary.** An AOT binary cannot load managed assemblies at runtime, so a check cannot be a file discovered on disk: adding one means `dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64`. That is already true of every other change because the binary refuses to run when it disagrees with `TestRig/src/`, and it is one command.

The engine is mod-agnostic on purpose: nothing in `TestRig.Playtest` names a mod, a prefab, a setting or a guid, and nothing may be added that does. A check supplies all of that; the engine supplies the rig, the safety and the evidence.

The PowerShell harness that preceded this one is deleted; git history has it. It never actually ran: its `lock` never printed the `TESTRIG-OWNER` line it required by regex, so every check would have failed to start and left the rig locked.

Rig rules apply here in full: `TestRig/CLAUDE.md` (the lock, the save tiers, the safety rules) and `TestRig/MANUAL.md` (the verbs and the endpoints). This file is what to know before running or writing a check.

## Three outcomes, and why there are three

| Outcome | Means | Exit |
|---|---|---|
| `pass` | the check made its observation and the value was right | 0 |
| `pass (degraded, N attempts)` | same, but something needed retrying. Still a pass, never a clean one. | 0 |
| `fail` | an `Assert*` verb read a value from the authority and found the wrong one. **The mod is the suspect.** | 1 |
| `inconclusive` | the rig, not the mod: a flake, a lost lock, a stale binary, an unclassified throw. **Nothing was learned about the mod.** | 8 |

The whole harness is built around one asymmetry: an inconclusive costs a re-run, a false fail costs a developer a day chasing a bug that is not there. So the only thing that can produce `fail` is an assert verb that read a value and found the wrong one. Everything else is inconclusive, **including a bug in the check itself** (`unclassified-error`) and an endpoint refusing a request (`action-refused`). Those are reported loudly with their detector and full error text, so they can never be mistaken for a clean run, but they never get to accuse the mod. Exit codes 1 and 8 are distinct on purpose; a caller that cannot tell them apart will eventually treat one as the other, and the PowerShell rig's flat 0/1/2 is exactly how that happened.

## Assert on the authority, never on the actor's own 200

**An endpoint's own 200 is a statement about the request, not about the world.** This comes from two live failures: a `/connect` answered ok while nothing had joined, and an `/inventory/arm` reported confirmed while the host-side check was inconclusive.

So the verbs split in two and do not mix, and the type system now enforces it. `ctx.Act` MAKES something happen and returns an `ActionResult`, which no assert verb accepts. `ctx.Read` READS a named value from a named instance through a named reader and returns an `Observation`, the only thing the assert verbs take. There is deliberately no `AssertTrue`, no `AssertOk` and no bare-boolean assert, and none may be added.

Which instance is the authority depends on the question: anything the server owns (the roster, whether hosting happened, a simulated object's state) is authoritative on the **host**; anything a client half decides for itself is authoritative on **that client**; "did the joiner arrive" is the host's roster, never the joiner's own answer.

## The verbs

Everything a check body can do is on `IPlaytestContext`, which is the single parameter its `Run` method takes.

Driving: `ctx.Act(on, path, body, blocking, noRetry, timeoutSeconds)`, `ctx.WaitStage(name, stage)`, `ctx.RestartInstance(name)` (never rig-wide), `ctx.ConnectJoiner(name, to)`, `ctx.SaveConsoleTail(step)`, `ctx.WriteEvidence(name, content)`, `ctx.Wait(seconds)`.

`path` is an `Endpoints` constant from `TestRig.Contracts` and `body` is a Contracts request record, so a field the plugin renames breaks this build rather than a run. `blocking` marks an endpoint that freezes that instance's whole control plane, so a transport silence there is explained rather than treated as a dead instance; the host, connect, save, load, new-world and wait-for endpoints are treated as blocking automatically. **Use `ConnectJoiner` rather than driving the connect endpoint yourself**: it reads the port off the host, POLLS the roster (inWorld on the joiner and the row appearing server-side are different instants) and retries from the menu. Four checks once reported `joiner-not-in-roster` on a rig that was joining fine, purely because each carried its own copy of that logic.

Reading: `ctx.Read(from, reader, select, of, readerArgs)`.

| `Reader` | Endpoint and shape |
|---|---|
| `Status` | `GET /status`: `role`, `hosting`, `hostPort`, `phase`, `saveRootIsolated`, everything the control plane computes. |
| `Roster` | `/status` narrowed to `connectedClients`. `of: <clientId>` picks a row. The host is in its own roster. |
| `Config` | `GET /config?guid=`. `of: "<Section>/<Key>"` picks one entry. |
| `Thing` | `GET /thing?refIds=&fields=`. An **instance** field on one object, per machine. `of: "<refId>/<Field>"` picks a field row. |
| `Reflect` | `GET /reflect`. **Statics only.** Instance fields belong to `Thing`. |
| `Nearby` | `GET /nearby`. `of: <referenceId>` picks one Thing. |
| `Console` | `GET /console/log`. A BOUNDED RING (2000 lines per source), so boot-time lines are routinely gone. Use it for what the check itself provokes. |
| `BepInExLog` | the instance's `BepInEx/LogOutput.log` FILE. No ring, and the state reset empties it per session, so it is the authority for anything printed during BOOT. `select: "count"`; `limit` clips lines and never the count; it reports `exists`. |
| `Inventory`, `Plugins`, `SavePath`, `Player`, `Dlc` | the remaining plain reads. |

`select` is a dotted path with array indexing and a `count` pseudo-member (`connectedClients[0].username`). A path that does not resolve reads null, and the assert decides whether absent is wrong.

Asserting (the only things that can produce `fail`): `ctx.AssertValue(from, reader, matcher, because, ...)` where the matcher is one of `ValueMatcher.Is / IsNot / Matches / AtLeast / AtMost / Contains`, plus `ctx.AssertAgreement(across, ...)`, `ctx.AssertChange(baseline, because, to:, unchanged:)`, `ctx.AssertBinaryUnderTest()`, and `ctx.SetInconclusive(because, detector)` for a check that discovers it cannot make its observation. `because` is mandatory: a report saying "hosting was False" is a puzzle, one saying why it matters is a finding. There is no `SetFail`. Booleans compare as booleans, numbers as numbers, everything else case-insensitively.

`SetInconclusive` is marked `[DoesNotReturn]`, so the compiler knows a guard that declines has ended the check, which is what lets a check read a nullable value straight after guarding it.

**A pass is gated on `ctx.AssertionCount` being non-zero**, exactly as it is gated on attestation. The PowerShell library had no counter anywhere, so a check with a valid binary block and an empty body reported a clean pass, and its offline suite registered exactly that shape twice while asserting only the result count.

Full contracts, parameter by parameter, are in `TestRig/src/TestRig.Playtest/Model/IPlaytestContext.cs`.

## Four traps that have each cost a day

- **`matchesPrefab` is not decoration.** A value equal to the untouched prefab's is indistinguishable from never having been set, and a live run drew the wrong conclusion from exactly that. Assert `matchesPrefab` is false on the object that was acted on and true on a control.
- **...but `matchesPrefab` is useless on a REFERENCE-typed member, and it fails silently.** `/thing` compares renderings, and for a type without an overridden `ToString` both render as the bare type name, so they always match. `Thing.CustomColor` is the case that bit: painted or not, it reads `"value": "Assets.Scripts.Objects.ColorSwatch"`, `"matchesPrefab": true`. A campaign spent a day on a mod defect that did not exist. **Look at a field's `valueType` before using it as evidence**; if the rendering is a type name rather than a value, the field cannot answer the question. For colour, read the row-level `customColorIndex`.
- **Pick a starting value the action cannot coincidentally produce.** `StructureCableStraight` spawns at `customColorIndex` 4, which is exactly what `ItemSprayCanRed` applies, so before and after are identical on a working stroke and on one that never happened. `/spawn/structure` takes a `colorIndex`: spawn the scene in a colour the action will change.
- **Assert that the ACTION landed before you assert what it implies.** Three checks asserted on console output that only appears when a stroke lands, with nothing asserting that a stroke landed, so a scene that was never painted and a mod that never spoke were indistinguishable.

## What the runner does around a check body

You write none of this. Per check, in order: take the rig session lock (which fires the state reset, so the check starts clean); snapshot the developer's tier-1 save folder as a **listing** and its hash (no file is ever opened, nothing is ever written); start the **hosts** first, wait for `menu`, `POST /host`, wait for `inWorld`, then read `/status` back from the host and require `hosting == true` and `role == listenHost`; start the **clients**, wait for `menu`, connect at the host's reported `hostPort`, wait for `inWorld`, then read the host's roster and require that it grew; attest the binary; capture a console tail, run the body, capture another; stop the instances **by name**, joiners first and hosts last, then release the lock, all in a `finally`; snapshot the save folder again and compare.

**The `InstanceSpec`** a check declares in its `CheckSpec`: `Name` (must already exist in the rig registry; the harness does not create instances), `Role` (`Host` or `Client`), `World` or `Save` (host only, exactly one), `ConnectTo` (client only, defaults to the first host), `GamePort` (host only), `Address` (client only).

**Attestation, and why a check cannot pass without it.** A live run nearly measured a stale seeded DLL and was saved by luck. `AssertBinaryUnderTest` runs before the body, and a check that never attests is downgraded to inconclusive with detector `binary-not-attested`. It checks three independent things because each alone can be satisfied by a stale rig: the **provision stamp** exists (so the tree is one this rig built), the **deployed file** matches the build under test by **content hash**, at `userdata/mods/Local_<Mod>/<Mod>.dll` (the path `deploy` writes, derived from the same helper so the two cannot drift; it named the unprefixed path for a while, which made `binary-not-deployed` the only possible answer on a correctly deployed instance), and a live `GET /config?guid=<mod>` read from inside each running process, which is the only one that can say what the process actually loaded.

**Which mod a check attests, the harness also checks the INSTANCE was provisioned for.** An
instance records the mods it exists to test (`create --under-test <Mod>`); a mod in that set is
not seeded from the developer's folder, so the deployed `Local_<Mod>/` is its only copy. Before
bring-up, and therefore before any game process starts, the harness compares the check's mod
against each named instance's set and ends the check `inconclusive (mod-not-under-test-here)`
if it is absent. Neither side is declared: the mod comes from `[CallerFilePath]` and the set
from the registry row. Without that comparison a check runs to completion against the
DEVELOPER'S published copy while reporting on this repository's build, or against two copies
loaded at once, and the output stays plausible either way. An instance that records the mod and
never deploys it has NO copy, which attestation reports as `under-test-not-deployed` rather
than as the ordinary not-deployed case.

**Which mod a check attests is not the check's to declare.** It comes from `[CallerFilePath]`, so the compiler records where the check was written under `Mods/<Mod>/playtests/` and a check cannot claim a different mod. Do not add a declaration field that re-states something derivable. The content hash replaced a length comparison: the PowerShell harness's binary attestation compared file **length** while documenting a content comparison, so a same-length different build attested cleanly.

## The lock policy, and teardown

**Each check takes and releases the lock itself.** That buys a state reset per check, since the reset is between sessions by design and two checks under one lock would get none. It costs the reset time, and it risks another agent taking the rig between checks, which is reported as inconclusive with detector `rig-unavailable` and never as a failure. `--wait-seconds N` queues; it is a queue, not a reservation. The lock is refreshed as a side effect of the harness driving something, at most once a minute. **There is no background refresher and there never may be**: that would hold the rig after the agent is gone.

The engine reads the owner id from the rig's structured output rather than by scraping a sentence, and it records the exit code a lock attempt produced. Those two together are why a refusal no retry could fix is no longer indistinguishable from a rig that was momentarily busy: exit 4 is another session's lock, 6 is a busy rig, 3 is a refusal.

**A FAILED acquisition can still have taken the lock, and it releases it.** Acquisition writes the lock file and then runs the state reset on top of it, so a reset that fails leaves a real reservation behind. That case is now reported as its own thing (owner id and all) and released before the check throws; the outcome of that release is appended to the inconclusive message rather than replacing it, because a release also restores and a restore that failed must not hide why nothing was measured. The one case that still cannot clean up after itself is a grant with no owner id at all, and it says so in capitals and names the three commands that clear it. Measured 2026-08-16: without this a suite lost checks 6, 7 and 8 to one sharing violation and a lock nobody could name.

**Teardown is guaranteed and it is by name.** The runner stops the instances it started, one at a time, joiners first and hosts last. It never runs a rig-wide stop: that reaches every instance on the machine including another session's live test. A stop that fails does not skip the release, because an instance left up holds the rig but a lock left held blocks every other agent too. Both are recorded.

## Evidence, and the flake taxonomy

One bundle per run under `TestRig/playtest/evidence/` (move it with `--evidence-root`), holding `run.json` and `run.md`, the before and after save-folder inventories and their verdict, and per check: outcome and detectors, the binary attestation, the lock record, the state-reset report as the rig printed it, every request and response numbered in order, every value a reader produced, per-step console tails, and every rig invocation with its stdout and exit code. A human must be able to audit a run they did not watch. A check can add its own file with `ctx.WriteEvidence`.

Every flake detector is a test over a probe, resolution is first match, and **every one of them ends a check as inconclusive, never as a failure**: `connect-first-attempt` (retry 3, 10 s apart), `launchpad-workshop-park` (restart, 2), `host-not-hosting` (abort), `joiner-not-in-roster` (abort), `lock-lost` (abort), `control-plane-silent` (wait, 6 by 10 s), `instance-dead` (restart, 1), `boot-timeout` (restart, 2), `transport-error` (retry 3, 3 s apart). `connect-first-attempt` is at the top because it is documented behaviour rather than a defect: a client that has just disconnected is still settling. The catalogue is `TestRig.Playtest/Flakes/FlakeCatalogue.cs`; a detector that throws is skipped and reported, never allowed to swallow a probe.

## Before the first run

```powershell
dotnet build Mods/<ModName>/<ModName>.sln -c Release
TestRig\testrig.exe lock --purpose "Playtest setup for <Mod Display Name>"
testrig create --target host1 --as <id> --role host
testrig create --target join1 --as <id>
testrig deploy <ModName> --target clients --as <id>
testrig unlock --as <id>
```

An instance that is not in the registry ends the check as inconclusive with detector `instance-not-provisioned` and the exact command that fixes it, because creating one costs minutes and rebuilds a tree the caller may not have meant to rebuild. Never write into the developer's own mod folder: `create --force` and `update-mods` both re-seed FROM it, which is why an instance's copy goes stale and why attestation exists.

If you changed a check or the engine, rebuild first. The binary refuses to run when it disagrees with `TestRig/src/`, so a forgotten rebuild is exit 7 rather than a run of the old code.

Run the offline suite after any change to the engine:

```
dotnet test TestRig/src/TestRig.slnx
```

Every seam is a real test double there, the clock and the sleeper are injected so a 300 second barrier costs nothing, and the real `TestRig/session.lock` is never touched.

## Known gaps in what a check can express

- **Reading another player's inventory from the host.** `GET /inventory` resolves `activeHand` only for the character the process owns, so a host cannot assert what a joiner is holding without asking the joiner, which is the authority inversion this harness otherwise avoids.
- **A structured world query.** A check cannot ask "what did I just spawn" and get the reference ids back in one place, so every check re-invents that bookkeeping.
- **A duplicate-free plugin list.** `/plugins` has been observed listing a StationeersLaunchPad mod twice with an empty `assemblyPath`, so a check reading it must not assume one row per guid. `Assert-BinaryUnderTest` goes through `/config`, which is unaffected.
- **A liveness probe during a blocking call**, and **a reader for what is on disk right now**. Neither exists; `control-plane-silent` infers its explanation, and a check that wants to prove a world reached disk has to drive `POST /save` and read its answer.
- **Putting an item into a REMOTE client's hand** is claimed rather than recorded. `/inventory/arm` states it works on any role and several checks drive it, but no live-run confirmation is recorded in these files. `/spawn/hand` definitely refuses on a joiner.
