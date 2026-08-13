<#
.SYNOPSIS
    Offline test suite for the TestRig session lock (TestRig/rig-lock.ps1).

.DESCRIPTION
    The lock is the mechanism that makes the rig shareable by agents doing
    unrelated work, so it is the one piece of the rig that has to be correct
    under contention rather than merely tidy. This suite exercises it end to end.

    It runs entirely offline: no game, no dedicated server, no client instance,
    no network. Every test points the library at a throwaway directory through
    Initialize-RigLockPaths, and the suite refuses to start if that redirection
    did not take. The real TestRig/session.lock is hashed before the run and
    verified untouched after it.

    No Pester. A dozen lines of assert helpers cover everything needed here, and
    a dependency that has to be installed before the lock can be tested is a
    dependency that stops the lock from being tested.

    The concurrency section spawns real pwsh processes that race for the same
    fresh lock, and can measure the PRE-FIX implementation as well (a copy of the
    original read-then-write functions is kept at the bottom of this file for
    exactly that purpose). A concurrency fix with no before-measurement is an
    assertion, not evidence.

.PARAMETER Section
    Run only sections whose name matches this wildcard. Default: all.

.PARAMETER SkipConcurrency
    Skip the process-spawning sections (fast inner loop while editing).

.PARAMETER Rounds
    Concurrency rounds. Default 20.

.PARAMETER Contenders
    Processes racing per round. Default 4.

.PARAMETER MeasurePreFix
    Also run the concurrency race against the pre-fix implementation and report
    how often it produced more than one winner. Default on.

.PARAMETER ChildRole
    Internal. Selects the child-process behaviour when this script is spawned by
    the concurrency section. Not for direct use.
#>
[CmdletBinding()]
param(
    [string] $Section = '*',
    [switch] $SkipConcurrency,
    [int]    $Rounds = 20,
    [int]    $Contenders = 4,
    [bool]   $MeasurePreFix = $true,

    # ---- internal child-process plumbing ----
    [ValidateSet('', 'acquire', 'acquire-prefix', 'refresh', 'unlock', 'assert', 'hold-mutex', 'release-after')]
    [string] $ChildRole = '',
    [string] $ChildHome,
    [string] $ChildResult,
    [string] $ChildGate,
    [string] $ChildOwner,
    [int]    $ChildIterations = 25,
    [int]    $ChildDelaySeconds = 2
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'rig-lock.ps1')

# =============================================================================
# Child-process modes
# =============================================================================
# The concurrency tests need several real processes behaving in specific ways.
# They live in this same file rather than in generated scratch scripts so the
# committed suite is self-contained and what races is what is reviewed.

function Wait-ChildGate {
    param([string] $Name, [string] $ResultFile)
    if (-not $Name) { return }
    # Announce readiness, then block. The parent releases every child at once, so
    # the race is between processes that are already loaded and warm, not between
    # pwsh start-up times.
    Set-Content -LiteralPath "$ResultFile.ready" -Value 'ready' -Encoding utf8
    $gate = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $Name)
    [void]$gate.WaitOne([TimeSpan]::FromSeconds(60))
    $gate.Dispose()
}

function Invoke-ChildRole {
    # NOTE: this is CALLED from the bottom of the file, not from here. Every role
    # below reaches for a function defined later in the script, and PowerShell
    # only has a function once its definition has executed. Dispatching at the top
    # made every pre-fix contender die with "command not found", which shows up as
    # a race with zero winners: a broken baseline that looks like a clean one.
    Initialize-RigLockPaths -RigHome $ChildHome
    $result = 'ERR no result'
    try {
        switch ($ChildRole) {
            'acquire' {
                Wait-ChildGate -Name $ChildGate -ResultFile $ChildResult
                $owner = New-RigLock -Purpose 'concurrency probe' -Tool 'rig-lock.tests.ps1' 6>$null 3>$null
                $result = "WIN $owner"
            }
            'acquire-prefix' {
                Wait-ChildGate -Name $ChildGate -ResultFile $ChildResult
                $owner = New-PreFixRigLock -Purpose 'concurrency probe' -Tool 'rig-lock.tests.ps1' 6>$null 3>$null
                $result = "WIN $owner"
            }
            'refresh' {
                Wait-ChildGate -Name $ChildGate -ResultFile $ChildResult
                for ($i = 0; $i -lt $ChildIterations; $i++) { Update-RigLock -CallerId $ChildOwner 6>$null 3>$null }
                $result = "OK $ChildIterations"
            }
            'unlock' {
                Wait-ChildGate -Name $ChildGate -ResultFile $ChildResult
                Remove-RigLock -CallerId $ChildOwner 6>$null 3>$null
                $result = 'OK unlocked'
            }
            'assert' {
                Wait-ChildGate -Name $ChildGate -ResultFile $ChildResult
                Assert-RigLockHeld -Action 'ProbeAction' -CallerId $ChildOwner -Tool 'rig-lock.tests.ps1' 6>$null 3>$null
                $result = 'OK asserted'
            }
            'hold-mutex' {
                # Take the critical section and never come out. The parent kills
                # this process to produce an abandoned mutex.
                Invoke-WithRigLockMutex -Context 'hold for the abandoned-mutex test' -Body {
                    Set-Content -LiteralPath "$ChildResult.ready" -Value 'holding' -Encoding utf8
                    Start-Sleep -Seconds 300
                }
                $result = 'OK (unexpectedly returned)'
            }
            'release-after' {
                Set-Content -LiteralPath "$ChildResult.ready" -Value 'ready' -Encoding utf8
                Start-Sleep -Seconds $ChildDelaySeconds
                Remove-RigLock -CallerId $ChildOwner 6>$null 3>$null
                $result = 'OK released'
            }
        }
    }
    catch {
        $result = "LOSE $(($_.Exception.Message -split "`n")[0])"
    }
    Set-Content -LiteralPath $ChildResult -Value $result -Encoding utf8
}

# =============================================================================
# Assert helpers
# =============================================================================

$script:Passed    = 0
$script:Failed    = 0
$script:Failures  = @()
$script:CurrentSection = ''

function Start-Section {
    param([string] $Name)
    $script:CurrentSection = $Name
    Write-Host ''
    Write-Host "== $Name " -NoNewline
    Write-Host ('=' * [Math]::Max(3, 60 - $Name.Length))
}

function Add-Pass { param([string] $Name) $script:Passed++; Write-Host "  pass  $Name" }
function Add-Fail {
    param([string] $Name, [string] $Detail)
    $script:Failed++
    $script:Failures += "[$script:CurrentSection] $Name`n        $Detail"
    Write-Host "  FAIL  $Name"
    Write-Host "        $Detail"
}

function Assert-True {
    param([bool] $Condition, [string] $Name, [string] $Detail = '')
    if ($Condition) { Add-Pass $Name } else { Add-Fail $Name "expected true. $Detail" }
}

function Assert-False {
    param([bool] $Condition, [string] $Name, [string] $Detail = '')
    if (-not $Condition) { Add-Pass $Name } else { Add-Fail $Name "expected false. $Detail" }
}

function Assert-Equal {
    param($Expected, $Actual, [string] $Name)
    if ($Expected -eq $Actual) { Add-Pass $Name }
    else { Add-Fail $Name "expected [$Expected], got [$Actual]" }
}

function Assert-Match {
    param([string] $Text, [string] $Pattern, [string] $Name)
    if ($Text -match $Pattern) { Add-Pass $Name }
    else { Add-Fail $Name "text did not match /$Pattern/. Text was: $Text" }
}

function Assert-Throws {
    param([scriptblock] $Body, [string] $Name, [string] $Pattern)
    try {
        $null = & $Body 6>$null 3>$null
        Add-Fail $Name 'expected a throw; the call returned normally'
    }
    catch {
        $msg = $_.Exception.Message
        if ($Pattern -and $msg -notmatch $Pattern) { Add-Fail $Name "threw, but the message did not match /$Pattern/. Message: $msg" }
        else { Add-Pass $Name }
    }
}

function Assert-NoThrow {
    param([scriptblock] $Body, [string] $Name)
    try { $null = & $Body 6>$null 3>$null; Add-Pass $Name }
    catch { Add-Fail $Name "unexpected throw: $($_.Exception.Message)" }
}

function Invoke-Quiet {
    param([scriptblock] $Body)
    return (& $Body 6>$null 3>$null)
}

function Test-SectionSelected {
    param([string] $Name)
    return ($Name -like $Section)
}

# =============================================================================
# Fixtures
# =============================================================================

$script:TempRoot   = $null
$script:RealHome   = $PSScriptRoot
$script:RealLock   = Join-Path $PSScriptRoot 'session.lock'
$script:RealBefore = $null

# A pid that cannot belong to a live process. Windows process ids are well under
# this, and Get-Process simply reports "no such process".
$script:DeadPid = 999999999

function Use-TestPaths {
    # The standard test wiring. Both image names are pwsh so a fixture can use a
    # real, live process id ($PID or a spawned child) as "the game is running",
    # and the instance root points inside the temp tree so the orphan scan never
    # picks up the machine's own pwsh processes.
    Initialize-RigLockPaths -RigHome $script:TempRoot `
        -ServerImageName 'pwsh' -ClientImageName 'pwsh' `
        -InstanceRoot (Join-Path $script:TempRoot 'ClientRig\instances')
}

function New-TestHome {
    # A fresh TestRig-shaped temp root, and the safety check that the redirection
    # actually took. If Initialize-RigLockPaths ever silently failed, every test
    # below would be operating on the real rig lock, so this is checked and not
    # assumed.
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("riglock-tests-" + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'DedicatedServer\data') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'ClientRig\data') | Out-Null
    $script:TempRoot = $root
    Use-TestPaths
    if ((Get-RigLockFilePath) -ne (Join-Path $root 'session.lock')) {
        throw "SAFETY ABORT: Initialize-RigLockPaths did not repoint the lock file. It is still $(Get-RigLockFilePath)."
    }
    if ((Get-RigLockFilePath) -eq $script:RealLock) {
        throw "SAFETY ABORT: the test home resolves to the REAL rig lock at $script:RealLock."
    }
    return $root
}

function Reset-TestHome {
    # Between tests: no lock file, no fake server, no fake instances.
    Remove-Item -LiteralPath (Get-RigLockFilePath) -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Get-RigDirtyFilePath) -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $script:TempRoot -Filter 'session.lock.*' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -Path $script:TempRoot -Filter 'session.dirty.*' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'DedicatedServer\data')
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'ClientRig\data')
    New-Item -ItemType Directory -Force -Path (Join-Path $script:TempRoot 'DedicatedServer\data') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $script:TempRoot 'ClientRig\data') | Out-Null
}

function Set-TestLock {
    # Write a lock file directly, so a test can pin any timer state it likes
    # without waiting for real minutes to pass.
    #
    # THE TWO AGES ARE SEPARATE KNOBS, because the lock has two timers with
    # different anchors and the interesting cases are the ones where they
    # disagree:
    #   -AgeMinutes   how old refreshed_at is        -> drives ttl_minutes
    #   -IdleMinutes  how long since the owner acted -> drives idle_ceiling_minutes
    # Defaulting IdleMinutes to AgeMinutes makes an ordinary fixture behave the
    # obvious way. A busy rig that self-renewed its heartbeat while its owner
    # vanished is -AgeMinutes 1 -IdleMinutes 120, and that case is the entire
    # point of the watchdog.
    #
    # Note the default ages: 30 minutes is "well past the 10 min TTL, well inside
    # the 60 min ceiling", which is what the pre-watchdog suite meant by 60.
    param(
        [string] $Owner = 'OWNERAAA',
        [string] $Purpose = 'a test reservation',
        [double] $AgeMinutes = 0,
        [double] $IdleMinutes = [double]::NaN,
        $Ttl = 10,
        $IdleCeiling = 60,
        [string] $RefreshedAtRaw,
        [string] $ActiveAtRaw,
        [switch] $NoRefreshedAt,
        [switch] $NoActiveAt
    )
    $stamp = [DateTime]::UtcNow.AddMinutes(-$AgeMinutes).ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    $idle  = if ([double]::IsNaN($IdleMinutes)) { $AgeMinutes } else { $IdleMinutes }
    $activeStamp = [DateTime]::UtcNow.AddMinutes(-$idle).ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    $f = [ordered]@{
        owner       = $Owner
        purpose     = $Purpose
        acquired_at = $stamp
    }
    if (-not $NoRefreshedAt) {
        $f['refreshed_at'] = if ($PSBoundParameters.ContainsKey('RefreshedAtRaw')) { $RefreshedAtRaw } else { $stamp }
    }
    if (-not $NoActiveAt) {
        $f['active_at'] = if ($PSBoundParameters.ContainsKey('ActiveAtRaw')) { $ActiveAtRaw } else { $activeStamp }
    }
    $f['ttl_minutes']          = $Ttl
    $f['idle_ceiling_minutes'] = $IdleCeiling
    $f['host']                 = 'TESTHOST'
    Write-RigLock $f
    # No return value on purpose: a fixture that emits its own state pollutes
    # every caller's pipeline. Tests that need the fields read them back through
    # Read-RigLock, which is the path the library itself uses.
}

function New-TestDediServer {
    param([Nullable[int]] $ProcessId = $PID, [string[]] $LogLines = @())
    $dir = Join-Path $script:TempRoot 'DedicatedServer\data'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    if ($null -ne $ProcessId) { Set-Content -LiteralPath (Join-Path $dir 'server.pid') -Value "$ProcessId" -Encoding utf8 }
    Set-Content -LiteralPath (Join-Path $dir 'server.log') -Value ($LogLines -join "`n") -Encoding utf8
}

function New-TestInstance {
    # Fake a provisioned client-rig instance on disk, exactly as the client-rig
    # launcher lays one out: data/<name>/game.pid + instance.json + logs/.
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $RawPid,
        [string] $Role,
        [switch] $NoManifest,
        [switch] $BrokenManifest,
        [string[]] $LogLines
    )
    $dir = Join-Path $script:TempRoot "ClientRig\data\$Name"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $pidValue = if ($PSBoundParameters.ContainsKey('RawPid')) { $RawPid } else { "$PID" }
    Set-Content -LiteralPath (Join-Path $dir 'game.pid') -Value $pidValue -Encoding utf8

    if ($BrokenManifest) {
        Set-Content -LiteralPath (Join-Path $dir 'instance.json') -Value '{ this is not json' -Encoding utf8
    }
    elseif (-not $NoManifest) {
        $m = [ordered]@{ instanceName = $Name; port = 27700 }
        if ($Role) { $m['role'] = $Role }
        $m | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dir 'instance.json') -Encoding utf8
    }

    if ($PSBoundParameters.ContainsKey('LogLines')) {
        $logDir = Join-Path $dir 'logs'
        New-Item -ItemType Directory -Force -Path $logDir | Out-Null
        Set-Content -LiteralPath (Join-Path $logDir 'unity-20260809-120000.log') -Value ($LogLines -join "`n") -Encoding utf8
    }
    return $dir
}

function New-TestLogFile {
    param([string[]] $Lines)
    $p = Join-Path $script:TempRoot ("log-" + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.log')
    Set-Content -LiteralPath $p -Value ($Lines -join "`n") -Encoding utf8
    return $p
}

# =============================================================================
# Sections
# =============================================================================

function Test-PathInjection {
    if (-not (Test-SectionSelected 'paths')) { return }
    Start-Section 'paths (3.1 path injection)'
    Reset-TestHome

    Assert-Equal (Join-Path $script:TempRoot 'session.lock') (Get-RigLockFilePath) 'lock file follows the injected home'
    Assert-Equal (Join-Path $script:TempRoot 'CLAUDE.md') (Get-RigLockRulesPath) 'rules path follows the injected home'
    Assert-Equal $script:TempRoot (Get-RigLockHomePath) 'home accessor reports the injected home'

    # The mutex must follow the lock file, or a test run would serialise against
    # (and be serialised by) a real rig session.
    $testMutex = Get-RigLockMutexFullName
    Initialize-RigLockPaths -RigHome $script:RealHome
    $realMutex = Get-RigLockMutexFullName
    Use-TestPaths
    Assert-True ($testMutex -ne $realMutex) 'the critical-section mutex name is derived from the lock path' "test=$testMutex real=$realMutex"
    Assert-Match $testMutex '^(Global|Local)\\StationeersTestRig\.SessionLock\.[0-9A-F]{16}$' 'mutex name has the documented shape'
}

function Test-StateMachine {
    if (-not (Test-SectionSelected 'state')) { return }
    Start-Section 'state machine'
    Reset-TestHome

    Assert-Equal 'None' (Invoke-Quiet { (Get-RigLockState -CallerId 'nobody').State }) 'no file -> None'
    Assert-Equal 'None' (Invoke-Quiet { (Get-RigLockState).State }) 'no file, no caller id -> None'

    Set-TestLock -Owner 'AAA11111'
    Assert-Equal 'Mine'        (Invoke-Quiet { (Get-RigLockState -CallerId 'AAA11111').State }) 'owner -> Mine'
    Assert-Equal 'LiveForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'BBB22222').State }) 'other, fresh timer -> LiveForeign'
    Assert-Equal 'LiveForeign' (Invoke-Quiet { (Get-RigLockState).State })                      'no caller id, fresh timer -> LiveForeign'

    Set-TestLock -Owner 'AAA11111' -AgeMinutes 30
    Assert-Equal 'DeadForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'BBB22222').State }) 'other, expired, idle rig -> DeadForeign'
    Assert-Equal 'Mine'        (Invoke-Quiet { (Get-RigLockState -CallerId 'AAA11111').State }) 'expired but mine is still Mine'

    # Assert-RigLockHeld across all four states.
    Reset-TestHome
    Assert-Throws { Assert-RigLockHeld -Action 'Start' -CallerId 'X' -Tool 'testrig.ps1' } 'Assert-RigLockHeld: None throws and points at lock' 'lock -Purpose'
    Set-TestLock -Owner 'AAA11111'
    Assert-NoThrow { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 'testrig.ps1' } 'Assert-RigLockHeld: Mine passes'
    Assert-Throws  { Assert-RigLockHeld -Action 'Start' -CallerId 'BBB22222' -Tool 'testrig.ps1' } 'Assert-RigLockHeld: LiveForeign throws and names -BreakLock' 'BreakLock'
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 30
    Assert-Throws  { Assert-RigLockHeld -Action 'Start' -CallerId 'BBB22222' -Tool 'testrig.ps1' } 'Assert-RigLockHeld: DeadForeign throws and says re-acquire' 'expired'

    # New-RigLock across all four states.
    Reset-TestHome
    $owner1 = Invoke-Quiet { New-RigLock -Purpose 'p1' -Tool 't' }
    Assert-Match $owner1 '^[0-9a-f]{8}$' 'New-RigLock from None mints an 8-char owner id'
    Assert-Equal 'p1' (Read-RigLock)['purpose'] 'New-RigLock from None writes the purpose'

    $owner2 = Invoke-Quiet { New-RigLock -Purpose 'p2 changed' -CallerId $owner1 -Tool 't' }
    Assert-Equal $owner1 $owner2 'New-RigLock from Mine re-asserts the SAME owner id'
    Assert-Equal 'p2 changed' (Read-RigLock)['purpose'] 'New-RigLock from Mine updates the purpose'

    Assert-Throws { New-RigLock -Purpose 'p3' -CallerId 'SOMEONE' -Tool 't' } 'New-RigLock from LiveForeign refuses' 'locked by another session'
    Assert-Equal $owner1 (Read-RigLock)['owner'] 'a refused acquisition leaves the existing owner in place'

    Set-TestLock -Owner 'OLDOWNER' -AgeMinutes 30
    $reclaimed = $false
    $owner3 = Invoke-Quiet { New-RigLock -Purpose 'p4' -Tool 't' -OnReclaim { $script:ReclaimFlag = $true } }
    Assert-True ($owner3 -ne 'OLDOWNER') 'New-RigLock from DeadForeign mints a new owner id'
    Assert-True ($script:ReclaimFlag -eq $true) 'New-RigLock from DeadForeign runs OnReclaim'
    $script:ReclaimFlag = $false

    # Update-RigLock across the states.
    Reset-TestHome
    Assert-Throws { Update-RigLock -CallerId 'ANY' } 'Update-RigLock with no lock throws' 'No rig session lock to refresh'
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 5
    Assert-NoThrow { Update-RigLock -CallerId 'AAA11111' } 'Update-RigLock as owner succeeds'
    Assert-Throws  { Update-RigLock -CallerId 'BBB22222' } 'Update-RigLock as non-owner throws' 'Refresh refused'

    # Remove-RigLock across the states.
    Reset-TestHome
    Assert-NoThrow { Remove-RigLock -CallerId 'ANY' } 'Remove-RigLock with no lock is a no-op, not an error'
    Set-TestLock -Owner 'AAA11111'
    Assert-Throws { Remove-RigLock -CallerId 'BBB22222' } 'Remove-RigLock as non-owner throws' 'Unlock refused'
    Assert-True (Test-Path -LiteralPath (Get-RigLockFilePath)) 'a refused unlock leaves the lock file in place'
    Assert-NoThrow { Remove-RigLock -CallerId 'AAA11111' } 'Remove-RigLock as owner succeeds'
    Assert-False (Test-Path -LiteralPath (Get-RigLockFilePath)) 'unlock deletes the lock file'
}

function Test-Ttl {
    if (-not (Test-SectionSelected 'ttl')) { return }
    Start-Section 'TTL'
    Reset-TestHome

    # Each case is written to disk and read back through Read-RigLock, so the
    # timer is judged on exactly the bytes a launcher would see.
    function Test-TimerFor { param([hashtable] $Opts) Set-TestLock @Opts; return (Test-RigLockTimerExpired (Read-RigLock)) }

    Assert-False (Test-TimerFor @{ AgeMinutes = 0 })  'a fresh lock is not expired'
    Assert-False (Test-TimerFor @{ AgeMinutes = 9 })  'inside the default 10 min TTL is not expired'
    Assert-True  (Test-TimerFor @{ AgeMinutes = 11 }) 'past the default 10 min TTL is expired'

    Assert-True  (Test-TimerFor @{ AgeMinutes = 2;  Ttl = 1 })  'a custom short -TtlMinutes is honoured'
    Assert-False (Test-TimerFor @{ AgeMinutes = 20; Ttl = 30 }) 'a custom long -TtlMinutes is honoured'

    Assert-True (Test-TimerFor @{ NoRefreshedAt = $true }) 'a missing refreshed_at is treated as expired (fail closed)'
    Assert-True (Test-TimerFor @{ RefreshedAtRaw = 'not-a-timestamp' }) 'an unparseable refreshed_at is treated as expired (fail closed)'
    Assert-True (Test-TimerFor @{ Ttl = 'banana' }) 'an unparseable ttl_minutes is treated as expired (fail closed)'
    Assert-True (Test-TimerFor @{ Ttl = -5 }) 'a negative ttl_minutes is treated as expired (fail closed)'

    Assert-NoThrow { Test-TimerFor @{ AgeMinutes = -120 } } 'a refreshed_at in the future does not crash'
    Assert-False (Test-TimerFor @{ AgeMinutes = -120 }) 'a refreshed_at in the future is not expired'
    Set-TestLock -RefreshedAtRaw 'nonsense'
    Assert-Equal 'unknown' (Get-RigLockAgeText (Read-RigLock)) 'age text degrades to unknown on a bad stamp'

    # Expired + busy: LiveForeign AND self-renewed.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 30
    New-TestInstance -Name 'alpha' -Role 'client' | Out-Null
    $before = (Read-RigLock)['refreshed_at']
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'BBB22222' }
    Assert-Equal 'LiveForeign' $st.State 'expired timer + busy rig -> LiveForeign, not DeadForeign'
    $after = (Read-RigLock)['refreshed_at']
    Assert-True ($after -ne $before) 'expired timer + busy rig self-renews refreshed_at' "before=$before after=$after"
    Assert-False (Test-RigLockTimerExpired (Read-RigLock)) 'the self-renewed lock has a fresh timer again'
    Assert-Match $st.Busy 'client instance' 'the LiveForeign result carries the busy reason'
}

function Test-IdleCeiling {
    if (-not (Test-SectionSelected 'watchdog')) { return }
    Start-Section 'idle watchdog (the absolute ceiling, and why it is a second field)'
    Reset-TestHome

    # ---- the fields exist and are written ----
    $owner = Invoke-Quiet { New-RigLock -Purpose 'watchdog fields' -Tool 't' }
    $lk = Read-RigLock
    Assert-True ($lk.Contains('active_at')) 'a new lock records active_at'
    Assert-True ($lk.Contains('idle_ceiling_minutes')) 'a new lock records idle_ceiling_minutes'
    Assert-Equal '60' $lk['idle_ceiling_minutes'] 'the idle ceiling defaults to 60 minutes'
    Assert-False (Test-RigLockIdleCeilingExceeded $lk) 'a lock taken a moment ago is nowhere near the ceiling'

    Reset-TestHome
    Invoke-Quiet { New-RigLock -Purpose 'custom ceiling' -Tool 't' -IdleCeilingMinutes 5 } | Out-Null
    Assert-Equal '5' (Read-RigLock)['idle_ceiling_minutes'] '-IdleCeilingMinutes is honoured on acquisition'

    # ---- an idle owner loses the rig ----
    Reset-TestHome
    Set-TestLock -Owner 'GONE0001' -AgeMinutes 90
    Assert-True  (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'an owner idle past the ceiling is over it'
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' }
    Assert-Equal 'DeadForeign' $st.State 'past the ceiling, a foreign lock is reclaimable'
    Assert-Equal 'idle-ceiling' $st.Reclaim 'and the reclaim reason names the ceiling, not the ttl'

    Set-TestLock -Owner 'GONE0001' -AgeMinutes 30
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' }
    Assert-Equal 'DeadForeign' $st.State 'inside the ceiling but past the TTL is still reclaimable'
    Assert-Equal 'ttl' $st.Reclaim 'and that one is reported as a TTL reclaim'

    # ---- THE CENTRAL DESIGN QUESTION, both halves ----
    # Below the ceiling a busy rig is untouchable, exactly as before: a live test
    # must not lose the rig in the gap between two of its own commands.
    Reset-TestHome
    Set-TestLock -Owner 'BUSY0001' -AgeMinutes 30 -IdleMinutes 30
    New-TestInstance -Name 'hostie' -Role 'host' | Out-Null
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' }
    Assert-Equal 'LiveForeign' $st.State 'BELOW the ceiling, a busy rig still keeps an expired lock alive (the old guarantee is intact)'

    # Past the ceiling it is NOT, and this is the change. "No hung agent can stop
    # other agents, only delay it" is only true if busy eventually loses too:
    # otherwise one forgotten client instance holds the whole rig for ever.
    Set-TestLock -Owner 'BUSY0001' -AgeMinutes 1 -IdleMinutes 120
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' }
    Assert-Equal 'DeadForeign' $st.State 'PAST the ceiling, even a BUSY rig is reclaimable'
    Assert-Equal 'idle-ceiling' $st.Reclaim 'and it is reported as an idle-ceiling reclaim'
    Assert-Match $st.Busy 'client instance' 'the reclaim carries the busy detail, so the warning can say what it is taking'

    # ---- the mechanism: why active_at has to be its own field ----
    # A busy rig self-renews its heartbeat. If the ceiling were anchored on
    # refreshed_at, that renewal would push the ceiling out for ever and the case
    # above could never be reached. So the renewal must move ONE clock only.
    Reset-TestHome
    Set-TestLock -Owner 'BUSY0001' -AgeMinutes 30 -IdleMinutes 45
    New-TestInstance -Name 'alpha' -Role 'client' | Out-Null
    $beforeR = (Read-RigLock)['refreshed_at']
    $beforeA = (Read-RigLock)['active_at']
    Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' } | Out-Null
    Assert-True  ((Read-RigLock)['refreshed_at'] -ne $beforeR) 'the busy self-renew DOES move refreshed_at'
    Assert-Equal $beforeA (Read-RigLock)['active_at'] 'the busy self-renew does NOT move active_at, so it cannot push the ceiling out'

    # And the owner's own actions move both, which is what makes a long but ACTIVE
    # session safe from the watchdog however long it runs.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 8 -IdleMinutes 8
    $beforeA = (Read-RigLock)['active_at']
    Invoke-Quiet { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 't' }
    Assert-True ((Read-RigLock)['active_at'] -ne $beforeA) 'a mutating command by the owner moves active_at'

    Set-TestLock -Owner 'AAA11111' -AgeMinutes 8 -IdleMinutes 8
    $beforeA = (Read-RigLock)['active_at']
    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' }
    Assert-True ((Read-RigLock)['active_at'] -ne $beforeA) '-RefreshLock moves active_at'

    Set-TestLock -Owner 'AAA11111' -AgeMinutes 8 -IdleMinutes 8
    $beforeA = (Read-RigLock)['active_at']
    Invoke-Quiet { Update-RigLockIfMine -CallerId 'AAA11111' }
    Assert-True ((Read-RigLock)['active_at'] -ne $beforeA) 'a readiness barrier moves active_at (it is the owner working)'

    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' -IdleCeilingMinutes 5 }
    Assert-Equal '5' (Read-RigLock)['idle_ceiling_minutes'] '-RefreshLock -IdleCeilingMinutes rewrites the ceiling'
    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' }
    Assert-Equal '5' (Read-RigLock)['idle_ceiling_minutes'] 'a refresh without one leaves the existing ceiling alone'

    # ---- reclaimable is not revoked ----
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 120
    Assert-Equal 'Mine' (Invoke-Quiet { (Get-RigLockState -CallerId 'AAA11111').State }) 'past the ceiling the lock is still MINE to its owner: first come, not revoked'
    Assert-NoThrow { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 't' } 'an owner who comes back before anybody reclaims can still act'
    Assert-False (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'and that action puts the lock back inside the ceiling'

    # ---- fail closed, exactly like the TTL ----
    Reset-TestHome
    Set-TestLock -Owner 'X' -AgeMinutes 90 -NoActiveAt
    Assert-True (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'a lock with no active_at falls back to acquired_at (older, never fresher)'
    Set-TestLock -Owner 'X' -AgeMinutes 5 -NoActiveAt
    Assert-False (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'and that fallback still leaves a recent lock inside the ceiling'
    Set-TestLock -Owner 'X' -ActiveAtRaw 'not-a-timestamp' -AgeMinutes 0
    Assert-False (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'an unparseable active_at falls through to acquired_at rather than crashing'
    Set-TestLock -Owner 'X' -ActiveAtRaw 'nope' -RefreshedAtRaw 'nope' -AgeMinutes 0
    $bad = Read-RigLock
    $bad['acquired_at'] = 'nope'
    Assert-True (Test-RigLockIdleCeilingExceeded $bad) 'a lock with NO parseable time at all counts as past the ceiling (fail closed)'
    Set-TestLock -Owner 'X' -AgeMinutes 0 -IdleCeiling 'banana'
    Assert-True (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'an unparseable idle_ceiling_minutes counts as exceeded (fail closed)'
    Set-TestLock -Owner 'X' -AgeMinutes 0 -IdleCeiling -5
    Assert-True (Test-RigLockIdleCeilingExceeded (Read-RigLock)) 'a negative idle_ceiling_minutes counts as exceeded (fail closed)'
    Assert-Equal 60 (Get-RigLockIdleCeiling @{ owner = 'X' }) 'a lock with no ceiling field at all is read as the 60 min default'

    # ---- the reclaim tears down what it takes ----
    Reset-TestHome
    Set-TestLock -Owner 'GONE0001' -AgeMinutes 120
    New-TestInstance -Name 'leftover' -Role 'host' | Out-Null
    $script:ReclaimFired = $false
    $new = Invoke-Quiet { New-RigLock -Purpose 'watchdog takeover' -Tool 't' -OnReclaim { $script:ReclaimFired = $true } }
    Assert-True ($new -and $new -ne 'GONE0001') 'the watchdog reclaim mints a new owner id'
    Assert-True $script:ReclaimFired 'the reclaim runs OnReclaim, which is where a launcher tears the leftovers down'
    $script:ReclaimFired = $false

    # ---- -Stop -Release can free a lock the ceiling has killed ----
    Reset-TestHome
    Set-TestLock -Owner 'GONE0001' -AgeMinutes 1 -IdleMinutes 120
    New-TestInstance -Name 'alpha' -Role 'client' | Out-Null
    Assert-True (Test-RigLockReleasableOnStop -Lock (Read-RigLock) -CallerId 'OTHER') `
        'the release predicate frees a lock past the ceiling even though its heartbeat is fresh'
    Set-TestLock -Owner 'GONE0001' -AgeMinutes 1 -IdleMinutes 1
    Assert-False (Test-RigLockReleasableOnStop -Lock (Read-RigLock) -CallerId 'OTHER') `
        'and it still refuses a lock that is fresh on both clocks'

    # ---- the refusal text tells a human how long is left ----
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 2 -IdleMinutes 2
    $st = Invoke-Quiet { Get-RigLockState -CallerId 'OTHER' }
    $text = Format-ForeignRigLock $st
    Assert-Match $text 'idle' 'the refusal names how long the holder has been idle'
    Assert-Match $text 'ceiling' 'and how long is left on the ceiling'
    Assert-NoThrow { Write-RigLockStatus -CallerId 'OTHER' } '-Status renders the ceiling countdown without throwing'
}

function Test-DirtyMarker {
    if (-not (Test-SectionSelected 'dirty')) { return }
    Start-Section 'crash marker (survives a kill, a reboot and a power cut)'
    Reset-TestHome
    Remove-Item -LiteralPath (Get-RigDirtyFilePath) -Force -ErrorAction SilentlyContinue

    Assert-Equal (Join-Path $script:TempRoot 'session.dirty') (Get-RigDirtyFilePath) 'the marker lives beside the lock, inside the injected home'
    $d = Get-RigDirtyState
    Assert-False $d.Dirty 'a rig nobody has mutated is not dirty'
    Assert-Match (Format-RigDirtyState $d) 'clean' 'and it says so in words'

    # ---- it goes down BEFORE the first mutating action, at the gate ----
    Set-TestLock -Owner 'AAA11111'
    Assert-False (Test-Path -LiteralPath (Get-RigDirtyFilePath)) 'taking a lock alone does not mark the rig dirty (nothing has been mutated yet)'
    Invoke-Quiet { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 't' }
    Assert-True (Test-Path -LiteralPath (Get-RigDirtyFilePath)) 'the FIRST mutating action marks the rig dirty'
    $m = Read-RigDirtyMarker
    Assert-Equal 'AAA11111' $m['owner'] 'the marker records who dirtied the rig'
    Assert-Equal 'Start'    $m['reason'] 'and which action did it'
    Assert-Match $m['boot_id'] '^(boot|approx|unknown)' 'and the boot identity of the machine it happened on'
    Assert-Equal "$PID" $m['writer_pid'] 'and the pid of the launcher process that wrote it'

    # Idempotent: the timestamp of the FIRST mutation is what matters, so later
    # commands must not keep rewriting it.
    $firstStamp = $m['marked_at']
    Start-Sleep -Milliseconds 1100
    Invoke-Quiet { Assert-RigLockHeld -Action 'Save' -CallerId 'AAA11111' -Tool 't' }
    Assert-Equal $firstStamp (Read-RigDirtyMarker)['marked_at'] 'later mutating commands leave the original marker alone'
    Assert-Equal 'Start' (Read-RigDirtyMarker)['reason'] 'and do not overwrite what first dirtied it'

    # A different owner DOES replace it: that is a new session's mess.
    Set-TestLock -Owner 'BBB22222'
    Invoke-Quiet { Assert-RigLockHeld -Action 'Provision' -CallerId 'BBB22222' -Tool 't' }
    Assert-Equal 'BBB22222' (Read-RigDirtyMarker)['owner'] 'a different owner writes its own marker'

    $stray = @(Get-ChildItem -Path $script:TempRoot -Filter 'session.dirty.*.tmp' -File -ErrorAction SilentlyContinue)
    Assert-Equal 0 $stray.Count 'the durable write leaves no staging file behind'

    # ---- the boot-id problem: a pid from before a reboot means nothing ----
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'LIVE0001' -Purpose 'p' -Reason 'Start' } | Out-Null
    $d = Get-RigDirtyState
    Assert-True  $d.Dirty       'a marker written by this process reads as dirty'
    Assert-True  $d.SameBoot    'and as written during this boot'
    Assert-True  $d.WriterAlive 'and its writer is alive, because it is this very process'
    Assert-False $d.Crashed     'so it is not a crash'
    Assert-Match (Format-RigDirtyState $d) 'STILL RUNNING' 'and the description says the session is still going'

    # SIMULATED REBOOT: same marker, different boot id. The recorded pid may well
    # be alive again as something unrelated, so it must not be consulted at all.
    Initialize-RigLockPaths -RigHome $script:TempRoot -ServerImageName 'pwsh' -ClientImageName 'pwsh' `
        -InstanceRoot (Join-Path $script:TempRoot 'ClientRig\instances') -BootId 'boot:2099-01-01T00:00:00Z'
    $d = Get-RigDirtyState
    Assert-True  $d.Dirty       'the marker survives the reboot'
    Assert-False $d.SameBoot    'and is recognised as predating it'
    Assert-False $d.WriterAlive 'its recorded pid is NOT trusted across a reboot, however alive that number is now'
    Assert-True  $d.Crashed     'so the previous session counts as gone'
    Assert-Match (Format-RigDirtyState $d) 'machine has restarted' 'and the description says why'
    Use-TestPaths

    # A RECYCLED pid within one boot: alive, but not the image it claims. Same
    # rule the rig already applies to the game process, applied to its own writer.
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'RECYCLED' -Purpose 'p' -Reason 'Start' } | Out-Null
    $raw = Read-RigDirtyMarker
    $raw['writer_image'] = 'rocketstation'      # this pid is alive, but it is pwsh, not that
    Write-RigFileDurable -Path (Get-RigDirtyFilePath) -Text (($raw.Keys | ForEach-Object { "$_=$($raw[$_])" }) -join "`n")
    $d = Get-RigDirtyState
    Assert-True  $d.SameBoot    'the recycled-pid marker is from this boot'
    Assert-False $d.WriterAlive 'but a live pid with the WRONG image is not the process that wrote it'
    Assert-True  $d.Crashed     'so it counts as a crashed session, which is the safe direction'

    # A dead pid within one boot is the ordinary kill case.
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'KILLED01' -Purpose 'p' -Reason 'Provision' } | Out-Null
    $raw = Read-RigDirtyMarker
    $raw['writer_pid'] = "$script:DeadPid"
    Write-RigFileDurable -Path (Get-RigDirtyFilePath) -Text (($raw.Keys | ForEach-Object { "$_=$($raw[$_])" }) -join "`n")
    $d = Get-RigDirtyState
    Assert-False $d.WriterAlive 'a marker whose writer pid names no process reads as dead'
    Assert-True  $d.Crashed     'which is the kill-mid-mutation case'

    # ---- clearing, and what counts as a marker at all ----
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'AAA11111' } | Out-Null
    Assert-True (Get-RigDirtyState).Dirty 'fixture check: the rig is marked'
    Clear-RigDirtyMarker
    Assert-False (Get-RigDirtyState).Dirty 'clearing the marker leaves the rig clean'
    Assert-NoThrow { Clear-RigDirtyMarker } 'clearing an absent marker is a no-op, not an error'

    Set-Content -LiteralPath (Get-RigDirtyFilePath) -Value @('# just a comment', 'reason=nothing') -Encoding utf8
    Assert-True ($null -eq (Read-RigDirtyMarker)) 'a file with no owner key is not a marker'
    Assert-False (Get-RigDirtyState).Dirty 'and a rig carrying one is not dirty'
    Set-Content -LiteralPath (Get-RigDirtyFilePath) -Value '' -Encoding utf8
    Assert-NoThrow { Get-RigDirtyState } 'an empty marker file does not throw'
    Remove-Item -LiteralPath (Get-RigDirtyFilePath) -Force -ErrorAction SilentlyContinue

    # ---- acquisition NOTICES a dirty rig ----
    # The restore itself lives in rig-reset.ps1, which this suite deliberately does
    # not dot-source, so what is pinned here is the detection and the reporting.
    # rig-reset.tests.ps1 asserts the restore actually runs.
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'CRASHED1' -Purpose 'a session that died' -Reason 'Start' } | Out-Null
    $raw = Read-RigDirtyMarker
    $raw['writer_pid'] = "$script:DeadPid"
    Write-RigFileDurable -Path (Get-RigDirtyFilePath) -Text (($raw.Keys | ForEach-Object { "$_=$($raw[$_])" }) -join "`n")
    $text = (New-RigLock -Purpose 'after a crash' -Tool 't' 3>&1 6>&1 | Out-String)
    Assert-Match $text 'DIRTY' 'acquisition says out loud that the previous session left the rig dirty'
    Assert-Match $text 'CRASHED1' 'and names the session that did it'
    Assert-True (Test-Path -LiteralPath (Get-RigDirtyFilePath)) 'with no restore implementation loaded the marker is left set rather than silently dropped'
    Assert-Match $text 'not dot-sourced' 'and that wiring fault is reported instead of being mistaken for a clean rig'

    # -Lock -KeepState inherits the mess on purpose, and says so.
    Reset-TestHome
    Invoke-Quiet { Write-RigDirtyMarker -Owner 'CRASHED1' -Purpose 'a session that died' -Reason 'Start' } | Out-Null
    $text = (New-RigLock -Purpose 'inheriting on purpose' -Tool 't' -KeepState 3>&1 6>&1 | Out-String)
    Assert-Match $text 'KeepState' '-KeepState on a dirty rig says which flag caused it'
    Assert-Match $text 'ON PURPOSE' 'and that the leftovers are deliberate'
    Assert-True (Test-Path -LiteralPath (Get-RigDirtyFilePath)) 'and the marker stays set, so the NEXT session still cleans up'
    Remove-Item -LiteralPath (Get-RigDirtyFilePath) -Force -ErrorAction SilentlyContinue
}

function Test-Ownership {
    if (-not (Test-SectionSelected 'ownership')) { return }
    Start-Section 'ownership'
    Reset-TestHome

    Set-TestLock -Owner 'AAA11111' -AgeMinutes 5
    Assert-Throws  { Update-RigLock -CallerId 'BBB22222' } 'a non-owner cannot refresh' 'Refresh refused'
    Assert-Throws  { Remove-RigLock -CallerId 'BBB22222' } 'a non-owner cannot unlock' 'Unlock refused'
    Assert-Throws  { Assert-RigLockHeld -Action 'Start' -CallerId 'BBB22222' -Tool 't' } 'a non-owner cannot run a mutating action' 'locked by another session'

    Assert-NoThrow { Update-RigLock -CallerId 'AAA11111' } 'the owner can refresh'
    Assert-NoThrow { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 't' } 'the owner can run a mutating action'
    Assert-NoThrow { Remove-RigLock -CallerId 'AAA11111' } 'the owner can unlock'

    # A mutating action refreshes the timer as a side effect.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 8
    $before = (Read-RigLock)['refreshed_at']
    Assert-True (Test-RigLockTimerExpired @{ refreshed_at = $before; ttl_minutes = '5' }) 'fixture check: the pre-action stamp is old enough to matter'
    Invoke-Quiet { Assert-RigLockHeld -Action 'Start' -CallerId 'AAA11111' -Tool 't' }
    $after = (Read-RigLock)['refreshed_at']
    Assert-True ($after -ne $before) 'a mutating action refreshes the timer as a side effect' "before=$before after=$after"

    # Update-RigLockIfMine: silent no-op unless it is really yours.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 8
    $before = (Read-RigLock)['refreshed_at']
    Assert-NoThrow { Update-RigLockIfMine -CallerId 'BBB22222' } 'Update-RigLockIfMine does not throw for a non-owner'
    Assert-Equal $before (Read-RigLock)['refreshed_at'] 'Update-RigLockIfMine is a no-op for a non-owner'
    Assert-NoThrow { Update-RigLockIfMine -CallerId '' } 'Update-RigLockIfMine does not throw for an empty caller id'
    Assert-Equal $before (Read-RigLock)['refreshed_at'] 'Update-RigLockIfMine is a no-op for an empty caller id'
    Invoke-Quiet { Update-RigLockIfMine -CallerId 'AAA11111' }
    Assert-True ((Read-RigLock)['refreshed_at'] -ne $before) 'Update-RigLockIfMine refreshes for the owner'

    Reset-TestHome
    Assert-NoThrow { Update-RigLockIfMine -CallerId 'AAA11111' } 'Update-RigLockIfMine does not throw when there is no lock at all'

    # The -RefreshLock -TtlMinutes contract, pinned on the library side.
    #
    # Both launchers had the same bug in their wrapper: they tested
    # $PSBoundParameters inside a function, which is that function's own (empty)
    # dictionary and never the script's, so -RefreshLock -TtlMinutes N silently
    # did nothing. The library was always correct, and these assertions say what
    # "correct" is so a future wrapper can be checked against it. The wrappers
    # now capture the script's bound parameters at script scope and read that.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111' -Ttl 10
    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' -TtlMinutes 45 }
    Assert-Equal '45' (Read-RigLock)['ttl_minutes'] 'a refresh WITH an explicit TTL rewrites ttl_minutes'
    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' }
    Assert-Equal '45' (Read-RigLock)['ttl_minutes'] 'a refresh WITHOUT a TTL leaves the existing value alone'
    Invoke-Quiet { Update-RigLock -CallerId 'AAA11111' -TtlMinutes 1 }
    Assert-Equal '1' (Read-RigLock)['ttl_minutes'] 'a refresh can shorten the TTL as well as lengthen it'
    Assert-True (Test-RigLockTimerExpired @{ refreshed_at = ([DateTime]::UtcNow.AddMinutes(-5)).ToString("yyyy-MM-ddTHH:mm:ss'Z'"); ttl_minutes = (Read-RigLock)['ttl_minutes'] }) `
        'the shortened TTL is the value the timer actually uses'
}

function Test-BreakLock {
    if (-not (Test-SectionSelected 'breaklock')) { return }
    Start-Section 'BreakLock'
    Reset-TestHome

    Set-TestLock -Owner 'AAA11111'
    Assert-Throws { New-RigLock -Purpose 'x' -Tool 't' } 'a live foreign lock is never broken implicitly' 'Only the user may authorize -BreakLock'

    $newOwner = Invoke-Quiet { New-RigLock -Purpose 'authorized takeover' -Tool 't' -BreakLock }
    Assert-True ($newOwner -ne 'AAA11111') '-BreakLock takes the lock and mints a NEW owner id'
    Assert-Equal $newOwner (Read-RigLock)['owner'] 'the broken-and-taken lock records the new owner'
    Assert-Equal 'authorized takeover' (Read-RigLock)['purpose'] 'the broken-and-taken lock records the new purpose'

    Reset-TestHome
    Set-TestLock -Owner 'AAA11111'
    Assert-Throws { Remove-RigLock -CallerId 'BBB22222' } 'unlock by a non-owner is refused without -BreakLock' 'Unlock refused'
    Assert-NoThrow { Remove-RigLock -CallerId 'BBB22222' -BreakLock } 'unlock by a non-owner succeeds with -BreakLock'
    Assert-False (Test-Path -LiteralPath (Get-RigLockFilePath)) '-BreakLock unlock removes the file'

    # -Force must never be a way to break a lock, on any entry point.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111'
    Assert-Throws { Remove-RigLock -CallerId 'BBB22222' -Force } '-Force does NOT break another session lock on Remove-RigLock' 'Unlock refused'
    Assert-True (Test-Path -LiteralPath (Get-RigLockFilePath)) 'the lock survives a -Force attempt by a non-owner'
    Assert-False ((Get-Command New-RigLock).Parameters.ContainsKey('Force')) 'New-RigLock has no -Force parameter at all'
    Assert-True  ((Get-Command New-RigLock).Parameters.ContainsKey('BreakLock')) 'New-RigLock takes -BreakLock'
    Assert-True  ((Get-Command Remove-RigLock).Parameters.ContainsKey('BreakLock')) 'Remove-RigLock takes -BreakLock'
}

function Test-BusySignal {
    if (-not (Test-SectionSelected 'busy')) { return }
    Start-Section 'busy signal (3.4 host-aware)'
    Reset-TestHome

    Assert-False (Get-RigBusySignal).Busy 'an empty rig is not busy'

    # Dedicated-server clause: alive AND at least one player.
    New-TestDediServer -ProcessId $PID -LogLines @('boot', 'Client Dev (76561) is ready')
    $b = Get-RigBusySignal
    Assert-True  $b.Busy 'dedicated server alive with 1 player is busy'
    Assert-Match $b.Detail 'player\(s\) connected to the dedicated server' 'the dedi reason names connected players'
    Assert-Equal 1 $b.ServerPlayers 'the dedi player count is reported'

    Reset-TestHome
    New-TestDediServer -ProcessId $PID -LogLines @('boot', 'nothing here')
    Assert-False (Get-RigBusySignal).Busy 'dedicated server alive with nobody connected is NOT busy'

    Reset-TestHome
    New-TestDediServer -ProcessId $script:DeadPid -LogLines @('Client Dev (76561) is ready')
    Assert-False (Get-RigBusySignal).Busy 'a stale server pid with a stale log is not busy'

    # Client clause.
    Reset-TestHome
    New-TestInstance -Name 'alpha' -Role 'client' | Out-Null
    $b = Get-RigBusySignal
    Assert-True  $b.Busy 'a live client instance is busy'
    Assert-Match $b.Detail '1 client instance\(s\) running' 'the client reason counts instances'
    Assert-Match $b.Detail 'alpha=client' 'the client reason names the instance and its role'
    Assert-False $b.HostLive 'a joiner-only rig reports HostLive false'

    Reset-TestHome
    New-TestInstance -Name 'alpha' -RawPid "$script:DeadPid" -Role 'client' | Out-Null
    Assert-False (Get-RigBusySignal).Busy 'a stale game.pid pointing at a dead process is not busy'

    Reset-TestHome
    New-TestInstance -Name 'alpha' -RawPid 'not-a-number' -Role 'client' | Out-Null
    Assert-NoThrow { Get-RigBusySignal } 'a garbage pid file does not throw'
    Assert-False (Get-RigBusySignal).Busy 'a garbage pid file is not busy'

    Reset-TestHome
    New-TestInstance -Name 'alpha' -RawPid '' -Role 'client' | Out-Null
    Assert-NoThrow { Get-RigBusySignal } 'an empty pid file does not throw'

    # Host-aware detail: the whole point of 3.4.
    Reset-TestHome
    New-TestInstance -Name 'hostie' -Role 'host' -LogLines @(
        'Client Alice (111) is ready'
        'Client Bob (222) is ready'
        'Client disconnected: Bob'
    ) | Out-Null
    New-TestInstance -Name 'joiner' -Role 'client' | Out-Null
    $b = Get-RigBusySignal
    Assert-True  $b.Busy 'a hosted session is busy'
    Assert-True  $b.HostLive 'a live host is reported as HostLive'
    Assert-Match $b.Detail '2 client instance\(s\) running' 'the hosted reason counts every instance'
    Assert-Match $b.Detail 'hostie=HOST \(1 connected\)' 'the hosted reason names the host and its connected client count'
    Assert-Match $b.Detail 'joiner=client' 'the hosted reason still names the joiners'
    Assert-Equal 'hostie' ($b.HostNames -join ',') 'HostNames lists the hosting instance'

    # A host with no log yet: connected count unknown, never a crash.
    Reset-TestHome
    New-TestInstance -Name 'hostie' -Role 'host' | Out-Null
    $b = Get-RigBusySignal
    Assert-True  $b.HostLive 'a host with no log is still a live host'
    Assert-Match $b.Detail 'hostie=HOST \(connected clients unknown\)' 'a host with no log reports the count as unknown, not as zero'

    # Graceful degradation: instances provisioned before the manifest had a role.
    Reset-TestHome
    New-TestInstance -Name 'legacy' -NoManifest | Out-Null
    $b = Get-RigBusySignal
    Assert-True  $b.Busy 'an instance with no manifest is still busy (liveness does not depend on the role field)'
    Assert-False $b.HostLive 'an instance with no manifest is not assumed to be a host'
    Assert-Match $b.Detail 'legacy=role unknown' 'an instance with no manifest reports role unknown'

    Reset-TestHome
    New-TestInstance -Name 'legacy' -BrokenManifest | Out-Null
    Assert-NoThrow { Get-RigBusySignal } 'a half-written manifest does not throw'
    Assert-Match (Get-RigBusySignal).Detail 'legacy=role unknown' 'a half-written manifest degrades to role unknown'

    Reset-TestHome
    New-TestInstance -Name 'noRole' | Out-Null
    Assert-Match (Get-RigBusySignal).Detail 'noRole=role unknown' 'a manifest without a role field degrades to role unknown'

    # Both clauses compose.
    Reset-TestHome
    New-TestDediServer -ProcessId $PID -LogLines @('Client Dev (76561) is ready')
    New-TestInstance -Name 'alpha' -Role 'client' | Out-Null
    $b = Get-RigBusySignal
    Assert-Match $b.Detail 'dedicated server' 'composed reason keeps the dedi clause'
    Assert-Match $b.Detail 'client instance' 'composed reason keeps the client clause'
    Assert-Match $b.Detail ';' 'composed reason joins the two clauses'
}

function Test-ProcessIdentity {
    if (-not (Test-SectionSelected 'identity')) { return }
    Start-Section 'process identity (a stale pid must not hold the rig forever)'
    Reset-TestHome

    # Windows recycles process ids and the rig's pid files outlive their
    # processes on a force-kill or a reboot, so a bare number is not proof of
    # life. $PID here stands in for a recycled id: a real, live process that is
    # simply not the game.
    Assert-True  ($null -ne (Get-RigLiveProcess -TargetPid $PID)) 'fixture check: this process is alive'
    Assert-True  ($null -eq (Get-RigLiveProcess -TargetPid $PID -ImageName 'rocketstation')) 'a live process with the wrong image is not the game'
    Assert-True  ($null -ne (Get-RigLiveProcess -TargetPid $PID -ImageName 'pwsh')) 'a live process with the right image is accepted'
    Assert-True  ($null -eq (Get-RigLiveProcess -TargetPid $script:DeadPid -ImageName 'pwsh')) 'a dead pid is never alive'
    Assert-True  ($null -eq (Get-RigLiveProcess -TargetPid $null -ImageName 'pwsh')) 'a null pid is never alive'

    # The whole loop, end to end: an expired foreign lock plus a stale server.pid
    # whose number now belongs to something else. Before the identity check this
    # reported busy, the expired lock self-renewed, and no timer could ever
    # reclaim the rig.
    Reset-TestHome
    Initialize-RigLockPaths -RigHome $script:TempRoot   # real image names
    Set-TestLock -Owner 'GHOST001' -AgeMinutes 30
    New-TestDediServer -ProcessId $PID -LogLines @('Client Alice (111) is ready')
    Assert-False (Get-RigBusySignal).Busy 'a recycled server pid does not make the rig busy'
    Assert-Equal 'DeadForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'OTHER').State }) `
        'an expired lock backed only by a recycled server pid is reclaimable, not immortal'

    New-TestInstance -Name 'ghost' -Role 'client' | Out-Null
    Assert-False (Get-RigBusySignal).Busy 'a recycled client pid does not make the rig busy'
    Assert-Equal 'DeadForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'OTHER').State }) `
        'an expired lock backed only by a recycled client pid is reclaimable, not immortal'

    # And the same fixtures DO report busy once the image names match, so the
    # check above is a real discrimination and not a blanket false.
    Use-TestPaths
    Assert-True (Get-RigBusySignal).Busy 'the same pid files report busy when the image name matches'
    Assert-Equal 'LiveForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'OTHER').State }) `
        'a genuinely busy rig still keeps an expired lock alive'
}

function Test-Orphans {
    if (-not (Test-SectionSelected 'orphans')) { return }
    Start-Section 'orphan processes (untracked, reported, never busy)'
    Reset-TestHome

    Assert-Equal 0 (@(Get-RigOrphanProcesses)).Count 'no orphans when nothing runs out of a rig tree'

    # Point the instance root at the folder pwsh lives in. Every untracked pwsh on
    # the machine now looks like an instance tree process, including this one,
    # which makes a deterministic fixture out of a real live process.
    $pwshDir = Split-Path -Parent (Get-Process -Id $PID).Path
    Initialize-RigLockPaths -RigHome $script:TempRoot -ServerImageName 'pwsh' -ClientImageName 'pwsh' -InstanceRoot $pwshDir

    $orphans = @(Get-RigOrphanProcesses)
    Assert-True (($orphans | Where-Object { $_.ProcessId -eq $PID }).Count -eq 1) 'an untracked process inside a rig tree is reported as an orphan'
    Assert-Equal 'rig' ($orphans | Where-Object { $_.ProcessId -eq $PID }).Scope 'an orphan inside a rig tree is scoped rig'

    # Tracked processes are not orphans.
    New-TestInstance -Name 'tracked' -Role 'client' | Out-Null
    $orphans = @(Get-RigOrphanProcesses)
    Assert-True (($orphans | Where-Object { $_.ProcessId -eq $PID }).Count -eq 0) 'a process claimed by a game.pid is not an orphan'

    Reset-TestHome
    New-TestDediServer -ProcessId $PID
    Assert-True ((@(Get-RigOrphanProcesses) | Where-Object { $_.ProcessId -eq $PID }).Count -eq 0) 'a process claimed by server.pid is not an orphan'

    # An orphan is reported but never counted as busy: an untracked process no
    # launcher can stop must not be able to pin the lock live forever.
    Reset-TestHome
    $b = Get-RigBusySignal
    Assert-False $b.Busy 'orphans do NOT make the rig busy'
    Assert-True  ($b.Orphans.Count -ge 1) 'orphans are still reported on the busy signal object'
    Assert-Match $b.Detail 'UNTRACKED rig game process' 'the reason text names untracked processes'
    Assert-Match $b.Detail 'not counted as busy' 'the reason text says explicitly that they are not busy'

    Set-TestLock -Owner 'GHOST001' -AgeMinutes 30
    Assert-Equal 'DeadForeign' (Invoke-Quiet { (Get-RigLockState -CallerId 'OTHER').State }) `
        'an expired lock is still reclaimable while orphans exist (they cannot pin the rig)'

    Assert-NoThrow { Write-RigOrphanWarning } 'the orphan warning renders without throwing'
    Assert-NoThrow { Write-RigLockStatus -CallerId 'OTHER' } '-Status renders the orphan warning without throwing'

    # The developer's own client is never reported: same image name, different
    # install root.
    Use-TestPaths
    Assert-Equal 0 (@(Get-RigOrphanProcesses)).Count 'a game process outside every rig tree is not reported (that is the developer own client)'
}

function Test-MeasurePlayers {
    if (-not (Test-SectionSelected 'players')) { return }
    Start-Section 'Measure-PlayersInLog'
    Reset-TestHome

    Assert-Equal 0 (Measure-PlayersInLog (Join-Path $script:TempRoot 'nope.log')) 'a missing file counts zero'
    Assert-Equal 0 (Measure-PlayersInLog '') 'an empty path counts zero and does not prompt'
    Assert-Equal 0 (Measure-PlayersInLog $null) 'a null path counts zero and does not prompt'

    Assert-Equal 2 (Measure-PlayersInLog (New-TestLogFile @(
        'Client Alice (111) is ready'
        'Client Bob (222) is ready'
    ))) 'counts ready events'

    Assert-Equal 1 (Measure-PlayersInLog (New-TestLogFile @(
        'Client Alice (111) is ready'
        'Client Bob (222) is ready'
        'Client disconnected: Bob'
    ))) 'subtracts disconnects'

    Assert-Equal 0 (Measure-PlayersInLog (New-TestLogFile @(
        'Client Alice (111) is ready'
        'Client disconnected: Alice'
        'Client disconnected: ghost'
        'Client disconnected: ghost2'
    ))) 'floors at zero when disconnects exceed readies'

    Assert-Equal 1 (Measure-PlayersInLog (New-TestLogFile @(
        'Loading world Lunar'
        'Client Alice (111) is ready'
        'Setting up 3 worker threads'
        'ready to serve'
        'the client is ready for something else entirely'
    ))) 'unrelated log lines do not move the count'

    Assert-Equal 0 (Measure-PlayersInLog (New-TestLogFile @())) 'an empty log counts zero'
}

function Test-FileFormat {
    if (-not (Test-SectionSelected 'format')) { return }
    Start-Section 'file format'
    Reset-TestHome

    $f = Get-RigLockFilePath
    Set-Content -LiteralPath $f -Value @(
        '# a comment'
        ''
        '   '
        'owner=ABC12345'
        '# another comment'
        'purpose=some purpose'
    ) -Encoding utf8
    $lock = Read-RigLock
    Assert-Equal 'ABC12345' $lock['owner'] 'comments and blank lines are ignored'
    Assert-Equal 'some purpose' $lock['purpose'] 'values parse after skipped lines'

    Set-Content -LiteralPath $f -Value @('purpose=orphan', 'ttl_minutes=10') -Encoding utf8
    Assert-True ($null -eq (Read-RigLock)) 'a file with no owner key is treated as no lock'
    Assert-Equal 'None' (Invoke-Quiet { (Get-RigLockState -CallerId 'X').State }) 'a file with no owner key reports state None'

    Set-Content -LiteralPath $f -Value '' -Encoding utf8
    Assert-NoThrow { Read-RigLock } 'an empty file does not throw'
    Assert-True ($null -eq (Read-RigLock)) 'an empty file is treated as no lock'

    Set-Content -LiteralPath $f -Value 'total garbage with no equals sign at all' -Encoding utf8
    Assert-NoThrow { Read-RigLock } 'a torn file does not throw'
    Assert-True ($null -eq (Read-RigLock)) 'a torn file is treated as no lock'

    Set-Content -LiteralPath $f -Value @('owner=ABC12345', 'purpose=a=b=c and = signs') -Encoding utf8
    Assert-Equal 'a=b=c and = signs' (Read-RigLock)['purpose'] 'a value containing = survives the read (split on the first = only)'

    Reset-TestHome
    Write-RigLock ([ordered]@{
        owner = 'RT123456'; purpose = 'round trip = test'
        acquired_at = '2026-01-01T00:00:00Z'; refreshed_at = '2026-01-02T03:04:05Z'
        ttl_minutes = 42; host = 'SOMEHOST'
    })
    $rt = Read-RigLock
    Assert-Equal 'RT123456'             $rt['owner']        'round trip: owner'
    Assert-Equal 'round trip = test'    $rt['purpose']      'round trip: purpose with an = in it'
    Assert-Equal '2026-01-01T00:00:00Z' $rt['acquired_at']  'round trip: acquired_at'
    Assert-Equal '2026-01-02T03:04:05Z' $rt['refreshed_at'] 'round trip: refreshed_at'
    Assert-Equal '42'                   $rt['ttl_minutes']  'round trip: ttl_minutes'
    Assert-Equal 'SOMEHOST'             $rt['host']         'round trip: host'

    $stray = @(Get-ChildItem -Path $script:TempRoot -Filter 'session.lock.*.tmp' -File -ErrorAction SilentlyContinue)
    Assert-Equal 0 $stray.Count 'Write-RigLock leaves no staging file behind'
}

function Test-ReleaseOrdering {
    if (-not (Test-SectionSelected 'release')) { return }
    Start-Section 'release ordering and the -Unlock host refusal (3.5)'
    Reset-TestHome

    # The two halves of the ordering dependency that -Stop -Release relies on.
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 30
    New-TestInstance -Name 'hostie' -Role 'host' | Out-Null
    $lockRaw = Read-RigLock

    Assert-True (Test-RigLockReleasableOnStop -Lock $lockRaw -CallerId 'BBB22222') `
        'HAZARD: the release predicate alone WOULD release an expired foreign lock, busy rig or not'

    $st = Invoke-Quiet { Get-RigLockState -CallerId 'BBB22222' }
    Assert-Equal 'LiveForeign' $st.State `
        'GUARD: Get-RigLockState first reports LiveForeign for the same expired-but-busy lock'

    # Composed in the shipped order, the guard fires before the predicate is ever
    # consulted, so a busy foreign session keeps its lock. Composed the other way
    # round, the lock would be gone. That is the whole reason the order is pinned.
    $releasedInShippedOrder = $false
    if ((Invoke-Quiet { (Get-RigLockState -CallerId 'BBB22222').State }) -ne 'LiveForeign') {
        $releasedInShippedOrder = (Test-RigLockReleasableOnStop -Lock $lockRaw -CallerId 'BBB22222')
    }
    Assert-False $releasedInShippedOrder 'shipped order (state check, then release) does NOT release a busy foreign lock'

    $releasedInWrongOrder = (Test-RigLockReleasableOnStop -Lock $lockRaw -CallerId 'BBB22222')
    Assert-True $releasedInWrongOrder 'reversed order WOULD release it, which is why the order is documented and pinned'

    # The predicate's own truth table.
    Reset-TestHome
    Assert-True  (Test-RigLockReleasableOnStop -Lock $null -CallerId 'X') 'release predicate: no lock is releasable'
    Set-TestLock -Owner 'AAA11111' -AgeMinutes 0
    Assert-True  (Test-RigLockReleasableOnStop -Lock (Read-RigLock) -CallerId 'AAA11111') 'release predicate: your own lock is releasable'
    Assert-False (Test-RigLockReleasableOnStop -Lock (Read-RigLock) -CallerId 'BBB22222') 'release predicate: a fresh foreign lock is NOT releasable'
    Assert-True  (Test-RigLockReleasableOnStop -Lock (Read-RigLock) -CallerId 'BBB22222' -BreakLock) 'release predicate: -BreakLock releases a fresh foreign lock'

    # -Unlock refuses while a host is live, and -Force is the override.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111'
    New-TestInstance -Name 'hostie' -Role 'host' | Out-Null
    Assert-Throws { Remove-RigLock -CallerId 'AAA11111' } '-Unlock refuses while a listen-host instance is live' 'listen-host instance is still live'
    Assert-True (Test-Path -LiteralPath (Get-RigLockFilePath)) 'the refused unlock left the lock in place'
    Assert-NoThrow { Remove-RigLock -CallerId 'AAA11111' -Force } '-Force overrides the live-host refusal'
    Assert-False (Test-Path -LiteralPath (Get-RigLockFilePath)) '-Unlock -Force released the lock'

    # A live joiner is not a host, so it does not trigger the refusal.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111'
    New-TestInstance -Name 'joiner' -Role 'client' | Out-Null
    Assert-NoThrow { Remove-RigLock -CallerId 'AAA11111' } '-Unlock is not blocked by a non-host instance'

    # Ownership is still checked first: -Force is not a back door.
    Reset-TestHome
    Set-TestLock -Owner 'AAA11111'
    New-TestInstance -Name 'hostie' -Role 'host' | Out-Null
    Assert-Throws { Remove-RigLock -CallerId 'BBB22222' -Force } '-Force does not let a non-owner unlock even past the host refusal' 'Unlock refused'
    Assert-NoThrow { Remove-RigLock -CallerId 'BBB22222' -BreakLock -Force } '-BreakLock plus -Force is the authorized path through both refusals'
}

function Test-Queueing {
    if (-not (Test-SectionSelected 'queue')) { return }
    Start-Section 'queueing (3.3 -WaitSeconds)'
    Reset-TestHome

    Assert-True ((Get-Command New-RigLock).Parameters.ContainsKey('WaitSeconds')) 'New-RigLock takes -WaitSeconds'
    $waitAttr = (Get-Command New-RigLock).Parameters['WaitSeconds'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ParameterAttribute] } | Select-Object -First 1
    Assert-False $waitAttr.Mandatory '-WaitSeconds is optional, so today the callers that do not pass it keep the old behaviour'

    Set-TestLock -Owner 'AAA11111'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-Throws { New-RigLock -Purpose 'x' -Tool 't' } '-WaitSeconds defaults to 0 and fails immediately' 'locked by another session'
    $sw.Stop()
    Assert-True ($sw.Elapsed.TotalSeconds -lt 3) 'the default acquisition does not wait' "took $([int]$sw.Elapsed.TotalSeconds)s"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-Throws { New-RigLock -Purpose 'x' -Tool 't' -WaitSeconds 4 -PollSeconds 1 } '-WaitSeconds N gives up after N seconds' 'after waiting 4s'
    $sw.Stop()
    Assert-True ($sw.Elapsed.TotalSeconds -ge 3.5) 'the bounded wait actually waited' "took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    Assert-True ($sw.Elapsed.TotalSeconds -lt 20) 'the bounded wait is bounded, not infinite' "took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"

    if ($SkipConcurrency) {
        Write-Host '  skip  queue: acquisition succeeds when the holder releases mid-wait (-SkipConcurrency)'
        return
    }

    # A second process releases while we are queued: the wait must convert into a
    # successful acquisition rather than run out the clock.
    Reset-TestHome
    Set-TestLock -Owner 'HOLDER01'
    $res = Join-Path $script:TempRoot 'release-after.txt'
    $proc = Start-ChildProcess -Role 'release-after' -ResultFile $res -Owner 'HOLDER01' -DelaySeconds 3
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $owner = $null
    Assert-NoThrow { $script:QueuedOwner = New-RigLock -Purpose 'queued acquisition' -Tool 't' -WaitSeconds 30 -PollSeconds 1 } `
        'a queued acquisition succeeds once the holder releases'
    $sw.Stop()
    $proc.WaitForExit(20000) | Out-Null
    Assert-True ($script:QueuedOwner -and $script:QueuedOwner -ne 'HOLDER01') 'the queued acquisition minted its own owner id'
    Assert-True ($sw.Elapsed.TotalSeconds -ge 2) 'the queued acquisition really waited for the release' "took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    Assert-True ($sw.Elapsed.TotalSeconds -lt 25) 'the queued acquisition returned promptly after the release' "took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
}

# =============================================================================
# Concurrency
# =============================================================================

function Start-ChildProcess {
    param(
        [Parameter(Mandatory)] [string] $Role,
        [Parameter(Mandatory)] [string] $ResultFile,
        [string] $Gate,
        [string] $Owner,
        [int] $Iterations = 25,
        [int] $DelaySeconds = 2
    )
    Remove-Item -LiteralPath $ResultFile -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$ResultFile.ready" -Force -ErrorAction SilentlyContinue
    $args = @(
        '-NoProfile', '-NonInteractive', '-File', $PSCommandPath
        '-ChildRole', $Role
        '-ChildHome', $script:TempRoot
        '-ChildResult', $ResultFile
        '-ChildIterations', "$Iterations"
        '-ChildDelaySeconds', "$DelaySeconds"
    )
    if ($Gate)  { $args += @('-ChildGate', $Gate) }
    if ($Owner) { $args += @('-ChildOwner', $Owner) }
    # -NoNewWindow inherits this console rather than allocating one, so nothing
    # flashes and nothing claims foreground focus (the rig rule applies here too).
    return Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $args -NoNewWindow -PassThru `
        -RedirectStandardOutput "$ResultFile.out" -RedirectStandardError "$ResultFile.err"
}

function Wait-ChildrenReady {
    param([string[]] $ResultFiles, [int] $TimeoutSeconds = 90)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $ready = @($ResultFiles | Where-Object { Test-Path -LiteralPath "$_.ready" })
        if ($ready.Count -eq $ResultFiles.Count) { return $true }
        Start-Sleep -Milliseconds 50
    }
    return $false
}

function Invoke-RaceRound {
    # One round: N processes, all warm and blocked on the same gate, released
    # together, each trying to acquire the same fresh lock.
    param([int] $Count, [string] $Role)
    Reset-TestHome
    $gateName = "Local\riglock-test-gate-" + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $gate = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $gateName)
    try {
        $files = @()
        $procs = @()
        for ($i = 0; $i -lt $Count; $i++) {
            $f = Join-Path $script:TempRoot "race-$i.txt"
            $files += $f
            $procs += Start-ChildProcess -Role $Role -ResultFile $f -Gate $gateName
        }
        if (-not (Wait-ChildrenReady -ResultFiles $files)) {
            foreach ($p in $procs) { try { $p.Kill() } catch { } }
            return [pscustomobject]@{ Winners = -1; Ghosts = -1; Note = 'children never reported ready' }
        }
        $gate.Set() | Out-Null
        foreach ($p in $procs) { $p.WaitForExit(60000) | Out-Null }

        $results = foreach ($f in $files) {
            if (Test-Path -LiteralPath $f) { (Get-Content -Raw -LiteralPath $f).Trim() } else { 'ERR no result file' }
        }
        $wins  = @($results | Where-Object { $_ -like 'WIN *' })
        $final = Read-RigLock
        $finalOwner = if ($final) { $final['owner'] } else { '<none>' }
        # A "ghost" is the exact failure mode this fix is about: a process that
        # believes it holds the rig while the file names somebody else.
        $ghosts = @($wins | Where-Object { ($_ -replace '^WIN ', '') -ne $finalOwner })
        return [pscustomobject]@{
            Winners     = $wins.Count
            Ghosts      = $ghosts.Count
            FinalOwner  = $finalOwner
            Results     = $results
        }
    }
    finally {
        $gate.Dispose()
    }
}

function Test-Concurrency {
    if (-not (Test-SectionSelected 'concurrency')) { return }
    if ($SkipConcurrency) { Start-Section 'concurrency (SKIPPED)'; return }
    Start-Section "concurrency: $Rounds rounds x $Contenders contenders"

    if ($MeasurePreFix) {
        Write-Host "  measuring the PRE-FIX implementation (read-then-write, no critical section)..."
        $preMulti = 0
        $preGhost = 0
        $preNoWin = 0
        $preDist  = @{}
        $preSample = ''
        for ($r = 1; $r -le $Rounds; $r++) {
            $o = Invoke-RaceRound -Count $Contenders -Role 'acquire-prefix'
            $preDist["$($o.Winners)"] = 1 + [int]$preDist["$($o.Winners)"]
            if ($o.Winners -gt 1) { $preMulti++ }
            if ($o.Winners -lt 1) { $preNoWin++; if (-not $preSample) { $preSample = ($o.Results -join ' | ') } }
            $preGhost += [Math]::Max(0, $o.Ghosts)
        }
        $dist = ($preDist.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) winner(s): $($_.Value) round(s)" }) -join ', '
        Write-Host "  PRE-FIX  rounds with more than one winner: $preMulti / $Rounds   ghost holders: $preGhost"
        Write-Host "  PRE-FIX  distribution: $dist"
        $script:PreFixMulti  = $preMulti
        $script:PreFixGhosts = $preGhost
        $script:PreFixDist   = $dist

        # The BASELINE is asserted, the bug rate is not.
        #
        # A round where nobody wins means the contenders never ran (a typo, a
        # missing function, a child that died on startup), and a broken baseline
        # reads exactly like a clean one: zero double-winners. That failure mode
        # already bit this suite once, so it is a hard failure now.
        #
        # The double-winner COUNT stays a reported number rather than an
        # assertion: the pre-fix race is probabilistic, and a suite that fails
        # because an old bug did not reproduce on a quiet machine is worse than
        # one that prints the measurement.
        Assert-Equal 0 $preNoWin "PRE-FIX baseline is valid: every round produced at least one winner. First bad round: $preSample"
    }

    Write-Host "  measuring the FIXED implementation..."
    $multi   = 0
    $ghosts  = 0
    $noWin   = 0
    $dist    = @{}
    $badRounds = @()
    for ($r = 1; $r -le $Rounds; $r++) {
        $o = Invoke-RaceRound -Count $Contenders -Role 'acquire'
        $dist["$($o.Winners)"] = 1 + [int]$dist["$($o.Winners)"]
        if ($o.Winners -gt 1) { $multi++;  $badRounds += "round ${r}: $($o.Winners) winners, results: $($o.Results -join ' | ')" }
        if ($o.Winners -lt 1) { $noWin++;  $badRounds += "round ${r}: no winner, results: $($o.Results -join ' | ')" }
        $ghosts += [Math]::Max(0, $o.Ghosts)
    }
    $distText = ($dist.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name) winner(s): $($_.Value) round(s)" }) -join ', '
    Write-Host "  FIXED    distribution: $distText"
    $script:FixedDist = $distText

    Assert-Equal 0 $multi  "no round produced more than one winner ($Rounds rounds x $Contenders contenders)"
    Assert-Equal 0 $noWin  'every round produced a winner (the lock is never left unowned)'
    Assert-Equal 0 $ghosts 'no process walked away holding an owner id that is not in the lock file'
    if ($badRounds.Count -gt 0) { $badRounds | ForEach-Object { Write-Host "        $_" } }
}

function Test-ConcurrentMutation {
    if (-not (Test-SectionSelected 'concurrency')) { return }
    if ($SkipConcurrency) { return }
    Start-Section 'concurrency: refresh storm, unlock race, abandoned mutex'

    # 1. Concurrent refreshes by the owner must not corrupt the file.
    Reset-TestHome
    $owner = Invoke-Quiet { New-RigLock -Purpose 'refresh storm' -Tool 't' }
    $gateName = "Local\riglock-test-gate-" + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $gate = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $gateName)
    $files = @()
    $procs = @()
    for ($i = 0; $i -lt 4; $i++) {
        $f = Join-Path $script:TempRoot "refresh-$i.txt"
        $files += $f
        $procs += Start-ChildProcess -Role 'refresh' -ResultFile $f -Gate $gateName -Owner $owner -Iterations 25
    }
    $ready = Wait-ChildrenReady -ResultFiles $files
    Assert-True $ready 'refresh storm: all children started'
    $gate.Set() | Out-Null
    foreach ($p in $procs) { $p.WaitForExit(120000) | Out-Null }
    $gate.Dispose()

    $results = foreach ($f in $files) { if (Test-Path -LiteralPath $f) { (Get-Content -Raw -LiteralPath $f).Trim() } else { 'ERR' } }
    Assert-Equal 4 (@($results | Where-Object { $_ -like 'OK *' }).Count) "refresh storm: all 4 refreshers completed (results: $($results -join ' | '))"
    $lock = Read-RigLock
    Assert-True ($null -ne $lock) 'refresh storm: the lock file is still readable'
    Assert-Equal $owner $lock['owner'] 'refresh storm: the owner survived 100 concurrent refreshes'
    Assert-Equal 8 $lock.Count 'refresh storm: the lock still has exactly its 8 fields'
    Assert-False (Test-RigLockTimerExpired $lock) 'refresh storm: the timer is fresh afterwards'
    $stray = @(Get-ChildItem -Path $script:TempRoot -Filter 'session.lock.*.tmp' -File -ErrorAction SilentlyContinue)
    Assert-Equal 0 $stray.Count 'refresh storm: no staging files left behind'

    # 2. Concurrent unlock and mutating action must not leave a half-state.
    Reset-TestHome
    $owner = Invoke-Quiet { New-RigLock -Purpose 'unlock race' -Tool 't' }
    $gateName = "Local\riglock-test-gate-" + [guid]::NewGuid().ToString('N').Substring(0, 10)
    $gate = [System.Threading.EventWaitHandle]::new($false, [System.Threading.EventResetMode]::ManualReset, $gateName)
    $fu = Join-Path $script:TempRoot 'race-unlock.txt'
    $fa = Join-Path $script:TempRoot 'race-assert.txt'
    $pu = Start-ChildProcess -Role 'unlock' -ResultFile $fu -Gate $gateName -Owner $owner
    $pa = Start-ChildProcess -Role 'assert' -ResultFile $fa -Gate $gateName -Owner $owner
    Assert-True (Wait-ChildrenReady -ResultFiles @($fu, $fa)) 'unlock race: both children started'
    $gate.Set() | Out-Null
    $pu.WaitForExit(60000) | Out-Null
    $pa.WaitForExit(60000) | Out-Null
    $gate.Dispose()
    $ru = (Get-Content -Raw -LiteralPath $fu).Trim()
    $ra = (Get-Content -Raw -LiteralPath $fa).Trim()
    Assert-Match $ru '^OK' "unlock race: the unlock completed (got: $ru)"
    Assert-True ($ra -like 'OK *' -or $ra -like 'LOSE*No rig session lock*') `
        "unlock race: the mutating action either ran before the unlock or failed cleanly (got: $ra)"
    Assert-False (Test-Path -LiteralPath (Get-RigLockFilePath)) 'unlock race: the final state is released, never a half-written lock'
    $stray = @(Get-ChildItem -Path $script:TempRoot -Filter 'session.lock.*.tmp' -File -ErrorAction SilentlyContinue)
    Assert-Equal 0 $stray.Count 'unlock race: no staging files left behind'

    # 3. A process killed while holding the mutex must not deadlock the next one.
    Reset-TestHome
    $fh = Join-Path $script:TempRoot 'hold-mutex.txt'
    $ph = Start-ChildProcess -Role 'hold-mutex' -ResultFile $fh
    Assert-True (Wait-ChildrenReady -ResultFiles @($fh)) 'abandoned mutex: the holder took the critical section'
    try { $ph.Kill() } catch { }
    $ph.WaitForExit(30000) | Out-Null
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Assert-NoThrow { $script:AbandonedOwner = New-RigLock -Purpose 'after an abandoned mutex' -Tool 't' } `
        'abandoned mutex: the next acquirer is not deadlocked'
    $sw.Stop()
    Assert-True ($sw.Elapsed.TotalSeconds -lt 10) 'abandoned mutex: recovery is immediate, not a timeout' "took $([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    Assert-Equal $script:AbandonedOwner (Read-RigLock)['owner'] 'abandoned mutex: the recovered acquisition wrote a coherent lock'
}

# =============================================================================
# Pre-fix implementation, kept only as the measurement baseline
# =============================================================================
# This is the acquisition path as it stood before the critical section existed:
# read the state, decide, then write, with nothing serialising the three steps.
# It is here so the concurrency section can put a number on the bug it fixes
# instead of asserting that a bug used to exist. Nothing in the live library
# calls it, and nothing should.
#
# It writes through the CURRENT Write-RigLock, which stages and swaps atomically.
# The original wrote through Move-Item -Force, which is if anything more prone to
# interleaving, so any double-winner count measured here is a lower bound on the
# original behaviour.

function Get-PreFixRigLockState {
    param([string] $CallerId)
    $lock = Read-RigLock
    if (-not $lock) { return [pscustomobject]@{ State = 'None'; Lock = $null; Busy = $null } }
    if ($CallerId -and $lock['owner'] -eq $CallerId) {
        return [pscustomobject]@{ State = 'Mine'; Lock = $lock; Busy = $null }
    }
    if (-not (Test-RigLockTimerExpired $lock)) {
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Busy = $null }
    }
    $busy = Get-RigBusySignal
    if ($busy.Busy) {
        $lock['refreshed_at'] = Get-RigNowUtc
        Write-RigLock $lock
        return [pscustomobject]@{ State = 'LiveForeign'; Lock = $lock; Busy = $busy.Detail }
    }
    return [pscustomobject]@{ State = 'DeadForeign'; Lock = $lock; Busy = $null }
}

function New-PreFixRigLock {
    param(
        [Parameter(Mandatory)] [string] $Purpose,
        [string] $CallerId,
        [int] $TtlMinutes = 10,
        [switch] $BreakLock,
        [Parameter(Mandatory)] [string] $Tool
    )
    $st = Get-PreFixRigLockState -CallerId $CallerId
    switch ($st.State) {
        'Mine' {
            $owner = $st.Lock['owner']
            Write-RigLock ([ordered]@{
                owner = $owner; purpose = $Purpose
                acquired_at = $st.Lock['acquired_at']; refreshed_at = (Get-RigNowUtc)
                ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
            })
            return $owner
        }
        'LiveForeign' {
            if (-not $BreakLock) { throw "Cannot acquire: the test rig is locked by another session." }
        }
    }
    $owner = [guid]::NewGuid().ToString('N').Substring(0, 8)
    Write-RigLock ([ordered]@{
        owner = $owner; purpose = $Purpose
        acquired_at = (Get-RigNowUtc); refreshed_at = (Get-RigNowUtc)
        ttl_minutes = $TtlMinutes; host = $env:COMPUTERNAME
    })
    return $owner
}

# =============================================================================
# Run
# =============================================================================

# Child dispatch happens HERE, at the bottom, so every function a role calls is
# already defined. See the note on Invoke-ChildRole.
if ($ChildRole) { Invoke-ChildRole; return }

Write-Host 'TestRig session lock: offline test suite'
Write-Host "  library : $(Join-Path $PSScriptRoot 'rig-lock.ps1')"

# Fingerprint the real lock before doing anything, and verify it afterwards. The
# suite must never touch the rig it is testing.
if (Test-Path -LiteralPath $script:RealLock) {
    $script:RealBefore = (Get-FileHash -LiteralPath $script:RealLock -Algorithm SHA256).Hash
    Write-Host "  real lock present, sha256 $($script:RealBefore.Substring(0,16)) (verified untouched at the end)"
}
else {
    Write-Host '  real lock absent (verified still absent at the end)'
}

$testHome = New-TestHome
Write-Host "  temp    : $testHome"

try {
    Test-PathInjection
    Test-StateMachine
    Test-Ttl
    Test-IdleCeiling
    Test-DirtyMarker
    Test-Ownership
    Test-BreakLock
    Test-BusySignal
    Test-ProcessIdentity
    Test-Orphans
    Test-MeasurePlayers
    Test-FileFormat
    Test-ReleaseOrdering
    Test-Queueing
    Test-Concurrency
    Test-ConcurrentMutation
}
finally {
    Start-Section 'safety'
    if ($null -ne $script:RealBefore) {
        $now = if (Test-Path -LiteralPath $script:RealLock) { (Get-FileHash -LiteralPath $script:RealLock -Algorithm SHA256).Hash } else { '<deleted>' }
        Assert-Equal $script:RealBefore $now 'the REAL TestRig/session.lock was not modified by this run'
    }
    else {
        Assert-False (Test-Path -LiteralPath $script:RealLock) 'this run did not create a REAL TestRig/session.lock'
    }
    if ($script:TempRoot -and (Test-Path -LiteralPath $script:TempRoot)) {
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $script:TempRoot
    }
    Assert-False (Test-Path -LiteralPath $script:TempRoot) 'the temp home was cleaned up'
}

Write-Host ''
Write-Host ('-' * 64)
if ($script:PreFixDist) {
    Write-Host "PRE-FIX  concurrency: $script:PreFixMulti / $Rounds rounds had more than one winner; $script:PreFixGhosts ghost holders"
    Write-Host "PRE-FIX  distribution: $script:PreFixDist"
}
if ($script:FixedDist) {
    Write-Host "FIXED    distribution: $script:FixedDist"
}
Write-Host "assertions: $($script:Passed) passed, $($script:Failed) failed"
if ($script:Failed -gt 0) {
    Write-Host ''
    Write-Host 'failures:'
    $script:Failures | ForEach-Object { Write-Host "  - $_" }
    Write-Host ''
    Write-Host 'RESULT: FAIL'
    exit 1
}
Write-Host 'RESULT: PASS'
exit 0
