# Playtest harness

Developer tooling. Runs a mod's in-game checks against the client rig with nobody at the keyboard, and reports **pass**, **fail** or **inconclusive** for each one.

It is mod-agnostic on purpose. Nothing in `playtest-lib.ps1` names a mod, a prefab, a setting or a guid, and nothing may be added that does. A check supplies all of that; the harness supplies the rig, the safety and the evidence.

Read `TestRig/CLAUDE.md` and `TestRig/ClientRig/README.md` first. This harness drives the client rig, so every rule there applies here: one session lock covering both rig halves, the save tiers, and the rule that no code may ever focus, raise or activate a game window.

```
playtest.ps1            the composition root: wires the seams, loads the checks, runs them
playtest-lib.ps1        the library a check is written against
playtest-lib.tests.ps1  the offline suite for the library (no game, no network)
README.md               this file
checks/<ModName>/*.playtest.ps1   the checks themselves, one folder per mod
```

---

## The three outcomes, and why there are three

| Outcome | Means | Exit code contribution |
|---|---|---|
| `pass` | The check made its observation and the value was right. | 0 |
| `pass (degraded, N attempts)` | Same, but something needed retrying. Still a pass, never a clean one. | 0 |
| `fail` | An `Assert-Rig*` verb read a value from the authority and found the wrong one. **The mod is the suspect.** | 1 |
| `inconclusive` | The rig, not the mod: a flake, a lost lock, a stale binary, an unclassified throw. **Nothing was learned about the mod.** | 2 |

The whole harness is built around one asymmetry: an inconclusive result costs a re-run, and a false fail costs a developer a day chasing a bug that is not there. So the only thing in the entire library that can produce `fail` is an `Assert-Rig*` verb that read a value and found the wrong one. Everything else is inconclusive, including:

- an endpoint refusing a request (`action-refused`);
- an unclassified exception out of a check body, such as a null reference in the check's own code (`unclassified-error`);
- a rig that would not come up, a lock that was lost, a binary that could not be attested.

That last one surprises people, so it is worth stating plainly: **a bug in the check itself reports inconclusive, not fail.** It is reported loudly with its detector and the full error text in the evidence bundle, so it can never be mistaken for a clean run, but it never gets to accuse the mod.

Exit codes are distinct on purpose: `1` means the mod is broken, `2` means the rig was flaky. A caller that cannot tell those apart will eventually treat one as the other.

### Degraded is not clean

A check whose `/connect` worked on the third attempt reports `pass (degraded, 3 attempts)`, and the detector that fired is on its record. Flakiness that disappears once it is survived is flakiness nobody ever fixes.

---

## Assert on the authority, not on the actor's report

This is the second decision that shapes everything, and it comes from two live failures on 2026-08-09: a `/connect` answered ok while nothing had joined, and an `/inventory/arm` reported confirmed while the host-side check was inconclusive. **An endpoint's own 200 is a statement about the request, not about the world.**

So the verbs split in two, and they do not mix:

| Verb | Job | Hands back |
|---|---|---|
| `Invoke-RigAction` | MAKE something happen | `Playtest.ActionResult`, which no assert verb accepts |
| `Read-RigValue` | READ a named value from a named instance through a named reader | `Playtest.Observation`, the only thing the assert verbs take |

`Assert-RigValue -From` takes an instance **name**. Handing it an action result, or a raw response object, gets a refusal that names the mistake rather than a silent string coercion. There is deliberately no `Assert-True`, no `Assert-Ok` and no bare-boolean assert of any kind; `Assert-RigOk` and `Assert-RigResponse` exist only to throw an explanation at anyone who reaches for one.

**Which instance is the authority** depends on what you are asking:

- anything the server owns (the roster, whether hosting happened, a simulated object's state) is authoritative on the **host**;
- anything a client half decides for itself (that player's own settings, what that player is allowed to do) is authoritative on **that client**;
- "did the joiner arrive" is the host's roster, never the joiner's own `/connect` answer.

---

## Writing a check

A check file is named `<something>.playtest.ps1`, lives under `checks/<ModName>/`, and calls `Register-PlaytestCheck` once per check. It is dot-sourced by the runner, so it needs no header and no imports.

```powershell
Register-PlaytestCheck `
    -Name 'the host own client half must not leak' `
    -Summary 'a host with the client-side toggle off must not disable the feature for a remote actor' `
    -Instances @(
        @{ Name = 'host1'; Role = 'host';   World = 'Lunar' }
        @{ Name = 'join1'; Role = 'client'; ConnectTo = 'host1' }
    ) `
    -Binary @{
        Mod                  = 'net.examplemod'
        ConfigEntryCount     = 33
        ConfigGroupCount     = 9
        DllPath              = 'Mods/ExampleMod/ExampleMod/bin/Release/ExampleMod.dll'
        DeployedRelativePath = 'userdata\mods\ExampleMod\ExampleMod.dll'
    } `
    -Body {
        param($ctx)

        # 1. Arrange, through actions. Nothing here is evidence of anything.
        Invoke-RigAction -On 'host1' -Path '/config/set' -Body @{
            guid = 'net.examplemod'; section = 'Client - Example'; key = 'Enable Example'; value = 'false'
        } | Out-Null

        # 2. Baseline, from the authority, BEFORE the thing under test.
        $before = Read-RigValue -From 'host1' -Reader nearby -Of 442 -Select 'colorIndex'

        # 3. Act.
        Invoke-RigAction -On 'join1' -Path '/player/use' -Body @{ targetId = 442 } | Out-Null

        # 4. Conclude, from the authority, and say why it matters.
        Assert-RigChange -Baseline $before -To 4 `
            -Because 'a remote actor must still be able to apply the effect when the host own client half has it off'

        # 5. And the control: the thing nobody touched must not have moved.
        $control = Read-RigValue -From 'host1' -Reader nearby -Of 445 -Select 'colorIndex'
        Assert-RigChange -Baseline $control -Unchanged `
            -Because 'only the object that was acted on may change'
    }
```

`ExampleMod` above is a placeholder. It is the one place in this folder a mod name may appear, and only because a worked example with `<Mod>` in it teaches nothing.

### What the runner does around your body

You do not write any of this. For each check, in order:

1. Take the rig session lock (which fires the state-hygiene reset, so the check starts on a clean rig).
2. Snapshot the developer's tier-1 save folder listing.
3. Start the **hosts** first, wait for `menu`, `POST /host`, wait for `inWorld`, then **read `/status` back from the host** and require `hosting == true` and `role == listenHost`.
4. Start the **clients**, wait for `menu`, `POST /connect` at the host's reported `hostPort`, wait for `inWorld`, then **read the host's roster** and require that it grew.
5. Attest the binary from the `-Binary` block.
6. Capture a console tail, run your body, capture another console tail.
7. Stop the instances **by name**, joiners first and hosts last, then release the lock. This happens in a `finally`, so it happens whatever your body did.
8. Snapshot the save folder again and compare.

### The `-Instances` spec

| Key | Means |
|---|---|
| `Name` | The provisioned instance name. It must already exist in the rig registry; the harness does not provision. |
| `Role` | `host` or `client`. Default `client`. |
| `World` | Host only. Create this world (`Lunar`, `Mars2`, `Europa3`, `MimasHerschel`, `Venus`, `Vulcan2`). |
| `Save` | Host only. Load this save instead. Exactly one of `World` or `Save`. |
| `ConnectTo` | Client only. Which host to join. Defaults to the first host in the list. |
| `GamePort` | Host only. Override the manifest's game port. |
| `Address` | Client only. Defaults to `127.0.0.1`. |

### The `-Binary` block, and why a check cannot pass without it

A live run in August 2026 nearly measured a stale seeded DLL: `-Provision -Force` re-seeds each instance's mods from the developer's own mod folder, and that copy was weeks old. It was caught by luck, because the file sizes happened to differ visibly.

So `Assert-BinaryUnderTest` runs before the body, and **a check that never attests cannot report pass**: the runner downgrades it to inconclusive with detector `binary-not-attested`. It checks three independent things, because each one alone can be satisfied by a stale rig:

- the **provision stamp** exists, so the instance is one this launcher built rather than a leftover;
- the **deployed file** matches the build under test by size, when `DllPath` and `DeployedRelativePath` are given;
- a live **`GET /config?guid=<mod>` entry count from inside each running process**, which is the only one of the three that can say what the process actually loaded.

Omitting `-Binary` and calling `Assert-BinaryUnderTest` yourself in the body is fine. Omitting both is not.

---

## The verbs

### Driving

| Verb | Contract |
|---|---|
| `Invoke-RigAction -On <instance> -Path <endpoint> [-Body <hashtable>] [-Blocking] [-NoRetry] [-TimeoutSec N]` | One endpoint on one instance, with the flake taxonomy applied. Returns `Playtest.ActionResult` (instance, path, attempts, degraded, raw response, evidence reference). `-Blocking` marks an endpoint that freezes that instance's whole control plane (`/host`, `/connect`, `/save`, `/load`, `/newworld`, `/waitfor`), so a transport silence there is explained rather than treated as a dead instance. Throws inconclusive when it cannot complete. |
| `Wait-RigStage -Name <instance> -Stage ping\|modsLoaded\|menu\|inWorld [-WaitSeconds N]` | Barrier on one instance, with the taxonomy applied to the wait itself. Deliberately not the launcher's own `-Wait`: the detectors need the `/status` blob at the moment the barrier gives up, and a barrier in a child process can only report that it timed out. |
| `Restart-RigInstance -Name <instance>` | Stop and start that ONE instance. Never `-All`. |
| `Connect-RigJoiner -Name <joiner> -To <host> [-Address] [-Port] [-Attempts N] [-GapSeconds N] [-RosterPollSeconds N]` | Join one instance to a host and prove it arrived **from the host's roster**. The single implementation, used by the harness's own bring-up and by any check body that bounces a joiner. It reads the port off the host, POLLS the roster rather than reading it once (inWorld on the joiner and the row appearing server-side are different instants), and retries from the menu, because a client that has just disconnected is still settling. A retry makes the check a degraded pass. **Use this rather than driving `/connect` yourself**: four checks reported `joiner-not-in-roster` on a rig that was joining fine, purely because each carried its own copy of the logic and the copies did not confirm-and-retry. |
| `Save-PlaytestConsoleTail [-Step <label>] [-Instances <names>]` | Append each instance's console tail to the evidence bundle. Never throws. |

### Reading

`Read-RigValue -From <instance> -Reader <name> [-Select <path>] [-Of <id>] [-ReaderArgs <hashtable>]`

| Reader | Endpoint and shape |
|---|---|
| `status` | `GET /status`. `role`, `hosting`, `hostPort`, `phase`, `saveRootIsolated`, and everything else the control plane computes. |
| `roster` | `GET /status` narrowed to `connectedClients`. `-Of <clientId>` picks one row. The host is in its own roster. |
| `config` | `GET /config?guid=<mod>`. `-ReaderArgs @{ guid = 'net.example' }`; `-Of '<Section>/<Key>'` picks one entry. |
| `thing` | `GET /thing?refIds=&fields=`. An **instance** field on one object, per machine. `-Of '<refId>'` picks the Thing; `-Of '<refId>/<Field>'` picks one field row, so `-Select value` and `-Select matchesPrefab` read what a check wants. |
| `reflect` | `GET /reflect?type=&member=`. **Statics only.** Instance fields belong to `thing`. |
| `nearby` | `GET /nearby`. `-Of <referenceId>` picks one Thing. |
| `console` | `GET /console/log`. A BOUNDED RING (2000 lines per source), so boot-time lines are routinely gone before a check can read them. Use it for anything printed while the check is driving. |
| `bepinexlog` | The instance's `BepInEx/LogOutput.log` FILE, resolved through the `instancesRoot` in the rig registry. No ring, no eviction, and the state reset empties it per session, so it is the authority for anything printed during BOOT. `-ReaderArgs @{ contains = '<s>'; limit = N }`, then `-Select count`. `limit` clips the returned lines and never the count, so a check counting six lines with a limit of five still reads six. It also reports `exists`, so an absent log is distinguishable from a mod that printed nothing. |
| `inventory` | `GET /inventory`. `-Of <slot key or index>`. |
| `plugins`, `savepath`, `player`, `dlc` | The remaining plain reads. |

**`matchesPrefab` is not decoration.** A value equal to the one on the object's untouched prefab is indistinguishable from never having been set on that instance, and a live run drew the wrong conclusion from exactly that. When a check reads an instance field as evidence that something happened, assert `matchesPrefab` is `$false` on the object that was acted on, and expect `$true` on a control that was not.

**...but `matchesPrefab` is useless on a REFERENCE-typed member, and it fails silently.** `/thing` decides `matchesPrefab` by rendering the instance value and the prefab value and comparing the renderings. For a reference type whose `ToString` is not overridden, both render as the bare type name, so the two always match. `Thing.CustomColor` is the case that bit: it is an `Assets.Scripts.Objects.ColorSwatch`, and on every Thing in the world, painted or not, it reads

```
"value": "Assets.Scripts.Objects.ColorSwatch"
"prefabValue": { "value": "Assets.Scripts.Objects.ColorSwatch" }
"matchesPrefab": true      <- always
"isNull": false            <- always
```

A campaign spent a day on a mod defect that did not exist because three checks read that member and concluded nothing had been painted. **Before using a field as evidence, look at its `valueType` in the response.** If the rendering is a type name rather than a value, the field cannot answer the question. For colour specifically, read the row-level `customColorIndex`, which `/thing` computes the way the game does.

**Two more traps in the same family, both measured:**

- **Pick a starting value the action cannot coincidentally produce.** `StructureCableStraight` spawns at `customColorIndex` 4, which is exactly what `ItemSprayCanRed` applies, so before and after are identical on a working stroke and on a stroke that never happened. `/spawn/structure` takes a `colorIndex`, so spawn the scene in a colour the action will change (gray 1 in, red 4 out) and the reading becomes falsifiable.
- **Assert that the ACTION landed before you assert what it implies.** Every check that reads a consequence should first assert the direct effect. The three checks that went wrong all asserted on console output that only appears when a stroke lands, with nothing anywhere asserting that a stroke landed, so a scene that was never painted and a mod that never spoke were indistinguishable.

`-Select` is a dotted path with array indexing and a `count` pseudo-member: `hosting`, `connectedClients.count`, `connectedClients[0].username`. A path that does not resolve reads `$null`, and the assert verb decides whether absent is wrong.

### Asserting (the only things that can produce `fail`)

| Verb | Contract |
|---|---|
| `Assert-RigValue -From <instance> -Reader <r> [-Select <path>] [-Of <id>] (-Is\|-IsNot\|-Matches\|-AtLeast\|-AtMost\|-Contains) <value> -Because <text>` | Read through a reader and require the value. Exactly one comparison. `-Because` is mandatory: a report saying "hosting was False" is a puzzle, one saying why it matters is a finding. |
| `Assert-RigAgreement -Across <names> -Reader <r> [-Select] [-Is] -Because <text>` | Every named instance must report the same value, and optionally that value. The shape of nearly every multiplayer check. |
| `Assert-RigChange -Baseline <observation> (-To <value>\|-Unchanged) -Because <text>` | Compare against a `Read-RigValue` taken before the action. A single snapshot cannot tell you whether a field changed, and `-Unchanged` is the control half of the same discipline. A remembered raw value is refused: it carries no instance, no reader and no evidence reference. The re-read reproduces the baseline's request in full, **`-ReaderArgs` included**: an observation carries a copy of them for exactly this. Without that it re-read as a bare `/thing` or `/config`, which answers 400, so every baseline taken through a reader with a query string ended the check inconclusive with no comparison made. |
| `Assert-BinaryUnderTest -On <names> -Mod <guid> [-ExpectedConfigCount N] [-ExpectedGroupCount N] [-DllPath] [-DeployedRelativePath]` | See above. |
| `Set-PlaytestInconclusive -Because <text> [-Detector <name>]` | For a check that discovers it cannot make its observation. There is no `Set-PlaytestFail`: a second way to spell "fail" would be the bare-boolean back door this harness exists to close. |

### Comparison semantics

Booleans compare as booleans (`'True'` from JSON equals `$true`), numbers as numbers, everything else as case-insensitive strings. A test that turns on the casing of a role name is a test that will break for no reason.

---

## The flake taxonomy

Every entry is a detector over a probe, not a category name. Resolution is first match, so specific detectors sit above general ones. **Every one of them ends a check as inconclusive, never as a failure.** Print the live list with `playtest.ps1 -ListFlakes`.

| Detector | Fires on | Remedy | Bound |
|---|---|---|---|
| `connect-first-attempt` | `/connect` answers `result=timeout`, `ok=false`, or fails at the transport | retry | 3 attempts, 10 s apart |
| `launchpad-workshop-park` | `loadedPluginCount <= 2` with `gameInitialized` false: a failed Steam Workshop query parked StationeersLaunchPad on its own error screen | restart that instance | 2 attempts |
| `host-not-hosting` | `/host` answered but the host's own `/status` says `hosting` is not true, or `role` is not `listenHost` | abort | not retried |
| `joiner-not-in-roster` | `/connect` answered ok and the HOST roster did not grow | abort | not retried |
| `lock-lost` | The rig session lock is no longer ours | abort | not retried |
| `control-plane-silent` | The control plane did not answer while a `-Blocking` endpoint was in flight | wait it out | 6 waits, 10 s apart |
| `instance-dead` | The connection was refused with no blocking call in flight | restart that instance | 1 attempt |
| `boot-timeout` | An instance did not reach the requested stage inside the barrier, and it is not the park | restart that instance | 2 attempts |
| `transport-error` | Anything else at the transport layer | retry | 3 attempts, 3 s apart |

Two more detectors exist for conditions that are diagnosed rather than retried, and they surface as the check's detector: `action-refused` (an endpoint said no and nothing in the taxonomy explains it), `unclassified-error` (a throw the harness does not recognise).

`connect-first-attempt` earns the top of the table because it is documented behaviour rather than a defect: a client that has just disconnected is still settling, and the first attempt after a restart routinely fails. A check that only connected on the second go is a degraded pass, not a fail.

### Adding a detector

```powershell
Register-PlaytestFlake -Name 'my-rig-condition' -Remedy retry -MaxAttempts 2 -GapSeconds 5 `
    -Summary 'what it is and why it is the rig rather than the mod' `
    -Test { param($Probe) return ($Probe.Kind -eq 'action' -and $Probe.Path -eq '/some/endpoint') }
```

A probe carries `Kind` (`action`, `transport`, `barrier`, `poststate`, `lock`), `Instance`, `Path`, `Attempt`, `Response`, `Status`, `Error`, `Stage` and `Blocking`. Detectors go to the FRONT of the list unless `-Before` names one to sit in front of. A detector that throws is skipped and reported, never allowed to swallow a probe.

---

## The lock policy: released and re-taken per check

Each check takes the rig session lock itself and releases it before the next one starts. That is a deliberate trade:

- **What it buys.** A new lock fires the state-hygiene reset, so every check starts on a clean rig instead of on the previous check's leftovers. Two checks under one lock would get no reset between them, because the reset is between sessions by design.
- **What it costs.** The reset time, once per check.
- **What it risks.** Another agent can take the rig between checks. That is reported as `inconclusive` with detector `rig-unavailable`, and never as a failure. `-LockWaitSeconds N` queues for the rig instead of failing at once; it is a queue and not a reservation, and promises no ordering fairness.

The lock is refreshed as a side effect of the harness driving something, at most once a minute. There is no background refresher and there never may be: that would hold the rig after the agent is gone and starve every other one.

## Teardown is guaranteed, and it is by name

`Use-Rig` acquires the lock, runs the body, and in a `finally` stops the instances **it started**, one `-Stop -Instance <name>` at a time, joiners first and hosts last, then releases the lock.

It never runs `-Stop -All`. That flag reaches every instance on the machine including another session's live test. A runner that dies mid-suite must not leave the rig held with no timer to reclaim it, and must never reach outside its own reservation on the way out.

A stop that fails does not skip the release: an instance left up holds the rig, but a lock left held blocks every other agent too. Both are recorded in the bundle.

---

## The evidence bundle

One bundle per run, under the repository's gitignored scratch directory (`.work/<date>-playtest-<suite>/` by default, `-EvidenceRoot` to move it). A human must be able to audit a run they did not watch.

```
.work/2026-08-11-playtest-examplemod/
  run.json                      machine-readable: every check, outcome, detectors, timings, exit code
  run.md                        the same as a table
  save-inventory-before.txt     the developer's tier-1 save folder: a LISTING and its sha256
  save-inventory-after.txt
  save-inventory.verdict.txt    IDENTICAL or CHANGED, with what moved
  checks/
    01-a-check-name/
      check.json                outcome, detectors, retries, lock owner, teardown notes
      binary.json               provision stamps, deployed sizes, live config counts per instance
      lock.txt                  the lock owner id, when it was taken and when it was released
      hygiene-reset.txt         the state reset report, exactly as the launcher printed it
      requests/0001-host1-post-host.json      every request and every response, numbered in order
      observations/0002-host1-status-hosting.json  every value a reader produced
      console/host1.tail.txt    per-step console tails, labelled with the step
      launcher/0003-start-host1.txt           every client-rig.ps1 invocation, its stdout and its exit code
```

The save-folder inventory is the one interaction this harness has with the developer's tier-1 folder, and it is a **directory listing**: relative path, size and write time per file, hashed. No file is ever opened, nothing is ever written. The offline suite pins that property directly by putting two files with identical metadata and different bytes in front of it and requiring the same hash out.

---

## Running

```powershell
# what checks exist, and what they need
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/ExampleMod -ListChecks

# the flake taxonomy, as the code has it
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -ListFlakes

# one check
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/ExampleMod -Only 'the host own client half*'

# the whole suite, queueing up to 5 minutes for the rig
pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/ExampleMod -LockWaitSeconds 300
```

Before the first run: build the mod, provision the instances, and copy the fresh build into each instance's own mod folder.

```powershell
dotnet build Mods/ExampleMod/ExampleMod.sln -c Release
.\TestRig\ClientRig\client-rig.ps1 -Lock -Purpose "Playtest setup for Example Mod"
.\TestRig\ClientRig\client-rig.ps1 -Provision -As <id> -Instance host1 -Role host
.\TestRig\ClientRig\client-rig.ps1 -Provision -As <id> -Instance join1 -Role client
# then copy bin/Release/ExampleMod.dll into TestRig/ClientRig/data/<instance>/userdata/mods/ExampleMod/
.\TestRig\ClientRig\client-rig.ps1 -Unlock -As <id>
```

Never write to the developer's own mod folder. `-Provision -Force` re-seeds from it, which is exactly why the copy in an instance is routinely stale and why `-Binary` exists.

The harness does not provision. An instance that is not in the rig registry ends the check as inconclusive with detector `instance-not-provisioned` and the exact command that fixes it, because a provision costs minutes and rebuilds a tree the caller may not have meant to rebuild.

## Testing the harness itself

```powershell
pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1
```

Offline: no game, no client instance, no network, no rig lock. Both seams are fakes, the clock and the sleep are injected so a 300 second barrier and a 10 second retry gap cost nothing, and the real `TestRig/session.lock` is fingerprinted before the run and verified untouched after it. Run it after any change to `playtest-lib.ps1`.

---

## Design note: the composition root

`playtest-lib.ps1` talks to nothing by itself. Two seams are injected through `Initialize-PlaytestLib`:

- `-Transport`, one HTTP call to one instance's control plane;
- `-RigCommand`, one `client-rig.ps1` invocation.

`playtest.ps1` is the only place that wires the real ones. It dot-sources `client-rig.ps1` for `Invoke-Control`, which returns an object, rather than parsing the stdout of `-Call`, which only prints JSON. Unwired, every driving verb in the library throws a message naming the runner.

That indirection is what makes the offline suite honest. A library that reaches the network by itself can only be tested with a game running, which means it stops being tested.

---

## Known gaps in what the rig can express

These are capabilities a check may reasonably want. Each one currently forces `Set-PlaytestInconclusive` or a workaround.

The largest one closed while this harness was being written: **per-Thing instance fields**. `/nearby` carries a fixed set of fields and `/reflect` reads statics only, so reading something like an emission colour per side used to mean InspectorPlus request files. `GET /thing?refIds=&fields=` now answers it directly, with a `matchesPrefab` flag on every field, and the `thing` reader here is built on it. There is deliberately no `inspector` reader: a file-drop round trip pretending to be an endpoint would have been the wrong shape to standardise on.

A second one closed on 2026-08-12: **boot-time log lines**. The console tee is a
2000-line ring per source and StationeersLaunchPad's mod loading evicts thousands of
lines during boot, so a check needing a line printed at load time could only decline
(`console-tee-evicted`) however correct the mod was. The `bepinexlog` reader reads
`BepInEx/LogOutput.log` off disk instead: no ring, nothing ages off, and the
between-session state reset empties it so what it holds is this run only.

Still open:

- **Putting an item into a REMOTE client's hand.** `/inventory/arm` claims to do this on any role, joiner included. If it holds up in a live run this gap is closed; until then note that `/spawn/hand` needs simulation authority and refuses on a joiner, and `/spawn/world viaServer=true` drops the item on the ground rather than into a slot. This is the gap that blocked a real check on 2026-08-09.
- **A structured world query.** Spawning a known scene (two objects, a known distance apart, on separate networks) is per-check `/spawn/structure` bookkeeping today. A check cannot ask "what did I just spawn" and get the reference ids back in one place, so every check re-invents that record keeping.
- **Reading another player's inventory from the host.** `GET /inventory` resolves `activeHand` only for the character the process owns, so a host cannot assert what a joiner is holding without asking the joiner, which is the authority inversion this harness otherwise avoids.
- **A duplicate-free plugin list.** `/plugins` has been observed listing a StationeersLaunchPad mod twice with an empty `assemblyPath`, which silently breaks any caller assuming one match per guid. `Assert-BinaryUnderTest` goes through `/config` instead, which is unaffected, but a check reading `plugins` should not assume one row.
- **A cheap liveness probe during a blocking call.** A blocking endpoint freezes the instance's whole control plane, `/ping` included, so `control-plane-silent` has to infer the explanation from the fact that the harness knows it issued a blocking call. A ping served off a separate thread would turn that inference into a measurement, and would also let a check tell "the instance is working" from "the instance is wedged" while a five-minute `/host` is in flight.
- **A save-confirmation reader.** `POST /save` answers 200 only on a confirmed save and 409 with `requested:true` otherwise, which is exactly right, but a check that wants to assert a world reached disk has to drive the action and read its response. There is no reader that answers "what is on disk for this instance right now" from the authority.
