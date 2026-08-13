<#
.SYNOPSIS
    Offline test suite for the TestRig playtest harness (TestRig/playtest/playtest-lib.ps1).

.DESCRIPTION
    The harness decides whether a mod is broken, and a harness that gets that
    wrong is worse than no harness at all: a false 'fail' spends a developer's
    day on a bug that is not there, and a 'pass' against a stale binary is a
    green light for code nobody measured. So the three things this suite is
    really about are the outcome logic, the flake classification, and the
    guarantees that hold when something goes wrong (teardown, the lock, the
    binary gate, the evidence).

    It runs entirely offline: no game, no client instance, no network, no rig
    lock. The library reaches the world through two injected seams, and both are
    fakes here, so nothing can escape the temp directory. The suite refuses to
    start if the redirection did not take, and fingerprints the real rig's lock
    file before the run to prove it was never touched.

    No Pester, for the same reason TestRig/rig-lock.tests.ps1 has none: a
    dependency that has to be installed before the harness can be tested is a
    dependency that stops the harness from being tested.

    The clock and the sleep are injected too, so a 300 second readiness barrier
    and a 10 second retry gap cost nothing here and are still exercised exactly
    as they run for real.

.PARAMETER Section
    Run only sections whose name matches this wildcard. Default: all.
#>
[CmdletBinding()]
param(
    [string] $Section = '*'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

. (Join-Path $PSScriptRoot 'playtest-lib.ps1')

# =============================================================================
# Assert helpers (same shape as rig-lock.tests.ps1 and rig-reset.tests.ps1)
# =============================================================================

$script:Passed   = 0
$script:Failed   = 0
$script:Failures = @()
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

function Assert-FileExists {
    param([string] $Path, [string] $Name)
    Assert-True (Test-Path -LiteralPath $Path) $Name "missing: $Path"
}

function Invoke-Quiet { param([scriptblock] $Body) return (& $Body 6>$null 3>$null) }

function Test-SectionSelected { param([string] $Name) return ($Name -like $Section) }

# Run a body and report which of the three outcomes it produced, exactly the way
# the runner classifies it. Nearly every assertion below is about this answer.
function Get-Outcome {
    param([scriptblock] $Body)
    try { $null = & $Body 6>$null 3>$null; return 'pass' }
    catch { return (Resolve-PlaytestError $_).Outcome }
}

function Get-OutcomeRecord {
    param([scriptblock] $Body)
    try { $null = & $Body 6>$null 3>$null; return [pscustomobject]@{ Outcome = 'pass'; Detector = ''; Message = '' } }
    catch { return (Resolve-PlaytestError $_) }
}

# =============================================================================
# Fixtures: a fake rig
# =============================================================================
# Two seams, both fake. The transport answers a small state machine that models
# the parts of the control plane the harness actually depends on (readiness,
# hosting, the roster) and the launcher seam records every invocation so the
# teardown guarantees can be measured rather than asserted about.

$script:TempRoot  = $null
$script:RigHome   = $null
$script:SaveRoot  = $null
$script:RealLock  = Join-Path (Split-Path -Parent $PSScriptRoot) 'session.lock'
$script:RealBefore = $null
$script:Fake      = $null
$script:FakeNow   = [DateTime]::new(2026, 8, 11, 12, 0, 0, [DateTimeKind]::Utc)

function Reset-Fake {
    $script:FakeNow = [DateTime]::new(2026, 8, 11, 12, 0, 0, [DateTimeKind]::Utc)
    $script:Fake = @{
        Calls           = @()
        Requests        = @()
        LockExit        = 0
        LockOwner       = 'a1b2c3d4'
        LockStdOut      = ''
        UnlockExit      = 0
        StartExit       = @{}
        StopExit        = @{}
        ConnectFailures = 0
        ConnectAttempts = 0
        StuckBoot       = @{}
        HostSetsHosting = $true
        ConnectJoins    = $true
        # The failure Connect-RigJoiner exists for, and it is NOT the same as
        # ConnectJoins=$false: /connect answers ok, the joiner reaches inWorld,
        # and the HOST roster does not carry it until attempt N. 0 means the
        # roster grows on the first attempt, which is the healthy rig.
        RosterJoinsAtAttempt = 0
        ConsoleSeq      = 100
        ConfigEntries   = 4
        ThingValue      = '(1,1,1,1)'   # Thing 442's ExampleField, so a test can move it
        StatusBoots     = @{}     # instance -> how many /status calls before it reaches menu
        StatusCalls     = @{}
        State           = @{
            hostie = @{ phase = 'menu'; gameInitialized = $true; loadedPluginCount = 22; hosting = $false; role = 'menu'; hostPort = 0; connectedClients = @() }
            joiner = @{ phase = 'menu'; gameInitialized = $true; loadedPluginCount = 22; hosting = $false; role = 'menu'; hostPort = 0; connectedClients = @() }
        }
    }
}

function Get-FakeInstanceForPort {
    param([int] $Port)
    switch ($Port) {
        27701 { return 'hostie' }
        27702 { return 'joiner' }
        default { return '' }
    }
}

function New-FakeStatus {
    param([string] $Name)
    $s = $script:Fake.State[$Name]
    return [pscustomobject]@{
        instanceName      = $Name
        phase             = $s.phase
        gameInitialized   = $s.gameInitialized
        loadedPluginCount = $s.loadedPluginCount
        hosting           = $s.hosting
        role              = $s.role
        hostPort          = $s.hostPort
        connectedClients  = @($s.connectedClients)
        saveRootIsolated  = $true
    }
}

$script:FakeTransport = {
    param([int] $Port, [string] $Path, [string] $BodyJson, [int] $TimeoutSec)
    $name = Get-FakeInstanceForPort -Port $Port
    $bare = ($Path -split '\?')[0]
    $script:Fake.Requests += [pscustomobject]@{ Port = $Port; Instance = $name; Path = $Path; Body = $BodyJson }
    if (-not $name) { throw "fake transport: no instance on port $Port" }

    switch ($bare) {
        '/status' {
            $n = 1 + [int]$script:Fake.StatusCalls[$name]
            $script:Fake.StatusCalls[$name] = $n
            $boot = [int]$script:Fake.StatusBoots[$name]
            if ($boot -gt 0 -and $n -le $boot) {
                # Still booting: what a parked or slow instance looks like.
                return [pscustomobject]@{
                    instanceName = $name; phase = 'menu'; gameInitialized = $false
                    loadedPluginCount = 2; hosting = $false; role = 'menu'; hostPort = 0
                    connectedClients = @()
                }
            }
            return (New-FakeStatus -Name $name)
        }
        '/host' {
            if ($script:Fake.HostSetsHosting) {
                $script:Fake.State[$name].phase    = 'inWorld'
                $script:Fake.State[$name].hosting  = $true
                $script:Fake.State[$name].role     = 'listenHost'
                $script:Fake.State[$name].hostPort = 27801
                $script:Fake.State[$name].connectedClients = @([pscustomobject]@{ clientId = '900000000001'; username = $name; isHost = $true })
            }
            else {
                # The failure the taxonomy exists for: 200 with nothing behind it.
                $script:Fake.State[$name].phase = 'inWorld'
            }
            return [pscustomobject]@{ ok = $true; hostPort = 27801 }
        }
        '/connect' {
            $script:Fake.ConnectAttempts++
            if ($script:Fake.ConnectAttempts -le $script:Fake.ConnectFailures) {
                return [pscustomobject]@{ ok = $false; result = 'timeout' }
            }
            $script:Fake.State[$name].phase = 'inWorld'
            $script:Fake.State[$name].role  = 'joinedClient'
            if ($script:Fake.ConnectJoins -and $script:Fake.ConnectAttempts -ge $script:Fake.RosterJoinsAtAttempt) {
                $script:Fake.State['hostie'].connectedClients = @(@($script:Fake.State['hostie'].connectedClients) + [pscustomobject]@{ clientId = '900000000002'; username = $name; isHost = $false })
            }
            return [pscustomobject]@{ ok = $true; result = 'connected' }
        }
        '/config' {
            $entries = @(1..([int]$script:Fake.ConfigEntries) | ForEach-Object {
                [pscustomobject]@{ section = "Client - Group$([Math]::Ceiling($_ / 2))"; key = "Key$_"; value = 'x' }
            })
            return [pscustomobject]@{ guid = 'net.example'; count = @($entries).Count; entries = $entries }
        }
        '/thing' {
            # The real endpoint answers 400 with no refId/refIds, and a non-2xx
            # arrives here as a throw. The fake has to reproduce that: without
            # it, a re-read that silently dropped its ReaderArgs still looked
            # like a working read, which is exactly how Assert-RigChange shipped
            # unable to re-read anything that needs a query string.
            if ($Path -notmatch '(^|[?&])(refId|refIds|id|ids)=') {
                throw "fake transport: 400 :: pass 'refId' (a Thing ReferenceId) or 'refIds' (a comma-separated list)."
            }
            # Two Things: 442 was acted on, 445 is the untouched control. The
            # control still reads its PREFAB value, which is the trap that
            # produced a retracted conclusion in a live run.
            return [pscustomobject]@{
                ok = $true; instance = $name; requested = 2; found = 2; missing = @()
                things = @(
                    [pscustomobject]@{
                        instance = $name; requestedRefId = '442'; found = $true
                        fields = @(
                            [pscustomobject]@{ name = 'ExampleField'; ok = $true; value = $script:Fake.ThingValue; matchesPrefab = $false }
                            [pscustomobject]@{ name = 'OtherField';   ok = $true; value = '4';                     matchesPrefab = $false }
                        )
                    }
                    [pscustomobject]@{
                        instance = $name; requestedRefId = '445'; found = $true
                        fields = @(
                            [pscustomobject]@{ name = 'ExampleField'; ok = $true; value = '(1,1,1,1)'; matchesPrefab = $true }
                        )
                    }
                )
            }
        }
        '/dlc'         { return [pscustomobject]@{ ok = $true; owned = @('ExamplePack') } }
        '/console/log' {
            # nextSeq advances on every read, so a test can tell the sequence
            # taken before a retried connect from one taken before the first.
            $script:Fake.ConsoleSeq++
            return [pscustomobject]@{ nextSeq = $script:Fake.ConsoleSeq; count = 1; dropped = 0; lines = @('[Example] a console line') }
        }
        '/nearby'      { return [pscustomobject]@{ things = @([pscustomobject]@{ referenceId = 442; colorIndex = 4 }, [pscustomobject]@{ referenceId = 445; colorIndex = 4 }) } }
        '/player'      { return [pscustomobject]@{ position = [pscustomobject]@{ x = 1.5; y = 2.5; z = 3.5 } } }
        '/ping'        { return [pscustomobject]@{ ok = $true } }
        default        { throw "fake transport: nothing wired for $bare" }
    }
}

$script:FakeRigCommand = {
    param([string[]] $ArgList)
    $script:Fake.Calls += , @($ArgList)
    $action = if (@($ArgList).Count -gt 0) { "$($ArgList[0])" } else { '' }
    $target = ''
    for ($i = 0; $i -lt @($ArgList).Count - 1; $i++) {
        if ("$($ArgList[$i])" -eq '-Target') { $target = "$($ArgList[$i + 1])" }
    }
    switch ($action) {
        'lock' {
            $out = if ($script:Fake.LockStdOut) { $script:Fake.LockStdOut } else {
                @(
                    '[Lock] Acquired the rig session lock (covers BOTH TestRig halves).'
                    "[Lock]   owner   : $($script:Fake.LockOwner)   (pass -As $($script:Fake.LockOwner) on every mutating command)"
                    '[Reset] client hostie: deleted setting.xml, re-copied BepInEx/config, re-applied SavePathOverride'
                    '[Reset] client joiner: deleted setting.xml, re-copied BepInEx/config, re-applied SavePathOverride'
                    "TESTRIG-OWNER $($script:Fake.LockOwner)"
                ) -join "`n"
            }
            return [pscustomobject]@{ ExitCode = $script:Fake.LockExit; StdOut = $out; StdErr = '' }
        }
        'unlock' { return [pscustomobject]@{ ExitCode = $script:Fake.UnlockExit; StdOut = '[Unlock] released'; StdErr = '' } }
        'start'  {
            # A started instance comes up at the menu with an empty roster, which
            # is what makes a per-check lock and restart meaningful: check two
            # must not inherit check one's world.
            if ($target -and $script:Fake.State.ContainsKey($target)) {
                $stuck = [bool]$script:Fake.StuckBoot[$target]
                $script:Fake.State[$target] = @{
                    phase = 'menu'
                    gameInitialized = (-not $stuck)
                    loadedPluginCount = 22
                    hosting = $false; role = 'menu'; hostPort = 0; connectedClients = @()
                }
                $script:Fake.StatusCalls[$target] = 0
            }
            return [pscustomobject]@{ ExitCode = [int]$script:Fake.StartExit[$target]; StdOut = "[Start] $target"; StdErr = '' }
        }
        'stop'   { return [pscustomobject]@{ ExitCode = [int]$script:Fake.StopExit[$target];  StdOut = "[Stop] $target";  StdErr = '' } }
        default   { return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' } }
    }
}

function Get-FakeCallStrings {
    return @($script:Fake.Calls | ForEach-Object { ($_ -join ' ') })
}

function Use-TestPaths {
    Initialize-PlaytestLib `
        -RigHome $script:RigHome `
        -EvidenceRoot (Join-Path $script:TempRoot 'evidence') `
        -Tier1SaveRoot $script:SaveRoot `
        -Transport $script:FakeTransport `
        -RigCommand $script:FakeRigCommand `
        -Registry {
            # instancesRoot is what the bepinexlog reader resolves an instance
            # TREE through, and the trees normally sit on the game install's
            # volume rather than under TestRig/, so the fake carries it too.
            @(
                [pscustomobject]@{ instanceName = 'hostie'; port = 27701; role = 'host';   instancesRoot = (Join-Path $script:TempRoot 'instances') }
                [pscustomobject]@{ instanceName = 'joiner'; port = 27702; role = 'client'; instancesRoot = (Join-Path $script:TempRoot 'instances') }
            )
        } `
        -Clock { $script:FakeNow } `
        -Sleep { param([double] $Seconds) $script:FakeNow = $script:FakeNow.AddSeconds($Seconds) }
}

function New-TestHome {
    $root = Join-Path ([System.IO.Path]::GetTempPath()) ("playtest-tests-" + [guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $script:TempRoot = $root
    $script:RigHome  = Join-Path $root 'TestRig'
    $script:SaveRoot = Join-Path $root 'DeveloperSaves'
    New-Item -ItemType Directory -Force -Path (Join-Path $script:RigHome 'ClientRig\data') | Out-Null
    New-Item -ItemType Directory -Force -Path $script:SaveRoot | Out-Null
    Reset-Fake
    Use-TestPaths
    if ((Get-PlaytestEvidenceRoot) -notlike "$root*") {
        throw "SAFETY ABORT: Initialize-PlaytestLib did not repoint the evidence root. It is $(Get-PlaytestEvidenceRoot)."
    }
    if ((Get-PlaytestTier1SaveRoot) -ne $script:SaveRoot) {
        throw "SAFETY ABORT: the tier-1 save root was not redirected into the temp tree. It is $(Get-PlaytestTier1SaveRoot)."
    }
    return $root
}

function Reset-TestHome {
    Reset-Fake
    Use-TestPaths
    Clear-PlaytestChecks
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'evidence')
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:RigHome 'ClientRig\data')
    New-Item -ItemType Directory -Force -Path (Join-Path $script:RigHome 'ClientRig\data') | Out-Null
}

function New-TestInstanceData {
    # A provisioned instance as the launcher leaves it: data/<name>/provision.stamp
    # plus, optionally, a seeded mod DLL of a given size.
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Role = 'client',
        [switch] $NoStamp,
        [int] $DeployedBytes = 0,
        [string] $DeployedRelative = 'userdata\mods\Example\Example.dll'
    )
    $dir = Join-Path (Join-Path $script:RigHome 'ClientRig\data') $Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    if (-not $NoStamp) {
        ([ordered]@{
            instanceName = $Name; provisionedUtc = '2026-08-11T11:00:00Z'; role = $Role
            port = 27701; gamePort = 27801; tree = "X:\Rig\$Name"
            sourceInstall = 'X:\Game'; sourceVersion = '0.2.6403.27689'
            pluginBuiltUtc = '2026-08-10T09:00:00Z'; launcherHostname = 'TESTHOST'
        } | ConvertTo-Json -Depth 4) | Set-Content -LiteralPath (Join-Path $dir 'provision.stamp') -Encoding utf8
    }
    if ($DeployedBytes -gt 0) {
        $dll = Join-Path $dir $DeployedRelative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dll) | Out-Null
        [System.IO.File]::WriteAllBytes($dll, (New-Object byte[] $DeployedBytes))
    }
    return $dir
}

function New-TestBuildDll {
    param([int] $Bytes = 96768, [string] $FileName = 'Example.dll')
    $dir = Join-Path $script:TempRoot 'build'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $p = Join-Path $dir $FileName
    [System.IO.File]::WriteAllBytes($p, (New-Object byte[] $Bytes))
    return $p
}

function New-TestContext {
    param([string] $CheckName = 'a check', [switch] $NoEvidence)
    $dir = ''
    if (-not $NoEvidence) {
        $dir = New-PlaytestCheckEvidence -BundleRoot (Join-Path $script:TempRoot 'evidence') -Index 1 -CheckName $CheckName
    }
    $ctx = New-PlaytestContext -CheckName $CheckName -SuiteName 'tests' -EvidenceDir $dir -Instances @(
        @{ Name = 'hostie'; Role = 'host';   World = 'Lunar' }
        @{ Name = 'joiner'; Role = 'client'; ConnectTo = 'hostie' }
    )
    $ctx.Owner = $script:Fake.LockOwner
    $ctx.LastRefreshUtc = $script:FakeNow
    return $ctx
}

# =============================================================================
# Sections
# =============================================================================

function Test-Paths {
    if (-not (Test-SectionSelected 'paths')) { return }
    Start-Section 'paths and injection'
    Reset-TestHome

    Assert-Equal (Join-Path $script:TempRoot 'evidence') (Get-PlaytestEvidenceRoot) 'the evidence root follows the injected value'
    Assert-Equal $script:RigHome  (Get-PlaytestRigHome)      'the rig home follows the injected value'
    Assert-Equal $script:SaveRoot (Get-PlaytestTier1SaveRoot) 'the tier-1 save root follows the injected value'
    Assert-Match (Get-PlaytestInstanceStampPath -Name 'hostie') 'ClientRig.data.hostie.provision\.stamp' 'a provision stamp resolves under the injected rig home'

    # A partial re-initialisation must leave the seams it was not given alone,
    # or every test after the first would be driving a half-wired library.
    Initialize-PlaytestLib -EvidenceRoot (Join-Path $script:TempRoot 'other')
    Assert-Equal (Join-Path $script:TempRoot 'other') (Get-PlaytestEvidenceRoot) 'a partial re-init changes what it was given'
    Assert-Equal $script:RigHome (Get-PlaytestRigHome) 'a partial re-init leaves what it was not given'
    Use-TestPaths

    # Unwired seams are a hard error naming the composition root, never a silent
    # fallback that reaches the real network from an offline test.
    Initialize-PlaytestLib -Transport $null -RigCommand $null
    Assert-Throws { Invoke-PlaytestTransport -Port 1 -Path '/status' -BodyJson '' -TimeoutSec 1 } 'an unwired transport throws and names the runner' 'playtest\.ps1'
    Assert-Throws { Invoke-RigCommand -ArgList @('-Status') } 'an unwired launcher seam throws and names the runner' 'playtest\.ps1'
    Use-TestPaths
    Assert-NoThrow { Invoke-PlaytestTransport -Port 27701 -Path '/status' -BodyJson '' -TimeoutSec 1 } 'the wired transport answers again'
}

function Test-Primitives {
    if (-not (Test-SectionSelected 'primitives')) { return }
    Start-Section 'primitives (path selection, comparison, slugs)'
    Reset-TestHome

    $o = [pscustomobject]@{
        hosting = $true
        network = [pscustomobject]@{ role = 'listenHost'; hostPort = 27801 }
        connectedClients = @(
            [pscustomobject]@{ clientId = '900000000002'; username = 'joiner' }
            [pscustomobject]@{ clientId = '900000000003'; username = 'third' }
        )
    }
    Assert-Equal $true          (Select-PlaytestPath -Object $o -Path 'hosting')                    'a top-level field selects'
    Assert-Equal 'listenHost'   (Select-PlaytestPath -Object $o -Path 'network.role')               'a dotted path selects'
    Assert-Equal 2              (Select-PlaytestPath -Object $o -Path 'connectedClients.count')     'count on a collection selects'
    Assert-Equal 'joiner'       (Select-PlaytestPath -Object $o -Path 'connectedClients[0].username') 'an index into a collection selects'
    Assert-Equal 'third'        (Select-PlaytestPath -Object $o -Path 'connectedClients[1].username') 'a later index selects'
    Assert-True  ($null -eq (Select-PlaytestPath -Object $o -Path 'nope'))                          'a missing field reads null rather than throwing'
    Assert-True  ($null -eq (Select-PlaytestPath -Object $o -Path 'network.nope.deeper'))           'a missing field partway down reads null'
    Assert-True  ($null -eq (Select-PlaytestPath -Object $o -Path 'connectedClients[9].username'))  'an index past the end reads null'
    Assert-Equal $o             (Select-PlaytestPath -Object $o -Path '.')                          'a dot selects the whole object'
    Assert-Equal 'listenHost'   (Select-PlaytestPath -Object @{ role = 'listenHost' } -Path 'role') 'a hashtable selects by key'

    Assert-True  (Test-PlaytestValueEqual $true 'True')     'True as text equals $true (a JSON control plane answers in text)'
    Assert-True  (Test-PlaytestValueEqual $false 'False')   'False as text equals $false'
    Assert-True  (Test-PlaytestValueEqual 33 '33')          'a number equals its text'
    Assert-True  (Test-PlaytestValueEqual 'listenHost' 'LISTENHOST') 'strings compare case-insensitively'
    Assert-False (Test-PlaytestValueEqual 'listenHost' 'joinedClient') 'different strings are not equal'
    Assert-False (Test-PlaytestValueEqual $true $null)      'null is not true'
    Assert-True  (Test-PlaytestValueEqual $null $null)      'null equals null'
    Assert-False (Test-PlaytestValueEqual 33 34)            'different numbers are not equal'

    Assert-Equal 'the-host-side-glow-check' (ConvertTo-PlaytestSlug 'The host-side: glow check!') 'a slug is lower case and file-name safe'
    Assert-Equal 'unnamed' (ConvertTo-PlaytestSlug '')      'an empty name still produces a usable slug'
    Assert-Equal '/connect' (Get-PlaytestBarePath '/connect?timeoutMs=300000') 'a bare path drops the query string'
    Assert-Equal '/status'  (Get-PlaytestBarePath '/status/') 'a bare path drops a trailing slash'
    Assert-Equal '?guid=net.example' (ConvertTo-PlaytestQuery @{ guid = 'net.example' }) 'a query string is built and escaped'
    Assert-Equal '' (ConvertTo-PlaytestQuery @{}) 'no parameters means no query string'
}

function Test-Outcomes {
    if (-not (Test-SectionSelected 'outcome')) { return }
    Start-Section 'the three outcomes'
    Reset-TestHome

    Assert-Equal 'pass' (Get-Outcome { 1 + 1 }) 'a body that returns normally is a pass'

    $sig = New-PlaytestSignal -Kind 'fail' -Message 'the value was wrong' -Detector 'assertion'
    Assert-Equal 'fail' (Get-Outcome { throw $sig }) 'a fail signal classifies as fail'
    $sig2 = New-PlaytestSignal -Kind 'inconclusive' -Message 'the rig wobbled' -Detector 'boot-timeout'
    $r = Get-OutcomeRecord { throw $sig2 }
    Assert-Equal 'inconclusive' $r.Outcome  'an inconclusive signal classifies as inconclusive'
    Assert-Equal 'boot-timeout' $r.Detector 'the detector travels with the signal'

    # THE decision of the whole harness: anything unclassified is inconclusive.
    # A false fail costs a developer a day chasing a bug that is not there.
    $r = Get-OutcomeRecord { throw 'something nobody anticipated' }
    Assert-Equal 'inconclusive' $r.Outcome 'an unclassified throw is INCONCLUSIVE, never a fail'
    Assert-Equal 'unclassified-error' $r.Detector 'an unclassified throw is named as such rather than hidden'
    Assert-Match $r.Message 'inconclusive rather than a failure' 'the unclassified message says why it is not a failure'

    $r = Get-OutcomeRecord { $null.Nope() }
    Assert-Equal 'inconclusive' $r.Outcome 'a null-reference inside a check is inconclusive, not a mod defect'

    # A signal wrapped by PowerShell must keep its classification, or a real
    # assertion failure would silently become an inconclusive.
    $wrapped = [System.Exception]::new('outer', (New-PlaytestSignal -Kind 'fail' -Message 'inner' -Detector 'assertion'))
    Assert-Equal 'fail' (Get-Outcome { throw $wrapped }) 'a signal wrapped in another exception keeps its kind'

    Assert-Equal 'inconclusive' (Get-Outcome { Set-PlaytestInconclusive -Because 'the world had no pipes in it' }) 'Set-PlaytestInconclusive ends a check as inconclusive'
    Assert-Equal 'check-declined' (Get-OutcomeRecord { Set-PlaytestInconclusive -Because 'x' }).Detector 'Set-PlaytestInconclusive carries its own detector'
    Assert-True ($null -eq (Get-Command Set-PlaytestFail -ErrorAction SilentlyContinue)) 'there is NO Set-PlaytestFail: only an assertion may fail a check'

    # The outcome text, including the degraded form.
    Assert-Equal 'pass' (Format-PlaytestOutcome -Outcome 'pass' -Degraded $false -Attempts 1) 'a clean pass renders as pass'
    Assert-Equal 'pass (degraded, 3 attempts)' (Format-PlaytestOutcome -Outcome 'pass' -Degraded $true -Attempts 3) 'a pass on the third attempt renders as degraded with the count'
    Assert-Equal 'pass (degraded, 2 attempts)' (Format-PlaytestOutcome -Outcome 'pass' -Degraded $true -Attempts 1) 'a degraded pass never renders as fewer than two attempts'
    Assert-Equal 'fail' (Format-PlaytestOutcome -Outcome 'fail' -Degraded $true -Attempts 3) 'a fail is a fail regardless of attempts'
    Assert-Equal 'inconclusive (boot-timeout)' (Format-PlaytestOutcome -Outcome 'inconclusive' -Degraded $false -Attempts 1 -Detector 'boot-timeout') 'an inconclusive names its detector'
    Assert-Equal 'inconclusive' (Format-PlaytestOutcome -Outcome 'inconclusive' -Degraded $false -Attempts 1) 'an inconclusive with no detector still renders'

    # Attempt bookkeeping: the worst single operation drives the text, the total
    # retries drive the report.
    $ctx = New-TestContext
    Add-PlaytestAttempt -Context $ctx -Attempts 1
    Assert-False $ctx.Degraded 'one attempt is not degraded'
    Add-PlaytestAttempt -Context $ctx -Attempts 3
    Assert-True  $ctx.Degraded 'three attempts marks the check degraded'
    Assert-Equal 3 $ctx.MaxAttempts 'the worst single operation is remembered'
    Add-PlaytestAttempt -Context $ctx -Attempts 2
    Assert-Equal 3 $ctx.MaxAttempts 'a later smaller retry does not lower the worst'
    Assert-Equal 3 $ctx.Attempts    'total retries accumulate across operations'
}

function Test-FlakeTaxonomy {
    if (-not (Test-SectionSelected 'flake')) { return }
    Start-Section 'flake taxonomy'
    Reset-TestHome

    $names = @((Get-PlaytestFlakeTaxonomy) | ForEach-Object { $_.Name })
    foreach ($required in @('connect-first-attempt', 'launchpad-workshop-park', 'boot-timeout', 'control-plane-silent', 'host-not-hosting')) {
        Assert-True ($names -contains $required) "the taxonomy ships a '$required' detector"
    }
    Assert-True ($names -contains 'lock-lost') "the taxonomy ships a 'lock-lost' detector (the lock is re-taken per check)"
    foreach ($f in (Get-PlaytestFlakeTaxonomy)) {
        Assert-True ([bool]$f.Summary) "detector '$($f.Name)' explains itself"
        Assert-True ([int]$f.MaxAttempts -ge 1) "detector '$($f.Name)' has a bounded attempt count"
        Assert-True (@('retry', 'wait', 'restart-instance', 'abort') -contains $f.Remedy) "detector '$($f.Name)' has a known remedy"
    }

    # Each detector fires on its own fixture.
    $p = New-PlaytestProbe -Kind 'action' -Instance 'joiner' -Path '/connect' -Response ([pscustomobject]@{ ok = $false; result = 'timeout' })
    Assert-Equal 'connect-first-attempt' (Resolve-PlaytestFlake $p).Name 'a /connect timeout classifies as connect-first-attempt'
    $p = New-PlaytestProbe -Kind 'transport' -Instance 'joiner' -Path '/connect?timeoutMs=300000' -ErrorText 'the operation timed out'
    Assert-Equal 'connect-first-attempt' (Resolve-PlaytestFlake $p).Name 'a /connect transport failure classifies the same, query string and all'

    $parked = [pscustomobject]@{ loadedPluginCount = 2; gameInitialized = $false; phase = 'menu' }
    $p = New-PlaytestProbe -Kind 'barrier' -Instance 'joiner' -Stage 'menu' -Status $parked
    Assert-Equal 'launchpad-workshop-park' (Resolve-PlaytestFlake $p).Name 'plugins stuck at 2 with gameInitialized false is the Workshop park'
    Assert-Equal 'restart-instance' (Resolve-PlaytestFlake $p).Remedy 'the Workshop park is fixed by restarting that instance'

    $slow = [pscustomobject]@{ loadedPluginCount = 18; gameInitialized = $false; phase = 'menu' }
    $p = New-PlaytestProbe -Kind 'barrier' -Instance 'joiner' -Stage 'menu' -Status $slow
    Assert-Equal 'boot-timeout' (Resolve-PlaytestFlake $p).Name 'a slow boot that is NOT the park classifies as boot-timeout'

    $p = New-PlaytestProbe -Kind 'transport' -Instance 'hostie' -Path '/ping' -ErrorText 'the operation timed out' -Blocking $true
    Assert-Equal 'control-plane-silent' (Resolve-PlaytestFlake $p).Name 'silence during a blocking call is explained, not a dead instance'
    Assert-Equal 'wait' (Resolve-PlaytestFlake $p).Remedy 'the remedy for an explained silence is to wait it out'
    $p = New-PlaytestProbe -Kind 'transport' -Instance 'hostie' -Path '/ping' -ErrorText 'No connection could be made because the target machine actively refused it'
    Assert-Equal 'instance-dead' (Resolve-PlaytestFlake $p).Name 'a refused connection with no blocking call is a dead instance'

    $p = New-PlaytestProbe -Kind 'poststate' -Instance 'hostie' -Path '/host' -Status ([pscustomobject]@{ hosting = $false; role = 'singlePlayer' })
    Assert-Equal 'host-not-hosting' (Resolve-PlaytestFlake $p).Name 'a world that is up without hosting classifies as host-not-hosting'
    Assert-Equal 'abort' (Resolve-PlaytestFlake $p).Remedy 'host-not-hosting is not retried, it is reported'
    $p = New-PlaytestProbe -Kind 'poststate' -Instance 'hostie' -Path '/host' -Status ([pscustomobject]@{ hosting = $true; role = 'listenHost' })
    Assert-True ($null -eq (Resolve-PlaytestFlake $p)) 'a host that IS hosting matches no detector'

    $p = New-PlaytestProbe -Kind 'lock'
    Assert-Equal 'lock-lost' (Resolve-PlaytestFlake $p).Name 'losing the lock classifies as lock-lost'

    # An assertion failure must never look like a flake: that is the single
    # confusion the taxonomy exists to prevent.
    $p = New-PlaytestProbe -Kind 'action' -Instance 'hostie' -Path '/player/use' -Response ([pscustomobject]@{ ok = $true })
    Assert-True ($null -eq (Resolve-PlaytestFlake $p)) 'a successful action matches no detector'

    # A detector that throws is skipped rather than allowed to swallow a probe.
    Register-PlaytestFlake -Name 'broken-detector' -Remedy 'retry' -Summary 'throws' -Test { param($Probe) throw 'boom' }
    $p = New-PlaytestProbe -Kind 'lock'
    Assert-Equal 'lock-lost' (Invoke-Quiet { (Resolve-PlaytestFlake $p).Name }) 'a detector that throws is skipped, not fatal'

    Register-PlaytestFlake -Name 'my-special-case' -Remedy 'abort' -Summary 'a mod-specific rig condition' -Test { param($Probe) return ($Probe.Path -eq '/very/specific') }
    $p = New-PlaytestProbe -Kind 'action' -Path '/very/specific'
    Assert-Equal 'my-special-case' (Resolve-PlaytestFlake $p).Name 'a registered detector takes precedence over the general ones'
    Reset-TestHome
}

function Test-Authority {
    if (-not (Test-SectionSelected 'authority')) { return }
    Start-Section 'assert on the authority, not the actor'
    Reset-TestHome
    $ctx = New-TestContext
    $script:PlaytestContext = $ctx

    # Argument quoting across the process boundary. This is not decoration: with
    # it missing, the lock purpose (which defaults to the CHECK NAME and so always
    # has spaces) reached the launcher as several arguments, the second landed
    # positionally on the launcher's int $Port, and EVERY check in EVERY suite
    # reported inconclusive/rig-unavailable. The harness could not take the lock.
    Assert-Equal 'plain' (ConvertTo-PlaytestArgument 'plain') 'an argument with no space is passed through untouched'
    Assert-Equal '"the first-use notice cap"' (ConvertTo-PlaytestArgument 'the first-use notice cap') 'an argument with spaces is quoted'
    Assert-Equal '""' (ConvertTo-PlaytestArgument '') 'an empty argument survives as an empty quoted string rather than vanishing'
    Assert-Equal '"a \"b\" c"' (ConvertTo-PlaytestArgument 'a "b" c') 'an embedded quote is escaped'
    Assert-Equal '"C:\rig dir\\"' (ConvertTo-PlaytestArgument 'C:\rig dir\') 'a trailing backslash is doubled so it cannot escape the closing quote'
    Assert-Equal 'C:\rig\client-rig.ps1' (ConvertTo-PlaytestArgument 'C:\rig\client-rig.ps1') 'a path with no space keeps its backslashes'

    # The decoys teach rather than exist.
    Assert-Throws { Assert-RigOk } 'Assert-RigOk refuses and explains why' 'statement about the request'
    Assert-Throws { Assert-RigResponse } 'Assert-RigResponse refuses and explains why' 'evidence, not a conclusion'
    Assert-True ($null -eq (Get-Command Assert-True -Module '*playtest*' -ErrorAction SilentlyContinue)) 'the library exports no bare-boolean assert'

    # An action result cannot be asserted on. This is the shape that enforces the
    # rule; there is no schema anywhere that could.
    $action = Invoke-Quiet { Invoke-RigAction -On 'hostie' -Path '/host' -Body @{ world = 'Lunar' } -Blocking -Context $ctx }
    Assert-Equal 'Playtest.ActionResult' $action.PSObject.TypeNames[0] 'an action hands back a Playtest.ActionResult'
    Assert-True ($null -ne $action.Response) 'the raw response is kept as evidence on the result'
    Assert-True ($null -eq $action.PSObject.Properties['ok']) 'the result does NOT promote ok to the top level'
    Assert-Throws { Assert-RigValue -From $action -Reader status -Select hosting -Is $true -Because 'x' -Context $ctx } `
        'asserting on an action result is refused' 'must be an instance NAME'
    Assert-Throws { Assert-RigValue -From $action.Response -Reader status -Select hosting -Is $true -Because 'x' -Context $ctx } `
        'asserting on a raw response object is refused' 'must be an instance NAME'
    Assert-Throws { Read-RigValue -From 'nosuchinstance' -Reader status -Context $ctx } `
        'reading from an instance the check does not own is refused' 'not one of this check'
    Assert-Match (Get-OutcomeRecord { Assert-RigValue -From $action -Reader status -Select hosting -Is $true -Because 'x' -Context $ctx }).Message 'AUTHORITY' `
        'the refusal message teaches which instance to read from'

    # The reader path is the easy one, and it produces an Observation.
    $obs = Invoke-Quiet { Read-RigValue -From 'hostie' -Reader status -Select 'hosting' -Context $ctx }
    Assert-Equal 'Playtest.Observation' $obs.PSObject.TypeNames[0] 'a reader hands back a Playtest.Observation'
    Assert-Equal $true    $obs.Value    'the observation carries the value'
    Assert-Equal 'hostie' $obs.Instance 'the observation names the instance it came from'
    Assert-Equal 'status' $obs.Reader   'the observation names the reader'
    Assert-True ([bool]$obs.EvidenceRef) 'the observation points at the request it came from'

    # Assertions.
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Is 'listenHost' -Because 'the world holder must be a listen host' -Context $ctx } 'a satisfied -Is passes'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Is 'joinedClient' -Because 'wrong on purpose' -Context $ctx }) 'an unsatisfied -Is FAILS (this is the only thing that may)'
    Assert-Match (Get-OutcomeRecord { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Is 'joinedClient' -Because 'the reason it matters' -Context $ctx }).Message 'the reason it matters' 'the failure message carries -Because'
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader status -Select 'hostPort' -AtLeast 1 -Because 'a host must bind a port' -Context $ctx } 'a satisfied -AtLeast passes'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigValue -From 'hostie' -Reader status -Select 'hostPort' -AtMost 10 -Because 'wrong on purpose' -Context $ctx }) 'an unsatisfied -AtMost fails'
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Matches '^listen' -Because 'shape check' -Context $ctx } 'a satisfied -Matches passes'
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -IsNot 'menu' -Because 'it left the menu' -Context $ctx } 'a satisfied -IsNot passes'
    Assert-Throws  { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Is 'x' -IsNot 'y' -Because 'z' -Context $ctx } 'two comparisons in one assertion are refused' 'exactly one comparison'
    Assert-Throws  { Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Because 'z' -Context $ctx } 'no comparison at all is refused' 'exactly one comparison'

    # The roster reader: the authority for "did the joiner arrive".
    Invoke-Quiet { Invoke-RigAction -On 'joiner' -Path '/connect' -Body @{ address = '127.0.0.1'; port = 27801 } -Blocking -Context $ctx } | Out-Null
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader roster -Select 'count' -Is 2 -Because 'the host roster is what proves a joiner arrived' -Context $ctx } 'the host roster is readable and counts the host plus the joiner'
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader roster -Of '900000000002' -Select 'username' -Is 'joiner' -Because 'the joiner must be in the roster by id' -Context $ctx } '-Of narrows the roster to one client'

    # The thing reader: an INSTANCE field on one object, per machine, which is
    # the shape a per-side check needs and which /reflect (statics only) cannot
    # answer.
    $obs = Invoke-Quiet { Read-RigValue -From 'hostie' -Reader thing -Of '442' -ReaderArgs @{ refIds = '442,445'; fields = 'ExampleField' } -Select 'requestedRefId' -Context $ctx }
    Assert-Equal '442' $obs.Value '-Of <refId> narrows the thing reader to one Thing'
    Assert-NoThrow { Assert-RigValue -From 'hostie' -Reader thing -Of '442/ExampleField' -Select 'value' -Is '(1,1,1,1)' -ReaderArgs @{ refIds = '442,445'; fields = 'ExampleField' } -Because 'the acted-on object must carry the new value' -Context $ctx } `
        '-Of <refId>/<Field> narrows to one field row so -Select value works'
    Assert-NoThrow { Assert-RigAgreement -Across @('hostie', 'joiner') -Reader thing -Of '442/ExampleField' -Select 'value' -ReaderArgs @{ refIds = '442'; fields = 'ExampleField' } -Because 'both machines must render the same thing' -Context $ctx } `
        'the thing reader supports the per-side agreement check the rig exists for'

    # The trap: a value equal to the prefab's is indistinguishable from never
    # having been set. A check that reads it as evidence repeats a retracted
    # conclusion, so matchesPrefab is assertable and the README says to assert it.
    $ctrl = Invoke-Quiet { Read-RigValue -From 'hostie' -Reader thing -Of '445/ExampleField' -ReaderArgs @{ refIds = '445'; fields = 'ExampleField' } -Select 'matchesPrefab' -Context $ctx }
    Assert-Equal $true $ctrl.Value 'an untouched control reports matchesPrefab true, which is NOT a reading'
    $acted = Invoke-Quiet { Read-RigValue -From 'hostie' -Reader thing -Of '442/ExampleField' -ReaderArgs @{ refIds = '442'; fields = 'ExampleField' } -Select 'matchesPrefab' -Context $ctx }
    Assert-Equal $false $acted.Value 'the acted-on object reports matchesPrefab false, so its value is real evidence'

    # Agreement across instances, which is the shape of nearly every real check.
    Assert-NoThrow { Assert-RigAgreement -Across @('hostie', 'joiner') -Reader config -ReaderArgs @{ guid = 'net.example' } -Select 'count' -Because 'both sides run the same build' -Context $ctx } 'agreement across two instances passes when they agree'
    Assert-Throws  { Assert-RigAgreement -Across @('hostie') -Reader status -Select 'role' -Because 'x' -Context $ctx } 'agreement with one instance is refused' 'at least two'
    $script:Fake.State['joiner'].role = 'joinedClient'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigAgreement -Across @('hostie', 'joiner') -Reader status -Select 'role' -Because 'they must not differ' -Context $ctx }) 'a genuine disagreement FAILS'
    Assert-Match (Get-OutcomeRecord { Assert-RigAgreement -Across @('hostie', 'joiner') -Reader status -Select 'role' -Because 'x' -Context $ctx }).Message 'hostie=\[listenHost\]' 'the disagreement message names each side reading'

    # Before and after, which is the only way to say a field changed.
    $baseline = Invoke-Quiet { Read-RigValue -From 'joiner' -Reader status -Select 'phase' -Context $ctx }
    Assert-Equal 'inWorld' $baseline.Value 'fixture check: the joiner is in world'
    Assert-NoThrow { Assert-RigChange -Baseline $baseline -Unchanged -Because 'nothing acted on it' -Context $ctx } 'an unchanged control passes'
    $script:Fake.State['joiner'].phase = 'menu'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigChange -Baseline $baseline -Unchanged -Because 'the control must not move' -Context $ctx }) 'a control that moved FAILS'
    Assert-NoThrow { Assert-RigChange -Baseline $baseline -To 'menu' -Because 'the disconnect should return it to the menu' -Context $ctx } 'a value that moved to the expected one passes'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigChange -Baseline $baseline -To 'inWorld' -Because 'wrong on purpose' -Context $ctx }) 'a value that moved to the wrong one fails'
    Assert-Throws { Assert-RigChange -Baseline 'inWorld' -To 'menu' -Because 'x' -Context $ctx } 'a remembered raw value is not a baseline' 'Playtest.Observation'
    Assert-Throws { Assert-RigChange -Baseline $baseline -To 'menu' -Unchanged -Because 'x' -Context $ctx } '-To and -Unchanged together are refused' 'either -To or -Unchanged'

    # A baseline taken through a reader with a QUERY STRING must be re-readable.
    # This shipped broken: the observation carried no ReaderArgs and the re-read
    # went out as a bare '/thing', which the endpoint answers 400. Every
    # before-and-after check on a per-Thing field, a config entry, a console tail
    # or an inventory slot therefore reported inconclusive with no comparison
    # made at all, and the readers that need a query are exactly the ones whose
    # baselines matter. The fake mirrors the real 400, so a regression here
    # cannot pass.
    $thingArgs = @{ refIds = '442,445'; fields = 'ExampleField' }
    $tBase = Invoke-Quiet { Read-RigValue -From 'hostie' -Reader thing -Of '442/ExampleField' -Select 'value' -ReaderArgs $thingArgs -Context $ctx }
    Assert-Equal '(1,1,1,1)' $tBase.Value 'fixture check: the acted-on Thing reads its current value'
    Assert-True ($null -ne $tBase.ReaderArgs) 'the observation CARRIES the ReaderArgs it was read with'
    Assert-Equal '442,445' "$($tBase.ReaderArgs.refIds)" 'and carries them by value, so the re-read reproduces the same request'
    Assert-NoThrow { Assert-RigChange -Baseline $tBase -Unchanged -Because 'nothing has acted on it yet' -Context $ctx } `
        'Assert-RigChange -Unchanged re-reads a query-string reader instead of 400ing into inconclusive'
    $script:Fake.ThingValue = '(0,0,0,0)'
    Assert-NoThrow { Assert-RigChange -Baseline $tBase -To '(0,0,0,0)' -Because 'the stroke must land on this side' -Context $ctx } `
        'Assert-RigChange -To re-reads a query-string reader and sees the move'
    Assert-Equal 'fail' (Get-Outcome { Assert-RigChange -Baseline $tBase -Unchanged -Because 'the control must not move' -Context $ctx }) `
        'and a query-string baseline that moved still FAILS rather than going inconclusive'
    $script:Fake.ThingValue = '(1,1,1,1)'

    # Mutating the hashtable after the baseline was taken must not retroactively
    # change what the baseline was read with.
    $thingArgs.refIds = '999'
    Assert-NoThrow { Assert-RigChange -Baseline $tBase -Unchanged -Because 'the baseline owns its own copy of the args' -Context $ctx } `
        'the ReaderArgs on an observation are a copy, not the caller hashtable'

    # And the guard itself: the fake refuses a query-less /thing, which is what
    # makes the three assertions above measurements rather than decoration.
    $r = Get-OutcomeRecord { Read-RigValue -From 'hostie' -Reader thing -Of '442/ExampleField' -Select 'value' -Context $ctx }
    Assert-Equal 'inconclusive' $r.Outcome 'a thing read with no ReaderArgs at all is still inconclusive, as the endpoint 400s'

    $script:PlaytestContext = $null
}

function Test-Actions {
    if (-not (Test-SectionSelected 'action')) { return }
    Start-Section 'actions, retries and the degraded pass'
    Reset-TestHome
    $ctx = New-TestContext

    # A refusal nothing in the taxonomy explains is inconclusive, never a fail:
    # an endpoint saying no is not the mod misbehaving.
    $script:Fake.ConnectFailures = 99
    $r = Get-OutcomeRecord { Invoke-RigAction -On 'joiner' -Path '/connect' -Body @{ port = 27801 } -Context $ctx }
    Assert-Equal 'inconclusive' $r.Outcome 'a /connect that never succeeds is inconclusive'
    Assert-Equal 'connect-first-attempt' $r.Detector 'and it is reported under the detector that explains it'
    Assert-Match $r.Message 'inconclusive and never failed' 'the message says explicitly that this is not a failure'

    # The documented shape: fails twice, works on the third.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.ConnectFailures = 2
    Assert-NoThrow { Invoke-RigAction -On 'joiner' -Path '/connect' -Body @{ port = 27801 } -Context $ctx } 'a /connect that succeeds on the third attempt does succeed'
    Assert-Equal 3 $ctx.MaxAttempts 'the attempt count is recorded'
    Assert-True  $ctx.Degraded 'the check is marked degraded'
    Assert-True  ($ctx.Detectors -contains 'connect-first-attempt') 'the detector that fired is recorded on the check'
    Assert-Equal 'pass (degraded, 3 attempts)' (Format-PlaytestOutcome -Outcome 'pass' -Degraded $ctx.Degraded -Attempts $ctx.MaxAttempts) 'it renders as a degraded pass, never a clean one'
    Assert-Equal 3 (@($script:Fake.Requests | Where-Object { $_.Path -eq '/connect' })).Count 'the retry really re-drove the endpoint'

    # -NoRetry pins the first attempt for a check that wants to measure it.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.ConnectFailures = 2
    Assert-Equal 'inconclusive' (Get-Outcome { Invoke-RigAction -On 'joiner' -Path '/connect' -NoRetry -Context $ctx }) '-NoRetry gives up after one attempt'
    Assert-Equal 1 (@($script:Fake.Requests | Where-Object { $_.Path -eq '/connect' })).Count '-NoRetry really only called once'

    # A blocked control plane during a blocking call is waited out, not counted
    # as a dead instance.
    Reset-TestHome
    $ctx = New-TestContext
    $probe = New-PlaytestProbe -Kind 'transport' -Instance 'hostie' -Path '/save' -ErrorText 'timed out' -Blocking $true
    Assert-Equal 'control-plane-silent' (Resolve-PlaytestFlake $probe).Name 'a silent control plane under /save is the explained case'

    # Losing the lock mid-check is inconclusive and stops the check at once.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.LastRefreshUtc = $script:FakeNow.AddMinutes(-5)
    $script:Fake.Calls = @()
    $script:Fake.LockExit = 0
    $script:FakeRigCommandBackup = $script:FakeRigCommand
    Initialize-PlaytestLib -RigCommand {
        param([string[]] $ArgList)
        $script:Fake.Calls += , @($ArgList)
        if ("$($ArgList[0])" -eq 'refresh-lock') { return [pscustomobject]@{ ExitCode = 1; StdOut = ''; StdErr = 'Refresh refused: the rig is locked by another session.' } }
        return [pscustomobject]@{ ExitCode = 0; StdOut = ''; StdErr = '' }
    }
    $r = Get-OutcomeRecord { Invoke-RigAction -On 'hostie' -Path '/status' -Context $ctx }
    Assert-Equal 'inconclusive' $r.Outcome 'losing the rig lock mid-check is inconclusive'
    Assert-Equal 'lock-lost' $r.Detector 'and is reported as lock-lost'
    Assert-Match $r.Message 'releases and re-takes the lock per check' 'the message explains that losing it is expected under the per-check lock policy'
    Use-TestPaths

    # An instance that was never provisioned stops the check with the fix in the
    # message, rather than driving a port that belongs to nothing.
    Reset-TestHome
    $ctx = New-PlaytestContext -CheckName 'x' -Instances @(@{ Name = 'ghost'; Role = 'client' })
    $ctx.Owner = 'a1b2c3d4'
    $r = Get-OutcomeRecord { Read-RigValue -From 'ghost' -Reader status -Context $ctx }
    Assert-Equal 'inconclusive' $r.Outcome 'an unprovisioned instance is inconclusive'
    Assert-Equal 'instance-not-provisioned' $r.Detector 'and names the reason'
    Assert-Match $r.Message 'testrig create -Target' 'and names the command that fixes it'
}

function Test-BinaryGate {
    if (-not (Test-SectionSelected 'binary')) { return }
    Start-Section 'the binary under test is asserted first'
    Reset-TestHome
    $ctx = New-TestContext
    $build = New-TestBuildDll -Bytes 4096

    New-TestInstanceData -Name 'hostie' -Role 'host'   -DeployedBytes 4096 | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' -DeployedBytes 4096 | Out-Null
    $script:Fake.ConfigEntries = 4

    Assert-NoThrow {
        Assert-BinaryUnderTest -On @('hostie', 'joiner') -Mod 'net.example' -ExpectedConfigCount 4 -ExpectedGroupCount 2 `
            -DllPath $build -DeployedRelativePath 'userdata\mods\Example\Example.dll' -Context $ctx
    } 'a matching stamp, a matching DLL and a matching live config count attest'
    Assert-True $ctx.BinaryAttested 'attesting sets the flag the runner gates on'
    Assert-FileExists (Join-Path $ctx.EvidenceDir 'binary.json') 'the attestation lands in the evidence bundle'

    # The near-miss this verb exists for: a provision re-seeded a stale copy.
    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceData -Name 'hostie' -Role 'host'   -DeployedBytes 89600 | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' -DeployedBytes 89600 | Out-Null
    $r = Get-OutcomeRecord {
        Assert-BinaryUnderTest -On @('hostie') -Mod 'net.example' -DllPath (New-TestBuildDll -Bytes 96768) `
            -DeployedRelativePath 'userdata\mods\Example\Example.dll' -Context $ctx
    }
    Assert-Equal 'inconclusive' $r.Outcome 'a STALE seeded DLL is inconclusive, never a fail'
    Assert-Equal 'binary-stale' $r.Detector 'and is named as a stale binary'
    Assert-Match $r.Message 'nothing was measured against the code under test' 'the message says why a fail would be wrong here'
    Assert-False $ctx.BinaryAttested 'a failed attestation does not set the flag'

    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceData -Name 'hostie' -NoStamp | Out-Null
    $r = Get-OutcomeRecord { Assert-BinaryUnderTest -On @('hostie') -Mod 'net.example' -Context $ctx }
    Assert-Equal 'provision-stamp-missing' $r.Detector 'an instance with no provision stamp cannot be attested'
    Assert-Match $r.Message 'testrig create -Target' 'and the message names the fix'

    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceData -Name 'hostie' | Out-Null
    $script:Fake.ConfigEntries = 4
    $r = Get-OutcomeRecord { Assert-BinaryUnderTest -On @('hostie') -Mod 'net.example' -ExpectedConfigCount 33 -Context $ctx }
    Assert-Equal 'binary-config-mismatch' $r.Detector 'a live config count that does not match the build under test is caught from INSIDE the process'
    Assert-Equal 'inconclusive' $r.Outcome 'and it is inconclusive rather than a failure'

    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceData -Name 'hostie' | Out-Null
    $r = Get-OutcomeRecord {
        Assert-BinaryUnderTest -On @('hostie') -Mod 'net.example' -DllPath (Join-Path $script:TempRoot 'no-such-build.dll') -Context $ctx
    }
    Assert-Equal 'binary-missing' $r.Detector 'a build under test that was never built stops the check'

    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceData -Name 'hostie' | Out-Null
    $r = Get-OutcomeRecord {
        Assert-BinaryUnderTest -On @('hostie') -Mod 'net.example' -DllPath (New-TestBuildDll -Bytes 4096) `
            -DeployedRelativePath 'userdata\mods\Example\Example.dll' -Context $ctx
    }
    Assert-Equal 'binary-not-deployed' $r.Detector 'an instance that never received the build is caught'
}

function Test-Teardown {
    if (-not (Test-SectionSelected 'teardown')) { return }
    Start-Section 'guaranteed teardown'
    Reset-TestHome

    # The happy path: acquire, run, stop, release.
    $ctx = New-TestContext
    Invoke-Quiet { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) $c.Started = @('joiner', 'hostie'); 'body ran' } } | Out-Null
    $calls = Get-FakeCallStrings
    Assert-Equal $script:Fake.LockOwner $ctx.Owner 'the owner id is read from the TESTRIG-OWNER line'
    Assert-True  (@($calls | Where-Object { $_ -like 'lock *' }).Count -eq 1) 'the lock is taken once'
    Assert-True  (@($calls | Where-Object { $_ -like 'unlock *' }).Count -eq 1) 'the lock is released once'
    Assert-True  (@($calls | Where-Object { $_ -like '*stop -Target joiner*' }).Count -eq 1) 'the joiner is stopped by NAME'
    Assert-True  (@($calls | Where-Object { $_ -like '*stop -Target hostie*' }).Count -eq 1) 'the host is stopped by NAME'
    Assert-Equal 0 (@($calls | Where-Object { $_ -match '(^|\s)-Target\s+(all|clients)(\s|$)' }).Count) 'NOTHING ever targeted all or clients (either would reach another session live test)'

    $stopIdx = @()
    for ($i = 0; $i -lt @($script:Fake.Calls).Count; $i++) {
        $s = ($script:Fake.Calls[$i] -join ' ')
        if ($s -like '*stop -Target*') { $stopIdx += "$s" }
    }
    Assert-Match $stopIdx[0] 'joiner' 'the joiner is stopped FIRST'
    Assert-Match $stopIdx[1] 'hostie' 'the host is stopped LAST (it holds the world)'

    # A body that throws still gets the whole teardown, and the throw survives.
    Reset-TestHome
    $ctx = New-TestContext
    $threw = $false
    try {
        Invoke-Quiet { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) $c.Started = @('hostie'); throw (New-PlaytestSignal -Kind 'fail' -Message 'the value was wrong' -Detector 'assertion') } }
    }
    catch { $threw = $true; $kind = (Get-PlaytestSignal $_.Exception).Kind }
    $calls = Get-FakeCallStrings
    Assert-True  $threw 'a throwing body still throws out of Use-Rig'
    Assert-Equal 'fail' $kind 'and its classification is not lost by the teardown'
    Assert-True  (@($calls | Where-Object { $_ -like '*stop -Target hostie*' }).Count -eq 1) 'the instance is stopped even though the body threw'
    Assert-True  (@($calls | Where-Object { $_ -like 'unlock *' }).Count -eq 1) 'THE LOCK IS RELEASED even though the body threw'

    # A stop that fails must not stop the release: an instance left up holds the
    # whole rig, but a lock left held blocks every other agent as well.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.StopExit['hostie'] = 1
    Invoke-Quiet { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) $c.Started = @('joiner', 'hostie') } } | Out-Null
    $calls = Get-FakeCallStrings
    Assert-True (@($calls | Where-Object { $_ -like '*stop -Target joiner*' }).Count -eq 1) 'a failing stop does not skip the other instances'
    Assert-True (@($calls | Where-Object { $_ -like 'unlock *' }).Count -eq 1) 'a failing stop does not skip the release'
    Assert-True (@($ctx.TeardownNotes).Count -ge 1) 'the failed stop is recorded rather than swallowed'
    Assert-Match (@($ctx.TeardownNotes) -join ' ') "stop of 'hostie' failed" 'and the note names the instance'

    # A release that fails is loud, and says the timer will reclaim it.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.UnlockExit = 1
    Invoke-Quiet { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) 'ok' } } | Out-Null
    Assert-Match (@($ctx.TeardownNotes) -join ' ') 'RELEASE FAILED' 'a failed release is recorded'
    Assert-Match (@($ctx.TeardownNotes) -join ' ') 'expires on its own timer' 'and the note says what happens next'

    # A rig somebody else holds is inconclusive, and nothing is driven.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.LockExit = 1
    $script:Fake.LockStdOut = 'Cannot acquire: the test rig is locked by another session.'
    $r = Get-OutcomeRecord { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) $c.Started = @('hostie') } }
    Assert-Equal 'inconclusive' $r.Outcome 'a rig held by another session is inconclusive'
    Assert-Equal 'rig-unavailable' $r.Detector 'and is named as such'
    Assert-Equal 0 (@(Get-FakeCallStrings | Where-Object { $_ -like 'start*' }).Count) 'and nothing was started without the lock'

    # A lock whose owner id cannot be read is refused rather than driven blind:
    # nothing could be released afterwards.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.LockStdOut = '[Lock] Acquired the rig session lock.'
    $r = Get-OutcomeRecord { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) 'ok' } }
    Assert-Equal 'inconclusive' $r.Outcome 'a lock with no readable owner id is inconclusive'
    Assert-Match $r.Message 'could not be read back' 'and says exactly what went wrong'

    # A re-asserted lock prints a different line, and that one parses too.
    Reset-TestHome
    $ctx = New-TestContext
    $script:Fake.LockStdOut = "[Lock] Re-asserted the rig session lock (owner a1b2c3d4). Pass -As a1b2c3d4 on mutating commands.`nTESTRIG-OWNER a1b2c3d4"
    Invoke-Quiet { Use-Rig -Purpose 'a test' -Context $ctx -Body { param($c) 'ok' } } | Out-Null
    Assert-Equal 'a1b2c3d4' $ctx.Owner 'the owner token is read on a re-assert too'
}

function Test-BringUp {
    if (-not (Test-SectionSelected 'bringup')) { return }
    Start-Section 'bring-up: hosts first, and every post-condition read from the authority'
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'

    Assert-NoThrow { Start-RigInstances -Context $ctx -BootWaitSeconds 60 -WorldWaitSeconds 60 } 'a healthy rig comes up'
    $calls = Get-FakeCallStrings
    $startIdx = @($calls | Where-Object { $_ -like 'start *' })
    Assert-Match $startIdx[0] 'hostie' 'the HOST is started first (a joiner has nothing to reach until it hosts)'
    Assert-Match $startIdx[1] 'joiner' 'the joiner is started second'
    Assert-True  (@($script:Fake.Requests | Where-Object { $_.Path -eq '/host' }).Count -eq 1) 'the host was told to host'
    Assert-True  (@($script:Fake.Requests | Where-Object { $_.Path -eq '/connect' }).Count -eq 1) 'the joiner was told to connect'
    Assert-Equal 2 @($script:Fake.State['hostie'].connectedClients).Count 'the fixture host ends with itself plus one joiner'
    Assert-True  (@($ctx.Started) -contains 'hostie') 'the host is registered for teardown'
    Assert-True  (@($ctx.Started) -contains 'joiner') 'the joiner is registered for teardown'

    # POST /host answered 200 and nothing is hosting. The only honest answer is
    # inconclusive, because the rig never got into the state the check needs.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.HostSetsHosting = $false
    $r = Get-OutcomeRecord { Start-RigInstances -Context $ctx -BootWaitSeconds 30 -WorldWaitSeconds 30 }
    Assert-Equal 'inconclusive' $r.Outcome 'a /host that answered 200 with nothing behind it is inconclusive'
    Assert-Equal 'host-not-hosting' $r.Detector 'and is named host-not-hosting'
    Assert-Match $r.Message 'inconclusive and never failed' 'and says it is not a mod defect'

    # POST /connect answered ok and the roster did not grow.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.ConnectJoins = $false
    $r = Get-OutcomeRecord { Start-RigInstances -Context $ctx -BootWaitSeconds 30 -WorldWaitSeconds 30 }
    Assert-Equal 'inconclusive' $r.Outcome 'a /connect that answered ok with an unchanged host roster is inconclusive'
    Assert-Equal 'joiner-not-in-roster' $r.Detector 'and is named joiner-not-in-roster'

    # An instance the launcher could not start.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.StartExit['hostie'] = 1
    $r = Get-OutcomeRecord { Start-RigInstances -Context $ctx -BootWaitSeconds 30 -WorldWaitSeconds 30 }
    Assert-Equal 'instance-start-failed' $r.Detector 'a launcher that could not start an instance stops the check'
    Assert-True (@($ctx.Started) -contains 'hostie') 'a failed start is STILL registered for teardown (the process may exist)'

    # The readiness barrier and its two flakes.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.StatusBoots['hostie'] = 3
    Assert-NoThrow { Wait-RigStage -Name 'hostie' -Stage 'menu' -WaitSeconds 300 -PollSeconds 5 -Context $ctx } 'a slow boot inside the barrier still reaches the stage'

    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.StatusBoots['hostie'] = 100000
    $r = Get-OutcomeRecord { Wait-RigStage -Name 'hostie' -Stage 'menu' -WaitSeconds 60 -PollSeconds 5 -Context $ctx }
    Assert-Equal 'inconclusive' $r.Outcome 'an instance parked on the Workshop error screen is inconclusive'
    Assert-Equal 'launchpad-workshop-park' $r.Detector 'and is named as the Workshop park'
    Assert-True (@(Get-FakeCallStrings | Where-Object { $_ -like '*stop -Target hostie*' }).Count -ge 1) 'the park remedy restarted that ONE instance'
    Assert-Equal 0 (@(Get-FakeCallStrings | Where-Object { $_ -match '(^|\s)-All(\s|$)' }).Count) 'the restart never reached for -All'

    # A boot that times out for a reason that is NOT the park: the remedy is a
    # restart, and when the restart works the check goes on as a degraded pass
    # rather than as a failure.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.State['hostie'].gameInitialized = $false
    $script:Fake.State['hostie'].loadedPluginCount = 18
    Assert-NoThrow { Wait-RigStage -Name 'hostie' -Stage 'menu' -WaitSeconds 60 -PollSeconds 5 -Context $ctx } 'a boot timeout that a restart fixes does not end the check'
    Assert-True  $ctx.Degraded 'but it does mark the check degraded'
    Assert-True  ($ctx.Detectors -contains 'boot-timeout') 'and records the detector that fired'
    Assert-True  (@(Get-FakeCallStrings | Where-Object { $_ -like '*start -Target hostie*' }).Count -ge 1) 'the remedy restarted that instance'

    # And when the restart does NOT fix it, the bounded retry gives up and the
    # check is inconclusive rather than hanging on the rig forever.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $script:Fake.StuckBoot['hostie'] = $true
    $script:Fake.State['hostie'].gameInitialized = $false
    $script:Fake.State['hostie'].loadedPluginCount = 18
    $r = Get-OutcomeRecord { Wait-RigStage -Name 'hostie' -Stage 'menu' -WaitSeconds 60 -PollSeconds 5 -Context $ctx }
    Assert-Equal 'boot-timeout' $r.Detector 'a boot that survives the restart is a boot timeout'
    Assert-Equal 'inconclusive' $r.Outcome 'and it is inconclusive, never a failure'
    Assert-Match $r.Message 'after 2 attempt' 'and the retry was bounded, not endless'
}

function New-TestInstanceLog {
    # The instance's BepInEx/LogOutput.log, where the bepinexlog reader looks:
    # <instancesRoot>/<name>/BepInEx/LogOutput.log
    param([Parameter(Mandatory)][string] $Name, [Parameter(Mandatory)][string[]] $Lines)
    $p = Join-Path (Join-Path (Join-Path (Join-Path $script:TempRoot 'instances') $Name) 'BepInEx') 'LogOutput.log'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p) | Out-Null
    Set-Content -LiteralPath $p -Value ($Lines -join "`r`n") -Encoding utf8
    return $p
}

function Test-JoinHelper {
    if (-not (Test-SectionSelected 'joinhelper')) { return }
    Start-Section 'Connect-RigJoiner: confirm from the host roster, poll it, and retry'
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'

    # The host has to be in a world for any of this to mean anything.
    Invoke-Quiet { Start-RigInstances -Context $ctx -BootWaitSeconds 60 -WorldWaitSeconds 60 } | Out-Null
    $baseConnects = @($script:Fake.Requests | Where-Object { $_.Path -eq '/connect' }).Count
    Assert-Equal 1 $baseConnects 'bring-up now goes through the shared helper and still connects exactly once on a healthy rig'
    Assert-False $ctx.Degraded 'a first-attempt join is a CLEAN pass, not a degraded one'

    # The real 2026-08-11 failure: a check body disconnects the joiner and
    # reconnects it, and the roster does not carry it on the first attempt.
    # The old copy of this logic aborted here. The helper must retry and win.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    Invoke-Quiet { Start-RigInstances -Context $ctx -BootWaitSeconds 60 -WorldWaitSeconds 60 } | Out-Null
    $script:Fake.State['hostie'].connectedClients = @($script:Fake.State['hostie'].connectedClients | Where-Object { $_.isHost })
    $script:Fake.ConnectAttempts = 0
    $script:Fake.RosterJoinsAtAttempt = 2
    $join = $null
    Assert-NoThrow { $script:JoinResult = Connect-RigJoiner -Name 'joiner' -To 'hostie' -WorldWaitSeconds 60 -GapSeconds 1 -RosterPollSeconds 6 -Context $ctx } `
        'a rejoin whose roster row only appears on the second attempt still lands'
    $join = $script:JoinResult
    Assert-Equal 2 $script:Fake.ConnectAttempts 'and it really re-drove /connect rather than re-reading the same answer'

    # The contract that stops a retry from breaking the check it was meant to fix.
    # Anything the mod prints once PER JOIN appears once per attempt, so a check
    # measuring "exactly one line" has to baseline from the attempt that landed.
    # Without this, check 02 counted 3 join summaries after 3 attempts and failed
    # a correct mod, which is exactly what happened on the first live run.
    Assert-Equal 2 $join.Attempts 'the join result reports how many attempts it took'
    Assert-True  ($null -ne $join.SeqBeforeConnect) 'and carries the console sequence read immediately before the FINAL connect'
    Assert-True  ($join.SeqBeforeConnect -gt 100) 'which is a later sequence than the one before the first attempt, so per-join output is counted once'
    Assert-Equal 'joiner' $join.Joiner 'and names the joiner'
    Assert-Equal 'hostie' $join.Host   'and the host it joined'
    Assert-True  $ctx.Degraded 'a retried join is a DEGRADED pass, never a clean one'
    Assert-True  ($ctx.Detectors -contains 'connect-first-attempt') 'and it records the detector that documents why'
    Assert-True  (@($script:Fake.Requests | Where-Object { $_.Path -eq '/disconnect' }).Count -ge 1) 'the retry disconnects first, so the next attempt starts from the menu rather than from a half state'

    # Exhausted: still inconclusive, still named, still never a failure.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    Invoke-Quiet { Start-RigInstances -Context $ctx -BootWaitSeconds 60 -WorldWaitSeconds 60 } | Out-Null
    $script:Fake.State['hostie'].connectedClients = @($script:Fake.State['hostie'].connectedClients | Where-Object { $_.isHost })
    $script:Fake.ConnectJoins = $false
    $r = Get-OutcomeRecord { Connect-RigJoiner -Name 'joiner' -To 'hostie' -WorldWaitSeconds 60 -Attempts 3 -GapSeconds 1 -RosterPollSeconds 4 -Context $ctx }
    Assert-Equal 'inconclusive'         $r.Outcome  'a join that never reaches the roster is inconclusive'
    Assert-Equal 'joiner-not-in-roster' $r.Detector 'and is named joiner-not-in-roster'
    Assert-Match $r.Message 'after 3 attempt' 'and says how many times it tried, so a bounded give-up is distinguishable from one shot'
    Assert-Match $r.Message 'inconclusive and never failed' 'and never accuses the mod'

    # The host has no port: nothing to join, and that is the host's problem.
    Reset-TestHome
    $ctx = New-TestContext
    $ctx.Owner = 'a1b2c3d4'
    $r = Get-OutcomeRecord { Connect-RigJoiner -Name 'joiner' -To 'hostie' -WorldWaitSeconds 30 -Context $ctx }
    Assert-Equal 'host-not-hosting' $r.Detector 'a host reporting no game port is host-not-hosting, not a join failure'
    Assert-Equal 0 (@($script:Fake.Requests | Where-Object { $_.Path -eq '/connect' }).Count) 'and nothing was asked to connect to it'
}

function Test-BepInExLogReader {
    if (-not (Test-SectionSelected 'bepinexlog')) { return }
    Start-Section 'the bepinexlog reader: boot lines the console ring has already evicted'
    Reset-TestHome
    $ctx = New-TestContext

    New-TestInstanceLog -Name 'hostie' -Lines @(
        '[Info   :StationeersLaunchPad] loading 67 mods'
        '[Error  :  ConflictStub] TEST FIXTURE ACTIVE: ColorCycler'
        '[Error  :  ConflictStub] TEST FIXTURE ACTIVE: NetworkPainter'
        '[Error  :SprayPaintPlus] CONFLICT: ColorCycler.dll is loaded'
        '[Error  :SprayPaintPlus] CONFLICT: NetworkPainter.dll is loaded'
        '[Error  :SprayPaintPlus] SprayPaintPlus NOT LOADED'
    ) | Out-Null

    $n = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'TEST FIXTURE ACTIVE' } -Select 'count' -Context $ctx
    Assert-Equal 2 $n.Value 'it counts the matching lines'
    $e = Read-RigValue -From 'hostie' -Reader bepinexlog -Select 'exists' -Context $ctx
    Assert-True $e.Value 'and reports that the file exists'
    $none = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'a line nobody printed' } -Select 'count' -Context $ctx
    Assert-Equal 0 $none.Value 'a substring nobody printed counts zero rather than throwing'

    # The property the whole reader exists for: -Limit clips what comes BACK,
    # never what is COUNTED. A check counting six banner lines with a limit of
    # five must read 6 and fail, not read 5 and pass.
    $clip = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'TEST FIXTURE ACTIVE'; limit = 1 } -Select 'count' -Context $ctx
    Assert-Equal 2 $clip.Value 'a limit clips the returned lines and never the count'
    $rows = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'TEST FIXTURE ACTIVE'; limit = 1 } -Select 'lines.count' -Context $ctx
    Assert-Equal 1 $rows.Value 'and the returned lines really were clipped'

    # An absent log is a distinguishable fact, not a count of zero that a check
    # would read as "the mod printed nothing". Reset-TestHome clears evidence and
    # ClientRig/data but NOT the instance trees, so the log has to go explicitly.
    Reset-TestHome
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $script:TempRoot 'instances')
    $ctx = New-TestContext
    $missing = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'anything' } -Select 'exists' -Context $ctx
    Assert-False $missing.Value 'an instance with no log reports exists=false'
    $missingCount = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'anything' } -Select 'count' -Context $ctx
    Assert-Equal 0 $missingCount.Value 'and counts zero without throwing, so a check reads exists before it believes a count'

    # It is unaffected by the ring the console reader is bounded by. This is the
    # whole point: check 05 declined on console-tee-evicted for lines that were
    # printed, real, and sitting in this file the entire time.
    Reset-TestHome
    $ctx = New-TestContext
    New-TestInstanceLog -Name 'hostie' -Lines (@('filler') * 5000 + @('[Error  :SprayPaintPlus] SprayPaintPlus NOT LOADED')) | Out-Null
    $deep = Read-RigValue -From 'hostie' -Reader bepinexlog -ReaderArgs @{ contains = 'SprayPaintPlus NOT LOADED' } -Select 'count' -Context $ctx
    Assert-Equal 1 $deep.Value 'a line 5000 rows deep is still readable, where a 2000-line ring would have dropped it'

    # The observation is a real one: it carries its ReaderArgs, so a baseline
    # taken through this reader can be re-read by Assert-RigChange.
    Assert-Equal 'bepinexlog' $deep.Reader 'the observation names the reader'
    Assert-Equal 'SprayPaintPlus NOT LOADED' $deep.ReaderArgs['contains'] 'and carries the ReaderArgs it was read with, which is what Assert-RigChange re-reads from'
    Assert-Match $deep.Source 'FILE ' 'and says it came from a file rather than from a GET'
}

function Test-Evidence {
    if (-not (Test-SectionSelected 'evidence')) { return }
    Start-Section 'the evidence bundle'
    Reset-TestHome
    $ctx = New-TestContext -CheckName 'the glow check'

    Invoke-Quiet { Use-Rig -Purpose 'evidence test' -Context $ctx -Body {
        param($c)
        Invoke-RigAction -On 'hostie' -Path '/host' -Body @{ world = 'Lunar' } -Blocking -Context $c | Out-Null
        Read-RigValue -From 'hostie' -Reader status -Select 'hosting' -Context $c | Out-Null
        Read-RigValue -From 'joiner' -Reader status -Select 'phase' -Context $c | Out-Null
        Save-PlaytestConsoleTail -Step 'mid check' -Context $c
    } } | Out-Null

    $dir = $ctx.EvidenceDir
    Assert-FileExists (Join-Path $dir 'lock.txt') 'the lock owner id is in the bundle'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $dir 'lock.txt')) 'owner   : a1b2c3d4' 'and it is the id the launcher printed'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $dir 'lock.txt')) 'released' 'and the release is stamped too'
    Assert-FileExists (Join-Path $dir 'hygiene-reset.txt') 'the hygiene reset report is in the bundle'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $dir 'hygiene-reset.txt')) 'SavePathOverride' 'and it carries what the reset actually did'

    $requests = @(Get-ChildItem -LiteralPath (Join-Path $dir 'requests') -File)
    Assert-True (@($requests).Count -ge 3) 'every request and response is recorded'
    $hostReq = @($requests | Where-Object { $_.Name -like '*host*' })
    Assert-True (@($hostReq).Count -ge 1) 'the action request is one of them'
    $body = Get-Content -Raw -LiteralPath $hostReq[0].FullName | ConvertFrom-Json
    Assert-Equal 'hostie' $body.instance 'the record names the instance'
    Assert-Equal 'POST'   $body.method   'the record names the method'
    Assert-Match $body.requestBody 'Lunar' 'the record carries the request body that was sent'
    Assert-True  ($null -ne $body.response) 'the record carries the response that came back'
    Assert-True  ($null -ne $body.utc) 'the record is timestamped'

    $obs = @(Get-ChildItem -LiteralPath (Join-Path $dir 'observations') -File)
    Assert-True (@($obs).Count -ge 1) 'every observation is recorded separately from its request'
    $o = Get-Content -Raw -LiteralPath $obs[0].FullName | ConvertFrom-Json
    Assert-Equal 'status' $o.reader 'the observation record names the reader'
    Assert-True  ([bool]$o.request) 'and points back at the request it came from'

    Assert-FileExists (Join-Path $dir 'console\hostie.tail.txt') 'a per-step console tail is captured per instance'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $dir 'console\hostie.tail.txt')) 'mid check' 'and it is labelled with the step'

    $launcher = @(Get-ChildItem -LiteralPath (Join-Path $dir 'launcher') -File)
    Assert-True (@($launcher).Count -ge 2) 'every launcher invocation is recorded'
    Assert-Match (Get-Content -Raw -LiteralPath $launcher[0].FullName) 'testrig' 'with the command line as it was run'

    # Ordering: the sequence numbers make the run replayable in order.
    $names = @($requests | ForEach-Object { $_.Name } | Sort-Object)
    Assert-Match $names[0] '^\d{4}-' 'requests are numbered in the order they happened'

    # A check with no evidence directory must not throw: evidence is a record,
    # never a dependency of the thing being recorded.
    Reset-TestHome
    $ctx = New-TestContext -NoEvidence
    Assert-NoThrow { Read-RigValue -From 'hostie' -Reader status -Select 'role' -Context $ctx } 'a context with no evidence directory still works'
    Assert-NoThrow { Save-PlaytestConsoleTail -Step 'x' -Context $ctx } 'a console tail with nowhere to go is a no-op, not an error'
}

function Test-SaveTier {
    if (-not (Test-SectionSelected 'savetier')) { return }
    Start-Section 'the developer save folder is listed, never read and never written'
    Reset-TestHome

    $a = Join-Path $script:SaveRoot 'Luna\world.xml'
    $b = Join-Path $script:SaveRoot 'Mars2\world.xml'
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $a) | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $b) | Out-Null
    Set-Content -LiteralPath $a -Value 'AAAAAAAA' -NoNewline -Encoding ascii
    Set-Content -LiteralPath $b -Value 'BBBBBBBB' -NoNewline -Encoding ascii
    $stamp = [DateTime]::new(2026, 7, 2, 10, 0, 0, [DateTimeKind]::Utc)
    (Get-Item $a).LastWriteTimeUtc = $stamp
    (Get-Item $b).LastWriteTimeUtc = $stamp

    $inv = Get-PlaytestSaveInventory
    Assert-True  $inv.exists 'the inventory finds the folder'
    Assert-Equal 2 $inv.fileCount 'it counts the files'
    Assert-Match ($inv.lines -join "`n") 'Luna.world\.xml\|8\|' 'each line carries the relative path and the size'
    Assert-True  ($inv.sha256.Length -eq 64) 'the listing hashes to a sha256'

    # THE property that proves it is a listing and not a content read: change the
    # BYTES without changing the length or the write time, and the hash must not
    # move. If this ever fails, something started opening the developer's saves.
    $before = $inv.sha256
    Set-Content -LiteralPath $a -Value 'ZZZZZZZZ' -NoNewline -Encoding ascii
    (Get-Item $a).LastWriteTimeUtc = $stamp
    $after = (Get-PlaytestSaveInventory).sha256
    Assert-Equal $before $after 'the hash is over the LISTING: no file is ever opened'

    # And nothing is written into the folder by taking an inventory.
    $treeBefore = @(Get-ChildItem -LiteralPath $script:SaveRoot -Recurse | ForEach-Object { "$($_.FullName)|$($_.Length)" }) -join "`n"
    Get-PlaytestSaveInventory | Out-Null
    Get-PlaytestSaveInventory | Out-Null
    $treeAfter = @(Get-ChildItem -LiteralPath $script:SaveRoot -Recurse | ForEach-Object { "$($_.FullName)|$($_.Length)" }) -join "`n"
    Assert-Equal $treeBefore $treeAfter 'taking an inventory writes nothing into the tier-1 folder'

    # A genuine change IS reported.
    $inv2 = Get-PlaytestSaveInventory
    Set-Content -LiteralPath (Join-Path $script:SaveRoot 'Luna\new.xml') -Value 'x' -NoNewline -Encoding ascii
    $inv3 = Get-PlaytestSaveInventory
    $cmp = Compare-PlaytestSaveInventory -Before $inv2 -After $inv3
    Assert-False $cmp.Identical 'a new file in the developer folder is reported as changed'
    Assert-Equal 1 @($cmp.Added).Count 'and the added entry is named'
    Assert-True (Compare-PlaytestSaveInventory -Before $inv2 -After $inv2).Identical 'an unchanged folder reports identical'

    # A missing folder is a fact, not a crash.
    $missing = Get-PlaytestSaveInventory -Root (Join-Path $script:TempRoot 'nope')
    Assert-False $missing.exists 'a save root that does not exist is reported, not fatal'
    Assert-Equal 'no-such-root' $missing.sha256 'and its hash says so'
}

function Test-Suite {
    if (-not (Test-SectionSelected 'suite')) { return }
    Start-Section 'the runner end to end'
    Reset-TestHome
    New-TestInstanceData -Name 'hostie' -Role 'host'   -DeployedBytes 4096 | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' -DeployedBytes 4096 | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $script:SaveRoot 'Luna') | Out-Null
    Set-Content -LiteralPath (Join-Path $script:SaveRoot 'Luna\world.xml') -Value 'x' -Encoding ascii

    $instances = @(
        @{ Name = 'hostie'; Role = 'host';   World = 'Lunar' }
        @{ Name = 'joiner'; Role = 'client'; ConnectTo = 'hostie' }
    )
    $binary = @{ Mod = 'net.example'; ConfigEntryCount = 4; ConfigGroupCount = 2 }

    Register-PlaytestCheck -Name 'a passing check' -Summary 'reads the truth' -Instances $instances -Binary $binary -Body {
        param($ctx)
        Assert-RigValue -From 'hostie' -Reader status -Select 'hosting' -Is $true -Because 'the host must actually be hosting' -Context $ctx
    }
    Register-PlaytestCheck -Name 'a failing check' -Summary 'reads a wrong value' -Instances $instances -Binary $binary -Body {
        param($ctx)
        Assert-RigValue -From 'hostie' -Reader status -Select 'role' -Is 'joinedClient' -Because 'wrong on purpose, to prove a fail is reachable' -Context $ctx
    }
    Register-PlaytestCheck -Name 'an unattested check' -Summary 'never says what it ran against' -Instances $instances -Body {
        param($ctx)
        Assert-RigValue -From 'hostie' -Reader status -Select 'hosting' -Is $true -Because 'true, but against an unknown build' -Context $ctx
    }

    Assert-Equal 3 @(Get-PlaytestChecks).Count 'three checks registered'
    $run = Invoke-Quiet { Invoke-PlaytestSuite -Name 'tests' -EvidenceRoot (Join-Path $script:TempRoot 'evidence') }

    Assert-Equal 3 @($run.Results).Count 'the runner ran all three'
    Assert-Equal 'pass' ($run.Results | Where-Object { $_.Name -eq 'a passing check' }).Outcome 'the passing check passes'
    Assert-Equal 'fail' ($run.Results | Where-Object { $_.Name -eq 'a failing check' }).Outcome 'the failing check FAILS (a fail is genuinely reachable)'
    $un = ($run.Results | Where-Object { $_.Name -eq 'an unattested check' })
    Assert-Equal 'inconclusive' $un.Outcome 'a check that never attested its binary CANNOT pass'
    Assert-Equal 'binary-not-attested' $un.Detector 'and the reason is named'
    Assert-Match $un.Message 'says nothing about any particular build' 'and it explains why a green result there would be worthless'

    Assert-Equal 1 $run.ExitCode 'a suite with a failure exits 1'
    Assert-True  $run.Tier1Identical 'the developer save folder was untouched across the run'

    # The lock is released and re-taken PER CHECK, which is what buys each check
    # the state-hygiene reset that hangs off a new lock.
    $calls = Get-FakeCallStrings
    Assert-Equal 3 (@($calls | Where-Object { $_ -like 'lock *' }).Count) 'the lock is taken once per check, not once per suite'
    Assert-Equal 3 (@($calls | Where-Object { $_ -like 'unlock *' }).Count) 'and released once per check'
    Assert-Equal 0 (@($calls | Where-Object { $_ -match '(^|\s)-All(\s|$)' }).Count) 'no -All anywhere in a whole suite run'

    $root = Join-Path $script:TempRoot 'evidence'
    Assert-FileExists (Join-Path $root 'run.json') 'the run report is written'
    Assert-FileExists (Join-Path $root 'run.md') 'and a human-readable summary beside it'
    Assert-FileExists (Join-Path $root 'save-inventory-before.txt') 'the tier-1 inventory is captured before the run'
    Assert-FileExists (Join-Path $root 'save-inventory-after.txt') 'and after it'
    Assert-FileExists (Join-Path $root 'save-inventory.verdict.txt') 'with a verdict beside them'
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $root 'save-inventory.verdict.txt')) 'IDENTICAL' 'and the verdict says identical'
    $json = Get-Content -Raw -LiteralPath (Join-Path $root 'run.json') | ConvertFrom-Json
    Assert-Equal 1 $json.failed 'run.json counts the failure'
    Assert-Equal 1 $json.passed 'run.json counts the pass'
    Assert-Equal 1 $json.inconclusive 'run.json counts the inconclusive'
    Assert-Equal 3 @($json.checks).Count 'run.json lists every check'
    Assert-True  ([bool]$json.checks[0].evidence) 'and points each one at its own evidence folder'
    foreach ($r in $run.Results) { Assert-FileExists (Join-Path $r.EvidenceDir 'check.json') "check.json exists for '$($r.Name)'" }

    # A suite with nothing worse than an inconclusive exits 2, so a caller can
    # tell "the mod is broken" from "the rig was flaky".
    Reset-TestHome
    New-TestInstanceData -Name 'hostie' -Role 'host' | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' | Out-Null
    $script:Fake.ConnectJoins = $false
    Register-PlaytestCheck -Name 'a flaky check' -Instances $instances -Binary $binary -Body { param($ctx) 'never reached' }
    $run = Invoke-Quiet { Invoke-PlaytestSuite -Name 'tests' -EvidenceRoot (Join-Path $script:TempRoot 'evidence2') }
    Assert-Equal 'inconclusive' $run.Results[0].Outcome 'a rig that would not come up is inconclusive'
    Assert-Equal 2 $run.ExitCode 'a suite whose worst result is inconclusive exits 2, not 1'

    # A degraded pass survives the whole runner and is still visibly degraded.
    Reset-TestHome
    New-TestInstanceData -Name 'hostie' -Role 'host' | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' | Out-Null
    $script:Fake.ConnectFailures = 2
    Register-PlaytestCheck -Name 'a degraded check' -Instances $instances -Binary $binary -Body {
        param($ctx)
        Assert-RigValue -From 'hostie' -Reader status -Select 'hosting' -Is $true -Because 'the host must be hosting' -Context $ctx
    }
    $run = Invoke-Quiet { Invoke-PlaytestSuite -Name 'tests' -EvidenceRoot (Join-Path $script:TempRoot 'evidence3') }
    Assert-Equal 'pass' $run.Results[0].Outcome 'a check whose connect took three goes still passes'
    Assert-Equal 'pass (degraded, 3 attempts)' $run.Results[0].Text 'but it is reported as degraded with the attempt count'
    Assert-Equal 0 $run.ExitCode 'a degraded pass still exits 0'
    Assert-True ($run.Results[0].Detectors -contains 'connect-first-attempt') 'and the detector that fired is on the record'

    # -Only selects.
    Reset-TestHome
    New-TestInstanceData -Name 'hostie' -Role 'host' | Out-Null
    New-TestInstanceData -Name 'joiner' -Role 'client' | Out-Null
    Register-PlaytestCheck -Name 'alpha check' -Instances $instances -Binary $binary -Body { param($ctx) }
    Register-PlaytestCheck -Name 'beta check'  -Instances $instances -Binary $binary -Body { param($ctx) }
    $run = Invoke-Quiet { Invoke-PlaytestSuite -Name 'tests' -Only 'alpha*' -EvidenceRoot (Join-Path $script:TempRoot 'evidence4') }
    Assert-Equal 1 @($run.Results).Count '-Only selects a subset of the checks'
    Assert-Equal 'alpha check' $run.Results[0].Name 'and it is the right one'
}

function Test-Surface {
    if (-not (Test-SectionSelected 'surface')) { return }
    Start-Section 'library surface: the helpers everything else stands on'
    Reset-TestHome

    # DRIFT GUARD. The reader catalogue and the ValidateSet on the three verbs are
    # written out separately, so a reader added to one and not the others is the
    # obvious mistake. This is the test that catches it.
    $catalogue = @((Get-PlaytestReaders).Keys)
    Assert-True (@($catalogue).Count -ge 10) 'the reader catalogue is populated'
    foreach ($verb in @('Read-RigValue', 'Assert-RigValue', 'Assert-RigAgreement')) {
        $set = ((Get-Command $verb).Parameters['Reader'].Attributes |
                Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] } |
                Select-Object -First 1).ValidValues
        $missing = @($catalogue | Where-Object { @($set) -notcontains $_ })
        $extra   = @(@($set) | Where-Object { $catalogue -notcontains $_ })
        Assert-Equal 0 @($missing).Count "$verb accepts every reader in the catalogue (missing: $($missing -join ', '))"
        Assert-Equal 0 @($extra).Count   "$verb accepts no reader the catalogue does not document (extra: $($extra -join ', '))"
    }
    foreach ($r in $catalogue) {
        Assert-True ([bool](Get-PlaytestReaders)[$r]) "reader '$r' documents what it reads"
    }

    # Instance name to control-plane port.
    Assert-Equal 27701 (Resolve-RigInstancePort -Name 'hostie') 'an instance name resolves to its control-plane port'
    Assert-Equal 27702 (Resolve-RigInstancePort -Name 'joiner') 'and each instance gets its own'
    Assert-Equal 'instance-not-provisioned' (Get-OutcomeRecord { Resolve-RigInstancePort -Name 'nope' }).Detector 'an unknown instance is inconclusive rather than a wrong port'

    # The clock and the sleep are injected, which is what makes a 300 second
    # barrier cost nothing here and behave identically for real.
    $t0 = Get-PlaytestNowUtc
    Wait-PlaytestSeconds 90
    Assert-Equal 90 (((Get-PlaytestNowUtc) - $t0).TotalSeconds) 'the injected sleep advances the injected clock'
    Assert-NoThrow { Wait-PlaytestSeconds 0 } 'a zero-second wait is a no-op'
    Assert-Match (Get-PlaytestStamp $t0) '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$' 'timestamps are ISO 8601 UTC'

    # Evidence must never be able to break the run it is recording.
    Assert-Equal 'null' (ConvertTo-PlaytestJson $null) 'a null serialises rather than throwing'
    $cyclic = [pscustomobject]@{ name = 'a' }
    $cyclic | Add-Member -NotePropertyName self -NotePropertyValue $cyclic
    Assert-NoThrow { ConvertTo-PlaytestJson $cyclic -Depth 3 } 'a value that will not serialise does not throw the run over'
    Assert-NoThrow { ConvertTo-PlaytestJson ([scriptblock]::Create('1')) } 'nor does a scriptblock'

    # The bundle layout, and the per-check folders inside it.
    Reset-TestHome
    $root = Join-Path $script:TempRoot 'bundle'
    $b = New-PlaytestEvidenceBundle -Root $root -SuiteName 'tests'
    Assert-FileExists $root 'the bundle root is created'
    Assert-FileExists (Join-Path $root 'checks') 'with a checks folder'
    Assert-True ([bool]$b.StartedUtc) 'and a start stamp'
    $d1 = New-PlaytestCheckEvidence -BundleRoot $root -Index 1 -CheckName 'first check'
    $d2 = New-PlaytestCheckEvidence -BundleRoot $root -Index 2 -CheckName 'first check'
    Assert-Match (Split-Path -Leaf $d1) '^01-first-check$' 'a check folder is numbered and slugged'
    Assert-True ($d1 -ne $d2) 'two checks with the SAME name get different folders (the index disambiguates)'
    foreach ($sub in @('requests', 'observations', 'console', 'launcher')) {
        Assert-FileExists (Join-Path $d1 $sub) "the check folder has a '$sub' subfolder"
    }

    # Write-PlaytestEvidence: overwrite by default, append on demand, sequence
    # numbers strictly increasing so a bundle replays in order.
    $ctx = New-TestContext
    Write-PlaytestEvidence -Name 'note.txt' -Content 'one' -Context $ctx | Out-Null
    Write-PlaytestEvidence -Name 'note.txt' -Content 'two' -Context $ctx | Out-Null
    Assert-Equal 'two' (Get-Content -Raw -LiteralPath (Join-Path $ctx.EvidenceDir 'note.txt')).Trim() 'writing evidence twice overwrites by default'
    Write-PlaytestEvidence -Name 'log.txt' -Content 'one' -Context $ctx | Out-Null
    Write-PlaytestEvidence -Name 'log.txt' -Content 'two' -Append -Context $ctx | Out-Null
    Assert-Match (Get-Content -Raw -LiteralPath (Join-Path $ctx.EvidenceDir 'log.txt')) 'one[\s\S]*two' '-Append keeps what was there'
    $a = Get-PlaytestNextSequence -Context $ctx
    $bq = Get-PlaytestNextSequence -Context $ctx
    Assert-True ($bq -gt $a) 'evidence sequence numbers strictly increase'

    # Restarting one instance is stop-then-start BY NAME, and needs the lock.
    Reset-TestHome
    $ctx = New-TestContext
    Invoke-Quiet { Restart-RigInstance -Name 'joiner' -Reason 'a test' -Context $ctx }
    $calls = Get-FakeCallStrings
    Assert-Equal 1 (@($calls | Where-Object { $_ -like '*stop -Target joiner*' }).Count) 'a restart stops that one instance'
    Assert-Equal 1 (@($calls | Where-Object { $_ -like '*start -Target joiner*' }).Count) 'and starts it again'
    Assert-Equal 0 (@($calls | Where-Object { $_ -match '(^|\s)-All(\s|$)' }).Count) 'and never reaches for -All'
    Assert-True  (@($ctx.Started) -contains 'joiner') 'a restarted instance is registered for teardown'
    $ctxNoLock = New-PlaytestContext -CheckName 'x' -Instances @(@{ Name = 'joiner' })
    Assert-Throws { Restart-RigInstance -Name 'joiner' -Context $ctxNoLock } 'a restart without the lock owner id is refused' 'without the rig lock owner'

    # The refresh discipline: at most once a minute, and only ever as a side
    # effect of the harness driving something. There is no background refresher
    # anywhere in this library and there must never be one.
    Reset-TestHome
    $ctx = New-TestContext
    Invoke-Quiet { Update-PlaytestLockIfDue -Context $ctx }
    Assert-Equal 0 (@(Get-FakeCallStrings | Where-Object { $_ -like 'refresh-lock*' }).Count) 'a refresh inside the last minute is skipped'
    $ctx.LastRefreshUtc = $script:FakeNow.AddMinutes(-2)
    Invoke-Quiet { Update-PlaytestLockIfDue -Context $ctx }
    Assert-Equal 1 (@(Get-FakeCallStrings | Where-Object { $_ -like 'refresh-lock*' }).Count) 'a refresh past the minute happens'
    Invoke-Quiet { Update-PlaytestLockIfDue -Context $ctx }
    Assert-Equal 1 (@(Get-FakeCallStrings | Where-Object { $_ -like 'refresh-lock*' }).Count) 'and immediately re-checking does not refresh again'
    $ctxNoOwner = New-PlaytestContext -CheckName 'x' -Instances @(@{ Name = 'joiner' })
    Assert-NoThrow { Update-PlaytestLockIfDue -Context $ctxNoOwner } 'a context with no lock owner refreshes nothing and does not throw'

    # Stopping nothing is a no-op, not an error.
    Reset-TestHome
    $ctx = New-TestContext
    Invoke-Quiet { Stop-RigInstances -Context $ctx }
    Assert-Equal 0 (@(Get-FakeCallStrings | Where-Object { $_ -like '*stop *' }).Count) 'teardown with nothing started issues no stops'

    # Detector bookkeeping is a set, not a list: one flaky endpoint retried three
    # times is one detector on the report.
    $ctx = New-TestContext
    Add-PlaytestDetector -Context $ctx -Name 'connect-first-attempt'
    Add-PlaytestDetector -Context $ctx -Name 'connect-first-attempt'
    Add-PlaytestDetector -Context $ctx -Name 'boot-timeout'
    Assert-Equal 2 @($ctx.Detectors).Count 'a detector that fires twice is recorded once'
    Assert-NoThrow { Add-PlaytestDetector -Context $ctx -Name '' } 'an empty detector name is ignored rather than recorded'
}

function Test-Registration {
    if (-not (Test-SectionSelected 'registration')) { return }
    Start-Section 'check registration'
    Reset-TestHome

    Assert-Throws { Register-PlaytestCheck -Name 'x' -Instances @(@{ Role = 'host' }) -Body { } } 'an instance with no name is refused' 'needs a Name'
    Assert-Throws { Register-PlaytestCheck -Name 'x' -Instances @(@{ Name = 'a'; Role = 'server' }) -Body { } } 'an unknown role is refused' "must be 'host' or 'client'"

    Clear-PlaytestChecks
    Register-PlaytestCheck -Name 'defaults' -Instances @(@{ Name = 'a' }) -Body { }
    $c = @(Get-PlaytestChecks)[0]
    Assert-Equal 'client' $c.Instances[0].Role 'an instance with no role defaults to client'
    Assert-Match $c.Purpose 'defaults' 'the lock purpose defaults to naming the check'
    Assert-Equal 20 $c.TtlMinutes 'the lock TTL defaults to longer than the launcher default, because a check outlives 10 minutes'

    Clear-PlaytestChecks
    Assert-Equal 0 @(Get-PlaytestChecks).Count 'the registration list can be cleared between suites'
}

# =============================================================================
# Run
# =============================================================================

Write-Host 'TestRig playtest harness: offline test suite'
Write-Host "  library : $(Join-Path $PSScriptRoot 'playtest-lib.ps1')"

if (Test-Path -LiteralPath $script:RealLock) {
    $script:RealBefore = (Get-FileHash -LiteralPath $script:RealLock -Algorithm SHA256).Hash
    Write-Host "  real rig lock present, sha256 $($script:RealBefore.Substring(0,16)) (verified untouched at the end)"
}
else {
    Write-Host '  real rig lock absent (verified still absent at the end)'
}

$testHome = New-TestHome
Write-Host "  temp    : $testHome"

try {
    Test-Paths
    Test-Primitives
    Test-Outcomes
    Test-FlakeTaxonomy
    Test-Authority
    Test-Actions
    Test-BinaryGate
    Test-Teardown
    Test-BringUp
    Test-JoinHelper
    Test-BepInExLogReader
    Test-Evidence
    Test-SaveTier
    Test-Suite
    Test-Surface
    Test-Registration
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
