# rig-hook.ps1 -- saved with UTF-8 BOM
#
# The single Claude Code hook for TestRig. It replaces rig-lock-hook.ps1, which
# emitted one identical ~840-word briefing for nine unrelated trigger shapes,
# including a read-only `ls` and this repository's own `git commit -m "TestRig: ..."`.
#
# THE DESIGN RULE HERE IS "POINT, DO NOT RESTATE". TestRig/CLAUDE.md auto-loads for
# any file access under TestRig/, so a hook that repeats the rules is paying context
# twice. This hook exists for the four things auto-load and prose cannot do:
#
#   deny     a tier-1 save-root override (POST /savepath force=true,
#            POST /host requireIsolatedSavePath=false). ClientDriver honours both
#            overrides on purpose, so only the CALLER's side can refuse them.
#   ask      the three destructive spellings the docs gate on a human and nothing
#            enforces: -BreakLock, `stop -Target all|clients`, `remove`. Read the
#            note in Send-Verdict first: 'ask' does NOT prompt under this machine's
#            bypassPermissions default, so each one also carries its reason as
#            additionalContext.
#   session  a mutating verb with no -As. The launcher refuses this too, but the
#            hook fires first and costs one round trip less.
#   author   an edit to the shared safety libraries: name the four offline suites.
#
# Everything else exits 0 in silence: read-only verbs, reads, non-safety edits,
# commit messages, and the Bash matcher's fail-open on compound commands.
#
# MATCHING. The `if` rules in settings.json are only a cheap pre-filter; every
# decision is re-derived here from tool_input on stdin. That is mandatory, not
# defensive: Bash `if` rules fail OPEN on any compound command (pipes, &&, loops),
# so a rule that decides anything by itself would fire on unrelated work. A
# `Bash(*savepath*)` rule that denied on its own would deny `ls | wc -l`.
#
# WHAT IS DELIBERATELY NOT HOOKED, because testrig.ps1 already refuses it in code
# and a launcher refusal cannot be skimmed past the way an injected reminder can:
#   - a mutating verb under another session's LIVE lock (Get-RigLockState /
#     Assert-RigLockHeld). The `stop` ask-gate exists only for the DeadForeign gap,
#     where a foreign session past its idle ceiling is still running instances.
#   - `deploy` onto a running server, `unlock` under a live listen host,
#     a verb that does not apply to its target, `create` on a colliding port.
#   - playtest.ps1 invocations. The harness takes and releases the lock itself,
#     per check, through the launcher; there is nothing for an operator to be told.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# --- stdin ------------------------------------------------------------------
# Stay out of the way on anything unparseable. A hook that throws is worse than a
# hook that says nothing.
$stdin = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($stdin)) { exit 0 }
try { $data = $stdin | ConvertFrom-Json } catch { exit 0 }
if ($null -eq $data) { exit 0 }

function Get-Field {
    param($Obj, [string] $Name)
    if ($null -eq $Obj) { return $null }
    if ($Obj.PSObject.Properties.Name -contains $Name) { return $Obj.$Name }
    return $null
}

$hookEvent = [string](Get-Field $data 'hook_event_name')
if (-not $hookEvent) { $hookEvent = 'PreToolUse' }

$toolInput = Get-Field $data 'tool_input'
$cmd       = [string](Get-Field $toolInput 'command')
$filePath  = [string](Get-Field $toolInput 'file_path')

function Send-Verdict {
    param(
        [string] $Context,
        [ValidateSet('', 'ask', 'deny')] [string] $Decision = '',
        [string] $Reason
    )
    $out = @{ hookEventName = $hookEvent }
    if ($Decision) {
        $out['permissionDecision']       = $Decision
        $out['permissionDecisionReason'] = $Reason
    }

    # AN 'ask' IS A NO-OP UNDER bypassPermissions, AND THAT IS THE MODE IN FORCE.
    #
    # permissions.defaultMode is "bypassPermissions" in the user-level settings on
    # this machine, which degrades a hook's 'ask' to an allow and DROPS the reason
    # string entirely: verified by observation, `remove -Target <unknown>` ran with
    # no prompt and no text. 'deny' is still honoured in that mode (also verified),
    # which is why the tier-1 override uses it.
    #
    # So an ask also mirrors its reason into additionalContext. Under a permission
    # mode that prompts, the user decides and this text is the reason they read.
    # Under bypassPermissions the prompt never happens, and this is the only thing
    # that reaches the agent at the moment it is about to destroy something. A gate
    # that vanishes silently is worse than no gate, because it reads as protection.
    if ($Decision -eq 'ask' -and -not $Context) {
        $Context = "[TestRig -- STOP AND CHECK BEFORE PROCEEDING]`n`n$Reason`n`nThis is a human gate. It is registered as permissionDecision 'ask', but permissions.defaultMode is bypassPermissions on this machine, so no prompt will appear and nothing will stop the command. Treat this text as the prompt: unless the user has asked for exactly this action, do not run it. Report what you were about to do and why, and wait."
    }
    if ($Context) { $out['additionalContext'] = $Context }
    @{ hookSpecificOutput = $out } | ConvertTo-Json -Depth 5 -Compress | Write-Output
    exit 0
}

# =============================================================================
# BRANCH: author. Edit/Write of the shared safety libraries.
# =============================================================================
if ($filePath) {
    $norm = ($filePath -replace '\\', '/')
    if ($norm -notmatch '(?i)/TestRig/') { exit 0 }

    # testrig.ps1 and the playtest harness both dot-source rig-lock.ps1,
    # rig-reset.ps1 and lib/, so a change to any of these lands on both halves.
    # The suites themselves are excluded: you are already in them.
    $isSafety =
        ($norm -match '(?i)/TestRig/rig-lock\.ps1$') -or
        ($norm -match '(?i)/TestRig/rig-reset\.ps1$') -or
        ($norm -match '(?i)/TestRig/testrig\.ps1$') -or
        ($norm -match '(?i)/TestRig/lib/[^/]+\.ps1$') -or
        ($norm -match '(?i)/TestRig/playtest/playtest-lib\.ps1$')
    if (-not $isSafety) { exit 0 }

    $leaf = $norm.Substring($norm.LastIndexOf('/') + 1)
    Send-Verdict -Context @"
[TestRig] You changed $leaf, which is shared safety code. testrig.ps1 and the playtest harness both dot-source rig-lock.ps1, rig-reset.ps1 and lib/, so a regression here reaches every rig action at once. Four offline suites cover it and none of them needs a game running:

    pwsh -NoProfile -File TestRig/rig-lock.tests.ps1
    pwsh -NoProfile -File TestRig/rig-reset.tests.ps1
    pwsh -NoProfile -File TestRig/testrig.tests.ps1
    pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1

1,413 assertions (284 / 377 / 353 / 399), about two and a half minutes for all four. Run them before the turn ends, and keep the suite in step with the change. Invariants and rationale: TestRig/CLAUDE.md, TestRig/RESEARCH.md.
"@
}

if (-not $cmd) { exit 0 }

# =============================================================================
# Does this command INVOKE the launcher, or merely mention it?
# =============================================================================
# `git commit -m "TestRig: one launcher, testrig.ps1"` only mentions it.
# `grep -n BreakLock TestRig/testrig.ps1` mentions it. Neither drives the rig.
# Two shapes count as an invocation:
#   A. a pwsh/powershell call whose -File argument is the launcher
#   B. the launcher path at the START of a command segment (./testrig.ps1 ...)
$invokesLauncher =
    ($cmd -match '(?i)\b(pwsh|powershell)(\.exe)?\b[^|;&\r\n]*-File\s+["'']?[\w.:\\/-]*testrig\.ps1') -or
    ($cmd -match '(?im)(?:^|[|;&]\s*|\s&\s*)["'']?(?:\.[\\/])?(?:[\w.:\\/-]*[\\/])?testrig\.ps1["'']?(\s|$)')

# The verb is positional 0. Anything else is a flag, so the first bare token after
# the script path is the verb (or follows an explicit -Verb).
$verb = ''
if ($invokesLauncher -and $cmd -match '(?i)testrig\.ps1["'']?\s+(.+)$') {
    $tokens = @($matches[1] -split '\s+' | Where-Object { $_ })
    if ($tokens.Count -gt 0) {
        $first = $tokens[0].Trim("'", '"')
        if ($first -ieq '-Verb' -and $tokens.Count -gt 1) { $first = $tokens[1].Trim("'", '"') }
        if ($first -match '^[A-Za-z][A-Za-z-]*$') { $verb = $first.ToLowerInvariant() }
    }
}

# =============================================================================
# BRANCH: deny. The two tier-1 save-root overrides.
# =============================================================================
# These reach the control plane either through the launcher's `call` verb or as a
# raw HTTP request, so the gate is "does this command issue a request", not "is
# this the launcher". It is a guard rail and not a wall: it matches command TEXT,
# so any indirection (the body in a variable, the body read from a file, a JSON
# payload assembled in a script) walks straight past it.
$issuesRequest =
    $invokesLauncher -or
    ($cmd -match '(?i)\b(curl|wget|Invoke-RestMethod|Invoke-WebRequest|irm|iwr)\b') -or
    ($cmd -match '(?i)http://(127\.0\.0\.1|localhost):\d+')

$savePathForce = ($cmd -match '(?i)savepath') -and
                 ($cmd -match '(?i)["'']?\bforce\b["'']?\s*[:=]\s*["'']?(true|1|\$true)\b')
$hostOverride  =  $cmd -match '(?i)["'']?requireIsolatedSavePath["'']?\s*[:=]\s*["'']?(false|0|\$false)\b'

if ($issuesRequest -and ($savePathForce -or $hostOverride)) {
    $which = if ($savePathForce) { 'POST /savepath with force=true' } else { 'POST /host with requireIsolatedSavePath=false' }
    Send-Verdict -Decision 'deny' -Reason @"
Blocked: this command carries a tier-1 save-root override ($which).

Both overrides exist for one purpose, which is to defeat ClientDriver's own refusal and point a RUNNING client's save root at the developer's real Stationeers user-data folder. That folder is tier 1 and off-limits unconditionally (root CLAUDE.md, "Workflow: save file access tiers"). ClientDriver has to keep honouring a documented escape hatch, so the caller is the only layer that can refuse it.

Drop the override. A provisioned rig instance already has an isolated save root under ClientRig/data/<instance>/userdata/, and GET /savepath reports both paths if you need to see why the refusal fired. If the user asked for exactly this, they have to say so and take the action themselves.
"@
}

if (-not $invokesLauncher) { exit 0 }

# =============================================================================
# BRANCH: ask. Three documented human gates, made mechanical.
# =============================================================================
# 'ask' and never 'deny': each of these has a legitimate authorised path, and a
# deny would break it. An ask routes the decision to the user, which is exactly
# what the documentation asks for and cannot itself provide.
if ($cmd -match '(?i)-BreakLock\b') {
    Send-Verdict -Decision 'ask' -Reason @"
-BreakLock takes a live lock off ANOTHER session and is human-gated: it needs the user's explicit say-so, and no code can tell an authorised break from an unauthorised one.

If the user has not asked for this in as many words, cancel and report the holder instead: testrig.ps1 status names the session, its purpose and what it is running. -BreakLock is not -Force; -Force only overrides a refusal inside your own session.
"@
}

if ($verb -eq 'remove') {
    Send-Verdict -Decision 'ask' -Reason @"
'remove' deletes the instance tree AND its save root at ClientRig/data/<instance>/userdata/. For a -Role host instance that save root is the world every joiner was in, and none of it is recoverable.

The launcher refuses only the narrow case of a host with a joiner still attached; a stopped instance is deleted with no further ceremony. Confirm the instance name, and that its world is expendable.
"@
}

if ($verb -eq 'stop' -and $cmd -match '(?i)-Target\s+["'']?(all|clients)\b') {
    Send-Verdict -Decision 'ask' -Reason @"
'stop -Target all' and '-Target clients' end every instance on this machine, not only the ones this session started.

The launcher refuses a stop only while a foreign lock reads LiveForeign. Past the 60-minute idle ceiling a foreign session's still-running test classifies as DeadForeign and is stopped without any refusal, which is the exact gap this gate covers. If you mean only your own instances, name them: -Target host1,client1.
"@
}

# =============================================================================
# BRANCH: session. A mutating verb with no -As.
# =============================================================================
# 'lock' is excluded: it is the command that produces the id, so telling it to go
# get one is noise. Everything else in this set is about to be refused by
# Assert-RigLockHeld, and the pointer saves the round trip.
$mutatingVerbs = @(
    'unlock', 'refresh-lock', 'capture-baseline', 'reset',
    'update-game', 'update-mods', 'deploy', 'create', 'remove',
    'start', 'stop', 'save', 'call', 'send'
)
if (($mutatingVerbs -contains $verb) -and ($cmd -notmatch '(?i)-As\s+\S')) {
    Send-Verdict -Context @"
[TestRig] '$verb' mutates the rig, and the rig is one shared resource behind one session lock covering both halves, so it needs -As <id>:

    pwsh -NoProfile -File TestRig/testrig.ps1 lock -Purpose "<what you are testing>"

That prints TESTRIG-OWNER <id> as its last line, which is the id to pass. Release with 'unlock -As <id>' when the test is done; a running instance holds the whole rig with no timer to save you. Rules: TestRig/CLAUDE.md, which auto-loads. Full surface: run testrig.ps1 with no verb.
"@
}

exit 0
