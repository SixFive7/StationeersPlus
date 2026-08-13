# Playtest harness

**Asked to run a mod's in-game checks? Run the harness. Do not hand-roll the rig sequence.**

```powershell
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/<ModName>
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite <path> -ListChecks     # what exists, and what it needs
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -ListFlakes                   # the flake taxonomy, as the code has it
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite <path> -Only 'the host*' [-LockWaitSeconds 300]
```

It runs a mod's checks against the client rig with nobody at the keyboard and reports **pass**, **fail** or **inconclusive** for each. It is mod-agnostic on purpose: nothing in `playtest-lib.ps1` names a mod, a prefab, a setting or a guid, and nothing may be added that does. A check supplies all of that; the harness supplies the rig, the safety and the evidence.

```
playtest.ps1            the composition root: wires the seams, loads the checks, runs them
playtest-lib.ps1        the library a check is written against, and the full contract of every verb
playtest-lib.tests.ps1  the offline suite (no game, no instance, no network, no rig lock)
checks/<ModName>/*.playtest.ps1
```

Rig rules apply here in full: `TestRig/CLAUDE.md` (the lock, the save tiers, the safety rules) and `TestRig/MANUAL.md` (the verbs and the endpoints). This file is what to know before running or writing a check.

## Three outcomes, and why there are three

| Outcome | Means | Exit |
|---|---|---|
| `pass` | the check made its observation and the value was right | 0 |
| `pass (degraded, N attempts)` | same, but something needed retrying. Still a pass, never a clean one. | 0 |
| `fail` | an `Assert-Rig*` verb read a value from the authority and found the wrong one. **The mod is the suspect.** | 1 |
| `inconclusive` | the rig, not the mod: a flake, a lost lock, a stale binary, an unclassified throw. **Nothing was learned about the mod.** | 2 |

The whole harness is built around one asymmetry: an inconclusive costs a re-run, a false fail costs a developer a day chasing a bug that is not there. So the only thing that can produce `fail` is an assert verb that read a value and found the wrong one. Everything else is inconclusive, **including a bug in the check itself** (`unclassified-error`) and an endpoint refusing a request (`action-refused`). Those are reported loudly with their detector and full error text, so they can never be mistaken for a clean run, but they never get to accuse the mod. Exit codes 1 and 2 are distinct on purpose; a caller that cannot tell them apart will eventually treat one as the other.

## Assert on the authority, never on the actor's own 200

**An endpoint's own 200 is a statement about the request, not about the world.** This comes from two live failures: a `/connect` answered ok while nothing had joined, and an `/inventory/arm` reported confirmed while the host-side check was inconclusive.

So the verbs split in two and do not mix. `Invoke-RigAction` MAKES something happen and returns a `Playtest.ActionResult`, which no assert verb accepts. `Read-RigValue` READS a named value from a named instance through a named reader and returns a `Playtest.Observation`, the only thing the assert verbs take. There is deliberately no `Assert-True`, no `Assert-Ok` and no bare-boolean assert; `Assert-RigOk` and `Assert-RigResponse` exist only to throw an explanation at anyone who reaches for one.

Which instance is the authority depends on the question: anything the server owns (the roster, whether hosting happened, a simulated object's state) is authoritative on the **host**; anything a client half decides for itself is authoritative on **that client**; "did the joiner arrive" is the host's roster, never the joiner's own answer.

## The verbs

Driving: `Invoke-RigAction -On <instance> -Path <endpoint> [-Body] [-Blocking] [-NoRetry] [-TimeoutSec]`, `Wait-RigStage -Name <instance> -Stage ping|modsLoaded|menu|inWorld`, `Restart-RigInstance -Name <instance>` (never rig-wide), `Connect-RigJoiner -Name <joiner> -To <host>`, `Save-PlaytestConsoleTail`.

`-Blocking` marks an endpoint that freezes that instance's whole control plane (`/host`, `/connect`, `/save`, `/load`, `/newworld`, `/waitfor`), so a transport silence there is explained rather than treated as a dead instance. **Use `Connect-RigJoiner` rather than driving `/connect` yourself**: it reads the port off the host, POLLS the roster (inWorld on the joiner and the row appearing server-side are different instants) and retries from the menu. Four checks once reported `joiner-not-in-roster` on a rig that was joining fine, purely because each carried its own copy of that logic.

Reading: `Read-RigValue -From <instance> -Reader <name> [-Select <path>] [-Of <id>] [-ReaderArgs <hashtable>]`.

| Reader | Endpoint and shape |
|---|---|
| `status` | `GET /status`: `role`, `hosting`, `hostPort`, `phase`, `saveRootIsolated`, everything the control plane computes. |
| `roster` | `/status` narrowed to `connectedClients`. `-Of <clientId>` picks a row. The host is in its own roster. |
| `config` | `GET /config?guid=`. `-Of '<Section>/<Key>'` picks one entry. |
| `thing` | `GET /thing?refIds=&fields=`. An **instance** field on one object, per machine. `-Of '<refId>/<Field>'` picks a field row. |
| `reflect` | `GET /reflect`. **Statics only.** Instance fields belong to `thing`. |
| `nearby` | `GET /nearby`. `-Of <referenceId>` picks one Thing. |
| `console` | `GET /console/log`. A BOUNDED RING (2000 lines per source), so boot-time lines are routinely gone. Use it for what the check itself provokes. |
| `bepinexlog` | the instance's `BepInEx/LogOutput.log` FILE. No ring, and the state reset empties it per session, so it is the authority for anything printed during BOOT. `-Select count`; `limit` clips lines and never the count; it reports `exists`. |
| `inventory`, `plugins`, `savepath`, `player`, `dlc` | the remaining plain reads. |

`-Select` is a dotted path with array indexing and a `count` pseudo-member (`connectedClients[0].username`). A path that does not resolve reads `$null`, and the assert decides whether absent is wrong.

Asserting (the only things that can produce `fail`): `Assert-RigValue -From <instance> -Reader <r> (-Is|-IsNot|-Matches|-AtLeast|-AtMost|-Contains) <value> -Because <text>`, `Assert-RigAgreement -Across <names>`, `Assert-RigChange -Baseline <observation> (-To <value>|-Unchanged)`, `Assert-BinaryUnderTest`, and `Set-PlaytestInconclusive` for a check that discovers it cannot make its observation. `-Because` is mandatory: a report saying "hosting was False" is a puzzle, one saying why it matters is a finding. There is no `Set-PlaytestFail`. Booleans compare as booleans, numbers as numbers, everything else case-insensitively.

Full contracts, parameter by parameter, are in `playtest-lib.ps1`'s own doc comments.

## Four traps that have each cost a day

- **`matchesPrefab` is not decoration.** A value equal to the untouched prefab's is indistinguishable from never having been set, and a live run drew the wrong conclusion from exactly that. Assert `matchesPrefab` is `$false` on the object that was acted on and `$true` on a control.
- **...but `matchesPrefab` is useless on a REFERENCE-typed member, and it fails silently.** `/thing` compares renderings, and for a type without an overridden `ToString` both render as the bare type name, so they always match. `Thing.CustomColor` is the case that bit: painted or not, it reads `"value": "Assets.Scripts.Objects.ColorSwatch"`, `"matchesPrefab": true`. A campaign spent a day on a mod defect that did not exist. **Look at a field's `valueType` before using it as evidence**; if the rendering is a type name rather than a value, the field cannot answer the question. For colour, read the row-level `customColorIndex`.
- **Pick a starting value the action cannot coincidentally produce.** `StructureCableStraight` spawns at `customColorIndex` 4, which is exactly what `ItemSprayCanRed` applies, so before and after are identical on a working stroke and on one that never happened. `/spawn/structure` takes a `colorIndex`: spawn the scene in a colour the action will change.
- **Assert that the ACTION landed before you assert what it implies.** Three checks asserted on console output that only appears when a stroke lands, with nothing asserting that a stroke landed, so a scene that was never painted and a mod that never spoke were indistinguishable.

## What the runner does around a check body

You write none of this. Per check, in order: take the rig session lock (which fires the state reset, so the check starts clean); snapshot the developer's tier-1 save folder as a **listing** and its hash (no file is ever opened, nothing is ever written); start the **hosts** first, wait for `menu`, `POST /host`, wait for `inWorld`, then read `/status` back from the host and require `hosting == true` and `role == listenHost`; start the **clients**, wait for `menu`, connect at the host's reported `hostPort`, wait for `inWorld`, then read the host's roster and require that it grew; attest the binary; capture a console tail, run the body, capture another; stop the instances **by name**, joiners first and hosts last, then release the lock, all in a `finally`; snapshot the save folder again and compare.

**The `-Instances` spec:** `Name` (must already exist in the rig registry; the harness does not create instances), `Role` (`host` or `client`), `World` or `Save` (host only, exactly one), `ConnectTo` (client only, defaults to the first host), `GamePort` (host only), `Address` (client only).

**The `-Binary` block, and why a check cannot pass without it.** A live run nearly measured a stale seeded DLL and was saved by luck. `Assert-BinaryUnderTest` runs before the body, and a check that never attests is downgraded to inconclusive with detector `binary-not-attested`. It checks three independent things because each alone can be satisfied by a stale rig: the **provision stamp** exists (so the tree is one this launcher built), the **deployed file** matches the build under test by size, and a live **`GET /config?guid=<mod>` entry count from inside each running process**, which is the only one that can say what the process actually loaded.

## The lock policy, and teardown

**Each check takes and releases the lock itself.** That buys a state reset per check, since the reset is between sessions by design and two checks under one lock would get none. It costs the reset time, and it risks another agent taking the rig between checks, which is reported as inconclusive with detector `rig-unavailable` and never as a failure. `-LockWaitSeconds N` queues; it is a queue, not a reservation. The lock is refreshed as a side effect of the harness driving something, at most once a minute. **There is no background refresher and there never may be**: that would hold the rig after the agent is gone.

**Teardown is guaranteed and it is by name.** `Use-Rig` stops the instances it started, one at a time, joiners first and hosts last. It never runs a rig-wide stop: that reaches every instance on the machine including another session's live test. A stop that fails does not skip the release, because an instance left up holds the rig but a lock left held blocks every other agent too. Both are recorded.

## Evidence, and the flake taxonomy

One bundle per run under `.work/<date>-playtest-<suite>/` (move it with `-EvidenceRoot`), holding `run.json` and `run.md`, the before and after save-folder inventories and their verdict, and per check: outcome and detectors, the binary attestation, the lock record, the state-reset report as the launcher printed it, every request and response numbered in order, every value a reader produced, per-step console tails, and every launcher invocation with its stdout and exit code. A human must be able to audit a run they did not watch.

Every flake detector is a test over a probe, resolution is first match, and **every one of them ends a check as inconclusive, never as a failure**: `connect-first-attempt` (retry 3, 10 s apart), `launchpad-workshop-park` (restart, 2), `host-not-hosting` (abort), `joiner-not-in-roster` (abort), `lock-lost` (abort), `control-plane-silent` (wait, 6 by 10 s), `instance-dead` (restart, 1), `boot-timeout` (restart, 2), `transport-error` (retry 3, 3 s apart). `connect-first-attempt` is at the top because it is documented behaviour rather than a defect: a client that has just disconnected is still settling. Add one with `Register-PlaytestFlake`; a detector that throws is skipped and reported, never allowed to swallow a probe.

## Before the first run of a suite

```powershell
dotnet build Mods/<ModName>/<ModName>.sln -c Release
pwsh -NoProfile -File TestRig/testrig.ps1 lock -Purpose "Playtest setup for <Mod Display Name>"
testrig create -Target host1 -As <id> -Role host
testrig create -Target join1 -As <id>
testrig deploy <ModName> -Target clients -As <id>
testrig unlock -As <id>
```

An instance that is not in the registry ends the check as inconclusive with detector `instance-not-provisioned` and the exact command that fixes it, because creating one costs minutes and rebuilds a tree the caller may not have meant to rebuild. Never write into the developer's own mod folder: `create -Force` and `update-mods` both re-seed FROM it, which is why an instance's copy goes stale and why `-Binary` exists.

Run the offline suite after any change to the library:

```powershell
pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1
```

Both seams are fakes there, the clock and the sleep are injected so a 300 second barrier costs nothing, and the real `TestRig/session.lock` is fingerprinted before the run and verified untouched after it.

## Known gaps in what a check can express

- **Reading another player's inventory from the host.** `GET /inventory` resolves `activeHand` only for the character the process owns, so a host cannot assert what a joiner is holding without asking the joiner, which is the authority inversion this harness otherwise avoids.
- **A structured world query.** A check cannot ask "what did I just spawn" and get the reference ids back in one place, so every check re-invents that bookkeeping.
- **A duplicate-free plugin list.** `/plugins` has been observed listing a StationeersLaunchPad mod twice with an empty `assemblyPath`, so a check reading it must not assume one row per guid. `Assert-BinaryUnderTest` goes through `/config`, which is unaffected.
- **A liveness probe during a blocking call**, and **a reader for what is on disk right now**. Neither exists; `control-plane-silent` infers its explanation, and a check that wants to prove a world reached disk has to drive `POST /save` and read its answer.
- **Putting an item into a REMOTE client's hand** is claimed rather than recorded. `/inventory/arm` states it works on any role and several checks drive it, but no live-run confirmation is recorded in these files. `/spawn/hand` definitely refuses on a joiner.
