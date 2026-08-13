<#
.SYNOPSIS
    The one launcher for the Stationeers test rig: both halves, one lock, one
    verb surface.

.DESCRIPTION
    testrig.ps1 <verb> [-Target all|server|clients|<instance>[,<instance>]] [options]

    THE RIG IS ONE SYSTEM. It has two halves, a headless dedicated server and N
    driven game clients, and they share the developer's one game install and the
    per-Windows-user Unity state that nothing separates. That is why there is one
    session lock, and it is why there is now one launcher: two entry points made
    the rig look like two systems, and an agent asked to "update the testrig"
    updated exactly one of them with nothing anywhere to notice. -Target defaults
    to 'all' on every rig-wide verb precisely so that cannot happen again.

    WHERE A VERB DOES NOT APPLY TO A TARGET, IT REFUSES AND EXPLAINS. Seven things
    genuinely cannot mean the same thing on both halves (entering a world at start,
    the control channel, save-confirmation evidence, anything needing a player
    character, N instances versus one install, creating an instance versus
    installing a server, and where a mod loads from). Each of those is a refusal
    that says what the verb needs, why this target cannot provide it, and the exact
    command that would work. Read the refusal; it is the documentation.

    Rig rules, and the session lock: TestRig/CLAUDE.md (READ FIRST)
    Operating reference:             TestRig/MANUAL.md
    Durable internals:               TestRig/RESEARCH.md
    Playtest harness:                TestRig/playtest/CLAUDE.md

.PARAMETER Verb
    What to do. Run with no verb to print the whole surface.

.PARAMETER Target
    Which half, or which instances. 'all' (both halves), 'server', 'clients'
    (every provisioned instance), or one or more instance names separated by
    commas. Rig-wide verbs default to 'all'; verbs that act on a running thing
    require an explicit target.

.PARAMETER As
    The owner id printed by 'lock'. Pass it on every mutating command.

.PARAMETER Purpose
    With 'lock': a short human-readable reason, shown to the user when another
    session is blocked.

.PARAMETER SaveName
    The world name to write, for 'save' and for 'stop'. Required on the server
    (its console has no notion of a current world name), optional on a client.
    This flag used to be spelled -Name on 'save', -SaveAs on the server's stop and
    -Name again on the client's stop, so one word meant three things.

.PARAMETER WaitSeconds
    How long a BLOCKING WAIT waits: the readiness barrier, and a save waiting for
    its confirmation. 300 on both halves.

.PARAMETER TimeoutSeconds
    Process-teardown grace for 'stop': how long a thing gets to exit cleanly
    before it is killed. 30. It is NEVER a save-confirmation budget.

.PARAMETER CallTimeoutSeconds
    How long ONE control-plane request may take. 0 means derive it from the
    request's own timeoutMs plus a margin.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $Verb,

    [string] $Target,

    # ---- lock ----
    [string] $Purpose,
    [string] $As,
    [switch] $BreakLock,
    [switch] $Force,
    [int]    $TtlMinutes = 10,
    [int]    $IdleCeilingMinutes = 60,
    [switch] $KeepState,
    [switch] $Release,

    # ---- worlds and saves ----
    [string] $Load,
    [string] $Map,
    [string] $New,
    [string] $SaveName,

    # ---- mods ----
    [Parameter(Position = 1)]
    [string] $Mod,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $FromModConfig,

    # ---- control channels ----
    [string] $Command,
    [string] $Path,
    [string] $Body,
    [int]    $CallTimeoutSeconds = 0,

    # ---- readiness ----
    [ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld', 'process')]
    [string] $Stage = 'menu',

    # ---- timing ----
    [int] $WaitSeconds    = 300,
    [int] $TimeoutSeconds = 30,

    # ---- instance shape (client half only) ----
    [ValidateSet('client', 'host')]
    [string] $Role = 'client',
    [int]    $Port = 0,
    [int]    $GamePort = 0,
    [int]    $UpdatePort = 0,
    [string] $ClientId,
    [string] $Username,
    [int]    $Width  = 800,
    [int]    $Height = 600,
    [bool]   $ForceGameplayInput = $true,
    [bool]   $SeedMods = $true,
    [string] $Desktop = 'StationeersRig',
    [string] $InstancesRoot,

    # ---- output ----
    [int]    $Tail = 50,
    [string] $Grep,
    [string] $OutFile,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'

# $PSBoundParameters is PER SCOPE. A function gets its own, and it is EMPTY for a
# function declared without a param block, so a ContainsKey test written inside one
# silently answers false. Every "was this actually typed, or is it sitting on its
# default" question in this launcher reads THIS capture, and functions that need
# the answer take it as a parameter. Getting it wrong is silent: 'refresh-lock
# -TtlMinutes 20' once tested a function's empty dictionary and never applied the
# new TTL at all.
$InvokedWith = $PSBoundParameters

$RigHome  = $PSScriptRoot
$RepoRoot = Split-Path -Parent $RigHome

foreach ($lib in @('rig-lock.ps1', 'rig-reset.ps1', 'lib\common.ps1', 'lib\server.ps1', 'lib\client.ps1')) {
    $libPath = Join-Path $RigHome $lib
    if (-not (Test-Path -LiteralPath $libPath)) {
        throw "The rig library $lib is missing at $libPath. All five are committed together; restore them before driving the rig."
    }
    . $libPath
}

Initialize-RigCommon -RigHome $RigHome -RepoRoot $RepoRoot
Initialize-RigServer -RigHome $RigHome -LauncherPath $PSCommandPath
Initialize-RigClient -RigHome $RigHome -InstancesRoot $InstancesRoot `
    -InstancesRootTyped:($InvokedWith.ContainsKey('InstancesRoot') -and $InstancesRoot)

# =============================================================================
# TARGETS
# =============================================================================

function Get-RigTargetEntries {
    # Registry entries for the client instances in a resolved target.
    param([Parameter(Mandatory)] $Resolved)
    if ($Resolved.Names.Count -eq 0) { return @() }
    return @(Get-RigClientEntries -Names $Resolved.Names)
}

function Resolve-RigTargetHere {
    # Resolve-RigTarget lives in lib/common.ps1 so a test can exercise it with no
    # rig at all; this wrapper is the one place that feeds it the real registry.
    param([Parameter(Mandatory)] [string] $Verb, [string] $Target, [switch] $AllowUnknown)
    $known = @(Read-Registry | ForEach-Object { [string]$_.instanceName })
    return (Resolve-RigTarget -Target $Target -Verb $Verb -KnownInstances $known -AllowUnknown:$AllowUnknown)
}

function Assert-RigVerbAppliesHere {
    # Same split: the matrix and its rules are testable in lib/common.ps1, and this
    # is where the verb's actual options are gathered and handed over.
    param([Parameter(Mandatory)] [string] $Verb, [Parameter(Mandatory)] $Resolved)
    $instanceOnly = @('Role', 'Port', 'ClientId', 'Username', 'Width', 'Height',
                      'ForceGameplayInput', 'SeedMods', 'Desktop', 'InstancesRoot')
    Assert-RigVerbApplies -Verb $Verb -Resolved $Resolved -Options @{
        Stage       = $Stage
        SaveName    = $SaveName
        HasWorld    = [bool]($Load -or $New)
        TypedInstanceFlags = @($instanceOnly | Where-Object { $InvokedWith.ContainsKey($_) })
    }
}

# =============================================================================
# THE LOCK, WHICH IS RIG-WIDE
# =============================================================================

function Invoke-RigLockReclaim {
    # What a reclaim of a dead or timed-out lock has to tear down, on BOTH halves.
    # It used to be one -OnReclaim per launcher, each blind to the other half, so a
    # reclaim from the server side left another session's client instances running
    # on the next agent's ports.
    if (Test-RigServerProcessAlive (Get-RigPidFromFile (Get-RigServerPaths).PidFile)) {
        Write-Warning "[Lock] Reclaimed the rig; stopping the orphaned dedicated server it left behind."
        Stop-RigServerProcesses
    }
    Stop-RigClientInstancesByPid | Out-Null
}

function Invoke-RigVerbLock {
    if (-not $Purpose) {
        throw "'lock' requires -Purpose `"<short reason>`", e.g. -Purpose `"Playtesting network paint for SprayPaintPlus`". See TestRig/CLAUDE.md."
    }
    # -WaitSeconds is the blocking-wait budget everywhere else and 0 (refuse at
    # once) here, so it only queues when the caller actually typed it. Forwarding
    # the default would silently turn every lock into a five-minute wait.
    $lockArgs = @{
        Purpose            = $Purpose
        CallerId           = $As
        TtlMinutes         = $TtlMinutes
        IdleCeilingMinutes = $IdleCeilingMinutes
        BreakLock          = [bool]$BreakLock
        KeepState          = [bool]$KeepState
        Tool               = 'testrig.ps1'
        OnReclaim          = { Invoke-RigLockReclaim }
    }
    if ($InvokedWith.ContainsKey('WaitSeconds')) { $lockArgs['WaitSeconds'] = $WaitSeconds }
    $outcome = New-RigLock @lockArgs

    # A MACHINE-READABLE OWNER LINE, on purpose and by contract.
    #
    # The playtest harness used to scrape the owner id out of this launcher's prose
    # with two regexes over the human-readable block, so any wording change would
    # have silently broken every check with 'rig-unavailable'. This line is the
    # stable answer: one token, one id, last line of a successful acquisition.
    if ($outcome -and $outcome.Owner) {
        Write-Host "TESTRIG-OWNER $($outcome.Owner)"
    }
}

function Invoke-RigVerbRefreshLock {
    if (-not $As) { throw "'refresh-lock' requires -As <id> (the owner id printed by 'lock')." }
    $refreshArgs = @{ CallerId = $As }
    if ($InvokedWith.ContainsKey('TtlMinutes'))         { $refreshArgs['TtlMinutes'] = $TtlMinutes }
    if ($InvokedWith.ContainsKey('IdleCeilingMinutes')) { $refreshArgs['IdleCeilingMinutes'] = $IdleCeilingMinutes }
    Update-RigLock @refreshArgs
}

function Invoke-RigVerbUnlock {
    # The busy warning used to exist on one launcher only. It is worth having on
    # both: releasing while something is still up hands the rig to the next agent
    # with foreign processes on its ports.
    $lock = Read-RigLock
    if ($lock -and $As -and $lock['owner'] -eq $As) {
        $busy = Get-RigBusySignal
        if ($busy.Busy) {
            Write-Warning "[Unlock] Releasing while the rig is still busy ($($busy.Detail)). Stop it first: testrig stop -Target all -As $As"
        }
    }
    # -Force is forwarded so the live-host refusal inside Remove-RigLock can be
    # overridden. -KeepState is forwarded so a session can hand the rig over dirty
    # ON PURPOSE; without it the release restores, which is where the
    # between-session guarantee is actually earned.
    Remove-RigLock -CallerId $As -BreakLock:$BreakLock -Force:$Force -KeepState:$KeepState
    # Only after a SUCCESSFUL release: Remove-RigLock throws on a refusal, and a
    # drift report on a lock that is still held would be reporting on a session
    # that is not over.
    Write-RigSharedStateDrift
}

function Invoke-RigVerbCaptureBaseline {
    Assert-RigLockHeld -Action 'capture-baseline' -CallerId $As -Tool 'testrig.ps1'
    New-RigBaselineCapture -CapturedBy $As -Force:$Force | Out-Null
}

function Invoke-RigVerbReset {
    <#
        The state reset, on demand.

        It has always existed and has never had a verb: the only way to run it was
        to take or release the lock, so "put the rig back the way it was" and "end
        my session" were the same command. They are not the same thing, and an agent
        that wanted the first had to fake the second.
    #>
    Assert-RigLockHeld -Action 'reset' -CallerId $As -Tool 'testrig.ps1'
    $plan = Get-RigResetPlan
    if ($DryRun) {
        Invoke-RigReset -Plan $plan -WhatIf -Reason 'explicit reset (dry run)' | Out-Null
        return
    }
    Invoke-RigReset -Plan $plan -KeepState:$KeepState -Reason 'explicit reset' | Out-Null
}

function Invoke-RigReleaseAfterStop {
    <#
        The -Release half of a stop, ONE implementation for both halves.

        ORDERING DEPENDENCY, DO NOT REORDER. The Get-RigLockState call in the stop
        verb MUST happen before this. Test-RigLockReleasableOnStop has no busy term,
        so on its own it would release a foreign lock the moment its timer lapsed,
        even with a test in full flight; what makes it safe is that the state call
        happens first and its expired-and-busy branch self-renews the lock and
        reports LiveForeign, so the stop throws before ever reaching here.
        TestRig/rig-lock.tests.ps1 pins both halves of that.

        The client half used to inline the same four terms rather than call this
        predicate, so the tested version and the shipped version were different code.
    #>
    $lock = Read-RigLock
    if (-not $lock) {
        Write-Host "[Stop] No rig session lock to release."
        return
    }
    if (Test-RigLockReleasableOnStop -Lock $lock -CallerId $As -BreakLock:$BreakLock) {
        # The restore runs BEFORE the lock file goes, so it happens while this
        # session still owns the rig. Same order as unlock, through the same shared
        # helper, so the two release paths cannot drift.
        Invoke-RigReleaseRestore -KeepState:$KeepState
        Remove-Item -Force -ErrorAction SilentlyContinue (Get-RigLockFilePath)
        Write-Host "[Stop] Rig session lock released."
        Write-RigSharedStateDrift
        return
    }
    Write-Warning "[Stop] -Release ignored: the lock is held by '$($lock['owner'])', not you. Use: testrig unlock -As <id>, or get the user's authorization for -BreakLock."
}

# =============================================================================
# RIG-WIDE STATUS
# =============================================================================

function Invoke-RigVerbStatus {
    <#
        BOTH HALVES IN ONE OUTPUT, including each half's game version and its mod
        staleness.

        This is the answer to the failure that prompted the whole consolidation. The
        rig's only staleness report was per client instance, and it named a
        client-half fix; nothing anywhere compared the server half against anything,
        so an agent that updated the clients was told it was done. The version rows
        below are the correction: one line per half, both against the same source
        install, both saying plainly whether they are behind.
    #>
    param([Parameter(Mandatory)] $Resolved)
    $entries = Get-RigTargetEntries -Resolved $Resolved

    Write-RigLockStatus -CallerId $As
    Write-Host ''

    if ($Resolved.Server) {
        Write-RigServerStatus
        Write-Host ''
    }
    if ($Resolved.Names.Count -gt 0 -or $Resolved.Kind -eq 'all' -or $Resolved.Kind -eq 'clients') {
        Write-RigClientStatus -Entries $entries
        Write-Host ''
    }

    Write-Host 'versions (game build each half was made from, against the source install):'
    $rows = @()
    if ($Resolved.Server) { $rows += (Get-RigServerVersionReport) }
    if ($entries.Count -gt 0) { $rows += @(Get-RigClientVersionReport -Entries $entries) }
    if ($rows.Count -eq 0) { Write-Host '  (nothing to report)' }
    foreach ($r in $rows) {
        $who  = if ($r.PSObject.Properties['Name'] -and $r.Name) { "$($r.Half)/$($r.Name)" } else { $r.Half }
        $note = if (-not $r.Present) { 'NOT INSTALLED' } elseif ($r.Stale) { "STALE (source is $($r.Source))" } else { 'current' }
        Write-Host ("  {0,-24} {1,-20} {2}" -f $who, $r.Version, $note)
        if ($r.Stale -and $r.Present) { Write-Host ("  {0,-24} fix: {1}" -f '', $r.Remedy) }
    }

    Write-Host ''
    Write-Host 'mod staleness (a payload older than what it came from; reported, never fixed here):'
    $stale = @()
    if ($Resolved.Server)     { $stale += @(Get-RigServerModStaleness) }
    if ($entries.Count -gt 0) { $stale += @(Get-RigClientModStaleness -Entries $entries) }
    if ($stale.Count -eq 0) {
        Write-Host '  none'
    }
    else {
        foreach ($s in $stale) {
            $where = if ($s.PSObject.Properties['Instance'] -and $s.Instance) { "$($s.Half)/$($s.Instance)" } else { $s.Half }
            Write-Host ("  {0,-24} {1,-16} {2}" -f $where, $s.Kind, $s.Name)
            Write-Host ("  {0,-24} fix: {1}" -f '', $s.Remedy)
        }
    }
}

function Invoke-RigVerbList {
    param([Parameter(Mandatory)] $Resolved)
    if ($Resolved.Server) {
        $p = Get-RigServerPaths
        $alive = Test-RigServerProcessAlive (Get-RigPidFromFile $p.PidFile)
        $state = if (-not (Test-Path $p.Exe)) { 'not installed' } elseif ($alive) { 'running' } else { 'stopped' }
        Write-Host "server (dedicated)  $state  $($p.InstallDir)"
    }
    $entries = Get-RigTargetEntries -Resolved $Resolved
    $rows = @(Get-RigClientListRows -Entries $entries)
    if ($rows.Count -eq 0) {
        Write-Host 'clients             none provisioned'
        return
    }
    $rows | Format-Table -AutoSize
}

# =============================================================================
# DISPATCH
# =============================================================================

$KnownVerbs = @(
    'lock', 'unlock', 'refresh-lock', 'capture-baseline', 'reset',
    'status', 'list', 'logs', 'snapshot',
    'update-game', 'update-mods', 'deploy',
    'create', 'remove',
    'start', 'stop', 'save', 'wait',
    'call', 'send',
    'host-mode', 'help'
)

function Write-RigSurface {
    $instancesDir = Get-RigClientInstancesDir
    Write-Host @"
The Stationeers test rig: one launcher, two halves, one session lock.

    testrig.ps1 <verb> [-Target all|server|clients|<instance>[,<instance>]] [options]

Rig rules, and the session lock: TestRig/CLAUDE.md   (READ FIRST)
Operating reference:             TestRig/MANUAL.md
Durable internals:               TestRig/RESEARCH.md
Playtest harness:                TestRig/playtest/CLAUDE.md

-Target defaults to 'all' on every rig-wide verb (status, list, logs, update-game,
update-mods, deploy, reset, and the lock verbs), so updating the rig updates BOTH
halves. Verbs that act on a running thing (start, stop, save, wait, call, send,
snapshot, create, remove) require an explicit target and will not guess.

THE SESSION LOCK IS RIG-WIDE. Acquire once, then pass -As <id> on every mutating
command:
    testrig lock -Purpose "<what you are testing>" [-TtlMinutes 10] [-IdleCeilingMinutes 60] [-WaitSeconds 0]
    testrig refresh-lock -As <id>                  while actively driving a test
    testrig unlock -As <id> [-Force] [-KeepState]  release; RESTORES the rig
    testrig capture-baseline -As <id> [-Force]     make this rig the definition of clean
    testrig reset -As <id> [-DryRun] [-KeepState]  restore now, without ending the session
    A successful lock prints TESTRIG-OWNER <id> as its last line. That line is a
    contract: it is what a harness reads, so nothing has to scrape prose.
    Two timers: -TtlMinutes is the liveness heartbeat, which a busy rig renews by
    itself; -IdleCeilingMinutes is the absolute idle ceiling, and past it the lock is
    reclaimable even on a busy rig. Only YOUR OWN commands reset the ceiling.
    Breaking another session's LIVE lock (-BreakLock) is human-gated: only on the
    user's say-so. -BreakLock is NOT -Force, which overrides refusals inside your own
    session and never touches a lock.

Setup, both halves at once:
    testrig update-game  -As <id>                  SteamCMD for the server, a re-link for each instance
    testrig update-mods  -As <id> [-FromModConfig <path>]
    testrig deploy <ModName> -As <id> [-Configuration Release|Debug]
    testrig create -Target <name> -As <id> [-Role host] [-GamePort N] [-ClientId N] [-Username N] [-Force]

Lifecycle:
    testrig start -Target server -As <id> -Load <SaveName> -Map <Map>   (or -New <Map>)
    testrig start -Target <name|clients> -As <id>                       boots to the MENU
    testrig wait  -Target <name|clients> -Stage menu|inWorld [-WaitSeconds 600]
    testrig wait  -Target server -Stage inWorld [-WaitSeconds 600]      InspectorPlus readiness probe
    testrig save  -Target <name|server|all> -As <id> [-SaveName <name>] [-WaitSeconds 300]
    testrig stop  -Target <name|clients|server|all> -As <id> [-SaveName <name>] [-Release]
    testrig remove -Target <name> -As <id> [-Force]

Driving:
    testrig call -Target <name> -As <id> -Path /host -Body '{"world":"Lunar"}'
    testrig call -Target clients -As <id> -Path /config/set -Body '{...}'      the fan-out
    testrig send -Target server -As <id> -Command 'status'                     stdin, fire and forget
    testrig snapshot -Target clients [-OutFile before.json]
    testrig logs -Target all [-Tail 50] [-Grep <regex>]
    testrig status [-As <id>]        both halves, plus game version and mod staleness per half

Hosting a world from a driven client (a listen host), in the only order that works.
The host must be IN ITS WORLD before any joiner connects:
    testrig start -Target host1 -As <id>
    testrig wait  -Target host1 -Stage menu
    testrig call  -Target host1 -As <id> -Path /host -Body '{"world":"Lunar"}'
    testrig wait  -Target host1 -Stage inWorld -WaitSeconds 600
    testrig start -Target client1 -As <id>
    testrig wait  -Target client1 -Stage menu
    testrig call  -Target client1 -As <id> -Path /connect -Body '{"address":"127.0.0.1","port":27801}'
    testrig wait  -Target client1 -Stage inWorld -WaitSeconds 600

Flags whose names used to mean more than one thing:
    -SaveName N        the world name to write, for save and for stop. Required on the
                       server (its console has no current-world name), optional on a client.
                       It was -Name on save, -SaveAs on the server's stop, -Name again on
                       the client's stop.
    -WaitSeconds N     how long a BLOCKING WAIT waits: the readiness barrier and a save
                       confirmation. 300 on both halves now; it was 30 on the server, so a
                       slow but successful save produced a false warning.
    -TimeoutSeconds N  process-teardown grace for stop, 30. Never a save budget.
    -CallTimeoutSeconds N  how long ONE control-plane request may take. 0 derives it from
                       the request's own timeoutMs plus a margin.

Instance trees are hard links into the game install, so they must be on the install's
volume. Set this once per shell (or record it in DEV.md) when the repository is on a
different drive:
    `$env:STATIONEERS_CLIENTRIG_ROOT = '<drive of the game install>\StationeersRig'
Current instances root: $instancesDir
    ($(Get-RigClientInstancesDirSource))

Where a verb does not apply to a target it REFUSES and explains, naming a command that
would work. Try 'testrig send -Target clients' to see one.
"@
}

if (-not $Verb -or $Verb -eq 'help') {
    Write-RigSurface
    return
}

$Verb = $Verb.ToLowerInvariant()
if ($KnownVerbs -notcontains $Verb) {
    $close = @($KnownVerbs | Where-Object { $_.StartsWith($Verb.Substring(0, [Math]::Min(3, $Verb.Length)), [StringComparison]::OrdinalIgnoreCase) })
    $hint  = if ($close.Count -gt 0) { " Did you mean: $($close -join ', ')?" } else { '' }
    throw "'$Verb' is not a testrig verb.$hint Run 'testrig' with no verb for the whole surface. Verbs: $(($KnownVerbs | Where-Object { $_ -ne 'host-mode' }) -join ', ')"
}

try {
    # host-mode is internal: the detached wrapper the server's start spawns. It
    # takes no target and never touches the lock, because the start that spawned it
    # already did.
    if ($Verb -eq 'host-mode') {
        Invoke-RigServerHostMode -Load $Load -Map $Map -New $New -GamePort $GamePort -UpdatePort $UpdatePort
        return
    }

    $resolved = Resolve-RigTargetHere -Verb $Verb -Target $Target -AllowUnknown:($Verb -eq 'create')
    Assert-RigVerbAppliesHere -Verb $Verb -Resolved $resolved

    switch ($Verb) {
        'lock'             { Invoke-RigVerbLock }
        'refresh-lock'     { Invoke-RigVerbRefreshLock }
        'unlock'           { Invoke-RigVerbUnlock }
        'capture-baseline' { Invoke-RigVerbCaptureBaseline }
        'reset'            { Invoke-RigVerbReset }
        'status'           { Invoke-RigVerbStatus -Resolved $resolved }
        'list'             { Invoke-RigVerbList   -Resolved $resolved }

        'logs' {
            if ($resolved.Server) { Invoke-RigServerLogs -Tail $Tail -Grep $Grep }
            foreach ($n in $resolved.Names) { Invoke-RigClientLogs -Instance $n -Tail $Tail -Grep $Grep }
        }

        'snapshot' {
            Invoke-RigClientSnapshot -Entries (Get-RigTargetEntries -Resolved $resolved) -OutFile $OutFile
        }

        'update-game' {
            if ($resolved.Server) { Invoke-RigServerUpdateGame -As $As }
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientUpdateGame -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) -Desktop $Desktop
            }
        }

        'update-mods' {
            if ($resolved.Server) { Invoke-RigServerUpdateMods -As $As -FromModConfig $FromModConfig }
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientUpdateMods -As $As -Entries (Get-RigTargetEntries -Resolved $resolved)
            }
        }

        'deploy' {
            # @(...) around the split so one mod name stays a one-element array. A
            # bare string reaching a [string[]] parameter is bound as a single
            # element anyway, but a scalar reaching a foreach is enumerated by
            # character, and this value passes through both.
            $mods = @()
            if ($Mod) { $mods = @($Mod.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
            if ($resolved.Server) { Invoke-RigServerDeploy -As $As -Mods $mods -Configuration $Configuration }
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientDeploy -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) -Mods $mods -Configuration $Configuration
            }
        }

        'create' {
            if ($resolved.Names.Count -ne 1) { throw "'create' builds one instance at a time. Name it with -Target <name>." }
            Invoke-RigClientCreate -As $As -Instance $resolved.Names[0] -Force:$Force -Typed $InvokedWith `
                -Role $Role -Port $Port -GamePort $GamePort -ClientId $ClientId -Username $Username `
                -Width $Width -Height $Height -ForceGameplayInput $ForceGameplayInput -SeedMods $SeedMods `
                -Desktop $Desktop | Out-Null
        }

        'remove' {
            if ($resolved.Names.Count -ne 1) { throw "'remove' deletes one instance at a time. Name it with -Target <name>." }
            Invoke-RigClientRemove -As $As -Instance $resolved.Names[0] -Force:$Force -Desktop $Desktop
        }

        'start' {
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientStart -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) `
                    -Desktop $Desktop -Width $Width -Height $Height
            }
            if ($resolved.Server) {
                Invoke-RigServerStart -As $As -Load $Load -Map $Map -New $New -GamePort $GamePort -UpdatePort $UpdatePort
            }
        }

        'stop' {
            # ORDERING DEPENDENCY, DO NOT REORDER: this state call must come before
            # Invoke-RigReleaseAfterStop. See the note on that function.
            #
            # A stop is allowed unless a LIVE FOREIGN lock exists, so cleaning up an
            # orphan or an expired session needs no ceremony and no -As.
            $st = Get-RigLockState -CallerId $As
            if ($st.State -eq 'LiveForeign') {
                if (-not $BreakLock) {
                    throw "[Stop] Refusing to stop a rig held by another live session.`n$(Format-ForeignRigLock $st)`nReport this to the user. Only the user may authorize -BreakLock. See TestRig/CLAUDE.md."
                }
                Write-Warning "[Stop] -BreakLock: stopping a rig held by another live session ('$($st.Lock['purpose'])')."
            }
            # Clients first, then the server: a joiner that is still attached when
            # its server goes down leaves the host holding a peer that never said
            # goodbye, which is the state a world would be saved in.
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientStop -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) `
                    -TimeoutSeconds $TimeoutSeconds -WaitSeconds $WaitSeconds -SaveName $SaveName -Force:$Force
            }
            if ($resolved.Server) {
                Invoke-RigServerStop -As $As -SaveName $SaveName -TimeoutSeconds $TimeoutSeconds -WaitSeconds $WaitSeconds
            }
            if ($Release) { Invoke-RigReleaseAfterStop }
            Write-Host '[Stop] Done.'
        }

        'save' {
            if ($resolved.Server) { Invoke-RigServerSave -As $As -SaveName $SaveName -WaitSeconds $WaitSeconds | Out-Null }
            if ($resolved.Kind -ne 'server') {
                Invoke-RigClientSave -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) `
                    -SaveName $SaveName -WaitSeconds $WaitSeconds
            }
        }

        'wait' {
            if ($resolved.Server) {
                $serverStage = if ($Stage -eq 'process') { 'process' } else { 'inWorld' }
                Invoke-RigServerWait -Stage $serverStage -WaitSeconds $WaitSeconds | Out-Null
            }
            if ($resolved.Kind -ne 'server') {
                $clientStage = if ($Stage -eq 'process') { 'ping' } else { $Stage }
                Invoke-RigClientWait -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) `
                    -Stage $clientStage -WaitSeconds $WaitSeconds
            }
        }

        'call' {
            if (-not $Path) { throw "'call' requires -Path <control-plane path>, for example -Path /status." }
            Invoke-RigClientCall -As $As -Entries (Get-RigTargetEntries -Resolved $resolved) `
                -Path $Path -Body $Body -CallTimeoutSeconds $CallTimeoutSeconds
        }

        'send' {
            if (-not $Command) { throw "'send' requires -Command '<console text>'." }
            Invoke-RigServerSend -As $As -Command $Command
        }
    }
}
catch {
    # A refusal is printed as itself and exits 2. Anything else is a real error and
    # goes out the normal way, so a caller can still tell a broken rig from a
    # command that does not apply.
    $msg = "$($_.Exception.Message)"
    if ($msg.StartsWith((Get-RigRefusalSentinel))) {
        Write-Host ''
        Write-Host $msg.Substring((Get-RigRefusalSentinel).Length)
        Write-Host ''
        exit 2
    }
    throw
}
