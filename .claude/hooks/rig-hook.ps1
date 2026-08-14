# rig-hook.ps1 -- saved with UTF-8 BOM
#
# The single Claude Code hook for TestRig. Two branches survive the move from the
# PowerShell launcher to the compiled one, because they are the only two things
# neither testrig.exe nor the in-game plugin can do for itself.
#
# THE DESIGN RULE HERE IS "POINT, DO NOT RESTATE". TestRig/CLAUDE.md auto-loads for
# any file access under TestRig/, and testrig.exe refuses in code and explains itself
# when it does. A hook that repeats either one pays for the same context twice.
#
#   deny   a tier-1 save-root override: POST /savepath force=true, POST /host
#          requireIsolatedSavePath=false. The merged TestRig plugin REMOVED both
#          parameters and answers 400, which is a strictly stronger guard than this
#          one because raw curl cannot route around a plugin-side refusal the way it
#          always could around a hook. It is not deployed everywhere yet, so this
#          branch is still load bearing. Measured 2026-08-14: the dedicated server
#          reports "stale deployed plugin: ClientDriver", the 'joiner' instance
#          carries ClientDriver.dll, and only 'hostie' carries TestRig.dll.
#          ClientDriver still reads both overrides (Routes.Host.cs:83,
#          Routes.Session.cs:571).
#          RETIRE THIS BRANCH once no ClientDriver.dll is left in either half.
#          'testrig status' names the deployed control plane per half.
#
#   ask    the three destructive spellings the docs gate on a human and nothing
#          enforces: --break-lock, 'stop --target all|clients', 'remove'. The
#          launcher supersedes none of them, each for a different reason:
#            --break-lock is the AUTHORISED path, so the binary permits it by design
#              and only announces the break afterwards. Nothing warns beforehand.
#            'remove --as <id>' from a session that legitimately holds the lock is
#              refused only in the narrow case of a host with a joiner attached.
#            'stop' is not lock-gated at all (VerbTable NeedsLock=false) and refuses
#              only while a foreign lock reads LiveForeign. Past the idle ceiling a
#              foreign session's still-running test classifies DeadForeign and is
#              stopped with no refusal at all.
#          Read the note in Send-Verdict first: 'ask' does NOT prompt under this
#          machine's bypassPermissions default, so each one mirrors its reason into
#          additionalContext, and that injection is the whole of what this delivers.
#
# REMOVED IN THE C# PORT, and what performs each job now:
#
#   session  a mutating verb with no --as (82 words). testrig.exe asserts the lock
#            CENTRALLY before any verb body runs (CliApp.cs: `if (spec.NeedsLock)
#            rig.Lock.AssertHeld(...)`), exits 5, and names the lock command, --as,
#            that one lock covers both halves, and TestRig/CLAUDE.md. Observed on
#            'deploy' and on 'remove'. The hook injected no permissionDecision, so
#            the command ran and was refused anyway: it added 82 words on top of a
#            refusal that already said all of it, and saved nothing.
#
#   author   an edit to the shared safety libraries, naming four PowerShell suites
#            (97 words). Those libraries are retained-not-live, and the suite is now
#            `dotnet test TestRig/src/TestRig.slnx`. rig-stop-hook.ps1 carries this,
#            watching TestRig/src/ and firing once per change instead of once per
#            edit. Its Edit|Write registrations were removed with it.
#
# MATCHING. The `if` rules in settings.json are only a cheap pre-filter; every
# decision is re-derived here from tool_input on stdin. That is mandatory, not
# defensive: Bash `if` rules fail OPEN on any compound command, and a bare `for`
# loop is enough to trigger it, so a rule that decided anything by itself would fire
# on unrelated work. A `Bash(*savepath*)` rule that denied on its own would deny
# `for f in a b; do echo $f; done`.
#
# BOTH LAUNCHER SPELLINGS ARE ACCEPTED. testrig.exe is the live one. testrig.ps1 is
# retained on disk as the feature list the port is checked against and must never be
# run, but a hook that stopped recognising it would go quiet on exactly the command
# that should not have been typed. Their option grammars differ (--break-lock against
# -BreakLock, --target against -Target, and the binary also accepts --target=all and
# --target:all), so both are matched.
#
# WHAT IS DELIBERATELY NOT HOOKED, because testrig.exe already refuses it in code and
# a launcher refusal cannot be skimmed past the way an injected reminder can:
#   - a mutating verb under another session's LIVE lock. The 'stop' ask-gate exists
#     only for the DeadForeign gap described above.
#   - 'deploy' onto a running server, 'unlock' under a live listen host, a verb that
#     does not apply to its target, 'create' on a colliding port.
#   - playtest runs. The harness takes and releases the lock itself, per check.

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

if (-not $cmd) { exit 0 }

# =============================================================================
# Does this command INVOKE the launcher, or merely mention it?
# =============================================================================
# `git commit -m "TestRig: one launcher, testrig.exe"` only mentions it.
# `grep -n break-lock TestRig/testrig.ps1` mentions it. Neither drives the rig.
# Two shapes count as an invocation:
#   A. a pwsh/powershell call whose -File argument is the retained script
#   B. the launcher at the START of a command segment: ./testrig.exe,
#      TestRig/testrig.exe, "c:\...\testrig.exe", & 'c:\...\testrig.ps1', or a
#      bare `testrig` resolved on PATH.
$invokesLauncher =
    ($cmd -match '(?i)\b(pwsh|powershell)(\.exe)?\b[^|;&\r\n]*-File\s+["'']?[\w.:\\/-]*testrig\.ps1') -or
    ($cmd -match '(?im)(?:^|[|;&]\s*|\s&\s*)["'']?(?:\.[\\/])?(?:[\w.:\\/-]*[\\/])?testrig(?:\.exe|\.ps1)?["'']?(\s|$)')

# The verb is positional 0. Anything else is a flag, so the first bare token after
# the launcher path is the verb (or follows an explicit -Verb / --verb).
$verb = ''
if ($invokesLauncher -and $cmd -match '(?i)testrig(?:\.exe|\.ps1)?["'']?\s+(.+)$') {
    $tokens = @($matches[1] -split '\s+' | Where-Object { $_ })
    if ($tokens.Count -gt 0) {
        $first = $tokens[0].Trim("'", '"')
        if (($first -ieq '-Verb' -or $first -ieq '--verb') -and $tokens.Count -gt 1) {
            $first = $tokens[1].Trim("'", '"')
        }
        if ($first -match '^[A-Za-z][A-Za-z-]*$') { $verb = $first.ToLowerInvariant() }
    }
}

# =============================================================================
# BRANCH: deny. The two tier-1 save-root overrides.
# =============================================================================
# These reach a control plane either through the launcher's `call` verb or as a raw
# HTTP request, so the gate is "does this command issue a request", not "is this the
# launcher". It is a guard rail and not a wall: it matches command TEXT, so any
# indirection (the body in a variable, the body read from a file, a JSON payload
# assembled in a script) walks straight past it. The wall is the merged plugin, which
# deleted both parameters; this branch covers the instances that still run
# ClientDriver, where the overrides remain live.
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

Both overrides exist for one purpose, which is to defeat the control plane's own refusal and point a RUNNING client's save root at the developer's real Stationeers user-data folder. That folder is tier 1 and off-limits unconditionally (root CLAUDE.md, "Workflow: save file access tiers").

The merged TestRig plugin deleted both parameters and answers 400, but it is not deployed on every instance yet, and anything still running ClientDriver honours them exactly as before. 'testrig status' names the deployed control plane per half.

Drop the override. A provisioned rig instance already has an isolated save root under its own userdata/, and GET /savepath reports both paths if you need to see why the refusal fired. If the user asked for exactly this, they have to say so and take the action themselves.
"@
}

if (-not $invokesLauncher) { exit 0 }

# =============================================================================
# BRANCH: ask. Three documented human gates, made mechanical.
# =============================================================================
# 'ask' and never 'deny': each of these has a legitimate authorised path, and a
# deny would break it. An ask routes the decision to the user, which is exactly
# what the documentation asks for and cannot itself provide.
if ($cmd -match '(?i)(--break-lock|-BreakLock)\b') {
    Send-Verdict -Decision 'ask' -Reason @"
--break-lock takes a live lock off ANOTHER session and is human-gated: it needs the user's explicit say-so, and no code can tell an authorised break from an unauthorised one. The launcher cannot refuse it either, because this is the authorised path; it only announces the break once it has happened.

If the user has not asked for this in as many words, cancel and report the holder instead: 'testrig status' names the session, its purpose and what it is running. --break-lock is not --force; --force only overrides a refusal inside your own session.
"@
}

if ($verb -eq 'remove') {
    Send-Verdict -Decision 'ask' -Reason @"
'remove' deletes the instance tree AND its save root under that instance's own userdata/. For a --role host instance that save root is the world every joiner was in, and none of it is recoverable.

The launcher refuses only the narrow case of a host with a joiner still attached; a stopped instance is deleted with no further ceremony. Confirm the instance name, and that its world is expendable.
"@
}

if ($verb -eq 'stop' -and $cmd -match '(?i)--?target[\s=:]+["'']?(all|clients)\b') {
    Send-Verdict -Decision 'ask' -Reason @"
'stop --target all' and '--target clients' end every instance on this machine, not only the ones this session started.

'stop' is not lock-gated at all, and refuses only while a foreign lock reads LiveForeign. Past the idle ceiling a foreign session's still-running test classifies as DeadForeign and is stopped without any refusal, which is the exact gap this gate covers. If you mean only your own instances, name them: --target hostie,joiner.
"@
}

exit 0
