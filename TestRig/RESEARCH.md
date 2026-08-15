# TestRig internals

Why the rig is shaped the way it is, and the measurements and game internals that constrain it. `TestRig/CLAUDE.md` is the rules; `TestRig/MANUAL.md` is the operating reference; this file is the reasoning, and it is what to read before changing the rig rather than driving it. The source tree's own conventions are in `TestRig/src/CLAUDE.md`, and the in-game plugin's are in `TestRig/dev-plugins/TestRig/README.md`.

**Measurement baselines.** Unless a section says otherwise, the client-half and lock findings below were measured at runtime on game **0.2.6403.27689**, Unity 2022.3.62f3, StationeersLaunchPad 0.5.0, BepInEx 5.4.23.5, and have not been re-measured since. The headless-pump and dedicated-server sections were re-measured on **0.2.6428.27798** on 2026-08-14, across seven instrumented runs by two independent agents, and say so where they differ from the older account. The installed build today is 0.2.6428.27798; `testrig status` prints what each half actually has.

## One binary, because a script made the rig cost more than the tests

Two things were consolidated, in that order, and the reasoning for each still binds.

**First, two launchers became one.** There were two entry points, `DedicatedServer/dedicated-server.ps1` and `ClientRig/client-rig.ps1`. The failure that ended them: an agent asked to "update the testrig" updated exactly one half, and nothing anywhere could notice. The concept was spelled `-Bootstrap` on one half and `-Provision -Force` on the other, neither launcher's help mentioned the other, and the only staleness the rig reported at all was per client instance and named a client-half fix. The rig said it was up to date because the half that had been updated was.

Three properties fell out of that fix and are the point of it:

- **`--target` defaults to `all` on every rig-wide verb**, so the natural spelling of an update hits both halves. Verbs that act on a specific running thing require an explicit target, so a typo neither narrows nor widens the blast radius.
- **`status` reports both halves against one source**, with a version line and a staleness line each, and names the fixing command.
- **A refusal is a feature, not an error path.** Seven things genuinely cannot mean the same thing on both halves. A verb that only says no leaves the caller's model of the rig wrong; one that says what the verb needs, why this target cannot provide it, and a command that would work, corrects it at the moment of the mistake, which is cheaper than any document. The matrix is data, every entry must name an alternative, and the suite fails if one does not.

That consolidation also collapsed the duplicated helpers, and each of them had already drifted: "is that pid alive" was a bare `Get-Process` on one half (so a recycled id made three verbs refuse and made status report a dead server as up) and an image check on the other; "where is the game install" had three validity tests and three ways to be told the path was wrong; "read a pid file" cast with `[int]`, which throws on a corrupt file, next to a `TryParse` version; "what game version is this" read a `version.txt` that has never existed in any Stationeers install, so every provision stamp recorded the Unity `FileVersion` instead and no game update could ever be detected; and `modconfig.xml` had three writers producing three formats, all of which the baseline stores byte for byte.

**Then the one launcher became one AOT binary**, `TestRig/testrig.exe`, built from `TestRig/src/`. The argument was never performance, though 200-400 ms of PowerShell startup per invocation mattered to a harness that shells the rig once per check. It was that **an uninformed agent must not be able to do the wrong thing easily**, and a refusal compiled into the tool cannot be skimmed past the way a document can. What that bought, each item measured against the PowerShell it replaced:

- **A shared wire contract.** The rig and the plugin hand-wrote JSON on both sides, and the consequence was live: the PowerShell playtest harness's fake transport answered `/dlc` with `{ok, owned}` while the real checks read `state.removedOwned` and `state.shared`. Renaming a field in the plugin left 399 assertions passing and every real check broken. `TestRig.Contracts` makes that a compile error, and it multi-targets `netstandard2.0` for the plugin's Mono runtime and `net10.0` for the AOT side.
- **A real seam under the process and the filesystem.** Everything reaching the outside world goes through an interface in `TestRig.Core/Abstractions`. Three blocking defects hid in exactly that seam because the PowerShell suite faked it, and the file holding them had zero tests.
- **Assertions on behaviour rather than on source text.** The PowerShell suite's assertion for the line that prints the session owner id was a grep of the launcher's own source, so it was green for the entire life of a feature that never once executed.
- **Distinct exit codes.** See `MANUAL.md`, "Exit codes"; the short version is that 0/1/2 made contention, a lapsed reservation and a broken rig indistinguishable, and the harness collapsed all of them into one inconclusive.
- **`--json` on every read-only verb**, so nothing downstream scrapes a sentence. The old harness scraped the lock owner id out of stdout.

The PowerShell tree was retained under `TestRig/` as the feature list this port was checked against, read rather than run, and was deleted in its own commit once the binary had driven a real multiplayer playtest end to end. A 1,560-row parity checklist enumerated every behaviour in its 10,927 lines and all 1,560 rows were read; 108 rows are marked fixed rather than merely carried, 15 are deliberately dropped and every one of those 15 is a PowerShell language artifact whose observable behaviour survives elsewhere. **No capability was dropped for being broken.** Git history is where the files are now; nothing in the working tree depends on them.

### The binary is committed, and staleness is a refusal

A consumer of the rig should never need a build step, so `testrig.exe` is in git. That costs exactly one risk, the committed artifact drifting from its source, and staleness of an on-disk artifact has already cost this project two whole sessions (once on stale mods, once on a stale game version) with the evidence present and scrolled past both times. So the binary embeds a SHA-256 digest of every `.cs`, `.csproj`, `.props`, `.sln` and `.slnx` under `TestRig/src/`, recomputes it at startup, and **exits 7 without acting** on a mismatch. If `TestRig/src/` is absent the binary was copied elsewhere, which is not the case this guards, and it runs.

The digest has exactly one implementation. `TestRig.BuildTool` calls `SourceHash.Compute` at build time and the binary calls the same method at run time. The design it replaced had a script computing it at build time and C# recomputing it at run time: two implementations that had to agree byte for byte forever, with a pairing test to police them. One implementation cannot disagree with itself.

A rebuild from unchanged sources still dirties `testrig.exe`, by 129 bytes out of 16.9 MB in two stamps ILC writes fresh each time; `git checkout -- TestRig/testrig.exe` clears it and `TestRig/src/CLAUDE.md` carries the offsets. **One build trap sits next to that and is worth knowing before it costs an hour.** Restoring a source file with a plain file copy preserves the COPY's write time, which can be older than the artifacts of the build that happened in between, so MSBuild reads the project as up to date and the previous compile survives untouched. An undone mutation test left its mutation in `TestRig.Core.dll` and in the binary published from it, and the symptom was a test that had passed twenty minutes earlier failing with no source difference at all. Touch the file, or clean.

### Two defects the port inherited, both live in the PowerShell rig

Both were found by two independent agents, and together they are why no mutating command was run through the old rig during the port.

**`TESTRIG-OWNER <id>` was never printed.** `New-RigLock` returns a bare string, so `$outcome.Owner` is always null and the guarded write never fires. The playtest harness required that exact line by regex, threw `inconclusive/rig-unavailable` without it, and then unlocked in its `finally` with the id it never received, so **every check would have failed to start and left the rig locked**. Both suite assertions covering it are source-text greps, so the suite was green and actively certified the no-op, and `CLAUDE.md` and `MANUAL.md` both documented the line as working. The C# rig prints it from `lock` and only from `lock`, pinned by a test that performs a real acquisition and reads the process's stdout.

**A failed world enumeration was indistinguishable from a rig with no worlds.** Covered under "State hygiene" in `MANUAL.md`; the mechanism was `-ErrorAction SilentlyContinue` swallowing every error, a marker line written as `worlds=`, and a snapshot reader testing the key's *presence* rather than its value. The planner's predicate was then true for every world it found: 25 real worlds, 185 MB, irreversible, no warning. The empty-set-is-real semantics was deliberate and tested; what was missing was any way to tell it from a failure. The port tri-states the enumeration, omits the key on failure, and refuses a bulk delete above five.

### The port's own suite certified the wrong behaviour three times

Those two were found by reading the PowerShell. Three more were found in the C# that replaced it, each held green by an assertion of its own. **None of the three was caught by any of 1,688 tests. Every one was caught by driving the real rig.** This is the single most valuable thing the port produced, so it is recorded here rather than in a session log.

| What was wrong | The assertion that kept it green |
|---|---|
| The shipped binary carried ZERO playtest checks. ILC's trimmer removed every check class, because `[ModuleInitializer]` is not a trimmer root under `PublishAot` with `TrimMode=full` and nothing else referenced them statically. A scan of the 16.7 MB artifact found none of the check name strings. | `ShippedChecksTests` asserted all eight were present. It ran on CoreCLR, where module initializers DO run. |
| `connectionId` is a `long` on the wire, in the 10^17 range, and was typed `int?` in Contracts, so `System.Text.Json` threw on the WHOLE `/status` payload and the joiner roster read as permanently empty. | `WireTrapFieldTests` asserted `ConnectionId == 2`. |
| Attestation derived an unprefixed deployed path while `deploy` writes `Local_<Mod>`, so it could never attest a correctly deployed instance. | `AttestationTests` and `ShippedChecksTests` asserted that same unprefixed path. |

The shape is identical in all three: **the test encodes what the code does rather than what the system needs**, so it is green precisely because it was written from the same misunderstanding as the code. Writing more tests does not fix that and coverage cannot see it, because every one of these assertions passes on a correct implementation too. Only running the real thing separates them, which is why the gate for retiring the PowerShell rig was a real end-to-end multiplayer playtest and not a suite count. It is the same failure mode as the source-text greps above, wearing better-looking assertions, and it reproduced inside a suite written specifically to avoid it.

Two of the three surfaced only because of the port's own fixes, which argues for those fixes rather than against them. The content-hash attestation caught a double-loaded mod that the old length-only comparison would have waved through, both DLLs being exactly 96,768 bytes. The typed contract turned a silent field mismatch into a hard deserialization failure, which is the only reason the roster bug was findable at all. That one carries its own warning attached: `RigWire.Deserialize` catches `JsonException` and returns null by design, which turned the loud failure straight back into a silent one, and the launcher concluded the joiner had never arrived.

## The plugin / launcher boundary is process creation

The launcher owns everything outside a game process, and everything that must keep working when a process is dead, wedged, or not yet born: laying down an instance tree, the desktop, starting, killing, pid files, the fan-out. The plugin owns everything inside a process, which is everything needing the Unity main thread or the game's own types. There is no third category, and the two never overlap. Two consequences make the shape correct rather than merely tidy.

**A coordinator cannot live inside the thing it coordinates.** Every multi-client test is "set up A, set up B, act as A, observe both", and the failures worth catching are ordering failures. An instance that has stopped participating in a barrier cannot report that it has stopped participating. The launcher is outside all of them, holds their pids, and can tell "not answering" from "answering, but not there yet". So the barrier, the fan-out and the snapshot are launcher work, and the plugin has no notion of a sibling except one narrow case: `PeerProbe` talks to siblings solely to detect a duplicate ClientId, because the moment that check must fire is the join and the join is initiated from inside.

**Configuration flows one way.** The launcher writes `data/<instance>/instance.json`; the plugin reads it at `Awake`. One writer, one reader. The manifest wins over the BepInEx config because it is rewritten on every create and therefore describes this run, whereas a `.cfg` is sticky across sessions: an instance was once observed booting with a setting left behind by the previous session and nothing indicating it. `/instance` reports `valueSources` so the winner is never a guess.

## Why the lock is rig-wide, and why it is shared code

The lock does not exist to gate what one agent does to its own instances. It exists because more than one agent runs on this machine and three actions are destructive to somebody else's work in a way that leaves no evidence: a rig-wide stop ends instances another session started (and a killed client cannot report afterwards that its run was interrupted, so the interrupted test does not fail, it produces a wrong answer); `remove` deletes a tree and a save root; and two concurrent creates both read the registry before either writes it, compute the same lowest free index, and derive the same default ClientId, which is exactly the collision a single create already refuses.

**One lock for both halves, not one per half.** They hard-link and mirror out of the same install and share per-Windows-user Unity state that nothing separates. On top of that the common case is a test that drives both at once, which under two locks means acquiring two in some order, and an agent acquiring them in the other order deadlocks against it.

**Liveness differs per half, deliberately.** The server counts as busy only when a player is connected, so an abandoned server with nobody on it can be reclaimed. The client half counts as busy when any instance process is alive, which is a lower bar and is correct: on the server a running process with no player is genuinely idle, whereas here the running processes ARE the test, there being no human to connect. The cost is that leaving instances up holds the whole rig with no timer to save you, which is why the release discipline is stated where an agent will read it.

**A host changed the argument, not the decision.** Liveness itself is unchanged: an alive instance is busy whatever role it has. What a host changed is the busy reason TEXT, which is what a human reads when deciding whether to authorize a break, and `unlock`, which used to warn and release anyway. With a live host that is how a world gets torn down by an unrelated agent, so `unlock` now refuses outright while a host is live, overridable with `--force`, which is the routine same-session override and still cannot touch another session's lock because ownership is checked first. The busy probe is filesystem-only, no HTTP, because it runs on the path of every gated command and a control-plane call to an instance mid-world-load can block for seconds. A lock check that hangs is worse than one that is slightly less precise.

Two related decisions: a pid file is not proof of life (Windows recycles ids and these files outlive their processes on a force-kill or a reboot, so the image is checked; otherwise one recycled id would report busy forever and no timer could reclaim the rig), and an orphan is reported but is NOT busy (nothing the launcher does can stop it, so counting it would pin the lock live with no way out except the human-gated break, turning a stray process into a permanently unreclaimable rig).

**One implementation, shared by both halves.** A second copy of the timer, the ownership check and the break gate would drift, and the half that drifted would be the half with the weaker guarantee. Two properties of the lock are worth stating because an API that assumed otherwise would be inventing a concept the design does not have: **the lock stores no process identity at all**, so there is no "owner died" transition and the idle ceiling is the entire substitute; and **`status` is the only genuinely read-only operation**, because reading the lock state self-renews a busy session's timer, so `stop`'s "just check the lock" call mutates the file.

**The idle ceiling is the only backstop that has ever actually fired, and it is worth trusting over anything outside the rig.** An agent took the lock at 07:10 local and was killed at 20:55, having spent **13.8 hours** in a diagnostic loop while holding two client instances and the dedicated server: roughly 28 GB of RAM and the developer's GPU, until the developer had to ask for the machine back. Two mechanisms were supposed to prevent that and exactly one worked. `status` reported the lock expired, the owner idle past the 60 minute ceiling and the lock reclaimable, which is the design behaving correctly under precisely the condition it was built for. The other was a 15 minute agent watchdog armed as a scheduled job outside the rig, and it could not fire at all: its schedule ticks only while the session is idle, and waiting on a running child does not count as idle, so its first tick arrived after the agent had already been killed. It cannot cover the window it exists to cover. `testrig status` answers the question directly and cheaply instead, and a stuck session looks exactly like a lock whose owner last acted a long time ago on a rig that is not busy.

## Why the restore hangs off both ends

The lock is the only mandatory choke point that already exists and is already enforced in code, so an agent cannot get the rig without getting it clean and cannot route around it. A rule in a document is a rule an agent skips.

Restoring at both ends is not redundancy for its own sake. The release is where the guarantee is earned: the agent that made the mess pays for it, while it still owns the rig and the rig is provably idle. The acquisition covers the one case a release can never cover, a session that crashed, was killed, or lost the machine to a reboot and so never reached its own release path. Restores are idempotent, so the pair costs nothing and leaves no gap.

`TestRig/session.dirty` is what makes "restore before granting" possible at all. It is written before the first mutating action of a session, durably (write-through, flushed, atomic replace), and cleared only by a completed restore, so absent means restored and present means somebody mutated the rig and no restore has finished since. It records the OS boot identity as well as the writing pid, because a pid alone lies across a reboot: Windows reuses process ids, so "pid 8123 is alive" after a restart says nothing about the launcher that wrote the marker before it. A marker from a different boot means its writer is definitely gone; one from this boot has its pid checked the same way a game pid is. Anything unverifiable counts as "that session is gone", because restoring twice is free and skipping a restore is not.

**Worlds belong to the session that made them, and the marker is what decides.** The marker records the world set as it stood at that first mutating action, and a world absent from it is deleted at the session boundary. That works because of WHEN the marker is written: everything it lists predates the session by construction, so a world the session goes on to create can never sneak into its own "was already here" set.

**Both halves' worlds are scoped this way, and the client half's used not to be.** Client instance saves were deleted wholesale on the reasoning that a client has no worlds worth keeping, which stopped being true the moment a listen host could write real ones there. The repository documents both save roots as tier 3 and says a world's lifetime is session-scoped without distinguishing them, so a host's world was destroyed by the next restore while the identically-tiered server world beside it was protected. One rule now covers both. Loose files at the top of a save root are not worlds and are still cleared.

**Three ways the recorded set can be wrong, and all three keep every world.** A failed enumeration is recorded as a failure and the key is omitted, rather than serialising as an empty set; a world name that cannot survive the marker's escaping-free format fails the whole scan; and a marker that is missing, unparseable, has no world line, or predates the last reboot is degraded. Deleting a world is the only irreversible thing the restore does, so an uncertain answer must never resolve to "delete": keeping a stale world costs one manual delete, the other mistake costs the test. On top of that, **a plan that would delete more than five worlds refuses outright**, names each one, and changes nothing; a session legitimately creating six is vanishingly rare, and a world set that reads as empty because its enumeration failed produces exactly that plan.

**The baseline used to decide this and must not again.** Staleness looks at the game version, the instance-name set and payload hashes; it never looks at a world. So a world staged deliberately after a capture, which is exactly what the repository's save rules prescribe for restoring a save under test, left the baseline reading FRESH, was absent from it, and was deleted at the next boundary as "a session that is over". The staged save was the test. Every degraded case now keeps every world and says which case it was: no marker (the ordinary clean-rig state, stated), an unparseable one, one with no `worlds=` line, or one from before the last reboot (the last three warn). Deleting a world is the only irreversible thing the restore does and it is hundreds of megabytes of somebody's test state, so an uncertain answer must never resolve to "delete": keeping a stale world costs one manual delete, the other mistake costs the test.

**The reset surface is an allow-list, not a deny-list**, so a deliberate instance-scoped change anywhere outside the named classes survives every restore and is never reported as drift. A deny-list would have the opposite default and would scrub exactly those changes.

The `SavePathOverride` writer is shared by the create path and the reset, which looks like a layering violation and is not: the config re-copy wipes `SavePathOverride`, and an instance without it writes its worlds into the developer's tier-1 save folder, so the two paths that must write that setting write it through one implementation. The suite measures the wipe and then pins the re-apply.

## Hosting from a driven client

The rig could drive clients and could not produce a host who plays, so every test whose subject was the host's own client half was unreachable. A listen host closes that: `NetworkRole.Server` with a player character, the dedicated server's code path with `IsBatchMode` false. The game-side facts are in `Research/GameSystems/ListenHost.md`; what follows is why the rig's shape around them is what it is.

**`/host` is modelled on `/connect`, not on `/newworld`.** `/connect` is the other endpoint that changes this process's network role, so it already carries the three things hosting needs: the duplicate-identity refusal, a per-step main-thread hop rather than one long call against the 20 s budget, and an embedded `/status` in the answer. `/newworld` carries none of them, and modelling on it would have produced an endpoint that answers 504 while the work is still going fine. The three "poll until `GameState == Running`" loops in `/connect`, `/load` and `/newworld` were separate copies that had drifted; they are one helper now, which `/host` also uses. Its one parameter worth naming is `failAtMenu`: a join that falls back to `GameState.None` has failed, whereas `/load`, `/newworld` and `/host` all START at None.

**The settings write is a direct field assignment, and that is the load-bearing choice.** `Settings.CurrentData.StartLocalHost` is read by `GameManager.StartGame()` at world entry and by nothing afterwards, so the write has to land BEFORE the load or the create; setting it on a world that is already up does nothing. The obvious route is the game's own `settings <name> <value>` console command, which `/load` and `/newworld` already use, and it is a trap: `SettingsCommand.OnValueChanged` calls `Settings.SaveSettings()`, which serialises the WHOLE `SettingData` to `setting.xml`. One such call persists `StartLocalHost=true`, and the next launch of that instance comes up hosting while a test believes it has a plain joiner. Closing the in-game settings panel does the same, and so does `Settings.ValidateSavePath()` returning true at boot. A direct field write stays in memory and dies with the process.

Nothing inside the endpoint can prevent the other three paths, so the state is reported instead. `/status.startLocalHostPersisted` reads the flag out of `setting.xml` on disk (a string scan, not an XML reader, so a malformed file degrades to "unknown" rather than throwing inside an endpoint a harness polls constantly), next to `startLocalHostInMemory`. `stop` then clears the flag from `setting.xml` after the process is gone, which it must be, because the game rewrites that file on exit. Worth knowing alongside it: a rebuild does not reset `data/<instance>/`, so `setting.xml` survives one; a fresh lock is what clears it.

**"The call returned" is not evidence, twice over.** `NetworkServer.Host()` returns early with nothing but a console line when `GameState == None` (hosting from the main menu), and after a failed bind it retries three times a second apart and then returns quietly. So `/host` asserts in two stages: that the world reached `Running` at all, then that `NetworkServer.IsHosting` is true and `/status.role` is `listenHost`, with a 15 s budget for the second to allow for the retry ladder. A failure at either stage answers 409 carrying the console tail, the requested port and the full `/status`, because the useful information after a silent failure is what the GAME said.

`/status.role` exists for the same reason one level up. `IsActive`, `IsServer` and `IsClient` are three views of one enum field and they read backwards for the case that matters most: a listen host is `NetworkRole.Server` and therefore reports `IsClient == false`, the opposite of the intuition that a hosting player is a client that also serves. Computing the answer once, inside the plugin, means nothing downstream re-derives it and gets it wrong.

**The tier-1 gate had to stop routing through a patched getter.** `Router.DefaultUserDataPath()` used to read `StationSaveUtils.DefaultPath`, which StationeersLaunchPad has already Harmony-patched to return its own `SavePathOverride`. On a created instance that is the instance's own save root, so the "is this inside the real user-data folder" check was comparing the candidate against the safe folder rather than the dangerous one, and both answers were inverted: pointing a running instance at the developer's real save folder was allowed with no `force=true`, while a legitimate redirect inside the instance's own root was refused. The comparand is computed here now, from the Windows shell folder, matching what the launcher computes, and it fails closed. `GET /savepath` reports both paths so the difference is visible instead of assumed.

`SavePathOverride` itself used to be written at the end of the mod seed, behind that function's early return for a developer with no `modconfig.xml`. An instance created on such a machine, or with `--no-seed-mods`, got no redirect at all and wrote into the developer's tier-1 folder behind a warning whose text mentioned only mods. It is written unconditionally now, ahead of the seed, and a failure throws for `--role host` and warns for `--role client`. That asymmetry is the point: a joining client reads a world the server owns, a host creates one. The registry entry is also written before the redirect is attempted, because the throw used to leave a tree with no registry entry and every remedy the message named unreachable.

**Two collisions the rig has to refuse, for different reasons.** ClientId, covered under Identity below, with one addition: the host consumes an id of its own and it exists FIRST, so a joiner that collides takes over the HOST's body. `/host` therefore applies the same `PeerProbe` gate `/connect` does, with the same `allowDuplicateIdentity` escape. `TotalPlayersInGame` on a host is `Clients.Count + 1` and the host appears in its own roster, so anything counting joiners subtracts it. Game port is the other: a second TCP listener on a taken port fails loudly, which makes the control-plane port check mostly bookkeeping, but RakNet does not behave that way. Two UDP bindings on one port coexist and which socket receives a datagram is decided by its destination address, not by who bound first. Nothing errors and nothing warns, so the joiner talks to whichever binding won and the test passes or fails against a session nobody chose. That failure is invisible from inside the game, which is why it has to be refused in the launcher before anything is launched.

**Teardown is classification first, action second.** Registry insertion order used to decide it, which normally meant the host went first and took the world down under every joiner still in it. The order now falls out of a classification pass over the WHOLE rig, taken before anything is stopped, because the refusals are only worth having while the rig is intact. It is two passes because one is not enough: pass 1 asks each live instance what it is, pass 2 classifies, and classification needs the whole rig, since an instance whose control plane does not answer is only safely a joiner while NOBODY is joined to anything. The moment any instance reports `joinedClient`, a silent process is a candidate for the thing it joined. On a cold boot nobody is joined to anything, so a booting instance does not make a rig-wide stop ceremonial. Killing a joiner instead of disconnecting it would leave the host holding a peer that never said goodbye, and that is precisely the state the host is about to write to disk.

**`/save` reports what the game said, never what was asked.** The client half could create a world and had no way to persist one, which was the largest remaining guarantee gap against the dedicated server. The evidence is the console, corroborated by the file: `Starting Save for <name>` separates "the save never started" from "the save started and is still running", which is the difference between a broken call and a big world; `Saved <name>` (or `Created new save` for a first save) is printed only after the `SaveResult` comes back successful. Every failure path prints through `ConsoleWindow.PrintError`, so a failed save answers immediately instead of burning the whole timeout. The head `.save` file's size and write stamp are read afterwards and reported, but are the PRIMARY signal only when the console tap is not patched, because the file's write time moves while the zip is still streaming and on its own can confirm a half-written save. Confirmed is 200; asked-for-but-unconfirmed is 409 with `requested:true` and a warning. Answering 200 for a fire-and-forget call would be worse than having no endpoint, because a test would then tear the rig down believing a world it never wrote is on disk.

## The cursor gate

The single most expensive thing in the rig's history: it cost a session and produced a confidently wrong acceptance-test result before it was found.

`Assets.Scripts.Inventory.InventoryManager.ManagerUpdate` opens with:

```csharp
if (Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen)
{
    return;
}
CheckDisplaySlotInput();
CheckSeatedInput();
...
switch (CurrentMode) { case Mode.Normal: NormalMode(); break; ... }
```

Everything input-driven sits below that early return. `CheckDisplaySlotInput` is the only writer of `InventoryManager.newScrollData` in the entire assembly (one assignment, `newScrollData = Input.mouseScrollDelta.y / 10f`, with no reset anywhere: the once-per-frame overwrite is the reset), so a shut gate means no wheel is sampled at all. `NormalMode` never runs, and that is where mods hang their per-frame gameplay hooks, so a driven client never reports its own client-half state to a server and the server falls back to its permissive default. The same `Cursor.visible` term gates movement.

**Why an unfocused window trips it.** Unity releases the cursor lock when the application loses focus. `MouseModeController.SetState` tries to re-lock every frame and cannot take it back while the window is in the background, so `Cursor.visible` stays true for the whole session. The dependency on foreground focus is entirely second-order, through the cursor, and entirely inside managed code: `Application.isFocused` has zero occurrences in the assembly, `Application.runInBackground` appears once inside a diagnostic print, and the single `OnApplicationFocus` only restores cursor state on regaining focus.

**The measurement, not the inference.** From one instance at the main menu, before it had ever entered a world:

```
gateAsserts             1685
cursorForcedHiddenCount 1685

GameManager.Update                       enter 1692  exit 1692
KeyManager.ManagerUpdate                 enter 1685  exit 1685
KeyMap.PollInputs                        enter 1685  exit 1685
InventoryManager.ManagerUpdate           enter 1685  exit 1685
InventoryManager.CheckDisplaySlotInput   (absent)
InventoryManager.NormalMode              (absent)
```

Balanced all the way down to `ManagerUpdate`, then nothing. **Balanced-then-absent is the shape of an early return; unbalanced would have been the shape of an exception.** That distinction ruled out the competing explanation, a throwing Harmony patch aborting `GameManager.Update`. In world with the gate forced open the two missing links appear and keep pace (14,774 / 14,774 and 15,001 / 15,001 on the two instances).

**The fix** is a prefix on `ManagerUpdate` that asserts the cursor state a few instructions before the gate reads it, so nothing can intervene within the frame: hide the cursor, lock it, and force `KeyManager.InputState` back to `Game`. No window focus, no OS input, no window-state call. The first version asserted unconditionally, which held the cursor hidden at the main menu where the cursor is the only way to interact with anything; `GameplayGate` scopes it to `GameState.Running` and yields while a confirmation dialog is up. `GameState` is resolved reflectively and cached per frame, so a game update that moves the enum degrades to "the gate never asserts, and `shutReason` says why".

Two further gates also read as "input did nothing": `ConsoleWindow.IsOpen` short-circuits every `KeyManager.GetButton*` call for any key other than `ToggleConsole`, and `KeyWrapBindings.KeyWrapOnEvent` filters every KeyWrap-bound action on `item.inputState.HasFlag(KeyManager.InputState)`, so a panel that pushes a state and never pops it leaves every bound action inert, `SwapHands` among them.

## Input layering

**Why `UnityEngine.Input` and not `KeyManager`.** Every `KeyManager` query bottoms out in the Unity layer, and a great deal of game code calls `Input.GetKey(KeyMap.X)` directly: there are 139 direct `Input.*` call sites in `Assembly-CSharp`. There is also no cached key state to sit under, which was checked rather than assumed: `KeyWrap.PollForInput` calls the Unity API directly and fires its C# events synchronously from inside that stack, its `IsPressed` properties are read by nothing in the assembly, `KeyMap.PollInputs` writes no state at all, and there is no modern input package anywhere (no `UnityEngine.InputSystem` references, no `Unity.InputSystem.dll`, only `UnityEngine.InputLegacyModule.dll`). So the Unity layer is the one chokepoint, and patching it means "Shift is held" says the same thing to every consumer. `KeyMap` is a static class of mutable `public static KeyCode` fields, rebindable at runtime, not an enum, so `/input/key` resolves an action name against the live field.

**The frame-window model.** Synthetic input is an absolute `Time.frameCount` window, never a countdown ticked from `Update`, because MonoBehaviour update order is undefined and a countdown can expire before the frame's real consumer runs. One frame of wheel is one notch: consumers act once per frame, so a two-frame injection scrolls two notches. `frames` defaults to 1 and `repeat` with a gap is how to travel several steps.

**The read-back is the contract.** "The driver applied the override" and "the game read the override" are different claims and only the second is worth anything; the old shape could only report the first, and answered `{"ok":true,"settled":true}` for a keypress that never happened. `VirtualInput` records, per KeyCode, how many times a synthetic value was handed back to a caller, split by `GetKey` / `GetKeyDown` / `GetKeyUp`, plus wheel and mouse-position counters, and the bookkeeping is written only at the moment a synthetic value is returned, so an untouched key costs nothing on the hot path. `consumed = delivered && the gate was open`. For the wheel the honest number is `gate.checkDisplaySlotInputRan`. `requireConsumed` defaults to true, which is the defect turned into a default. `ScrollDataBackstopPatch` is belt and braces: a postfix on `CheckDisplaySlotInput` assigns `newScrollData` directly, so if the `get_mouseScrollDelta` prefix ever fails to apply the value still lands in the field consumers read.

## Identity

**Stationeers does not get identity from Steam at join time.** It reads `PlayerCookie-v2.xml` from `Application.persistentDataPath` and honours it verbatim whenever `Version == 2 && ClientId != 0`:

```csharp
// NetworkManager
public static ulong  LocalClientId => Cookie?.ClientId ?? 0;
public static string Username      => Cookie?.Username ?? string.Empty;

// NetworkManager.Init(TransportType), from GameManager.Awake
Cookie = ((!GameManager.IsBatchMode) ? PlayerCookie.Load() : null);
```

Steam is consulted only on the create path (`CreateNewCookie`), not the load path, and both fields have public setters. The server's `VerifyConnection` checks exactly three things: blacklist by the same self-reported `ClientId`, password string equality, and exact game-version string equality. Steam auth is dead code: the only `BeginAuthSession`-family call site is `SteamTransport.Authenticate`, which has zero callers. The familiar server log line is formatted `{name} ({ClientId})` from the client-supplied name, entirely self-reported.

The injection point is a postfix on `NetworkManager.Init(TransportType)`: that is where the cookie is loaded, the earliest point the identity can be rewritten, and long before anything reads `LocalClientId`. Live rewrite through `POST /identity` works too, because the value only has to be correct at the instant the handshake copies it into `VerifyPlayerMessage`.

**`PlayerCookie.Save()` must be suppressed** because `persistentDataPath` is per-Windows-user and cannot be separated (see below), so every instance shares the developer's real cookie file. `Save()` writes the in-memory `ClientId` over it, and its triggers include dismissing the old-save popup, dismissing the major-update popup, and opening the in-game menu with Esc while a world is running. A prefix skips the original whenever an override is configured; skipping is safe because the cookie is only ever read at startup.

**Duplicate identity is silent and destructive.** `NetworkBase.Clients` is a bare `List<Client>` with no dedupe, and the damage is one level down: `RegisterBrain` does a silent `PlayerBrains[steamId] = this` overwrite, so the second joiner resolves onto the first joiner's character, and `GameManager.ClientInfo.TryAdd` silently drops the second client's info. Hence three layers of guard: the launcher refuses to create a duplicate id or port, `PeerProbe` asks every sibling control plane who it is, and `/connect` refuses to join into a detected conflict unless `allowDuplicateIdentity=true`. The join is where the enforcement belongs, because that is where the damage happens. ClientId 0 is refused everywhere: it is the batch-mode sentinel.

It works:

```
12:36:18: Client RigClientOne (900000000001) is ready
12:37:16: Client RigClientTwo (900000000002) is ready
```

Distinct non-zero ids, distinct names, both in world simultaneously, distinct `Human` reference ids at distinct positions, `playersInGame: 3`.

## Window size, and the desktop

**The launch flags lose.** `-screen-fullscreen 0 -screen-width W -screen-height H` are honoured by the native player when it creates the window and then thrown away by the game, twice, neither call guarded by `IsBatchMode`: `Settings.LoadSettings()` (reached from `WorldManager.ManagerAwake()` above that method's own `IsBatchMode` block) and `Settings.ApplyVideoSettings()` (the last statement of `GameManager.Start()`, which runs after `CommandLine.ExecutePostLaunchCommands()`). Both call `Screen.SetResolution` from `CurrentData`, whose `<FullScreen>` defaults to true.

So the plugin corrects the source rather than fighting the symptom: a prefix on `ApplyVideoSettings` and a postfix on `LoadSettings` rewrite `CurrentData.FullScreen / ScreenWidth / ScreenHeight` before the game reads them, and the game's own call then asks for a window. Measured on both instances, in world and at the menu: `screenWidth 800, screenHeight 600, screenFullScreen false, setResolutionCalls 0, settingsRewrites 838`. `setResolutionCalls: 0` is the number that matters. **Nothing writes to the PlayerPrefs registry key**, which is shared with the developer's own client: a registry diff across a full session showed only Unity's own per-run bookkeeping moving, and the values that did move were deliberately not restored, because restoring is still a write to a key this tool must not write to. The original diagnosis blamed that key for the fullscreen launch and was wrong; the key already held Windowed at the start of the session and the cause was purely `Settings.CurrentData`.

Three traps on the way: `ScreenWidth` and `ScreenHeight` are declared **string**, and `ApplyVideoSettings` uses a bare `int.Parse` inside an `async void`, so write digits only; the game's `Settings` class is `Assets.Scripts.Serialization.Settings` and more than one loaded assembly carries a type called `Settings`, so resolving by the bare name returned the wrong one (a `using Assets.Scripts;` does not fix it, because C# `using` imports one namespace and not its descendants), and `WindowMode` therefore resolves it reflectively by scanning for a type with both a static `CurrentData` field and a static `LoadSettings` method; `<Monitor>` is serialized and read by nothing.

**The separate Win32 desktop is the mechanism, not an optimisation.** Launching through `CreateProcess` with `STARTF_USESHOWWINDOW` and `SW_SHOWNOACTIVATE`, sampling the foreground every 3 seconds for two minutes: **40 focus steals out of 40 samples**, foreground moved within 3 seconds of launch and never came back. The cause is that `wShowWindow` only governs the first `ShowWindow(SW_SHOWDEFAULT)`, and Unity calls `ShowWindow` explicitly once its window exists. With `CreateDesktopW` and `STARTUPINFO.lpDesktop` pointed at it, same sampling, both instances running, through a full boot and an entire acceptance test: **0 focus steals out of 55**, the developer's foreground was their editor at every sample.

`SwitchDesktop` is deliberately not imported and nothing switches to that desktop. The instances render, run, join and are driven over HTTP on a desktop that is never shown. It costs nothing measurable: same plugin count, same Steam entitlements, normal boot time, GPU rendering fine. .NET's `ProcessStartInfo` cannot express `lpDesktop` or `wShowWindow` with `UseShellExecute = false`, which is the entire reason the launcher carries a `CreateProcessW` P/Invoke.

**This section used to say the desktop "lives as long as a process runs on it, so there is nothing to clean up", and that was the wrong half of the rule.** A desktop is destroyed when its last HANDLE closes AND no window exists on it, and a launching game has neither for the first seconds of its life. The launcher created the desktop, leaked the handle and exited, which closed it, which destroyed the desktop out from under a process still loading its DLLs. Measured 2026-08-14 on this machine, holding `hProcess` to read the exit code: the child died 0.02 s in with **`0xC0000142`, `STATUS_DLL_INIT_FAILED`**, having created no Unity log file at all and written nothing to the event log. Holding the handle for 2 s was enough to survive, and so was launching while another instance already had a window there, which is exactly what made the failure look intermittent rather than total. The fix hands the CHILD an inheritable handle of its own, which removes the race instead of widening it: the desktop now genuinely dies with its last instance, which is what this section always claimed.

A second trap sits beside it and no build guard can catch it: **`CreateProcessW` does not fail when `lpDesktop` names a desktop that does not exist.** It returns success, creates nothing, and silently lands the process on the CALLER's desktop, which is the exact catastrophe the whole mechanism exists to prevent, reached by a typo. So the desktop is created immediately before every launch rather than trusted to an earlier separate call. `CreateDesktopW` opens an existing desktop, so doing it twice costs one call; do not separate the two steps.

One reporting consequence: `GetForegroundWindow` returns NULL when the calling process is on a desktop that is not receiving input, so the old report showed `foregroundPid: 0` and could not tell "I am a background window on the developer's desktop" from "I am on a desktop of my own". `NativeWindow` now compares the process's own desktop name against the input desktop's and answers `foreground`, `background`, `otherDesktop`, `noForeground` or `unknown`, with `foregroundPid` null rather than 0 when it is not knowable.

The two fixes are independent: the gate is what makes input work and works whether or not the desktop is separate; the desktop is what keeps the developer's foreground.

## Instance provisioning

**Hard links, and what must never be one.** Read-only bulk is NTFS-hard-linked from the real install: `rocketstation_Data`, `MonoBleedingEdge`, and the engine binaries at the install root. Hard links share the file data, so nothing the game or a mod writes to may be a link: `app.info`, `doorstop_config.ini`, `Fixing The Controls modifiers.ini` and the whole `BepInEx/` tree are real copies. `imgui.ini` and `output_log.txt` are not carried at all, being regenerated and resolved against the working directory.

**The link count is a function of the install and moves with every game update.** Three different figures (1,050, 1,046, 1,051) were carried in three documents at once, none of them re-derived after a patch. Measured against the rule the create path actually applies, on **0.2.6428.27798**: `rocketstation_Data` 1,028 files minus `app.info` = 1,027 links, `MonoBleedingEdge` 20, install root 9 files minus `imgui.ini` (skipped) minus the two real copies = 6, for **1,053 links sharing about 6.9 GB**. Do not restate a link count as a durable fact; "about a thousand" is the durable part, and the `create` summary prints the real one for the tree it just built.

Hard links cannot cross volumes, so the instances root must be on the install's drive; the launcher checks and refuses with the exact remediation rather than silently making a 7 GB copy. The resolved root is recorded in the registry entry, and every later action reads it back. A relocatable root that only the create path knew about was worse than no relocation at all: a live run found the start verb reporting a provisioned instance as having no tree, because it looked under `instances/` beside the script while the tree sat on another volume, and the state reset skipped its half of the work for the same reason (no BepInEx tree found, so no config re-copy and no `SavePathOverride` re-apply) while reporting only "no instance tree".

**A recorded root wins, and `create --force` does not relocate an instance.** That was suspected and is not reproducible: measured with `STATIONEERS_CLIENTRIG_ROOT` unset, so the default and the recorded root genuinely differed, the rebuild put all 1,053 links back under the RECORDED root with nothing at the default. What was missing was coverage rather than correctness, because every test fixture set the variable, so no test ever exercised the case. One real silent-relocation case does exist and now warns: an entry that records **no** root at all, where `create` resolves its own and never reaches the notice that would have said so.

**One directory buys all the isolation that is achievable.** The BepInEx root is always `<dir of rocketstation.exe>\BepInEx` and no environment variable relocates it: `BepInEx.dll` and `BepInEx.Preloader.dll` carry zero `BEPINEX_*` env vars, `BepInEx.cfg` has no `[Paths]` section, and `doorstop_config.ini` uses a path relative to the game directory. So a separate install directory automatically yields a separate BepInEx config, plugin set, cache, log and InspectorPlus folders, in one move.

**The two flags that matter, and the one that must never be used.** `-settingspath <file>` gives each instance its own `setting.xml` (it takes a FILE path despite help text saying `<full-directory-path>`). `-logFile <unique path>` is mandatory for a subtler reason than it looks: two instances without it both start fine, which is the trap, and what happens is that the second starter wins `Player.log`, the first instance's log goes nowhere with no error, and `Player-prev.log` is left at 0 bytes because instance one rotated the developer's real previous log into it and instance two rotated the already-moved log over it again.

**`-settings SavePath` must never be used.** It moves the save tree but not `StationSaveUtils.DefaultPath`, so StationeersLaunchPad scans an empty `<SavePath>\mods\`, finds nothing, and rewrites the developer's **shared** `modconfig.xml` with every `<Local>` entry deleted. Observed on a first boot: five local mod entries silently removed, the file 289 lines down to 274, and the instance then loaded 32 plugins instead of 37, exactly the five missing mods. Nothing warned. The correct lever is StationeersLaunchPad's own `SavePathOverride`, which moves `DefaultPath` itself: with it set and the flag dropped, the developer's `modconfig.xml`, `setting.xml` and `modrepos.xml` were byte-identical before and after across four instance boots, each instance wrote its own `modconfig.xml`, and the plugin count went 32 to 37, matching the developer's own client exactly. Because it moves `DefaultPath`, `<Local>` mod folders must exist under the instance's own save root, which is why creating one copies them and repoints the paths.

`-nographics` without `-batchmode` is rejected by the Unity 2022.3.62f3 Windows player: it prints `-nographics requires -batchmode`, pops a modal Win32 error window, and holds a live process that never boots, never honours `-logFile`, never loads BepInEx and never opens a control plane. There is no windowless-but-not-batchmode mode.

### What is not separable

**`Application.persistentDataPath`.** Editing `rocketstation_Data\app.info` was tested directly and does nothing: the instance still reported `persistentDataPath = .../Rocketwerkz/rocketstation`, `companyName = Rocketwerkz` and `productName = rocketstation`, while `dataPath` correctly followed the instance directory, proving the process really was running out of the copy. The player takes company and product from the serialized PlayerSettings inside `globalgamemanagers`. No new `AppData\LocalLow` folder and no new registry key appeared. So `PlayerCookie-v2.xml`, `Player.log`, `Blueprints\` and the PlayerPrefs key are shared by every instance and by the developer's client, and identity is handled in code instead.

**The Steam session.** One Steam client, one account. Every instance reports the developer's entitlements by default and they are not independent Steam identities. That part cannot be changed on one machine.

**Entitlement is NOT part of that, and this section used to say it was.** Until 2026-08-11 it concluded that a test needing one DLC owner and one non-owner was out of reach. The way that was wrong is worth keeping: it states something true about Steam IDENTITY and then draws a conclusion about ENTITLEMENT, which does not follow. Nothing in the game holds a per-player entitlement record at all. `DLCManager._ownedDLC` is a private static filled once from Steam during `Start()`, so it is scoped to a PROCESS, not to the account session behind it; `SharedDLCManager._sharedDLC` is likewise a per-process `ushort` behind a public settable property; and the server's pool is fed by `AvailableDLCMessage`, whose `Process(long hostId)` discards the sender id and ORs in the claimed bitmask with no validation, so entitlement on the wire is client-asserted and never server-checked. What an instance reports is therefore a per-process value a plugin can change, with no second opinion anywhere to contradict it.

**That capability shipped.** The plugin's `Instance/DlcEntitlement.cs` exposes `/dlc`, `/dlc/remove` and `/dlc/restore`, removal only and in memory only, and two committed checks run on it (`Mods/SprayPaintPlus/playtests/DlcNonOwnerReachesMetallic.cs` and `DlcEntitlementOutlivesTheOwner.cs`). The one hard rule is sequencing: a joiner announces `DLCManager.GetOwnedDLC()` at the end of its join and a listen host re-seeds the pool at the end of the load, so a removal after world entry is silently undone. `GET /dlc` carries the full ordering. Call sites and mechanism: `Research/GameSystems/DLCGating.md`.

Why the wrong claim survived three weeks is the general lesson: it was written on 2026-07-27, when the rig could not put two clients in one session at all, and its stated reason was "one Steam account, so there is nobody left connected to observe". Two genuinely separate connected clients landed on 2026-07-30, which retired that reason outright, but nobody re-derived the conclusion it supported, so a sentence about Steam identity kept being read as a sentence about entitlement and propagated into three documents as settled fact. **When a blocker's stated reason stops being true, the blocker needs re-deriving, not re-wording.**

### Measured cost

| Item | Measured |
|---|---|
| Disk, first instance | 3.6 MB actual (2.7 MB BepInEx copy plus directory entries for the hard links) |
| Disk, second instance | 9.7 MB (adds a 6.6 MB copy of the local mods) |
| Shared via hard links | about 7 GB per instance that costs nothing (6.9 GB across 1,053 links on 0.2.6428.27798) |
| Create time | about 2.7 s for the link tree, about 5 s including the mod seed |
| RAM, idle at the menu | about 5.0 GB working set per instance |
| RAM, in world after 10 minutes | about 10.0 GB working set per instance |
| Boot to main menu | about 100 s solo, about 110 s with two booting at once |
| Join to in-world | 42 s and 49 s |

RAM is the constraint, not disk. Two instances plus the dedicated server fit comfortably in 128 GB; four would be tight.

## The console tee bound

The tee once took a client to a 12.75 GB working set with a frozen pump after ingesting over 500,000 lines, and a later run reported 654 dropped lines within five minutes of a fresh launch. With N instances the risk multiplies by N.

A line count alone is not a bound. The lines that arrive during a storm are stack traces and a single one can be megabytes, so 8,000 unbounded strings is not a bounded amount of memory. `ConsoleTap` caps on three axes: lines per source, characters per line (truncate with a marker), and total characters per source (evict oldest until under budget). The third is the one that actually holds when lines are large. All three report, on every `/console/log` response and on `/status`. Eviction nulls the slot rather than merely decrementing a count, so the ring does not pin evicted strings. Two rings, not one: the BepInEx side sees every `Debug.Log` every mod makes, which during mod load is thousands of lines in a couple of seconds, and sharing one ring would evict exactly the lines a test cares about. The sequence counter stays global across both, so `since` polling still yields one ordered stream.

## Plugin lifecycle traps

**The BepInEx plugin component is destroyed during boot.** `OnDestroy` fires on the `BaseUnityPlugin` component while the process keeps running, and `Chainloader.PluginInfos[guid].Instance` is null for every plugin afterwards, including StationeersLaunchPad's own. The first build stopped its `TcpListener` from `OnDestroy` and the control plane silently died a minute after launch. The listener is therefore owned by a static, is never torn down from `OnDestroy`, and a watchdog re-binds if the socket goes away. `Application.quitting` is the only teardown signal that means the process is going away.

## The main-thread pump on a headless server

Re-measured on **0.2.6428.27798** on 2026-08-14, over seven instrumented runs of 190-312 s by two independent agents under the repository's research conflict protocol. The two agree on every rate. This section **replaces** an earlier account in this file that said the plugin's `DontDestroyOnLoad` object is destroyed "exactly twice per session" and that `ImGuiManager.LateUpdate` is the primary pump. Both claims were wrong, and each predicted a different wrong fix. The curated versions are `Research/Patterns/MainThreadDispatcher.md` and `Research/GameSystems/SimulationTickDriverHooks.md`.

**A plugin's own `Update` never fires on the dedicated server. Not rarely: zero times, in every run.** The component and everything it creates in `Awake` are destroyed **135-219 ms in, at `Time.frameCount == 0`, before the first scene loads**, with `Start()` never reached. `DontDestroyOnLoad` does not save it: the call appears to succeed (scene name `DontDestroyOnLoad`, handle -12) but does not bind, because `SceneManager.sceneCount` is 0 at that moment, so a `DontDestroyOnLoad` object and a plain one beside it die 1 ms apart. The repo lore that "`Update` does not reliably fire after world load" describes a real symptom and attributes it to the wrong cause. The player loop never stalls; it runs at about 25 Hz for the life of the process. What dies is the object. Only a **replacement** object ticks, which is why every earlier account looked inconsistent.

Static state survives: Harmony patches, background threads, and a `SceneManager.sceneLoaded` subscription registered in `Awake`. So the pump host is created from the **first `sceneLoaded` callback**, at 282 ms and still at frame 0, after which it survives indefinitely and misses nothing (Update 5867 at `Time.frameCount` 5867, no gap). Recreating at the later Base scene load instead puts it at frame 1925 and loses everything before that. Nothing may create it in `Awake`.

**The frame index of that first callback is a sample, not a constant, and nothing may key off the number.** The instrumented probe saw it at frame 0; the merged plugin's own log line reported frame 1834 in one measured headless run and **frame 1635** in the first real one, on the same game build. What varies is how much work happens before the first scene load, which headless is the mod-content load and therefore depends on the mod set. The design does not depend on the value: the UniTask boot loop covers whatever the window turns out to be and retires when the host exists, and `pumpHostCreatedAtFrame` is reported so a run can say what it got rather than assert what it should be.

**`ImGuiManager.LateUpdate` does not exist on the dedicated server.** Not "present but never called": the class is gutted in the server assembly. Mono.Cecil metadata gives the client build 19 methods and 17 fields against **1 method (`.ctor`) and 0 fields** on the server build, and the base chain declares no `LateUpdate` either. Live, `AccessTools.Method(ImGuiManager, "LateUpdate")` returned null in every run and `FindObjectsOfType(ImGuiManager)` returned 0 instances at every sample. Everything that rode that one postfix was therefore dead headless, which is why it cannot be a primary pump on either build.

**`GameManager.Update` is the steady-state primary on both builds, and is unusable during boot.** It runs on thread 1 at about 24 Hz once `GameState.Running`, unaffected by pause, but at **0.11-0.16 per second before that**, while frames advance at 25 Hz throughout. Making it the sole pump would leave a control plane nearly frozen for the whole 80-90 s boot, which is exactly the window a caller spends polling for readiness. It needs the frame-0 pump host beside it.

**`UnityMainThreadDispatcher` works, drains from `ManagerUpdate`, and has no `Update`.** Its twelve methods are `.cctor`, `.ctor`, `ActionWrapper`, `ClearAll`, three `Enqueue` overloads, `Exists`, `Instance`, `ManagerAwake`, `ManagerUpdate`, `OnDestroy`; patching `UnityMainThreadDispatcher.Update` resolves nothing. `GameManager.Update` is the sole caller of `ManagerBase.ManagerUpdate` in the assembly, so patching `GameManager.Update` gets the same tick and is earlier. It executes enqueued work **while the world is paused**: over 287 s with no client and force-unpause off, `ManagerUpdate` ran 4,699 times at about 24 Hz and enqueued items executed on thread 1 with 4-37 ms latency. Two operational caveats: `Exists()` is false for the first ~35 s of boot and `Instance()` **throws** rather than returning null in that window, so check before every submit; and latency during world generation is measured in seconds, not milliseconds (4,238 ms and 4,650 ms against 4-37 ms once up), so a fixed marshal budget must be seconds.

**`ElectricityManager.ElectricityTick` is not a main-thread pump and must never become one.** It fires on a UniTask ThreadPool worker: measured 115 calls, 115 of them off the main thread (id 40 against main id 1), and in earlier runs its thread id rotated through 20, 25, 42, 50, 9, 58, 44, 45 and 57, never 1. Draining a main-thread marshalling queue from there executes every queued Unity call on a worker, where `UnityEngine.Object.FindObjectsOfType` crashes the engine native side intermittently. That is why every scenario body iterates `OcclusionManager.AllThings` instead, and why the merged plugin's drain refuses to run work off the captured main thread. `ElectricityTick` remains the simulation-liveness signal and the scenario pump, nothing else. `Research/Patterns/MainThreadDispatcher.md` recommended exactly this mistake as a recovery pattern until 2026-08-14 and has been corrected.

**Do not build on `FixedUpdate`.** `Update` and `LateUpdate` are unaffected by pause (24.85-25.06 per second in every regime). `FixedUpdate` is gated on `Time.timeScale`, not on `IsGamePaused`, and on a headless server the two disagree: `GameManager.StartGame()` assigns `Time.timeScale = 1f` while `IsGamePaused` is already true, so `DelayedStartupPause`'s `SetGamePause(true)` hits the `if (IsGamePaused != pauseGame)` guard and never drops the scale. A nominally paused server therefore still runs `FixedUpdate`, while a real `SetGamePause(true)` transition stops it dead. Neither emits a log line.

### The world parks, and it never ticks once

`GameManager.DelayedStartupPause` pauses a dedicated server's world about five seconds after start with no client connected. It is load-bearing for the whole server half and was absent from this file entirely, which cost one session outright as the fourth blocker on a regression guard.

Measured on 0.2.6428.27798, `-new Lunar`, `Force Unpause Without Client` false, no client, 287 s: `IsGamePaused` true throughout, **`GameTickCount` 0 for the entire run**, `SetGamePause` fired twice and both before any tick, `ElectricityTick` never fired once. **Zero ticks, not "a few ticks then a park"**, so a tick count is not a park detector and nothing may be written as one. What is unaffected is the main thread, which keeps running at about 24 Hz while the world is parked, so a control plane inside the process answers normally and a readiness probe that depends on the simulation does not.

Two consequences worth stating where they will be read. The main thread keeps running while the world is parked, which is why `wait --stage inWorld` on the server reads `/status.phase` from the merged plugin rather than inferring readiness from simulation activity. And an in-process probe armed at boot on a parked server produces one log line and then silence forever, which is indistinguishable from a typo in its id.

**`ImGuiManager.RenderOverlay` skips `OrbitalSimulation.Draw` entirely while the splash or loading screen is up**, and StationeersLaunchPad hangs all its in-game ImGui windows off a prefix on that method, so `/modsettings` needs `gameInitialized == true` and not merely loaded mods.

**`ConfirmationPanel.IsVisible` is `gameObject.activeInHierarchy`** and reads true during boot with an empty data stack behind it. A dialog counts as showing only when `_dataStack` has data.

**StationeersLaunchPad mods are invisible to `Chainloader.PluginInfos`**, which only lists what BepInEx loaded out of `BepInEx/plugins/`. `/config` and `/plugins` therefore resolve plugins by scanning loaded assemblies for `[BepInPlugin]`.

**A failed Steam Workshop query parks the client forever.** When StationeersLaunchPad's `FetchWorkshopPage` throws (a transient Steamworks `NullReferenceException`), it prints "Mods failed to load" and sits on its own ImGui screen, never reaching the menu, with `loadedPluginCount` stuck at 2 and `gameInitialized` false. The barrier names this explicitly when it times out. Stop and start the instance; it clears on retry.

**The join has a 10 second timer a modded server cannot beat.** `NetworkClient.OnJoinStart` arms a timer whose only job is to give up and pop a modal, so the handshake reaches the server and then the client cancels itself mid-transfer. `/connect` calls `NetworkClient.StopConnectionTimer()` immediately after `JoinClientFromMenu` (`suppressTimeout`, on by default) and uses its own timeout; if a dialog appears anyway it reads it, clicks OK and reports the text. Related: `/connect` often fails on the first attempt after a server restart and succeeds on the second, because the client is still settling from the previous disconnect, and `NetworkClient` is not findable for the first minute (`FindObjectOfType` only sees active components, so `/connect` falls back to `Resources.FindObjectsOfTypeAll`).

## The cursor-force wedge

`/cursor/force` exists, is guarded, and should be avoided.

The cursor is a tuple, not one field. Vanilla always writes `FoundThing` and `CursorTargetCollider` together, and `{FoundThing = X, CursorTargetCollider = null}` is a pair the game itself can never produce. `PlantAnalyserCartridge.GetScannedPlant` walks straight into it: `Thing.GetSlot(null)` reaches `Dictionary.TryGetValue(null)` and throws on every Thing, because the dictionary is eagerly constructed.

That throw is unrecoverable. The cartridge runs from `GameManager.Update`, and `CursorManager.ManagerUpdate`, the only caller of `SetCursorTarget`, runs later in the same method with no try/catch between them. The exception aborts the frame before the cursor can be rebuilt, the stale target survives, and it throws again next frame, forever. `NetworkManager.ManagerUpdate` is in the same loop, so a wedged client also stops processing network packets. Measured at 100 exceptions per 6 seconds; only leaving the world recovered it.

So `/cursor/force` refuses a target it cannot find a collider for, preferring a collider that is actually a key in the target's slot lookup, and `clear` writes the game's three fields directly rather than only dropping the pin, so it recovers a client whose `SetCursorTarget` is no longer reachable (that still lands because the plugin's pump is not downstream of the aborted `GameManager.Update`). `FoundTerrain` is pinned to `Invalid` deliberately: `CursorManager.GetCurrentVoxelWorld` hard-casts `CursorTargetCollider` to `BoxCollider` guarded only by `CursorTerrain.IsValid`, so a valid terrain paired with a non-box collider is a second way to throw out of the same loop.

**Nothing needs the cursor.** `OnServer.AttackWith` with an explicit target has no distance or line-of-sight gate (a stroke landed from 15 m away), so `/player/use` with a `targetId` is always the better route. Picking an item up needs no cursor either: `OnServer.MoveToSlot` is what every inventory drag ends in, and on a client it is a `MoveToSlotMessage` to the server, whose only gates are `CanEnter` and "destination slot is empty". No proximity check, no ownership check. That is what `/inventory/arm` and `/inventory/move` do, and it is why forcing the cursor onto a dropped item was both unnecessary and doomed. Full inventory: `Research/GameSystems/CursorManager.md`.

## Transport

The control plane is a raw `TcpListener` speaking minimal HTTP/1.1, not `HttpListener`. `HttpListener` on the Microsoft CLR goes through http.sys and needs a URL ACL reservation or elevation; under Unity's Mono the managed implementation has its own quirks with keep-alive and binary bodies. A socket plus a small parser has no such dependencies.

One request per connection, always answered with `Connection: close`, served inline on the accept thread. Requests are therefore strictly sequential, which is a feature: a harness that fires two engine mutations concurrently gets nondeterministic results.

**A caller that hangs up before reading its answer is normal traffic, not a fault, and it used to cost a stack trace every time.** The accept loop logged `ex.ToString()` for anything the serve threw, so a poller that timed out or gave up produced a seven-frame `IOException ... connection was aborted` out of `WriteResponse`, six of them in the dedicated server's first real session. A control plane that stack-traces its own routine traffic makes a real fault harder to find, which is the only thing that log is for. The discriminator is the `SocketException.SocketErrorCode`, never the message text, which is localised: `ConnectionAborted`, `ConnectionReset`, `NotConnected`, `Shutdown`, `OperationAborted` and `Interrupted` are counted and reported as `/status.driver.serverClientDisconnects`, with one prose line the first time so the counter is discoverable from the log alone. `TimedOut` and `ObjectDisposedException` are deliberately excluded: the first means a caller that connected and then stopped reading for the whole send timeout, and the second would mean this code closed its own stream. Both are worth a trace.

The JSON reader is hand-rolled because the game ships no JSON library a BepInEx plugin can safely reference. It diverges from strict JSON in exactly one place: an **undefined** escape keeps both characters rather than dropping the backslash, so a hand-written `"C:\Rig\Scratch"` survives. The escapes JSON does define (`\b`, `\f`, `\n`, `\r`, `\t`) still consume the backslash, which is why `/savepath` refuses a path containing a control character instead of using it, and why a query parameter is the reliable way to send a Windows path. `ClientId` travels through the manifest as a **string**, because the JSON number parser goes through `double` and silently loses precision above 2^53, and a truncated ClientId is exactly the failure the field exists to prevent.

## Dedicated-server internals worth knowing

**Why the wrapper exists.** The server is launched by a detached wrapper with no console window (the binary re-invoking itself as the internal `host-mode` verb; it used to be a hidden PowerShell process, and before that `Start-Process -WindowStyle Hidden`, which allocated a conhost window that briefly stole focus on Windows 10 and 11 before `SW_HIDE` was honoured). The wrapper holds the server's redirected stdin, and that reaches nothing (see "Stdin does not reach a batch-mode server" below); the console is reached four other ways, of which the rig uses two. The launch line, which is where `-load` / `-new` and every `-settings` pair go. An in-process `ConsoleWindow.Submit`, which is what `POST /console/exec` drives and what `save` and the graceful half of `stop` ride on. `serverrun` from an authenticated client, which is a `ServerRunCommandMessage` and needs a connected player. And real keystrokes at a real attached console, which exist only when `-logFile` is absent, so never here. Reading a command's ANSWER is a separate problem from submitting it: `status` writes to the in-game console rather than the `-logFile`, so it cannot be scraped from outside, and `/console/exec` returns the lines the command printed. The connection lifecycle IS logged, which is how the player count for lock liveness is read, and it stays log-scraped so it answers on a server with no plugin deployed. There is no `clients` console command to ask instead: it existed once and is gone at 0.2.6428.27798, along with `networkdebugwindow`, while `announce`, `roomevaluator`, `pylonlog`, `deletenear`, `voxelfillnear`, `proxy`, `organs` and `thumbnail` have been added. The wrapper's process image is checked as `testrig`, with `pwsh` and `powershell` still accepted, because a rig mid-migration can have a PowerShell wrapper alive and a wrapper reported dead is a wrapper whose orphaned server nothing will clean up.

**Readiness has no cheap signal from OUTSIDE the process, and one good one from inside.** The process registering its pid happens long before the world is tickable, and the gap is dominated by save size (a populated world takes minutes, an empty map seconds). Per-mod "Patches applied" lines fire during prefab load, before world load, so they confirm the mod loaded and nothing else.

`wait --stage inWorld` therefore polls `/status.phase` on the merged plugin's own port, which is the process stating its game state rather than anything inferring one. It replaced an InspectorPlus probe whose CONSUMPTION was read as readiness, and that inference was measured wrong on 2026-08-15: a `--new Moon` the game rejected left a server with no world running indefinitely, InspectorPlus consumed the probe four seconds in, and the barrier reported "the world is loaded and the simulation is ticking". Consumption is evidence about InspectorPlus and about nothing else.

Two things end that wait before its deadline. `No such world name:` in the log is a hard failure carrying the game's own list of what it would have accepted, because the server prints it once and then runs forever with no world; and nothing answering on the control port is its own failure naming the deploy, because without the plugin nothing outside the process can prove a world is loaded. The hand alternatives that remain valid are a named probe save (the console's save is only processed after world load completes, so its confirmation line is a synchronous gate) and grepping for the first AutoSave line (reliable, zero setup, and slow, since it is bounded by the AutoSave period).

**`-new <World>` is validated before launch, against the install's own data.** The accepted name is the `World Id` attribute inside `StreamingAssets/Worlds/<Folder>/<File>.xml`, and it is NOT the folder name in four of nine cases on 0.2.6428.27798: `Europa` holds `Europa3`, `Mimas` holds `MimasHerschel`, and `Vulcan` holds both `Vulcan` and `Vulcan2` in two files. Worlds carrying `<IsTutorial Value="true" />` are excluded, which is what makes the parsed set exactly the seven the server prints when it rejects one. A catalogue that cannot be read validates nothing and says so, because refusing a world the game would have started is a worse failure than the ninety-second boot this prevents.

### Stdin does not reach a batch-mode server, and the control plane does

This was carried for months as "one observation, recorded and not reproduced since": on 0.2.6228.27061 a session found `send`, `save` and the graceful path of `stop` having no observable effect, with the wrapper forwarding correctly and the game not acting. **It reproduces, and it is now measured on both channels.** On 0.2.6428.27798, 2026-08-15, against a server in world with the merged plugin deployed:

| Channel | What happened |
|---|---|
| `send --command quit` (stdin, via the wrapper's control file) | The wrapper consumed the control file in **0.26 s**, so the launcher's entire half of the channel worked, and the server was **still alive 90 s later having written zero bytes to its log**. |
| `POST /console/exec {"command":"quit"}` (in-process, `ConsoleWindow.Submit`) | The call returned in **0.16 s** and the process exited **2.55 s** later, with a full 17 KB Unity shutdown dump. |

A console `help` submitted through the plane between the two returned 452 lines, so the server was healthy throughout and the stdin quit was genuinely ignored rather than lost to a wedged process. The game internals are in `Research/GameSystems/DedicatedServerSettings.md`, section "The console channel: what actually feeds the runtime dispatcher"; the short version is that the only reader wired to the dispatcher is `RocketSystemConsole.ConsoleInputThread` on `Console.ReadKey`, which reads the Win32 console input buffer and not a stream, and `ConsoleWindow.CustomLogFile` gates that reader's construction on `-logFile`, which this half always passes. Removing `-logFile` does not buy a working stdin either; that was measured too.

**So both verbs that have to make the game DO something go through the plane, and keep stdin as the fallback.** `stop --target server` asks for a quit there first: that is why a 30 s grace followed by "force-killing" was the normal outcome of every graceful stop and is not any more, since a control-plane quit is about 2.5 s. Two consequences beyond the timing. An **orphaned** server, whose wrapper died, used to be force-killed unconditionally because stdin needs the wrapper; it now gets a graceful quit, because the plane does not. And the force-kill warning splits in two, because the cases mean opposite things: after stdin it is expected and names the deploy that would fix it, while after a quit the console accepted it is a real problem and says so.

`save --target server` followed on 2026-08-15 and submits `save "<name>"` the same way. It had been left on stdin, so the one verb whose entire purpose is to persist a world was the one still using the channel that does not carry, and it survived only because its two confirmations (the log's own `Saved <name>` line, and the file on disk) are downstream of the channel and kept working while nothing above them did. That is why the defect read as "saves sometimes time out" rather than as "saves never happen": an autosave landing inside the wait window confirms through the same two witnesses. The fallback is kept for a server whose plugin is not deployed, with one difference from `stop` said out loud in the warning: a quit has a force-kill behind it, and a save has nothing behind it, so a save that falls back is not expected to happen.

Stdin is kept rather than deleted, for the plugin-less case and because a force-kill after it is a correct outcome. What remains on it: the two fallbacks above, and `send --target server`, which is a direct request for the channel and now warns on every use that it reaches nothing and names `call --path /console/exec` instead. One smaller thing worth knowing: the wrapper's `game.WriteLine` sits inside a catch that treats `IOException` as "locked or already gone, retried on the next tick", but the control file has already been deleted by then, so a pipe failure there loses the command silently.

## Checks are compiled in, and that makes attestation stronger

An AOT binary cannot load managed assemblies at runtime, so a playtest check cannot be a file discovered on disk. Checks are C# under `Mods/<Mod>/playtests/`, globbed into `TestRig.Playtests` and compiled into the binary; adding one requires a rebuild, which the source-hash rule already required of every other change and which is one command.

That constraint turned out to improve the thing it was expected to cost. The old design wanted attestation derived from a check's location rather than from a declaration, because five declared hashtable keys were trusted with no way to check them. In C# the location is `[CallerFilePath]`: **the compiler supplies the value, so a check cannot lie about which mod it tests**, and three of the five fields (`Mod`, `DllPath`, `DeployedRelativePath`) become derivable. The remaining two were config-entry counts, which are properties of a build rather than of a location; they are replaced by a content hash of the deployed DLL against the build, which is the question they were badly approximating. That also closes a real hole: `Assert-BinaryUnderTest` compared file **length** while its own docstring claimed a content comparison, so a same-length different build attested cleanly.

The engine stays mod-agnostic, as it always was: nothing in `TestRig.Playtest` names a mod, a prefab, a setting or a guid, and `TestRig.Playtests` is the only place a check lives. A mod with no checks contributes nothing.

An earlier decision in this port went the other way, hosting each check as a PowerShell subprocess so the bodies could port verbatim, on the reasoning that a check body carries expensive knowledge in its `-Because` strings and comments (one records a campaign lost to reading `Thing.CustomColor`, a reference-typed member whose rendering is its bare type name, so `matchesPrefab` is always true; another records eight measured layouts proving that cables spawned by `Constructor.SpawnConstruct` never join each other's `CableNetwork` on this rig). That knowledge was carried across as comments rather than lost, and the reasoning is recorded here so the trade is not silently re-litigated: the cost of a check is dominated by starting game instances, never by interpreting its body.

## The merged plugin, and why it was blocked on the pump

`ClientDriver` (the control plane inside a game client) and `ScenarioRunner` (in-process probes on the dedicated server) do the same job in one process each, and the split exists because `ScenarioRunner` predates the control plane, not because a headless process cannot host a listener. `TestRig/dev-plugins/TestRig/` is the single plugin that replaces both, and it is deployed on both halves: instances on 27700 + index, the dedicated server on 27750.

**That one fact retired three refusals and one inference.** `call --target server` refused with text describing the pre-merge world while the plane was up and answering; `snapshot` refused on grounds that were no longer true and now refuses for the reason it actually has (the server owns no registry row to key a per-instance row on); `wait --target server` refused `ping` and `modsLoaded`, which the server can now reach; and readiness stopped being inferred from a file disappearing. A refusal whose reason has stopped being true is worse than no refusal, because it corrects a caller's model in the wrong direction at the exact moment they are forming one. Re-read the whole matrix whenever the shape of the rig changes.

The listener was never the obstacle: it is `System.Net.Sockets` plus `System.Text` on a runtime both halves already run, owned by a static rather than by the MonoBehaviour, re-bound by a watchdog. **The obstacle was the pump**, and the whole of "The main-thread pump on a headless server" above is the measurement that unblocked it. The design that follows from it: a thread-identity check on the drain so queued Unity work can only execute on the captured main thread, three hooks feeding that drain because no single one covers both boot and steady state, and the game's own `UnityMainThreadDispatcher` as a second route. Scenario dispatch deliberately stays on the simulation-tick worker, because roughly 85 scenario bodies were written against that contract and marshalling them quietly would change what they measure.

Two things the merge fixed that were defects rather than duplication. `ClientDriver`'s fallback pump resolved `Assets.Scripts.Atmospherics.ElectricityManager`, a namespace that does not exist, and only ever resolved through a bare-name fallback that would have matched any type called `ElectricityManager` in any loaded assembly. And `/config/set` had drifted: the server's poller defaulted `save=false` while the client's route defaulted `save=true`. **`save=true` wins on both**, because a write that is not persisted disappears on the next reload, producing a test that passed once and cannot be reproduced, silently, since the in-memory value was correct for the whole run.

Full detail, including the endpoints the dedicated server refuses and how the four scenario-arming traps are closed: `TestRig/dev-plugins/TestRig/README.md`.

## Relevant central pages

- [Research/GameSystems/ListenHost.md](../Research/GameSystems/ListenHost.md) - the boot chain behind `/host`: `StartLocalHost`, `NetworkServer.Host`, the RakNet bind, and why `NetworkRole` has no `Host` value.
- [Research/Patterns/MainThreadDispatcher.md](../Research/Patterns/MainThreadDispatcher.md) - the curated version of the headless pump measurements, including why the plugin's GameObject dies at frame 0 and why `ElectricityTick` must not drain a marshalling queue.
- [Research/GameSystems/SimulationTickDriverHooks.md](../Research/GameSystems/SimulationTickDriverHooks.md) - the tick chain, and what a postfix on it may and may not touch.
- [Research/GameSystems/DLCGating.md](../Research/GameSystems/DLCGating.md) - the entitlement mechanism the `/dlc` routes write to.
- [Research/GameSystems/CursorManager.md](../Research/GameSystems/CursorManager.md) - the full cursor state inventory behind the wedge.
- [Research/GameSystems/DedicatedServerSettings.md](../Research/GameSystems/DedicatedServerSettings.md) - the settings surface and console commands behind the server half's flag set.
- [Research/Workflows/DrivingTheGameClientProgrammatically.md](../Research/Workflows/DrivingTheGameClientProgrammatically.md) - the curated version of the input, gate and window findings.
- [Research/Workflows/StationeersLaunchPadDedicatedServer.md](../Research/Workflows/StationeersLaunchPadDedicatedServer.md) - the mod load path the server half's `update-mods` replicates.
- [Research/Workflows/InspectorPlusUsage.md](../Research/Workflows/InspectorPlusUsage.md) - request and snapshot conventions, including the headless force-unpause.

## Corrections owed elsewhere

- `Research/Workflows/StationeersLaunchPadDedicatedServer.md` (option D, "Two separate client instances on one machine") names the PlayerPrefs key as `HKCU\Software\Rocketwerkz Limited\rocketstation`. That key **does not exist**. The real one is `HKCU\Software\Rocketwerkz\rocketstation`; `Rocketwerkz Limited` is only an `AssemblyCompany` string. Re-verified against the live registry on 2026-08-09: `Test-Path 'HKCU:\Software\Rocketwerkz Limited'` is false, `HKCU:\Software\Rocketwerkz\rocketstation` is true, and `HKCU:\Software` has exactly one `Rocketwerkz*` child. The same page's wider claim, that two client instances need two Steam logins, is also stale: this rig runs two on one login, because identity comes from the manifest rather than from Steam. Correcting a central page needs the fresh-validator protocol in `Research/WORKFLOW.md`, which is why it is still owed rather than done.
- The rig's own docs no longer carry the wrong key; they were corrected on 2026-08-09.
- Nothing is owed on the headless pump. `Research/Patterns/MainThreadDispatcher.md` and `Research/GameSystems/SimulationTickDriverHooks.md` were both corrected and committed on 2026-08-14 under the fresh-validator protocol, and this file now follows them rather than the other way round.

## An instance's mods come from two places, and the set that decides is explicit

Every client instance records the mods it exists to TEST. A mod in that set is not seeded from
the developer's folder and gets no modconfig entry from the seed; `deploy` writes
`Local_<Mod>/` and that is its only copy. Every mod outside the set is seeded exactly as
before, at whatever the developer has installed.

**The set is explicit, per instance, and never inferred from "this repository builds it."** A
rig is normally testing one mod, and this repository carries work in progress for the others,
so seeding them at their published state is what stops an unrelated half-finished mod from
changing the behaviour of the one under test. Inferring the set would break that in silence.

What it fixes: `create` seeded `<Mod>/` and `deploy` wrote `Local_<Mod>/` beside it, both
carrying an `About.xml`, so StationeersLaunchPad loaded BOTH. Awake fires twice and every
Harmony patch registers twice. A doubled side-effecting patch produced delta 10000 instead of
5000 during a battery verification, with nothing in any log to say so, because two plausible
halves of one number look exactly like one correct number. The rig had guarded exactly this
for the control plugin since the merge and did nothing at all for mods.

Four consequences, all of them enforcement rather than convention:

- `deploy` refuses a mod the instance does not record, and names the command that records it.
  With no `--mod` it deploys that instance's own set rather than every released mod, which was
  the fan-out that produced the pairs in the first place.
- `create --force` preserves the set, exactly as it preserves the role, the ports and the
  identity. It is the routine way to pick up a new plugin build, and emptying the set in
  passing would put the developer's copy back beside the deployed one.
- The playtest harness compares the check's mod against each instance's set BEFORE bring-up
  and refuses with `mod-not-under-test-here`. Neither side is declared: the mod comes from the
  check's own `[CallerFilePath]` and the set from the registry row, so the comparison costs
  nothing and cannot drift.
- Attestation separates `under-test-not-deployed` from `binary-not-deployed`, because an
  instance that records a mod has no seeded copy either: the remedy differs and so does what a
  reader should go looking for.

## An exited process stays enumerable, and the rig believed it

Windows keeps a process object enumerable after the process has exited, for as long as
anybody still holds a handle to it, and `Process.StartTime` keeps answering for one. So
`Process.GetProcessesByName` lists a process that has already gone, and anything that
describes what it lists reports a live game process.

That is what made the release-time state restore skip. Measured twice, both times on a clean
teardown: the instance quit, the teardown confirmed the pid had exited and deleted its pid
file, and the restore then refused with `untracked rig game process(es) are running:
rocketstation pid 79888` about a process that had exited seconds earlier. A process whose pid
file has just been deleted is by definition untracked, and an untracked game process is one of
the three conditions the restore refuses on. Nothing was lost, because the restore also runs
at the next acquisition, but the release half of the both-ends guarantee never fired.

The fix is in the process table, not at the call sites: `Describe` reports no match for a
process whose `HasExited` is true, so `TryGet`, `FindByImage` and the orphan scan all agree
with each other and with the OS. Two members exist because they answer different questions:
`TryGet` needs a start time (that is what closes pid reuse) and returns nothing for a process
it cannot fully describe, while `IsRunning` needs only `HasExited` and therefore keeps
answering for a process that is unwinding. A teardown polls the second, because polling the
first would end the wait on the very answer that started the problem.
