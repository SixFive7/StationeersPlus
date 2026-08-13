# =============================================================================
# TestRig playtest harness - shared library
# =============================================================================
# A playtest CHECK is a piece of a mod's behaviour that can only be confirmed by
# running the game. This library is what a check is written against. It is
# deliberately mod-agnostic: nothing here names a mod, a prefab, a setting or a
# guid, and nothing may be added that does. A check supplies all of that.
#
# Read TestRig/playtest/CLAUDE.md first if you are writing a check. This header
# states only the four decisions that shape every function below.
#
# 1. THREE OUTCOMES, NEVER TWO: pass, fail, inconclusive.
#    A harness that reports rig problems as mod defects is worse than no harness,
#    because it spends a developer's afternoon on a bug that is not there. So the
#    only thing that can produce 'fail' is an Assert-Rig* verb that read a value
#    and found the wrong one. Everything else, including an unclassified throw
#    out of a check body, is 'inconclusive'. That direction is chosen on purpose:
#    an inconclusive result costs a re-run, a false fail costs a day.
#    A check that needed more than one attempt is 'pass (degraded, N attempts)'
#    and never a clean pass, so flakiness stays visible after it is survived.
#
# 2. ASSERT ON THE AUTHORITY, NOT ON THE ACTOR'S REPORT.
#    On 2026-08-09 a /connect answered ok while nothing had joined, and an
#    /inventory/arm reported confirmed while the host-side check was
#    inconclusive. An endpoint's own 200 is a statement about the request, not
#    about the world. So the shapes here split in two:
#      Invoke-RigAction  MAKES something happen and hands back a
#                        Playtest.ActionResult, which no assert verb accepts.
#      Read-RigValue     READS a named value from a NAMED instance through a
#                        NAMED reader, and hands back a Playtest.Observation,
#                        which is the only thing the assert verbs take.
#    Assert-RigValue takes an instance NAME, never a response object, so there is
#    no way to spell "assert on what the actor said" without going out of your
#    way. There is deliberately no Assert-True, no Assert-Ok and no bare-boolean
#    assert of any kind; Assert-RigOk and Assert-RigResponse exist only to throw
#    an explanation at anyone who reaches for one.
#
# 3. GUARANTEED TEARDOWN, AND BY NAME.
#    Use-Rig acquires the lock, runs the body, and in a finally stops the
#    instances IT started, one -Stop -Instance <name> at a time, then releases.
#    It never runs -Stop -All: that reaches another session's live test. A runner
#    that dies mid-suite must not leave the rig held.
#
# 4. THE BINARY UNDER TEST IS ASSERTED BEFORE ANYTHING ELSE.
#    A live run in August 2026 nearly measured a stale seeded DLL and was caught
#    by luck. Assert-BinaryUnderTest checks the provision stamp, the deployed
#    file against the build under test, and a live GET /config entry count from
#    inside each running instance. A check that never calls it CANNOT report
#    pass: the runner downgrades it to inconclusive with detector
#    'binary-not-attested'.
#
# COMPOSITION ROOT. This library talks to nothing by itself. Two seams are
# injected through Initialize-PlaytestLib: -Transport (one HTTP call to one
# instance's control plane) and -RigCommand (one testrig.ps1 invocation).
# TestRig/playtest/playtest.ps1 is the composition root that wires the real ones;
# it dot-sources the launcher's client library for Invoke-Control, which returns an object,
# rather than parsing the stdout of -Call, which only prints JSON. The offline
# suite wires fakes. Unwired, every driving verb throws a message naming the
# runner, which is what keeps this file honestly testable without a game.
#
# Tests: TestRig/playtest/playtest-lib.tests.ps1 (offline, no game, no network,
# temp directories, never the real rig). Run it after any change here:
#     pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1
#
# Everything here is prefixed Playtest* or *-Rig* so dot-sourcing cannot collide
# with a launcher's own helpers.
# =============================================================================

# ---- injection and paths ---------------------------------------------------
# Every outward path and every outward call is set in one place, so the whole
# harness can be pointed at a temp directory and a set of fakes. Same shape as
# Initialize-RigLockPaths in TestRig/rig-lock.ps1, and for the same reason: a
# mechanism that cannot be redirected cannot be tested.

$script:PlaytestRigHome        = $null
$script:PlaytestEvidenceRoot   = $null
$script:PlaytestTransport      = $null
$script:PlaytestRigCommand     = $null
$script:PlaytestRegistry       = $null
$script:PlaytestClock          = { [DateTime]::UtcNow }
$script:PlaytestSleep          = { param([double] $Seconds) Start-Sleep -Milliseconds ([int]($Seconds * 1000)) }
$script:PlaytestTier1SaveRoot  = $null
$script:PlaytestChecks         = @()
$script:PlaytestContext        = $null
$script:PlaytestSuiteName      = 'playtest'

function Initialize-PlaytestLib {
    <#
    .SYNOPSIS
        Point the harness at a rig, an evidence root and a pair of seams.

    .DESCRIPTION
        Called once by the composition root (TestRig/playtest/playtest.ps1) before
        any check runs, and once per test by the offline suite with fakes.

        -Transport is the ONE HTTP call:
            param([int] $Port, [string] $Path, [string] $BodyJson, [int] $TimeoutSec)
            returns the parsed response object, or throws. A non-2xx answer must
            throw with the response body in the message, because that body is
            usually the only thing that explains a refusal.

        -RigCommand is the ONE launcher invocation:
            param([string[]] $ArgList)
            returns an object with ExitCode, StdOut and StdErr. It must not throw
            on a non-zero exit; the caller decides what a failure means.

        -Registry returns the client rig's registry entries (rig.json), so an
        instance name can be resolved to a control-plane port without the harness
        knowing where the file lives.
    #>
    param(
        [string] $RigHome,
        [string] $EvidenceRoot,
        [scriptblock] $Transport,
        [scriptblock] $RigCommand,
        [scriptblock] $Registry,
        [scriptblock] $Clock,
        [scriptblock] $Sleep,
        [string] $Tier1SaveRoot
    )
    if ($PSBoundParameters.ContainsKey('RigHome'))       { $script:PlaytestRigHome       = $RigHome }
    if ($PSBoundParameters.ContainsKey('EvidenceRoot'))  { $script:PlaytestEvidenceRoot  = $EvidenceRoot }
    if ($PSBoundParameters.ContainsKey('Transport'))     { $script:PlaytestTransport     = $Transport }
    if ($PSBoundParameters.ContainsKey('RigCommand'))    { $script:PlaytestRigCommand    = $RigCommand }
    if ($PSBoundParameters.ContainsKey('Registry'))      { $script:PlaytestRegistry      = $Registry }
    if ($PSBoundParameters.ContainsKey('Clock'))         { $script:PlaytestClock         = $Clock }
    if ($PSBoundParameters.ContainsKey('Sleep'))         { $script:PlaytestSleep         = $Sleep }
    if ($PSBoundParameters.ContainsKey('Tier1SaveRoot')) { $script:PlaytestTier1SaveRoot = $Tier1SaveRoot }
}

function Get-PlaytestEvidenceRoot { return $script:PlaytestEvidenceRoot }
function Get-PlaytestRigHome      { return $script:PlaytestRigHome }
function Get-PlaytestTier1SaveRoot { return $script:PlaytestTier1SaveRoot }
function Get-PlaytestContext      { return $script:PlaytestContext }
function Get-PlaytestChecks       { return $script:PlaytestChecks }

function Clear-PlaytestChecks {
    # The registration list is script state, so a runner that loads two suites in
    # one process has to be able to start from empty.
    $script:PlaytestChecks = @()
}

# ---- primitives ------------------------------------------------------------

function Get-PlaytestNowUtc {
    return (& $script:PlaytestClock)
}

function Get-PlaytestStamp {
    param([DateTime] $When)
    if (-not $PSBoundParameters.ContainsKey('When')) { $When = Get-PlaytestNowUtc }
    return $When.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
}

function Wait-PlaytestSeconds {
    param([double] $Seconds)
    if ($Seconds -le 0) { return }
    & $script:PlaytestSleep $Seconds
}

function ConvertTo-PlaytestSlug {
    # File-name safe, stable, lower case. Used for evidence folders and files, so
    # a check name with a slash or a colon in it cannot produce an unwritable path.
    param([string] $Text)
    if (-not $Text) { return 'unnamed' }
    $s = ($Text -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
    if (-not $s) { return 'unnamed' }
    if ($s.Length -gt 60) { $s = $s.Substring(0, 60).Trim('-') }
    return $s
}

function ConvertTo-PlaytestJson {
    param($Value, [int] $Depth = 12)
    if ($null -eq $Value) { return 'null' }
    try { return ($Value | ConvertTo-Json -Depth $Depth) }
    catch { return ('"<not serialisable: ' + ($_.Exception.Message -replace '"', "'") + '>"') }
}

function Select-PlaytestPath {
    <#
    .SYNOPSIS
        Read a dotted path out of a parsed response.

    .DESCRIPTION
        Supports 'a.b.c', array indexing 'connectedClients[0].username', and the
        pseudo-member 'count' on any collection. A path that does not resolve
        returns $null rather than throwing, because "the field is absent" is a
        legitimate observation and the assert verb is what decides whether absent
        is wrong. '.' or an empty path returns the whole object.
    #>
    param($Object, [string] $Path)
    if (-not $Path -or $Path -eq '.') { return $Object }
    $cur = $Object
    foreach ($rawPart in ($Path -split '\.')) {
        if ($null -eq $cur) { return $null }
        $part = $rawPart
        $indexes = @()
        $m = [regex]::Match($part, '^([^\[\]]*)((\[\d+\])*)$')
        if ($m.Success) {
            $part = $m.Groups[1].Value
            foreach ($im in [regex]::Matches($m.Groups[2].Value, '\[(\d+)\]')) {
                $indexes += [int]$im.Groups[1].Value
            }
        }
        if ($part) {
            $isCollection = ($cur -is [System.Array]) -or (($cur -is [System.Collections.IList]) -and -not ($cur -is [System.Collections.IDictionary]))
            if ($cur -is [System.Collections.IDictionary]) {
                if ($cur.Contains($part))      { $cur = $cur[$part] }
                elseif ($part -eq 'count')     { $cur = $cur.Count }
                else                           { return $null }
            }
            elseif ($part -eq 'count' -and $isCollection) {
                $cur = @($cur).Count
            }
            elseif ($part -eq 'count' -and $null -eq $cur.PSObject.Properties[$part]) {
                # A single object standing in for a one-element collection.
                $cur = 1
            }
            else {
                $prop = $cur.PSObject.Properties[$part]
                if (-not $prop) { return $null }
                $cur = $prop.Value
            }
        }
        foreach ($i in $indexes) {
            $arr = @($cur)
            if ($i -ge $arr.Count) { return $null }
            $cur = $arr[$i]
        }
    }
    return $cur
}

function Test-PlaytestValueEqual {
    # Comparison for assertions. Booleans compare as booleans, numbers as numbers,
    # everything else as case-insensitive strings, because a control plane answers
    # in JSON and 'True' from one endpoint and $true from another are the same
    # observation. A test that turns on the casing of a role name is a test that
    # will break for no reason.
    param($Expected, $Actual)
    if ($null -eq $Expected -and $null -eq $Actual) { return $true }
    if ($null -eq $Expected -or $null -eq $Actual)  { return $false }
    if (($Expected -is [bool]) -or ($Actual -is [bool])) {
        $e = if ($Expected -is [bool]) { $Expected } else { "$Expected" -eq 'true' -or "$Expected" -eq 'True' -or "$Expected" -eq '1' }
        $a = if ($Actual   -is [bool]) { $Actual }   else { "$Actual"   -eq 'true' -or "$Actual"   -eq 'True' -or "$Actual"   -eq '1' }
        return ($e -eq $a)
    }
    $en = 0.0; $an = 0.0
    if ([double]::TryParse("$Expected", [ref]$en) -and [double]::TryParse("$Actual", [ref]$an)) {
        return ($en -eq $an)
    }
    return ([string]::Equals("$Expected", "$Actual", [System.StringComparison]::OrdinalIgnoreCase))
}

# ---- signals: how a check ends ---------------------------------------------

function New-PlaytestSignal {
    <#
    .SYNOPSIS
        The exception a check ends on, carrying which of the three outcomes it is.

    .DESCRIPTION
        Kind is 'fail' or 'inconclusive'. Nothing else may produce 'fail': the
        assert verbs are the only callers that pass it. The classification travels
        in the exception's Data dictionary rather than in the message text, so a
        message can be rewritten without silently reclassifying a result.
    #>
    param(
        [Parameter(Mandatory)][ValidateSet('fail', 'inconclusive')] [string] $Kind,
        [Parameter(Mandatory)] [string] $Message,
        [string] $Detector = '',
        $Detail
    )
    $ex = [System.Exception]::new($Message)
    $ex.Data['PlaytestKind']     = $Kind
    $ex.Data['PlaytestDetector'] = $Detector
    $ex.Data['PlaytestDetail']   = (ConvertTo-PlaytestJson $Detail)
    return $ex
}

function Get-PlaytestSignal {
    # Find our marker on an exception or anywhere down its inner chain. PowerShell
    # wraps some throws, and a wrapped signal that read as unclassified would turn
    # a real assertion failure into an inconclusive.
    param($Exception)
    $e = $Exception
    $guard = 0
    while ($e -and $guard -lt 12) {
        if ($e.Data -and $e.Data.Contains('PlaytestKind')) {
            return [pscustomobject]@{
                Kind     = [string]$e.Data['PlaytestKind']
                Detector = [string]$e.Data['PlaytestDetector']
                Detail   = [string]$e.Data['PlaytestDetail']
                Message  = $e.Message
            }
        }
        $e = $e.InnerException
        $guard++
    }
    return $null
}

function Resolve-PlaytestError {
    <#
    .SYNOPSIS
        Turn whatever came out of a check body into one of the three outcomes.

    .DESCRIPTION
        A marked signal keeps its own kind. ANYTHING ELSE is inconclusive, never
        fail. That includes a bug in the check itself, a null-reference in a
        helper, and a launcher that threw. The reasoning is the same one that
        drives the whole harness: an unclassified throw did not observe the mod
        misbehaving, so calling it a mod defect is a lie the developer pays for.
        It is reported loudly, with detector 'unclassified-error' and the full
        error text in the evidence bundle, so it can never pass for a clean run.
    #>
    param($ErrorRecord)
    $sig = Get-PlaytestSignal $ErrorRecord.Exception
    if ($sig) {
        return [pscustomobject]@{
            Outcome  = $sig.Kind
            Detector = $sig.Detector
            Message  = $sig.Message
            Detail   = $sig.Detail
        }
    }
    return [pscustomobject]@{
        Outcome  = 'inconclusive'
        Detector = 'unclassified-error'
        Message  = "The check threw something the harness does not classify, so its result is inconclusive rather than a failure: $($ErrorRecord.Exception.Message)"
        Detail   = (ConvertTo-PlaytestJson @{
            type       = $ErrorRecord.Exception.GetType().FullName
            scriptLine = "$($ErrorRecord.InvocationInfo.ScriptName):$($ErrorRecord.InvocationInfo.ScriptLineNumber)"
            positionMessage = "$($ErrorRecord.InvocationInfo.PositionMessage)"
        })
    }
}

function Set-PlaytestInconclusive {
    <#
    .SYNOPSIS
        End the current check as inconclusive, on purpose.

    .DESCRIPTION
        For a check that discovers it cannot make the observation it came to make:
        a world that did not contain what the check needs, a capability the rig
        does not have yet, a precondition another agent's change removed. There is
        no matching Set-PlaytestFail: failing is what the assert verbs do, and a
        second way to spell it would be the bare-boolean back door this harness
        exists to close.
    #>
    param(
        [Parameter(Mandatory)] [string] $Because,
        [string] $Detector = 'check-declined',
        $Detail
    )
    throw (New-PlaytestSignal -Kind 'inconclusive' -Message $Because -Detector $Detector -Detail $Detail)
}

# ---- the flake taxonomy ----------------------------------------------------
# Each entry is a real detector over a real probe, not a category name. The order
# is significant: resolution is first match, so the specific detectors sit above
# the general ones. Every remedy is bounded; there is no unbounded retry anywhere
# in this file, because an agent that hangs on a wedged rig is worse than one that
# reports inconclusive and frees the lock.

$script:PlaytestFlakes = @(
    [ordered]@{
        Name        = 'connect-first-attempt'
        Summary     = 'POST /connect fails on a first attempt and succeeds on a later one. Documented behaviour: the client is still settling from the previous disconnect.'
        Remedy      = 'retry'
        MaxAttempts = 3
        GapSeconds  = 10
        Reference   = 'TestRig/RESEARCH.md, Plugin lifecycle traps'
        Test        = {
            param($Probe)
            if ($Probe.Kind -ne 'action' -and $Probe.Kind -ne 'transport') { return $false }
            if ((Get-PlaytestBarePath $Probe.Path) -ne '/connect') { return $false }
            if ($Probe.Error) { return $true }
            $r = $Probe.Response
            if ($null -eq $r) { return $true }
            if ("$($r.result)" -eq 'timeout') { return $true }
            return ($null -ne $r.PSObject.Properties['ok'] -and $r.ok -ne $true)
        }
    }
    [ordered]@{
        Name        = 'launchpad-workshop-park'
        Summary     = 'A failed Steam Workshop query parks StationeersLaunchPad on its own error screen forever: loadedPluginCount stuck at 2 with gameInitialized false. It clears on a restart of that instance.'
        Remedy      = 'restart-instance'
        MaxAttempts = 2
        GapSeconds  = 5
        Reference   = 'TestRig/RESEARCH.md, Plugin lifecycle traps'
        Test        = {
            param($Probe)
            $s = $Probe.Status
            if ($null -eq $s) { return $false }
            if ($null -eq $s.PSObject.Properties['loadedPluginCount']) { return $false }
            return (([int]$s.loadedPluginCount -le 2) -and ($s.gameInitialized -ne $true))
        }
    }
    [ordered]@{
        Name        = 'host-not-hosting'
        Summary     = 'POST /host answered but the host-side authority disagrees: /status.hosting is not true, or /status.role is not listenHost. NetworkServer.Host() gives up quietly after three failed binds, so the call returning proves nothing.'
        Remedy      = 'abort'
        MaxAttempts = 1
        GapSeconds  = 0
        Reference   = 'TestRig/MANUAL.md, Working sequences'
        Test        = {
            param($Probe)
            if ($Probe.Kind -ne 'poststate') { return $false }
            if ((Get-PlaytestBarePath $Probe.Path) -ne '/host') { return $false }
            $s = $Probe.Status
            if ($null -eq $s) { return $true }
            return (($s.hosting -ne $true) -or ("$($s.role)" -ne 'listenHost'))
        }
    }
    [ordered]@{
        Name        = 'joiner-not-in-roster'
        Summary     = 'POST /connect answered ok but the HOST roster does not carry the joiner. The joining side reporting success is not evidence that anything joined; the server-side roster is.'
        Remedy      = 'abort'
        MaxAttempts = 1
        GapSeconds  = 0
        Reference   = 'TestRig/MANUAL.md, the /status fields a multiplayer test reads'
        Test        = {
            param($Probe)
            if ($Probe.Kind -ne 'poststate') { return $false }
            return ((Get-PlaytestBarePath $Probe.Path) -eq '/connect')
        }
    }
    [ordered]@{
        Name        = 'lock-lost'
        Summary     = 'The rig session lock is no longer ours. The suite releases and re-takes the lock per check, so losing it to another agent mid-suite is possible and is never a mod defect.'
        Remedy      = 'abort'
        MaxAttempts = 1
        GapSeconds  = 0
        Reference   = 'TestRig/CLAUDE.md, The session lock covers the whole rig'
        Test        = { param($Probe) return ($Probe.Kind -eq 'lock') }
    }
    [ordered]@{
        Name        = 'control-plane-silent'
        Summary     = 'The control plane did not answer while a blocking endpoint was in flight. A blocking call freezes that instance whole control plane, /ping included, so the silence is explained and is waited out rather than counted against anything.'
        Remedy      = 'wait'
        MaxAttempts = 6
        GapSeconds  = 10
        Reference   = 'TestRig/MANUAL.md, Flags'
        Test        = {
            param($Probe)
            return (($Probe.Kind -eq 'transport') -and ($Probe.Blocking -eq $true))
        }
    }
    [ordered]@{
        Name        = 'instance-dead'
        Summary     = 'The control plane refused the connection with no blocking call in flight, so the process is gone or its listener died.'
        Remedy      = 'restart-instance'
        MaxAttempts = 1
        GapSeconds  = 5
        Reference   = 'TestRig/RESEARCH.md, Plugin lifecycle traps'
        Test        = {
            param($Probe)
            if ($Probe.Kind -ne 'transport') { return $false }
            return ("$($Probe.Error)" -match 'refused|actively refused|No connection could be made|unable to connect')
        }
    }
    [ordered]@{
        Name        = 'boot-timeout'
        Summary     = 'An instance did not reach the requested readiness stage inside the barrier, and it is not the Workshop park. Roughly 100 s from cold is normal; longer than the barrier is not.'
        Remedy      = 'restart-instance'
        MaxAttempts = 2
        GapSeconds  = 5
        Reference   = 'TestRig/MANUAL.md, Readiness'
        Test        = { param($Probe) return ($Probe.Kind -eq 'barrier') }
    }
    [ordered]@{
        Name        = 'transport-error'
        Summary     = 'A control-plane request failed at the transport layer and nothing more specific matched.'
        Remedy      = 'retry'
        MaxAttempts = 3
        GapSeconds  = 3
        Reference   = 'TestRig/MANUAL.md, the endpoint catalogue'
        Test        = { param($Probe) return ($Probe.Kind -eq 'transport') }
    }
)

function Get-PlaytestBarePath {
    # The endpoint without its query string or trailing slash, lower case. Query
    # parameters are how a Windows path is sent, so matching on the raw path would
    # miss every request that carried one.
    param([string] $Path)
    if (-not $Path) { return '' }
    return (($Path -split '\?')[0]).TrimEnd('/').ToLowerInvariant()
}

function Get-PlaytestFlakeTaxonomy {
    # The shipped taxonomy, in resolution order. The README renders this table;
    # keeping one source for it means the document cannot drift from the code.
    return $script:PlaytestFlakes
}

function Register-PlaytestFlake {
    <#
    .SYNOPSIS
        Add a detector to the taxonomy, ahead of the general ones.
    .DESCRIPTION
        -Before names an existing detector to sit in front of; the default puts
        the new one first, because a detector added later is almost always more
        specific than the ones already there.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [scriptblock] $Test,
        [Parameter(Mandatory)][ValidateSet('retry', 'wait', 'restart-instance', 'abort')] [string] $Remedy,
        [string] $Summary = '',
        [int] $MaxAttempts = 2,
        [double] $GapSeconds = 5,
        [string] $Reference = '',
        [string] $Before
    )
    $entry = [ordered]@{
        Name = $Name; Summary = $Summary; Remedy = $Remedy
        MaxAttempts = $MaxAttempts; GapSeconds = $GapSeconds; Reference = $Reference; Test = $Test
    }
    if ($Before) {
        $out = @()
        $placed = $false
        foreach ($f in $script:PlaytestFlakes) {
            if (-not $placed -and $f.Name -eq $Before) { $out += $entry; $placed = $true }
            $out += $f
        }
        if (-not $placed) { $out += $entry }
        $script:PlaytestFlakes = $out
    }
    else {
        $script:PlaytestFlakes = @($entry) + $script:PlaytestFlakes
    }
}

function New-PlaytestProbe {
    # What a detector is shown. One shape for every failure site, so a detector
    # never has to know which call produced it.
    param(
        [Parameter(Mandatory)][ValidateSet('action', 'transport', 'barrier', 'poststate', 'lock')] [string] $Kind,
        [string] $Instance = '',
        [string] $Path = '',
        [int] $Attempt = 1,
        $Response,
        $Status,
        [string] $ErrorText = '',
        [string] $Stage = '',
        [bool] $Blocking = $false
    )
    return [pscustomobject]@{
        PSTypeName = 'Playtest.FlakeProbe'
        Kind       = $Kind
        Instance   = $Instance
        Path       = $Path
        Attempt    = $Attempt
        Response   = $Response
        Status     = $Status
        Error      = $ErrorText
        Stage      = $Stage
        Blocking   = $Blocking
    }
}

function Resolve-PlaytestFlake {
    # First match wins. A detector that throws is treated as not matching and is
    # reported, because a broken detector must not be able to swallow a probe.
    param([Parameter(Mandatory)] $Probe)
    foreach ($f in $script:PlaytestFlakes) {
        $matched = $false
        try { $matched = [bool](& $f.Test $Probe) }
        catch {
            Write-Warning "[Playtest] Flake detector '$($f.Name)' threw while classifying a $($Probe.Kind) probe and was skipped: $($_.Exception.Message)"
            $matched = $false
        }
        if ($matched) { return $f }
    }
    return $null
}

# ---- evidence --------------------------------------------------------------
# A human must be able to audit a run they did not watch. That means the bundle
# carries what was asked, what came back, and what the rig looked like on either
# side of the run, not a summary of them.

function New-PlaytestEvidenceBundle {
    param(
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $SuiteName
    )
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'checks') | Out-Null
    return [pscustomobject]@{
        PSTypeName = 'Playtest.EvidenceBundle'
        Root       = $Root
        SuiteName  = $SuiteName
        StartedUtc = (Get-PlaytestStamp)
    }
}

function New-PlaytestCheckEvidence {
    param(
        [Parameter(Mandatory)] [string] $BundleRoot,
        [Parameter(Mandatory)] [int] $Index,
        [Parameter(Mandatory)] [string] $CheckName
    )
    $dir = Join-Path (Join-Path $BundleRoot 'checks') ('{0:d2}-{1}' -f $Index, (ConvertTo-PlaytestSlug $CheckName))
    foreach ($sub in @('', 'requests', 'observations', 'console', 'launcher')) {
        $p = if ($sub) { Join-Path $dir $sub } else { $dir }
        New-Item -ItemType Directory -Force -Path $p | Out-Null
    }
    return $dir
}

function Write-PlaytestEvidence {
    <#
    .SYNOPSIS
        Drop a named artifact into the current check's evidence folder.
    .DESCRIPTION
        -Kind is the subfolder ('requests', 'observations', 'console', 'launcher')
        or 'root' for the check folder itself. Checks may call this directly for
        anything the harness cannot know about, such as a mod-specific dump.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Content,
        [ValidateSet('root', 'requests', 'observations', 'console', 'launcher')] [string] $Kind = 'root',
        [switch] $Append,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $ctx.EvidenceDir) { return $null }
    $dir = if ($Kind -eq 'root') { $ctx.EvidenceDir } else { Join-Path $ctx.EvidenceDir $Kind }
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $file = Join-Path $dir $Name
    if ($Append) { Add-Content -LiteralPath $file -Value $Content -Encoding utf8 }
    else         { Set-Content -LiteralPath $file -Value $Content -Encoding utf8 }
    return $file
}

function Get-PlaytestNextSequence {
    param($Context)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx) { return 0 }
    $ctx.Sequence = [int]$ctx.Sequence + 1
    return $ctx.Sequence
}

function Write-PlaytestRequestRecord {
    # Every request and every response, one file each, numbered in the order they
    # happened. This is the part of the bundle that makes a run auditable: a
    # summary would only carry what the harness already understood.
    param(
        [Parameter(Mandatory)] [string] $Instance,
        [Parameter(Mandatory)] [string] $Method,
        [Parameter(Mandatory)] [string] $Path,
        [string] $BodyJson = '',
        $Response,
        [string] $ErrorText = '',
        [int] $Attempt = 1,
        [int] $ElapsedMs = 0,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $ctx.EvidenceDir) { return '' }
    $seq  = Get-PlaytestNextSequence -Context $ctx
    $name = '{0:d4}-{1}-{2}-{3}.json' -f $seq, (ConvertTo-PlaytestSlug $Instance), $Method.ToLowerInvariant(), (ConvertTo-PlaytestSlug (Get-PlaytestBarePath $Path))
    $record = [ordered]@{
        sequence  = $seq
        utc       = (Get-PlaytestStamp)
        instance  = $Instance
        method    = $Method
        path      = $Path
        attempt   = $Attempt
        elapsedMs = $ElapsedMs
        requestBody = $BodyJson
        response  = $Response
        error     = $ErrorText
    }
    Write-PlaytestEvidence -Kind 'requests' -Name $name -Content (ConvertTo-PlaytestJson $record) -Context $ctx | Out-Null
    return "requests/$name"
}

function Get-PlaytestSaveInventory {
    <#
    .SYNOPSIS
        A listing of the developer's tier-1 save folder, and a hash of that listing.

    .DESCRIPTION
        The one interaction this harness has with the tier-1 save folder, and it
        is a DIRECTORY LISTING: relative path, length and last-write time per
        file. No file is ever opened, nothing is ever written, nothing is ever
        moved. The hash is over the listing text, so it answers exactly one
        question at the session boundary: did anything in the developer's own
        saves move while the rig was driving the game.

        The offline suite pins the read-only property directly, by putting two
        files with identical metadata and different bytes in front of it and
        requiring the same hash out.
    #>
    param([string] $Root)
    if (-not $PSBoundParameters.ContainsKey('Root')) { $Root = $script:PlaytestTier1SaveRoot }
    $result = [ordered]@{
        root      = "$Root"
        exists    = $false
        fileCount = 0
        lines     = @()
        sha256    = ''
    }
    if (-not $Root -or -not (Test-Path -LiteralPath $Root)) {
        $result['sha256'] = 'no-such-root'
        return [pscustomobject]$result
    }
    $result['exists'] = $true
    $items = @(Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object FullName)
    $lines = foreach ($f in $items) {
        $rel = $f.FullName.Substring($Root.Length).TrimStart('\', '/')
        '{0}|{1}|{2}' -f $rel, $f.Length, $f.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    }
    $lines = @($lines)
    $text  = ($lines -join "`n")
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    $hash  = [System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::HashData($bytes)).Replace('-', '')
    $result['fileCount'] = $lines.Count
    $result['lines']     = $lines
    $result['sha256']    = $hash
    return [pscustomobject]$result
}

function Write-PlaytestSaveInventory {
    param(
        [Parameter(Mandatory)] [string] $BundleRoot,
        [Parameter(Mandatory)][ValidateSet('before', 'after')] [string] $When,
        $Inventory
    )
    $file = Join-Path $BundleRoot "save-inventory-$When.txt"
    $head = @(
        "# Developer save folder (tier 1), listing only. No file was opened and nothing was written."
        "# root      : $($Inventory.root)"
        "# exists    : $($Inventory.exists)"
        "# files     : $($Inventory.fileCount)"
        "# sha256    : $($Inventory.sha256)"
        "# capturedAt: $(Get-PlaytestStamp)"
        ''
    )
    Set-Content -LiteralPath $file -Value (($head + @($Inventory.lines)) -join "`n") -Encoding utf8
    return $file
}

function Compare-PlaytestSaveInventory {
    param($Before, $After)
    $same = ($Before.sha256 -eq $After.sha256)
    $added   = @(@($After.lines)  | Where-Object { @($Before.lines) -notcontains $_ })
    $removed = @(@($Before.lines) | Where-Object { @($After.lines)  -notcontains $_ })
    return [pscustomobject]@{
        Identical = $same
        Before    = $Before.sha256
        After     = $After.sha256
        Added     = $added
        Removed   = $removed
    }
}

# ---- talking to the rig ----------------------------------------------------

function Invoke-PlaytestTransport {
    param([int] $Port, [string] $Path, [string] $BodyJson, [int] $TimeoutSec)
    if (-not $script:PlaytestTransport) {
        throw "No control-plane transport is wired. Run checks through TestRig/playtest/playtest.ps1, which dot-sources the launcher's client library and wires its Invoke-Control (an object) rather than parsing the stdout of the 'call' verb (only printed JSON). A library that reaches the network by itself cannot be tested offline, which is why this is a hard error and not a fallback."
    }
    return (& $script:PlaytestTransport $Port $Path $BodyJson $TimeoutSec)
}

function Invoke-RigCommand {
    <#
    .SYNOPSIS
        One testrig.ps1 invocation, recorded in the evidence bundle.
    .DESCRIPTION
        Returns the wired command's result object (ExitCode, StdOut, StdErr) and
        never throws on a non-zero exit: the caller decides what a launcher
        failure means, because "the stop failed" and "the lock refused" want very
        different handling.
    #>
    param(
        [Parameter(Mandatory)] [string[]] $ArgList,
        [string] $Label = '',
        $Context
    )
    if (-not $script:PlaytestRigCommand) {
        throw "No launcher seam is wired. Run checks through TestRig/playtest/playtest.ps1, which wires testrig.ps1. See the composition-root note at the top of playtest-lib.ps1."
    }
    $started = Get-PlaytestNowUtc
    $res = & $script:PlaytestRigCommand $ArgList
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if ($ctx -and $ctx.EvidenceDir) {
        $seq  = Get-PlaytestNextSequence -Context $ctx
        $name = '{0:d4}-{1}.txt' -f $seq, (ConvertTo-PlaytestSlug ($Label ? $Label : ($ArgList -join ' ')))
        $body = @(
            "# testrig $($ArgList -join ' ')"
            "# started : $(Get-PlaytestStamp $started)"
            "# exit    : $($res.ExitCode)"
            ''
            '--- stdout ---'
            "$($res.StdOut)"
            '--- stderr ---'
            "$($res.StdErr)"
        ) -join "`n"
        Write-PlaytestEvidence -Kind 'launcher' -Name $name -Content $body -Context $ctx | Out-Null
    }
    return $res
}

function Resolve-RigInstancePort {
    # Instance name to control-plane port, off the rig registry. A name that is
    # not in the registry is not a typo to shrug at: it means the check is about
    # to drive something that was never provisioned, so it stops the check as
    # inconclusive with the exact command that fixes it.
    param([Parameter(Mandatory)] [string] $Name)
    if (-not $script:PlaytestRegistry) {
        throw "No rig registry is wired, so instance '$Name' cannot be resolved to a control-plane port. See the composition-root note at the top of playtest-lib.ps1."
    }
    $entries = @(& $script:PlaytestRegistry)
    $e = $entries | Where-Object { "$($_.instanceName)" -eq $Name } | Select-Object -First 1
    if (-not $e) {
        $known = ($entries | ForEach-Object { "$($_.instanceName)" }) -join ', '
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'instance-not-provisioned' `
            -Message "Instance '$Name' is not in the client rig registry, so this check cannot run. Create it first: testrig create -Target $Name -As <id> [-Role host]. Known instances: $known" `
            -Detail @{ requested = $Name; known = $known })
    }
    return [int]$e.port
}

function Resolve-RigInstanceEntry {
    # The whole registry row, for the few things that need more than the port
    # (the instance tree, so a reader can open a file the endpoint cannot serve).
    # Same refusal as Resolve-RigInstancePort: an unprovisioned name stops the
    # check rather than resolving to a guess.
    param([Parameter(Mandatory)] [string] $Name)
    if (-not $script:PlaytestRegistry) {
        throw "No rig registry is wired, so instance '$Name' cannot be resolved. See the composition-root note at the top of playtest-lib.ps1."
    }
    $entries = @(& $script:PlaytestRegistry)
    $e = $entries | Where-Object { "$($_.instanceName)" -eq $Name } | Select-Object -First 1
    if (-not $e) {
        $known = ($entries | ForEach-Object { "$($_.instanceName)" }) -join ', '
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'instance-not-provisioned' `
            -Message "Instance '$Name' is not in the client rig registry, so this check cannot run. Create it first: testrig create -Target $Name -As <id> [-Role host]. Known instances: $known" `
            -Detail @{ requested = $Name; known = $known })
    }
    return $e
}

function Resolve-RigInstanceLogPath {
    <#
    .SYNOPSIS
        The instance's BepInEx LogOutput.log, on disk.
    .DESCRIPTION
        The instance trees normally sit on the game install's volume rather than
        under TestRig/, so the path comes from the instancesRoot recorded in the
        registry entry at provision time, with the same fallback order the
        launcher uses. An entry written before that field existed falls back to
        <RigHome>/ClientRig/instances.
    #>
    param([Parameter(Mandatory)] [string] $Name)
    $e = Resolve-RigInstanceEntry -Name $Name
    $root = "$($e.instancesRoot)"
    if (-not $root) {
        # The same fallback order the launcher uses, environment variable
        # included. This step used to be missing here, so an entry written before
        # the root was recorded resolved to a path under TestRig/ that a rig built
        # on the install's volume has never had, and the log read came back
        # "absent" rather than wrong, which is the hardest kind of wrong to notice.
        $root = if ($env:STATIONEERS_CLIENTRIG_ROOT) { $env:STATIONEERS_CLIENTRIG_ROOT }
                else { Join-Path (Join-Path (Get-PlaytestRigHome) 'ClientRig') 'instances' }
    }
    return (Join-Path (Join-Path (Join-Path $root $Name) 'BepInEx') 'LogOutput.log')
}

function Read-PlaytestBepInExLog {
    <#
    .SYNOPSIS
        The instance's BepInEx log FILE, shaped like the console tee's response.
    .DESCRIPTION
        The console tee is a bounded ring (2000 lines per source by default) and
        StationeersLaunchPad's mod loading evicts thousands of lines during boot,
        so a boot-time line is routinely gone before any check can read it. That
        is what turned check 05 into 'console-tee-evicted': the line it needed was
        real, printed, and unreadable, and declining was the only honest answer.

        The log file has no ring and no eviction, and the between-session state
        reset deletes it, so each check's run starts from an empty one. That makes
        it the right authority for anything printed during boot.

        The shape deliberately mirrors GET /console/log ('count' plus 'lines' with
        a 'text' per row) so a check switches reader name and nothing else, and
        'exists' plus 'bytes' are reported so an absent file is a distinguishable
        fact rather than a count of zero.
    #>
    param(
        [Parameter(Mandatory)] [string] $Instance,
        [string] $Contains = '',
        [int] $Limit = 0
    )
    $path = Resolve-RigInstanceLogPath -Name $Instance
    if (-not (Test-Path -LiteralPath $path)) {
        return [pscustomobject]@{
            ok = $false; instance = $Instance; path = $path; exists = $false
            bytes = 0; count = 0; matched = 0; lines = @()
        }
    }
    $bytes = 0
    try { $bytes = [int64](Get-Item -LiteralPath $path).Length } catch { $bytes = 0 }
    # Read shared: the game holds this file open for append while it runs, so a
    # plain Get-Content would fail exactly when a check needs it most.
    $all = @()
    try {
        $fs = [System.IO.File]::Open($path, [System.IO.FileMode]::Open,
                                     [System.IO.FileAccess]::Read,
                                     [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
        try {
            $sr = New-Object System.IO.StreamReader($fs)
            try { $all = @($sr.ReadToEnd() -split "`r?`n") } finally { $sr.Dispose() }
        }
        finally { $fs.Dispose() }
    }
    catch {
        return [pscustomobject]@{
            ok = $false; instance = $Instance; path = $path; exists = $true
            bytes = $bytes; count = 0; matched = 0; lines = @()
            error = "$($_.Exception.Message)"
        }
    }
    $hits = if ($Contains) { @($all | Where-Object { $_ -and "$_".Contains($Contains) }) } else { @($all | Where-Object { $_ }) }
    $matched = @($hits).Count
    if ($Limit -gt 0 -and $matched -gt $Limit) { $hits = @($hits | Select-Object -First $Limit) }
    return [pscustomobject]@{
        ok = $true; instance = $Instance; path = $path; exists = $true
        bytes = $bytes; totalLines = @($all).Count
        # 'count' is the number of MATCHES, exactly as GET /console/log means it,
        # and is not clipped by -Limit: a check counting six banner lines with a
        # limit of 5 must read 6 and fail, not read 5 and pass.
        count = $matched; matched = $matched
        lines = @($hits | ForEach-Object { [pscustomobject]@{ source = 'bepinexfile'; text = $_ } })
    }
}

function Update-PlaytestLockIfDue {
    <#
    .SYNOPSIS
        Refresh the rig lock from our own foreground work, at most once a minute.
    .DESCRIPTION
        The lock rules forbid a background refresher and forbid refreshing to hold
        the rig for an absent human. This refreshes only as a side effect of the
        harness actually driving something, which is exactly the sanctioned shape.
        A refusal here means another session now owns the rig: that is a 'lock'
        probe, which classifies as the lock-lost flake and ends the check as
        inconclusive.
    #>
    param($Context)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $ctx.Owner) { return }
    $now = Get-PlaytestNowUtc
    if ($ctx.LastRefreshUtc -and ($now - $ctx.LastRefreshUtc).TotalSeconds -lt 60) { return }
    $ctx.LastRefreshUtc = $now
    $res = Invoke-RigCommand -ArgList @('refresh-lock', '-As', $ctx.Owner) -Label 'refresh-lock' -Context $ctx
    if ([int]$res.ExitCode -ne 0) {
        $probe = New-PlaytestProbe -Kind 'lock' -ErrorText "$($res.StdErr)$($res.StdOut)"
        $flake = Resolve-PlaytestFlake $probe
        Add-PlaytestDetector -Context $ctx -Name (($flake) ? $flake.Name : 'lock-lost')
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'lock-lost' `
            -Message "The rig session lock is no longer ours, so this check is inconclusive rather than failed. The suite releases and re-takes the lock per check, so another agent taking the rig between checks is expected. Launcher said: $(("$($res.StdErr)$($res.StdOut)" -split "`n")[0])" `
            -Detail @{ owner = $ctx.Owner; exit = $res.ExitCode })
    }
}

function Add-PlaytestDetector {
    param($Context, [string] $Name)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $Name) { return }
    if ($ctx.Detectors -notcontains $Name) { $ctx.Detectors += $Name }
}

function Add-PlaytestAttempt {
    # Any operation that needed more than one go marks the whole check degraded.
    # A check that only passed on the third attempt is not a clean pass, and the
    # report says so rather than letting the flakiness disappear once survived.
    #
    # Two numbers, because they answer different questions: MaxAttempts is the
    # worst single operation (the one the outcome text names) and Attempts is the
    # total number of retries across the check (how much re-driving it took).
    param($Context, [int] $Attempts)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx) { return }
    if ($Attempts -gt [int]$ctx.MaxAttempts) { $ctx.MaxAttempts = $Attempts }
    if ($Attempts -gt 1) {
        $ctx.Degraded = $true
        $ctx.Attempts = [int]$ctx.Attempts + ($Attempts - 1)
    }
}

# ---- actions: making something happen --------------------------------------

function Invoke-RigAction {
    <#
    .SYNOPSIS
        Drive one endpoint on one instance, with the flake taxonomy applied.

    .DESCRIPTION
        This is the DOING verb. Its return value is a Playtest.ActionResult, and
        no assert verb accepts one: an endpoint's own answer is recorded as
        evidence and is never the thing a check concludes from. To conclude
        something, read it back with Read-RigValue or Assert-RigValue from the
        instance that is the authority for it.

        -Blocking says this endpoint freezes the whole control plane of that
        instance while it runs (/host, /connect, /save, /load, /newworld,
        /waitfor). A transport silence during one of those is explained, so it is
        waited out rather than counted as a dead instance.

        Every attempt, its response and its timing land in the evidence bundle.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $On,
        [Parameter(Mandatory)] [string] $Path,
        $Body,
        [switch] $Blocking,
        [int] $TimeoutSec = 0,
        [switch] $NoRetry,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    Assert-PlaytestInstanceName -Name $On -Context $ctx -Parameter '-On'
    Update-PlaytestLockIfDue -Context $ctx

    $bodyJson = if ($null -eq $Body) { '' }
                elseif ($Body -is [string]) { $Body }
                else { ($Body | ConvertTo-Json -Depth 8 -Compress) }
    $method   = if ($bodyJson) { 'POST' } else { 'GET' }
    $port     = Resolve-RigInstancePort -Name $On
    $bare     = Get-PlaytestBarePath $Path
    $longPaths = @('/host', '/connect', '/save', '/load', '/newworld', '/waitfor')
    $timeout  = if ($TimeoutSec -gt 0) { $TimeoutSec }
                elseif ($Blocking -or ($longPaths -contains $bare)) { 330 }
                else { 120 }

    $attempt = 1
    $lastRef = ''
    while ($true) {
        $started  = Get-PlaytestNowUtc
        $response = $null
        $errText  = ''
        try { $response = Invoke-PlaytestTransport -Port $port -Path $Path -BodyJson $bodyJson -TimeoutSec $timeout }
        catch { $errText = "$($_.Exception.Message)" }
        $elapsed = [int]((Get-PlaytestNowUtc) - $started).TotalMilliseconds

        $lastRef = Write-PlaytestRequestRecord -Instance $On -Method $method -Path $Path -BodyJson $bodyJson `
            -Response $response -ErrorText $errText -Attempt $attempt -ElapsedMs $elapsed -Context $ctx

        $ok = (-not $errText)
        if ($ok -and $null -ne $response -and $null -ne $response.PSObject.Properties['ok']) {
            $ok = ($response.ok -eq $true)
        }
        if ($ok) {
            Add-PlaytestAttempt -Context $ctx -Attempts $attempt
            return [pscustomobject]@{
                PSTypeName  = 'Playtest.ActionResult'
                Instance    = $On
                Path        = $Path
                Attempts    = $attempt
                Degraded    = ($attempt -gt 1)
                Response    = $response
                ElapsedMs   = $elapsed
                EvidenceRef = $lastRef
            }
        }

        $probe = New-PlaytestProbe -Kind (($errText) ? 'transport' : 'action') -Instance $On -Path $Path `
            -Attempt $attempt -Response $response -ErrorText $errText -Blocking ([bool]$Blocking)
        $flake = Resolve-PlaytestFlake $probe
        if (-not $flake) {
            Add-PlaytestAttempt -Context $ctx -Attempts $attempt
            throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'action-refused' `
                -Message "$On refused $Path and nothing in the flake taxonomy explains it, so this check is inconclusive rather than failed. An endpoint refusing is not the mod misbehaving; only a value read back through a reader can say that. Response: $(ConvertTo-PlaytestJson $response) Error: $errText" `
                -Detail @{ instance = $On; path = $Path; evidence = $lastRef })
        }
        Add-PlaytestDetector -Context $ctx -Name $flake.Name

        $maxAttempts = if ($NoRetry) { 1 } else { [int]$flake.MaxAttempts }
        if ($flake.Remedy -eq 'abort' -or $attempt -ge $maxAttempts) {
            Add-PlaytestAttempt -Context $ctx -Attempts $attempt
            throw (New-PlaytestSignal -Kind 'inconclusive' -Detector $flake.Name `
                -Message "$On could not complete $Path after $attempt attempt(s): $($flake.Summary) This is a rig condition, so the check is inconclusive and never failed. Error: $errText Response: $(ConvertTo-PlaytestJson $response)" `
                -Detail @{ instance = $On; path = $Path; detector = $flake.Name; attempts = $attempt; evidence = $lastRef })
        }
        if ($flake.Remedy -eq 'restart-instance') {
            Restart-RigInstance -Name $On -Reason $flake.Name -Context $ctx
        }
        Wait-PlaytestSeconds ([double]$flake.GapSeconds)
        $attempt++
    }
}

function Assert-PlaytestInstanceName {
    <#
    .SYNOPSIS
        The guard that keeps an actor's own report out of the assert verbs.

    .DESCRIPTION
        Every driving and reading verb takes an instance NAME. Handing one an
        action result, a parsed response or any other object gets this message
        instead of a silent string coercion, because "assert on the authority"
        has to be enforced by the shape rather than remembered from a document.
    #>
    param($Name, $Context, [string] $Parameter = '-From')
    if ($Name -is [string] -and $Name -and $Name -notmatch '^@?\{' -and $Name -notmatch '^Playtest\.') {
        $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
        if ($ctx -and @($ctx.InstanceNames).Count -gt 0 -and (@($ctx.InstanceNames) -notcontains $Name)) {
            throw "$Parameter '$Name' is not one of this check's instances ($((@($ctx.InstanceNames)) -join ', ')). Name the instance that is the AUTHORITY for the value you are asserting: on a listen host that is the host for anything the server owns, and the joiner only for what its own client half decides."
        }
        return
    }
    $shown = if ($null -eq $Name) { '<null>' } else { "$Name" }
    throw "$Parameter must be an instance NAME, and it was given [$shown]. This harness never asserts on what an endpoint said about itself: an action's own 200 is a statement about the request, not about the world. Read the value back from the instance that is the authority for it, through a named reader: Assert-RigValue -From <instance> -Reader status -Select hosting -Is `$true."
}

function Restart-RigInstance {
    # The remedy for the Workshop park and for a dead control plane: stop that ONE
    # instance by name and start it again. Never -All, which would reach another
    # session's live test as well as the rest of ours.
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Reason = '',
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $ctx.Owner) {
        throw "Cannot restart '$Name' without the rig lock owner id. Restarts only happen inside Use-Rig."
    }
    Write-Host "[Playtest]   restarting '$Name' ($Reason)"
    Invoke-RigCommand -ArgList @('stop', '-Target', $Name, '-As', $ctx.Owner, '-TimeoutSeconds', '60') -Label "restart-stop-$Name" -Context $ctx | Out-Null
    $res = Invoke-RigCommand -ArgList @('start', '-Target', $Name, '-As', $ctx.Owner) -Label "restart-start-$Name" -Context $ctx
    if ([int]$res.ExitCode -ne 0) {
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'instance-restart-failed' `
            -Message "Restarting '$Name' after a $Reason failed, so this check is inconclusive. Launcher exit $($res.ExitCode): $(("$($res.StdErr)$($res.StdOut)" -split "`n")[0])" `
            -Detail @{ instance = $Name; reason = $Reason })
    }
    if ($ctx.Started -notcontains $Name) { $ctx.Started += $Name }
}

# ---- readers: getting a value from the authority ---------------------------

$script:PlaytestReaders = [ordered]@{
    'status'    = 'GET /status. The one computed answer to what this process is: role, hosting, hostPort, connectedClients, phase, save hygiene.'
    'roster'    = 'GET /status then connectedClients. The SERVER-side roster, which is what makes did-the-joiner-arrive assertable from the host.'
    'config'    = 'GET /config?guid=<mod>. Every ConfigEntry of a loaded plugin, as the running process sees it. -Of <Section>/<Key> picks one.'
    'thing'     = 'GET /thing?refId=&fields=. An INSTANCE field on one Thing, per machine. -Of <refId> picks the Thing, -Of <refId>/<Field> picks one field row so -Select value and -Select matchesPrefab work.'
    'reflect'   = 'GET /reflect?type=&member=. Any STATIC field or property by full type name. Instance fields belong to the thing reader.'
    'nearby'    = 'GET /nearby. Things around the player; -Of <referenceId> picks one.'
    'console'   = 'GET /console/log. The sequence-numbered tee, for a line a mod printed. A BOUNDED RING: boot-time lines are routinely evicted, so read those through bepinexlog instead.'
    'bepinexlog' = 'The instance BepInEx/LogOutput.log FILE. No ring and no eviction, and the state reset empties it per session, so it is the authority for anything printed during boot. -ReaderArgs @{ contains = <s>; limit = N }, and -Select count.'
    'inventory' = 'GET /inventory. Every slot of a character. -Of <slot key or index> picks one.'
    'plugins'   = 'GET /plugins. Every plugin found by assembly scan.'
    'savepath'  = 'GET /savepath. Where this process writes, and whether that is isolated from the developer folder.'
    'player'    = 'GET /player. The player block only.'
    'dlc'       = 'GET /dlc. What this process believes it is entitled to.'
}

function Get-PlaytestReaders { return $script:PlaytestReaders }

function Read-RigValue {
    <#
    .SYNOPSIS
        Read one named value from one named instance through one named reader.

    .DESCRIPTION
        The only way a check gets a value it may conclude from. Returns a
        Playtest.Observation carrying the value, where it came from, when, and the
        evidence file the raw response landed in.

        -Select is a dotted path into the reader's response, with array indexing
        and a 'count' pseudo-member: 'hosting', 'connectedClients.count',
        'connectedClients[0].username'.

        -Of narrows a reader that returns a collection: for 'roster' it is a
        clientId, for 'nearby' a referenceId, for 'config' a '<section>/<key>'.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $From,
        [Parameter(Mandatory)][ValidateSet('status', 'roster', 'config', 'thing', 'reflect', 'nearby', 'console', 'bepinexlog', 'inventory', 'plugins', 'savepath', 'player', 'dlc')] [string] $Reader,
        [string] $Select = '.',
        [string] $Of = '',
        [hashtable] $ReaderArgs,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    Assert-PlaytestInstanceName -Name $From -Context $ctx -Parameter '-From'
    Update-PlaytestLockIfDue -Context $ctx

    $query = if ($ReaderArgs) { $ReaderArgs } else { @{} }

    # bepinexlog reads a FILE, not the control plane, so it takes the same
    # observation path as every other reader but skips the transport. Everything
    # after this branch (evidence record, observation shape, ReaderArgs
    # pass-through for Assert-RigChange) is deliberately shared.
    if ($Reader -eq 'bepinexlog') {
        $started  = Get-PlaytestNowUtc
        $response = Read-PlaytestBepInExLog -Instance $From `
            -Contains "$($query['contains'])" -Limit ([int]$query['limit'])
        $elapsed = [int]((Get-PlaytestNowUtc) - $started).TotalMilliseconds
        $ref = Write-PlaytestRequestRecord -Instance $From -Method 'FILE' -Path "$($response.path)" `
            -Response $response -ErrorText '' -ElapsedMs $elapsed -Context $ctx
        return (New-PlaytestObservation -From $From -Reader $Reader -Select $Select -Of $Of `
            -ReaderArgs $ReaderArgs -Scope $response -Source "FILE $($response.path)" `
            -EvidenceRef $ref -Context $ctx)
    }

    $port  = Resolve-RigInstancePort -Name $From
    $path = switch ($Reader) {
        'status'    { '/status' }
        'roster'    { '/status' }
        'player'    { '/player' }
        'plugins'   { '/plugins' }
        'savepath'  { '/savepath' }
        'dlc'       { '/dlc' }
        'thing'     { '/thing' + (ConvertTo-PlaytestQuery $query) }
        'inventory' { '/inventory' + (ConvertTo-PlaytestQuery $query) }
        'config'    { '/config' + (ConvertTo-PlaytestQuery $query) }
        'reflect'   { '/reflect' + (ConvertTo-PlaytestQuery $query) }
        'nearby'    { '/nearby' + (ConvertTo-PlaytestQuery $query) }
        'console'   { '/console/log' + (ConvertTo-PlaytestQuery $query) }
    }

    $started  = Get-PlaytestNowUtc
    $response = $null
    $errText  = ''
    try { $response = Invoke-PlaytestTransport -Port $port -Path $path -BodyJson '' -TimeoutSec 60 }
    catch { $errText = "$($_.Exception.Message)" }
    $elapsed = [int]((Get-PlaytestNowUtc) - $started).TotalMilliseconds
    $ref = Write-PlaytestRequestRecord -Instance $From -Method 'GET' -Path $path -Response $response -ErrorText $errText -ElapsedMs $elapsed -Context $ctx

    if ($errText) {
        $probe = New-PlaytestProbe -Kind 'transport' -Instance $From -Path $path -ErrorText $errText
        $flake = Resolve-PlaytestFlake $probe
        if ($flake) { Add-PlaytestDetector -Context $ctx -Name $flake.Name }
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector (($flake) ? $flake.Name : 'reader-unreachable') `
            -Message "Could not read '$Reader' from '$From', so nothing can be concluded and the check is inconclusive: $errText" `
            -Detail @{ instance = $From; reader = $Reader; path = $path; evidence = $ref })
    }

    return (New-PlaytestObservation -From $From -Reader $Reader -Select $Select -Of $Of `
        -ReaderArgs $ReaderArgs -Scope $response -Source "GET $path" -EvidenceRef $ref -Context $ctx)
}

function New-PlaytestObservation {
    <#
    .SYNOPSIS
        Narrow a reader response, record it, and hand back a Playtest.Observation.
    .DESCRIPTION
        Shared by every reader, including the ones that do not go through the
        control plane. Two readers building their own observation is two places
        for the ReaderArgs pass-through to be forgotten, and forgetting it once
        already cost a campaign: Assert-RigChange re-reads from the observation,
        so without the args the re-read hits '/thing' or '/config' with no query
        string, the endpoint answers 400, and the check reports inconclusive with
        no comparison made. The clone is shallow so a check that reuses and
        mutates its own hashtable cannot retroactively change what the baseline
        was read with.
    #>
    param(
        [Parameter(Mandatory)][string] $From,
        [Parameter(Mandatory)][string] $Reader,
        [string] $Select = '.',
        [string] $Of = '',
        [hashtable] $ReaderArgs,
        $Scope,
        [string] $Source,
        [string] $EvidenceRef,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    $narrowed = Resolve-PlaytestReaderScope -Reader $Reader -Response $Scope -Of $Of
    $value = Select-PlaytestPath -Object $narrowed -Path $Select
    $obs = [pscustomobject]@{
        PSTypeName  = 'Playtest.Observation'
        Instance    = $From
        Reader      = $Reader
        Select      = $Select
        Of          = $Of
        ReaderArgs  = $(if ($ReaderArgs) { $h = @{}; foreach ($k in $ReaderArgs.Keys) { $h[$k] = $ReaderArgs[$k] }; $h } else { $null })
        Value       = $value
        Source      = $Source
        CapturedUtc = (Get-PlaytestStamp)
        EvidenceRef = $EvidenceRef
    }
    $seq  = Get-PlaytestNextSequence -Context $ctx
    $name = '{0:d4}-{1}-{2}-{3}.json' -f $seq, (ConvertTo-PlaytestSlug $From), (ConvertTo-PlaytestSlug $Reader), (ConvertTo-PlaytestSlug $Select)
    Write-PlaytestEvidence -Kind 'observations' -Name $name -Content (ConvertTo-PlaytestJson ([ordered]@{
        instance = $From; reader = $Reader; select = $Select; of = $Of
        value = $value; source = $Source; capturedUtc = $obs.CapturedUtc; request = $EvidenceRef
    })) -Context $ctx | Out-Null
    return $obs
}

function ConvertTo-PlaytestArgument {
    <#
    .SYNOPSIS
        Quote one argument for a child process command line.

    .DESCRIPTION
        `Start-Process -ArgumentList <string[]>` joins its elements with plain
        spaces and quotes nothing, so any argument containing a space arrives at
        the child as several arguments. The lock purpose defaults to the check's
        own name and therefore ALWAYS contains spaces: every check in every suite
        died at 'rig-unavailable' with `Cannot convert value "first-use" to type
        "System.Int32"`, because `-Purpose the first-use notice cap ...` bound
        `the` to -Purpose and then `first-use` positionally to the launcher's int
        $Port. The harness could not take the lock at all.

        The rule is CommandLineToArgvW's, which is what the child's parser uses:
        wrap in double quotes, double any run of backslashes that precedes a
        quote or ends the string, and escape embedded quotes. It is a pure
        function so the offline suite can pin it without a process.
    #>
    param([string] $Value)
    if ($null -eq $Value) { return '""' }
    if ($Value -eq '')    { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $escaped = [regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function ConvertTo-PlaytestQuery {
    # Query string, not a JSON body. A query parameter is percent-decoded by the
    # HTTP layer and never goes through the plugin's JSON string reader, which is
    # the only way a Windows path survives a request intact.
    param([hashtable] $Parameters)
    if (-not $Parameters -or $Parameters.Count -eq 0) { return '' }
    $parts = foreach ($k in ($Parameters.Keys | Sort-Object)) {
        '{0}={1}' -f [uri]::EscapeDataString("$k"), [uri]::EscapeDataString("$($Parameters[$k])")
    }
    return '?' + ($parts -join '&')
}

function Resolve-PlaytestReaderScope {
    # Narrow a reader's response to the thing -Of names, before -Select runs.
    # Keeping this out of Select-PlaytestPath means a check never has to know
    # which JSON shape a given endpoint happens to use for its collection.
    param([string] $Reader, $Response, [string] $Of)
    switch ($Reader) {
        'roster' {
            $rows = @(Select-PlaytestPath -Object $Response -Path 'connectedClients')
            if (-not $Of) { return $rows }
            return ($rows | Where-Object { "$($_.clientId)" -eq $Of } | Select-Object -First 1)
        }
        'nearby' {
            $rows = @(Select-PlaytestPath -Object $Response -Path 'things')
            if (@($rows).Count -eq 0) { $rows = @($Response) }
            if (-not $Of) { return $rows }
            return ($rows | Where-Object { "$($_.referenceId)" -eq $Of -or "$($_.id)" -eq $Of } | Select-Object -First 1)
        }
        'thing' {
            # -Of '<refId>' picks the Thing row; -Of '<refId>/<Field>' picks one
            # field row inside it, so -Select value and -Select matchesPrefab read
            # what a check actually wants. Same shape as the config reader's
            # '<Section>/<Key>', so there is one convention to remember.
            $rows = @(Select-PlaytestPath -Object $Response -Path 'things')
            if (-not $Of) { return $rows }
            $parts = $Of -split '/', 2
            $row = ($rows | Where-Object { "$($_.requestedRefId)" -eq $parts[0] -or "$($_.referenceId)" -eq $parts[0] } | Select-Object -First 1)
            if ($parts.Count -le 1 -or -not $row) { return $row }
            $fields = @(Select-PlaytestPath -Object $row -Path 'fields')
            return ($fields | Where-Object { "$($_.name)" -eq $parts[1] -or "$($_.resolvedName)" -eq $parts[1] } | Select-Object -First 1)
        }
        'config' {
            if (-not $Of) { return $Response }
            $rows = @(Select-PlaytestPath -Object $Response -Path 'entries')
            $parts = $Of -split '/', 2
            $section = $parts[0]
            $key = if ($parts.Count -gt 1) { $parts[1] } else { '' }
            return ($rows | Where-Object {
                ("$($_.section)" -eq $section) -and (-not $key -or "$($_.key)" -eq $key)
            } | Select-Object -First 1)
        }
        'inventory' {
            if (-not $Of) { return $Response }
            $rows = @(Select-PlaytestPath -Object $Response -Path 'slots')
            return ($rows | Where-Object { "$($_.key)" -eq $Of -or "$($_.index)" -eq $Of } | Select-Object -First 1)
        }
        default { return $Response }
    }
}

# ---- assertions ------------------------------------------------------------
# The ONLY things in this file that can produce 'fail'. Each one reads through a
# named reader first, so the value it judges came from the authority rather than
# from the actor.

function Assert-RigValue {
    <#
    .SYNOPSIS
        Read a value from a named instance through a named reader and require it.

    .DESCRIPTION
        The workhorse. Exactly one comparison may be given.

        -Because is mandatory. A report that says "hosting was False" is a puzzle;
        one that says "the host must actually be hosting: NetworkServer.Host()
        gives up quietly after three failed binds" is a finding.

    .EXAMPLE
        Assert-RigValue -From host1 -Reader status -Select role -Is 'listenHost' `
            -Because 'the world holder must be a listen host, not a single-player session'
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $From,
        [Parameter(Mandatory)][ValidateSet('status', 'roster', 'config', 'thing', 'reflect', 'nearby', 'console', 'bepinexlog', 'inventory', 'plugins', 'savepath', 'player', 'dlc')] [string] $Reader,
        [string] $Select = '.',
        [string] $Of = '',
        [hashtable] $ReaderArgs,
        $Is,
        $IsNot,
        [string] $Matches,
        $AtLeast,
        $AtMost,
        [string] $Contains,
        [Parameter(Mandatory)] [string] $Because,
        $Context
    )
    # The @() is load bearing. A pipeline that yields exactly one string yields
    # the STRING, not a one-element array, and $given[0] on a bare string is its
    # first CHARACTER: the switch below then matched nothing, every comparison
    # silently evaluated to false, and every satisfied assertion reported a
    # failure with an empty expectation. That is the worst failure this harness
    # could have, so it is pinned by a test for each comparison.
    $given = @(@('Is', 'IsNot', 'Matches', 'AtLeast', 'AtMost', 'Contains') | Where-Object { $PSBoundParameters.ContainsKey($_) })
    if (@($given).Count -ne 1) {
        throw "Assert-RigValue takes exactly one comparison (-Is, -IsNot, -Matches, -AtLeast, -AtMost or -Contains) and was given $(@($given).Count). Two comparisons in one assertion hide which of them failed."
    }
    $obs = Read-RigValue -From $From -Reader $Reader -Select $Select -Of $Of -ReaderArgs $ReaderArgs -Context $Context
    $actual = $obs.Value
    $ok = $false
    $want = ''
    switch ($given[0]) {
        'Is'       { $ok = Test-PlaytestValueEqual $Is $actual;                    $want = "is [$Is]" }
        'IsNot'    { $ok = -not (Test-PlaytestValueEqual $IsNot $actual);          $want = "is not [$IsNot]" }
        'Matches'  { $ok = ("$actual" -match $Matches);                            $want = "matches /$Matches/" }
        'AtLeast'  { $ok = ([double]"$actual" -ge [double]"$AtLeast");             $want = "is at least [$AtLeast]" }
        'AtMost'   { $ok = ([double]"$actual" -le [double]"$AtMost");              $want = "is at most [$AtMost]" }
        'Contains' { $ok = @(@($actual) | Where-Object { "$_" -like "*$Contains*" }).Count -gt 0; $want = "contains [$Contains]" }
    }
    if ($ok) {
        Write-Host "[Playtest]   ok   $From.$Reader.$Select $want"
        return $obs
    }
    throw (New-PlaytestSignal -Kind 'fail' -Detector 'assertion' `
        -Message "$From.$Reader.$Select $want, but it was [$actual]. $Because" `
        -Detail @{
            instance = $From; reader = $Reader; select = $Select; of = $Of
            expected = $want; actual = "$actual"; because = $Because; evidence = $obs.EvidenceRef
        })
}

function Assert-RigAgreement {
    <#
    .SYNOPSIS
        Require several instances to report the same value for the same reader.

    .DESCRIPTION
        The shape of nearly every real multiplayer check: the host and the joiner
        must agree about something the server owns, or must deliberately disagree
        about something each client half decides. Reads every instance in the list
        and compares them to each other; -Is additionally pins what they must all
        say.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $Across,
        [Parameter(Mandatory)][ValidateSet('status', 'roster', 'config', 'thing', 'reflect', 'nearby', 'console', 'bepinexlog', 'inventory', 'plugins', 'savepath', 'player', 'dlc')] [string] $Reader,
        [string] $Select = '.',
        [string] $Of = '',
        [hashtable] $ReaderArgs,
        $Is,
        [Parameter(Mandatory)] [string] $Because,
        $Context
    )
    if (@($Across).Count -lt 2) {
        throw "Assert-RigAgreement needs at least two instances in -Across; agreement with itself is not an observation."
    }
    $obs = @()
    foreach ($name in $Across) {
        $obs += (Read-RigValue -From $name -Reader $Reader -Select $Select -Of $Of -ReaderArgs $ReaderArgs -Context $Context)
    }
    $first = $obs[0]
    $disagree = @($obs | Where-Object { -not (Test-PlaytestValueEqual $first.Value $_.Value) })
    if (@($disagree).Count -gt 0) {
        $seen = ($obs | ForEach-Object { "$($_.Instance)=[$($_.Value)]" }) -join ' '
        throw (New-PlaytestSignal -Kind 'fail' -Detector 'assertion' `
            -Message "$($Across -join ' and ') disagree about $Reader.$Select : $seen. $Because" `
            -Detail @{ reader = $Reader; select = $Select; readings = $seen; because = $Because })
    }
    if ($PSBoundParameters.ContainsKey('Is') -and -not (Test-PlaytestValueEqual $Is $first.Value)) {
        $seen = ($obs | ForEach-Object { "$($_.Instance)=[$($_.Value)]" }) -join ' '
        throw (New-PlaytestSignal -Kind 'fail' -Detector 'assertion' `
            -Message "$($Across -join ' and ') agree about $Reader.$Select but on the wrong value: expected [$Is], all reported [$($first.Value)]. $Because" `
            -Detail @{ reader = $Reader; select = $Select; readings = $seen; expected = "$Is"; because = $Because })
    }
    Write-Host "[Playtest]   ok   $($Across -join '/') agree on $Reader.$Select = [$($first.Value)]"
    return $obs
}

function Assert-RigChange {
    <#
    .SYNOPSIS
        Require a value to have moved (or not moved) since a baseline reading.

    .DESCRIPTION
        A single snapshot cannot tell you whether a field changed, so any
        hypothesis of the form "doing X should make Y become Z" needs a baseline
        captured with Read-RigValue before the action and this verb after it.
        -Unchanged is the control half of the same discipline: the thing that was
        NOT acted on must still read the same, which is what separates "the patch
        works" from "everything went emissive".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] $Baseline,
        $To,
        [switch] $Unchanged,
        [Parameter(Mandatory)] [string] $Because,
        $Context
    )
    if (-not $Baseline -or -not $Baseline.PSObject.Properties['Reader'] -or -not $Baseline.PSObject.Properties['Instance']) {
        throw "-Baseline must be a Playtest.Observation from Read-RigValue. A remembered raw value is not a baseline: it carries no reader, no instance and no evidence reference, so a failure could not say what was compared with what."
    }
    if ($Unchanged -and $PSBoundParameters.ContainsKey('To')) {
        throw "Assert-RigChange takes either -To or -Unchanged, not both."
    }
    if (-not $Unchanged -and -not $PSBoundParameters.ContainsKey('To')) {
        throw "Assert-RigChange needs -To <value> or -Unchanged."
    }
    # The re-read must reproduce the baseline's request exactly, ReaderArgs
    # included. It used to drop them, so every baseline taken through a reader
    # with a query string (thing, config, console, inventory, reflect, nearby)
    # re-read as a bare '/thing' or '/config', got a 400, and ended the check
    # inconclusive with no comparison made at all.
    $baselineArgs = if ($Baseline.PSObject.Properties['ReaderArgs']) { $Baseline.ReaderArgs } else { $null }
    $now = Read-RigValue -From $Baseline.Instance -Reader $Baseline.Reader -Select $Baseline.Select -Of $Baseline.Of -ReaderArgs $baselineArgs -Context $Context
    if ($Unchanged) {
        if (Test-PlaytestValueEqual $Baseline.Value $now.Value) {
            Write-Host "[Playtest]   ok   $($Baseline.Instance).$($Baseline.Reader).$($Baseline.Select) unchanged at [$($now.Value)]"
            return $now
        }
        throw (New-PlaytestSignal -Kind 'fail' -Detector 'assertion' `
            -Message "$($Baseline.Instance).$($Baseline.Reader).$($Baseline.Select) was expected to stay at [$($Baseline.Value)] and is now [$($now.Value)]. $Because" `
            -Detail @{ instance = $Baseline.Instance; before = "$($Baseline.Value)"; after = "$($now.Value)"; because = $Because })
    }
    if (Test-PlaytestValueEqual $To $now.Value) {
        Write-Host "[Playtest]   ok   $($Baseline.Instance).$($Baseline.Reader).$($Baseline.Select) moved [$($Baseline.Value)] -> [$($now.Value)]"
        return $now
    }
    throw (New-PlaytestSignal -Kind 'fail' -Detector 'assertion' `
        -Message "$($Baseline.Instance).$($Baseline.Reader).$($Baseline.Select) was expected to become [$To] and reads [$($now.Value)] (baseline was [$($Baseline.Value)]). $Because" `
        -Detail @{ instance = $Baseline.Instance; before = "$($Baseline.Value)"; after = "$($now.Value)"; expected = "$To"; because = $Because })
}

function Assert-BinaryUnderTest {
    <#
    .SYNOPSIS
        Prove that the running instances carry the build this check is about.

    .DESCRIPTION
        Runs before anything else, and a check that never calls it cannot report
        pass: the runner downgrades an unattested check to inconclusive with
        detector 'binary-not-attested'. The reason is a real near-miss. A live run
        re-seeded each instance from the developer's own mod folder, that copy was
        weeks old, and the whole session would have measured the wrong DLL had the
        file sizes not happened to differ visibly.

        Three independent things, because each one alone can be satisfied by a
        stale rig:
          - the provision stamp exists and names the tree, so the instance is one
            this launcher built rather than a leftover;
          - the deployed file matches the build under test by length and write
            time, when -DllPath names the build;
          - a live GET /config?guid=<mod> from INSIDE each running process returns
            the expected number of entries, which is the only one of the three
            that can tell what the process actually loaded.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string[]] $On,
        [Parameter(Mandatory)] [string] $Mod,
        [int] $ExpectedConfigCount = 0,
        [int] $ExpectedGroupCount = 0,
        [string] $DllPath = '',
        [string] $DeployedRelativePath = '',
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    $report = [ordered]@{
        mod = $Mod; expectedConfigCount = $ExpectedConfigCount; expectedGroupCount = $ExpectedGroupCount
        buildUnderTest = ''; instances = @()
    }
    $build = $null
    if ($DllPath) {
        if (-not (Test-Path -LiteralPath $DllPath)) {
            throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'binary-missing' `
                -Message "The build under test was named as '$DllPath' and is not there, so nothing can be attested and the check is inconclusive. Build the mod first." `
                -Detail @{ dllPath = $DllPath })
        }
        $build = Get-Item -LiteralPath $DllPath
        $report['buildUnderTest'] = "{0} ({1} bytes, {2})" -f $DllPath, $build.Length, $build.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ss'Z'")
    }

    foreach ($name in $On) {
        Assert-PlaytestInstanceName -Name $name -Context $ctx -Parameter '-On'
        $row = [ordered]@{ instance = $name; stamp = $null; deployed = ''; configCount = -1; groupCount = -1 }

        $stampPath = Get-PlaytestInstanceStampPath -Name $name
        if ($stampPath -and (Test-Path -LiteralPath $stampPath)) {
            try { $row['stamp'] = (Get-Content -Raw -LiteralPath $stampPath | ConvertFrom-Json) } catch { $row['stamp'] = $null }
        }
        if (-not $row['stamp']) {
            throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'provision-stamp-missing' `
                -Message "Instance '$name' has no readable provision.stamp, so there is no way to say what it was built from and the check is inconclusive. Rebuild it: testrig create -Target $name -Force -As <id>" `
                -Detail @{ instance = $name; stamp = "$stampPath" })
        }

        if ($build -and $DeployedRelativePath) {
            $deployed = Join-Path (Split-Path -Parent $stampPath) $DeployedRelativePath
            $row['deployed'] = "$deployed"
            if (-not (Test-Path -LiteralPath $deployed)) {
                throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'binary-not-deployed' `
                    -Message "Instance '$name' does not carry '$DeployedRelativePath', so it is not running the build under test and the check is inconclusive. Copy the build in after provisioning and before start; never write to the developer's own mod folder." `
                    -Detail @{ instance = $name; expected = "$deployed" })
            }
            $have = Get-Item -LiteralPath $deployed
            if ($have.Length -ne $build.Length) {
                throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'binary-stale' `
                    -Message "Instance '$name' carries a DIFFERENT build of $Mod than the one under test ($($have.Length) bytes against $($build.Length)). A provision re-seeds from the developer's own mod folder and that copy is routinely stale; copy the fresh build in and start again. The check is inconclusive rather than failed, because nothing was measured against the code under test." `
                    -Detail @{ instance = $name; deployedBytes = $have.Length; buildBytes = $build.Length })
            }
        }

        if ($ExpectedConfigCount -gt 0 -or $ExpectedGroupCount -gt 0) {
            $obs = Read-RigValue -From $name -Reader 'config' -Select '.' -ReaderArgs @{ guid = $Mod } -Context $ctx
            $entries = @(Select-PlaytestPath -Object $obs.Value -Path 'entries')
            $count = if (@($entries).Count -gt 0) { @($entries).Count } else { [int](Select-PlaytestPath -Object $obs.Value -Path 'count') }
            $groups = @($entries | ForEach-Object { "$($_.section)" } | Sort-Object -Unique)
            $row['configCount'] = $count
            $row['groupCount']  = @($groups).Count
            if ($ExpectedConfigCount -gt 0 -and $count -ne $ExpectedConfigCount) {
                throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'binary-config-mismatch' `
                    -Message "Instance '$name' reports $count config entries for $Mod and this check is written against $ExpectedConfigCount. The running process is not carrying the build under test, so the check is inconclusive rather than failed." `
                    -Detail @{ instance = $name; got = $count; expected = $ExpectedConfigCount })
            }
            if ($ExpectedGroupCount -gt 0 -and @($groups).Count -ne $ExpectedGroupCount) {
                throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'binary-config-mismatch' `
                    -Message "Instance '$name' reports $(@($groups).Count) settings groups for $Mod and this check is written against $ExpectedGroupCount. The running process is not carrying the build under test, so the check is inconclusive rather than failed." `
                    -Detail @{ instance = $name; got = @($groups).Count; expected = $ExpectedGroupCount })
            }
        }
        $report['instances'] += $row
        Write-Host "[Playtest]   binary  $name : $Mod, $($row['configCount']) config entries, provisioned $($row['stamp'].provisionedUtc)"
    }

    Write-PlaytestEvidence -Name 'binary.json' -Content (ConvertTo-PlaytestJson $report) -Context $ctx | Out-Null
    if ($ctx) { $ctx.BinaryAttested = $true }
    return $report
}

function Get-PlaytestInstanceStampPath {
    # data/<instance>/provision.stamp, off the rig home. Injected in the offline
    # suite through -RigHome, so nothing here has to know a real path.
    param([Parameter(Mandatory)] [string] $Name)
    if (-not $script:PlaytestRigHome) { return '' }
    return (Join-Path (Join-Path (Join-Path $script:PlaytestRigHome 'ClientRig\data') $Name) 'provision.stamp')
}

function Assert-RigOk {
    # A decoy that exists to explain itself. Reaching for it is the exact mistake
    # this harness is built to prevent, and a clear error beats a rule in a file.
    throw "There is no Assert-RigOk in this harness, deliberately. An endpoint answering ok is a statement about the request, not about the world: a /connect answered ok on 2026-08-09 while nothing had joined. Assert on the authority instead: Assert-RigValue -From <the instance that owns the fact> -Reader <reader> -Select <field> -Is <value> -Because '<why>'."
}

function Assert-RigResponse {
    throw "There is no Assert-RigResponse in this harness, deliberately. An action's response is evidence, not a conclusion. Read the value back from the instance that is the authority for it: Assert-RigValue -From <instance> -Reader status -Select hosting -Is `$true -Because '<why>'."
}

# ---- lock, bring-up and teardown -------------------------------------------

function New-PlaytestContext {
    param(
        [Parameter(Mandatory)] [string] $CheckName,
        [string] $SuiteName = '',
        [string] $EvidenceDir = '',
        [hashtable[]] $Instances = @()
    )
    return [pscustomobject]@{
        PSTypeName     = 'Playtest.Context'
        CheckName      = $CheckName
        SuiteName      = $SuiteName
        EvidenceDir    = $EvidenceDir
        Instances      = $Instances
        InstanceNames  = @($Instances | ForEach-Object { "$($_.Name)" })
        Owner          = ''
        Started        = @()
        Sequence       = 0
        Attempts       = 0
        MaxAttempts    = 1
        Degraded       = $false
        Detectors      = @()
        BinaryAttested = $false
        LastRefreshUtc = $null
        TeardownNotes  = @()
    }
}

function Use-Rig {
    <#
    .SYNOPSIS
        Hold the rig for the duration of one body, and give it back whatever happens.

    .DESCRIPTION
        Acquires the session lock, runs the body, and in a finally stops the
        instances this call started, ONE 'stop -Target <name>' at a time, then
        releases the lock.

        It never stops by 'all' or 'clients'. Either reaches every instance on the
        machine including another session's live test, and a harness that reaches
        outside its own reservation is a harness nobody can leave running.

        The stop order is joiners first and hosts last, matching the launcher's
        own teardown rule: whoever holds the world saves and quits after everyone
        attached to it has left.

        A body that throws still gets the full teardown, and the throw is
        re-raised afterwards so the runner can classify it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $Purpose,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [Parameter(Mandatory)] $Context,
        [int] $TtlMinutes = 20,
        [int] $WaitSeconds = 0,
        [switch] $KeepState
    )
    $ctx = $Context
    $lockArgs = @('lock', '-Purpose', $Purpose, '-TtlMinutes', "$TtlMinutes")
    if ($WaitSeconds -gt 0) { $lockArgs += @('-WaitSeconds', "$WaitSeconds") }
    if ($KeepState)         { $lockArgs += '-KeepState' }

    $lockRes = Invoke-RigCommand -ArgList $lockArgs -Label 'lock' -Context $ctx
    $text = "$($lockRes.StdOut)`n$($lockRes.StdErr)"
    if ($ctx.EvidenceDir) {
        Write-PlaytestEvidence -Name 'hygiene-reset.txt' -Content $text -Context $ctx | Out-Null
    }
    if ([int]$lockRes.ExitCode -ne 0) {
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'rig-unavailable' `
            -Message "Could not take the rig session lock, so this check did not run and is inconclusive rather than failed. Another agent may hold it; the lock names its purpose. Launcher said: $((($text -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 3) -join ' ')" `
            -Detail @{ exit = $lockRes.ExitCode })
    }
    # ONE machine-readable line, by contract with the launcher.
    #
    # This used to scrape the owner id out of the launcher's human-readable block
    # with two regexes over two different sentences, so any rewording of that
    # prose would silently have broken every check in every suite with
    # 'rig-unavailable' and nothing would have said why. testrig.ps1 prints
    # 'TESTRIG-OWNER <id>' as the last line of a successful acquisition precisely
    # so a harness never has to read prose.
    $owner = ''
    $m = [regex]::Match($text, '(?m)^\s*TESTRIG-OWNER\s+([0-9a-fA-F]{6,16})\s*$')
    if ($m.Success) { $owner = $m.Groups[1].Value }
    if (-not $owner) {
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'rig-unavailable' `
            -Message "The lock was taken but the owner id could not be read back from the launcher output, so nothing can be driven safely and nothing could be released afterwards. Check TestRig/session.lock by hand before running again." `
            -Detail @{ output = $text })
    }
    $ctx.Owner = $owner
    $ctx.LastRefreshUtc = Get-PlaytestNowUtc
    if ($ctx.EvidenceDir) {
        Write-PlaytestEvidence -Name 'lock.txt' -Content (@(
            "owner   : $owner"
            "purpose : $Purpose"
            "ttl     : $TtlMinutes min"
            "acquired: $(Get-PlaytestStamp)"
        ) -join "`n") -Context $ctx | Out-Null
    }
    Write-Host "[Playtest]   lock owner $owner"

    try {
        return (& $Body $ctx)
    }
    finally {
        Stop-RigInstances -Context $ctx
        $unlock = Invoke-RigCommand -ArgList @('unlock', '-As', $owner) -Label 'unlock' -Context $ctx
        if ([int]$unlock.ExitCode -ne 0) {
            $note = "RELEASE FAILED (exit $($unlock.ExitCode)). The lock expires on its own timer, but until then the rig is held. Check: testrig status -As $owner"
            $ctx.TeardownNotes += $note
            Write-Warning "[Playtest] $note"
        }
        else {
            Write-Host "[Playtest]   lock released ($owner)"
        }
        if ($ctx.EvidenceDir) {
            Write-PlaytestEvidence -Name 'lock.txt' -Append -Content (@(
                "released: $(Get-PlaytestStamp) (exit $($unlock.ExitCode))"
                "notes   : $((@($ctx.TeardownNotes)) -join ' | ')"
            ) -join "`n") -Context $ctx | Out-Null
        }
    }
}

function Stop-RigInstances {
    # By NAME, joiners before hosts, one command each, never -Target all/clients. Every failure
    # is recorded and none of them stops the loop: the release that follows
    # matters more than any single stop, and an instance left up would hold the
    # whole rig for every other agent.
    param($Context)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx -or -not $ctx.Owner) { return }
    $started = @($ctx.Started)
    if (@($started).Count -eq 0) { return }
    $hosts   = @($ctx.Instances | Where-Object { "$($_.Role)" -eq 'host' }   | ForEach-Object { "$($_.Name)" })
    $ordered = @(@($started | Where-Object { $hosts -notcontains $_ }) + @($started | Where-Object { $hosts -contains $_ }))
    foreach ($name in $ordered) {
        $res = Invoke-RigCommand -ArgList @('stop', '-Target', $name, '-As', $ctx.Owner, '-TimeoutSeconds', '60') -Label "stop-$name" -Context $ctx
        if ([int]$res.ExitCode -ne 0) {
            # The launcher REFUSES to quit on top of a world whose save it could
            # not confirm, which is right for a world somebody wants to keep and
            # wrong for every world this harness makes. A check's world is
            # created fresh from `World = <id>` and has no station name yet, so
            # `/save` with no name has nothing to save under and the refusal
            # fires on EVERY host check. Unhandled, that leaves the instance up
            # and the rig lock held, which is the one thing teardown exists to
            # prevent. Retry once with -Force and record that the world was
            # discarded; a check that wanted its world kept must save it by name
            # in its own body first.
            $first = (("$($res.StdErr)$($res.StdOut)" -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1)
            Write-Host "[Playtest]   stop of '$name' refused, retrying with -Force (the check's world is disposable)"
            $forced = Invoke-RigCommand -ArgList @('stop', '-Target', $name, '-As', $ctx.Owner, '-TimeoutSeconds', '60', '-Force') -Label "stop-forced-$name" -Context $ctx
            if ([int]$forced.ExitCode -ne 0) {
                $note = "stop of '$name' failed even with -Force (exit $($forced.ExitCode)): $((("$($forced.StdErr)$($forced.StdOut)" -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 1))"
                $ctx.TeardownNotes += $note
                Write-Warning "[Playtest] $note"
            }
            else {
                $note = "stopped '$name' with -Force after: $first"
                $ctx.TeardownNotes += $note
                Write-Host "[Playtest]   stopped $name (forced; that world is gone)"
            }
        }
        else {
            Write-Host "[Playtest]   stopped $name"
        }
    }
    $ctx.Started = @()
}

function Wait-RigStage {
    <#
    .SYNOPSIS
        Barrier on one instance, with the flake taxonomy applied to the wait.

    .DESCRIPTION
        The launcher has its own -Wait, and this is deliberately not it: the
        detectors need the /status blob at the moment the barrier gives up, and a
        barrier run in a child process can only report that it timed out. The
        Workshop park in particular is only distinguishable from a slow boot by
        loadedPluginCount and gameInitialized.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)][ValidateSet('ping', 'modsLoaded', 'menu', 'inWorld')] [string] $Stage,
        [int] $WaitSeconds = 300,
        [double] $PollSeconds = 5,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    $port = Resolve-RigInstancePort -Name $Name
    $attempt = 1
    if ($PollSeconds -le 0) { $PollSeconds = 1 }
    # Two brakes on the inner loop, not one. The wall-clock deadline is the real
    # budget; the poll cap is what stops a frozen or injected clock from turning
    # a barrier into an infinite loop, which is the difference between a harness
    # that reports a boot timeout and one that hangs holding the rig.
    $maxPolls = [int][Math]::Ceiling($WaitSeconds / $PollSeconds) + 2
    while ($true) {
        $deadline = (Get-PlaytestNowUtc).AddSeconds($WaitSeconds)
        $last = $null
        $polls = 0
        while (((Get-PlaytestNowUtc) -lt $deadline) -and ($polls -lt $maxPolls)) {
            $polls++
            $status = $null
            try { $status = Invoke-PlaytestTransport -Port $port -Path '/status' -BodyJson '' -TimeoutSec 15 }
            catch { $status = $null }
            if ($status) {
                $last = $status
                $reached = switch ($Stage) {
                    'ping'       { $true }
                    'modsLoaded' { [int]$status.loadedPluginCount -gt 10 }
                    'menu'       { ($status.gameInitialized -eq $true) -and ("$($status.phase)" -eq 'menu') }
                    'inWorld'    { "$($status.phase)" -eq 'inWorld' }
                }
                if ($reached) {
                    Add-PlaytestAttempt -Context $ctx -Attempts $attempt
                    Write-Host "[Playtest]   $Name reached '$Stage'"
                    return $status
                }
            }
            Wait-PlaytestSeconds $PollSeconds
        }

        $probe = New-PlaytestProbe -Kind 'barrier' -Instance $Name -Stage $Stage -Attempt $attempt -Status $last
        $flake = Resolve-PlaytestFlake $probe
        if ($flake) { Add-PlaytestDetector -Context $ctx -Name $flake.Name }
        $maxAttempts = if ($flake) { [int]$flake.MaxAttempts } else { 1 }
        if (-not $flake -or $flake.Remedy -eq 'abort' -or $attempt -ge $maxAttempts) {
            Add-PlaytestAttempt -Context $ctx -Attempts $attempt
            $why = if ($flake) { $flake.Summary } else { 'nothing in the taxonomy explains it' }
            throw (New-PlaytestSignal -Kind 'inconclusive' -Detector (($flake) ? $flake.Name : 'boot-timeout') `
                -Message "'$Name' did not reach '$Stage' within ${WaitSeconds}s after $attempt attempt(s). $why Last status: phase=$($last.phase) gameInitialized=$($last.gameInitialized) plugins=$($last.loadedPluginCount)" `
                -Detail @{ instance = $Name; stage = $Stage; attempts = $attempt })
        }
        if ($flake.Remedy -eq 'restart-instance') { Restart-RigInstance -Name $Name -Reason $flake.Name -Context $ctx }
        Wait-PlaytestSeconds ([double]$flake.GapSeconds)
        $attempt++
    }
}

function Start-RigInstances {
    <#
    .SYNOPSIS
        Bring the check's instances up in the only order that works, and prove it.

    .DESCRIPTION
        Hosts first and all the way into their world, then joiners, because
        /connect has nothing to reach until a host is hosting. Every post
        condition is read back from the AUTHORITY:

          - a host is hosting when ITS OWN /status says hosting true and role
            listenHost, not when /host answered 200;
          - a joiner has arrived when the HOST roster carries it, not when
            /connect answered ok.

        Both of those were live failures before they were rules.
    #>
    param($Context, [int] $BootWaitSeconds = 300, [int] $WorldWaitSeconds = 600)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    $hostSpecs   = @($ctx.Instances | Where-Object { "$($_.Role)" -eq 'host' })
    $clientSpecs = @($ctx.Instances | Where-Object { "$($_.Role)" -ne 'host' })

    foreach ($spec in $hostSpecs) {
        Start-RigInstanceProcess -Spec $spec -Context $ctx
        Wait-RigStage -Name $spec.Name -Stage 'menu' -WaitSeconds $BootWaitSeconds -Context $ctx | Out-Null
        if ($spec.World -or $spec.Save) {
            $body = @{}
            if ($spec.World) { $body['world'] = "$($spec.World)" }
            if ($spec.Save)  { $body['save']  = "$($spec.Save)" }
            if ($spec.GamePort) { $body['port'] = [int]$spec.GamePort }
            Invoke-RigAction -On $spec.Name -Path '/host' -Body $body -Blocking -Context $ctx | Out-Null
            Wait-RigStage -Name $spec.Name -Stage 'inWorld' -WaitSeconds $WorldWaitSeconds -Context $ctx | Out-Null

            # The authority for "is this hosting" is the host's own status, and
            # nothing else. NetworkServer.Host() no-ops from the menu and gives up
            # quietly after three failed binds, so the call returning says nothing.
            $status = $null
            try { $status = Invoke-PlaytestTransport -Port (Resolve-RigInstancePort -Name $spec.Name) -Path '/status' -BodyJson '' -TimeoutSec 30 } catch { $status = $null }
            $probe = New-PlaytestProbe -Kind 'poststate' -Instance $spec.Name -Path '/host' -Status $status
            $flake = Resolve-PlaytestFlake $probe
            if ($flake) {
                Add-PlaytestDetector -Context $ctx -Name $flake.Name
                throw (New-PlaytestSignal -Kind 'inconclusive' -Detector $flake.Name `
                    -Message "'$($spec.Name)' answered POST /host but is not hosting: /status reports hosting=$($status.hosting) role=$($status.role). $($flake.Summary) The rig could not be brought up, so the check is inconclusive and never failed." `
                    -Detail @{ instance = $spec.Name; hosting = "$($status.hosting)"; role = "$($status.role)" })
            }
            Write-Host "[Playtest]   $($spec.Name) is hosting on port $($status.hostPort)"
        }
    }

    foreach ($spec in $clientSpecs) {
        Start-RigInstanceProcess -Spec $spec -Context $ctx
        Wait-RigStage -Name $spec.Name -Stage 'menu' -WaitSeconds $BootWaitSeconds -Context $ctx | Out-Null
        $targetName = if ($spec.ConnectTo) { "$($spec.ConnectTo)" } elseif (@($hostSpecs).Count -gt 0) { "$($hostSpecs[0].Name)" } else { '' }
        if (-not $targetName) { continue }
        Connect-RigJoiner -Name "$($spec.Name)" -To $targetName `
            -Address $(if ($spec.Address) { "$($spec.Address)" } else { '127.0.0.1' }) `
            -WorldWaitSeconds $WorldWaitSeconds -Context $ctx | Out-Null
    }
}

function Connect-RigJoiner {
    <#
    .SYNOPSIS
        Join one instance to a host, and prove it arrived from the HOST roster.

    .DESCRIPTION
        The single implementation of "connect a joiner", used by the harness's own
        bring-up AND by any check body that bounces a joiner. That it exists is the
        fix for a specific failure: on 2026-08-11 four of eight checks came back
        inconclusive with 'joiner-not-in-roster', and none of them was a join
        problem. Ten of ten hand-driven joins landed on the same rig the same
        evening, and the harness's own bring-up connected every one of those four.
        What failed was the SECOND connect, the one a check body issues after it has
        disconnected the joiner to change its client half, because that path had its
        own copy of the logic and the copy did not retry.

        Three things this does that a bare POST /connect does not:

          - It confirms from the HOST, never from the joiner. A /connect answered ok
            on a run where nothing had joined; the server-side roster is the only
            authority for "did it arrive".
          - It POLLS the roster instead of reading it once. The roster is written
            when the server registers the client, which is not the same instant the
            joiner reports inWorld, so a single read right after the barrier can be
            a real join measured too early.
          - It RETRIES, because "a client that has just disconnected is still
            settling" is documented behaviour and the reason connect-first-attempt
            sits at the top of the flake taxonomy. Each retry disconnects first, so
            the next attempt starts from the menu rather than from a half state.

        A retry makes the check a DEGRADED pass, never a clean one, exactly like
        every other retried condition.

    .OUTPUTS
        An object carrying Roster (the host-side count after arrival), Attempts,
        and SeqBeforeConnect.

        SeqBeforeConnect is the joiner's console sequence number read immediately
        before the FINAL /connect, and it exists because retrying broke a check
        that retrying was supposed to fix. Anything the mod prints once PER JOIN
        (the join summary, the effective-settings line) appears once per attempt,
        so a check that baselined its console before the whole helper ran counted
        three lines after three attempts and failed a correct mod. Baseline from
        this instead: it is the sequence as of the join that actually landed, so
        the count is per-join however many attempts it took.
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $To,
        [string] $Address = '127.0.0.1',
        [int] $Port = 0,
        [int] $WorldWaitSeconds = 600,
        [int] $Attempts = 3,
        [double] $GapSeconds = 10,
        [double] $RosterPollSeconds = 30,
        $Context
    )
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }

    function Get-RosterCount([string] $HostName) {
        $s = $null
        try { $s = Invoke-PlaytestTransport -Port (Resolve-RigInstancePort -Name $HostName) -Path '/status' -BodyJson '' -TimeoutSec 30 }
        catch { return -1 }
        return @(Select-PlaytestPath -Object $s -Path 'connectedClients').Count
    }

    $hostStatus = $null
    try { $hostStatus = Invoke-PlaytestTransport -Port (Resolve-RigInstancePort -Name $To) -Path '/status' -BodyJson '' -TimeoutSec 30 } catch { $hostStatus = $null }
    $resolvedPort = if ($Port -gt 0) { $Port } elseif ($hostStatus -and [int]$hostStatus.hostPort -gt 0) { [int]$hostStatus.hostPort } else { 0 }
    if ($resolvedPort -le 0) {
        Add-PlaytestDetector -Context $ctx -Name 'host-not-hosting'
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'host-not-hosting' `
            -Message "'$To' does not report a game port, so '$Name' has nothing to join and the check is inconclusive." `
            -Detail @{ host = $To; joiner = $Name })
    }

    $before = @(Select-PlaytestPath -Object $hostStatus -Path 'connectedClients').Count
    $lastAfter = $before
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        if ($attempt -gt 1) {
            # Start from the menu. Reconnecting from a half-joined state is what
            # the settling window is about, and neither of these two is a reason
            # to stop trying: the disconnect is best-effort and the barrier after
            # it is only there to give the client time to land at the menu.
            try { Invoke-RigAction -On $Name -Path '/disconnect' -Body @{ } -Blocking -NoRetry -Context $ctx | Out-Null } catch { }
            try { Wait-RigStage -Name $Name -Stage 'menu' -WaitSeconds 180 -Context $ctx | Out-Null } catch { }
            Wait-PlaytestSeconds $GapSeconds
            Add-PlaytestDetector -Context $ctx -Name 'connect-first-attempt'
            Add-PlaytestAttempt -Context $ctx -Attempts $attempt
            Write-Host "[Playtest]   $Name retrying the join to $To (attempt $attempt of $Attempts)"
        }

        # NOT `continue` from inside a catch. `break` and `continue` in a
        # try/catch are flow-control exceptions in PowerShell: they unwind past
        # the enclosing try and, from inside a function, can escape the loop and
        # even the function entirely, so the caller's own foreach silently takes
        # the continue. A flag keeps the control flow ordinary and readable.
        $arrived = $false
        $lastError = $null
        # Read immediately before the connect, so a caller measuring per-join
        # output baselines from the attempt that actually landed rather than from
        # before the retries. See the OUTPUTS note above.
        $seqBefore = $null
        try {
            $s = Invoke-PlaytestTransport -Port (Resolve-RigInstancePort -Name $Name) -Path '/console/log?limit=1' -BodyJson '' -TimeoutSec 30
            $seqBefore = $s.nextSeq
        }
        catch { $seqBefore = $null }
        try {
            Invoke-RigAction -On $Name -Path '/connect' -Body @{ address = $Address; port = $resolvedPort } -Blocking -Context $ctx | Out-Null
            Wait-RigStage -Name $Name -Stage 'inWorld' -WaitSeconds $WorldWaitSeconds -Context $ctx | Out-Null
            $arrived = $true
        }
        catch {
            # Keep it: if every attempt fails this way, the last one explains the
            # give-up better than the roster count does.
            $lastError = $_
        }

        if ($arrived) {
            # Poll rather than read once: inWorld on the joiner and the row
            # appearing in the server roster are two different instants.
            $deadline = (Get-PlaytestNowUtc).AddSeconds($RosterPollSeconds)
            do {
                $lastAfter = Get-RosterCount $To
                if ($lastAfter -gt $before) {
                    Write-Host "[Playtest]   $Name is in $To roster ($lastAfter client(s), attempt $attempt)"
                    return [pscustomobject]@{
                        PSTypeName       = 'Playtest.JoinResult'
                        Joiner           = $Name
                        Host             = $To
                        Roster           = $lastAfter
                        Attempts         = $attempt
                        SeqBeforeConnect = $seqBefore
                    }
                }
                Wait-PlaytestSeconds 2
            } while ((Get-PlaytestNowUtc) -lt $deadline)
        }
        elseif ($attempt -ge $Attempts -and $lastError) {
            # Every attempt died at the connect itself rather than at the roster,
            # so the connect's own signal is the honest answer. Rethrowing it
            # keeps its detector (connect-first-attempt, boot-timeout, whatever
            # fired) instead of relabelling it as a roster problem it is not.
            throw $lastError
        }
    }

    $flake = ($script:PlaytestFlakes | Where-Object { $_.Name -eq 'joiner-not-in-roster' } | Select-Object -First 1)
    Add-PlaytestDetector -Context $ctx -Name 'joiner-not-in-roster'
    throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'joiner-not-in-roster' `
        -Message "'$Name' reported a connection but the roster on '$To' did not grow ($before then $lastAfter) after $Attempts attempt(s), each polled for $RosterPollSeconds s. $($flake.Summary) The rig could not be brought up, so the check is inconclusive and never failed." `
        -Detail @{ joiner = $Name; host = $To; before = $before; after = $lastAfter; attempts = $Attempts })
}

function Start-RigInstanceProcess {
    param($Spec, $Context)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    $name = "$($Spec.Name)"
    $res = Invoke-RigCommand -ArgList @('start', '-Target', $name, '-As', $ctx.Owner) -Label "start-$name" -Context $ctx
    if ($ctx.Started -notcontains $name) { $ctx.Started += $name }
    if ([int]$res.ExitCode -ne 0) {
        throw (New-PlaytestSignal -Kind 'inconclusive' -Detector 'instance-start-failed' `
            -Message "Could not start '$name', so the check did not run and is inconclusive. Launcher exit $($res.ExitCode): $((("$($res.StdErr)$($res.StdOut)" -split "`n") | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' ')" `
            -Detail @{ instance = $name; exit = $res.ExitCode })
    }
}

function Save-PlaytestConsoleTail {
    <#
    .SYNOPSIS
        Append each instance's console tail to the evidence bundle, per step.
    .DESCRIPTION
        Called by the runner around every step and callable from a check. It never
        throws: an unreachable console is a gap in the evidence, and turning that
        into a failed check would be exactly the confusion this harness exists to
        avoid.
    #>
    param([string] $Step = 'step', [string[]] $Instances, $Context)
    $ctx = if ($Context) { $Context } else { $script:PlaytestContext }
    if (-not $ctx) { return }
    $names = if ($Instances) { $Instances } else { @($ctx.InstanceNames) }
    foreach ($name in $names) {
        $text = ''
        try {
            $port = Resolve-RigInstancePort -Name $name
            $resp = Invoke-PlaytestTransport -Port $port -Path '/console/log?limit=120&source=console' -BodyJson '' -TimeoutSec 20
            $lines = @(Select-PlaytestPath -Object $resp -Path 'lines')
            $text = (@($lines | ForEach-Object { if ($_ -is [string]) { $_ } else { "$($_.text)" } }) -join "`n")
        }
        catch { $text = "<console unreachable: $($_.Exception.Message)>" }
        Write-PlaytestEvidence -Kind 'console' -Name "$((ConvertTo-PlaytestSlug $name)).tail.txt" -Append -Content (@(
            ''
            "===== $Step ($(Get-PlaytestStamp)) ====="
            $text
        ) -join "`n") -Context $ctx | Out-Null
    }
}

# ---- suite and runner ------------------------------------------------------

function Register-PlaytestCheck {
    <#
    .SYNOPSIS
        Declare one check: what it needs running, what binary it is about, what it does.

    .DESCRIPTION
        -Instances is an ordered list of hashtables:
            @{ Name='host1'; Role='host';   World='Lunar' }
            @{ Name='join1'; Role='client'; ConnectTo='host1' }
        Role 'host' brings the instance up and puts it into World or Save, then
        proves it is hosting from its own status. Role 'client' joins the named
        host and is proved to have arrived from the HOST roster.

        -Binary is the attestation this check needs before it may pass:
            @{ Mod='net.example'; ConfigEntryCount=33; ConfigGroupCount=9;
               DllPath='<build under test>'; DeployedRelativePath='userdata\mods\Example\Example.dll' }

        -Body receives the context object and does the work. It has one job:
        make something happen with Invoke-RigAction, then conclude with an
        Assert-Rig* verb reading from the authority.
    #>
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [hashtable[]] $Instances,
        [Parameter(Mandatory)] [scriptblock] $Body,
        [string] $Summary = '',
        [hashtable] $Binary,
        [string] $Purpose = '',
        [int] $TtlMinutes = 20
    )
    foreach ($spec in $Instances) {
        if (-not $spec.Name) { throw "Register-PlaytestCheck '$Name': every -Instances entry needs a Name." }
        $role = if ($spec.Role) { "$($spec.Role)" } else { 'client' }
        if ($role -ne 'host' -and $role -ne 'client') {
            throw "Register-PlaytestCheck '$Name': instance '$($spec.Name)' has Role '$role'; it must be 'host' or 'client'."
        }
        $spec['Role'] = $role
    }
    $script:PlaytestChecks += [pscustomobject]@{
        PSTypeName = 'Playtest.Check'
        Name       = $Name
        Summary    = $Summary
        Instances  = $Instances
        Binary     = $Binary
        Body       = $Body
        Purpose    = if ($Purpose) { $Purpose } else { "Playtest: $Name" }
        TtlMinutes = $TtlMinutes
    }
}

function Format-PlaytestOutcome {
    # 'pass', 'pass (degraded, 3 attempts)', 'fail', 'inconclusive (boot-timeout)'.
    # The degraded form exists so a check that only worked on the third go can
    # never be read later as a clean one.
    param([string] $Outcome, [bool] $Degraded, [int] $Attempts, [string] $Detector = '')
    switch ($Outcome) {
        'pass' {
            if ($Degraded) { return "pass (degraded, $([Math]::Max(2, $Attempts)) attempts)" }
            return 'pass'
        }
        'fail' { return 'fail' }
        default {
            if ($Detector) { return "inconclusive ($Detector)" }
            return 'inconclusive'
        }
    }
}

function Invoke-PlaytestCheck {
    <#
    .SYNOPSIS
        Run one registered check under its own lock, and classify the result.
    #>
    param(
        [Parameter(Mandatory)] $Check,
        [Parameter(Mandatory)] [string] $BundleRoot,
        [int] $Index = 1,
        [int] $LockWaitSeconds = 0
    )
    $evidenceDir = New-PlaytestCheckEvidence -BundleRoot $BundleRoot -Index $Index -CheckName $Check.Name
    $ctx = New-PlaytestContext -CheckName $Check.Name -SuiteName $script:PlaytestSuiteName -EvidenceDir $evidenceDir -Instances $Check.Instances
    $script:PlaytestContext = $ctx
    $started = Get-PlaytestNowUtc

    $outcome  = 'pass'
    $detector = ''
    $message  = ''
    $detail   = ''

    try {
        Use-Rig -Purpose $Check.Purpose -TtlMinutes $Check.TtlMinutes -WaitSeconds $LockWaitSeconds -Context $ctx -Body {
            param($c)
            Start-RigInstances -Context $c
            if ($Check.Binary) {
                $b = $Check.Binary
                Assert-BinaryUnderTest -On @($c.InstanceNames) -Mod "$($b.Mod)" `
                    -ExpectedConfigCount ([int]$b.ConfigEntryCount) -ExpectedGroupCount ([int]$b.ConfigGroupCount) `
                    -DllPath "$($b.DllPath)" -DeployedRelativePath "$($b.DeployedRelativePath)" -Context $c | Out-Null
            }
            Save-PlaytestConsoleTail -Step 'after bring-up' -Context $c
            # finally, not a following statement: a check that ended on an
            # assertion failure is exactly the one whose console tail is worth
            # having, and skipping the capture on the failing path would leave
            # the interesting run as the one with the thinnest evidence.
            try     { & $Check.Body $c }
            finally { Save-PlaytestConsoleTail -Step 'after check body' -Context $c }
        } | Out-Null
    }
    catch {
        $r = Resolve-PlaytestError $_
        $outcome  = $r.Outcome
        $detector = $r.Detector
        $message  = $r.Message
        $detail   = $r.Detail
    }

    # The binary gate. A check that never attested what it was running cannot
    # pass, because a green result against an unknown DLL is worse than no result.
    if ($outcome -eq 'pass' -and -not $ctx.BinaryAttested) {
        $outcome  = 'inconclusive'
        $detector = 'binary-not-attested'
        $message  = "The check body completed but never attested the binary under test, so its result says nothing about any particular build and cannot be a pass. Give the check a -Binary block, or call Assert-BinaryUnderTest yourself before the first action."
    }

    $ended = Get-PlaytestNowUtc
    $result = [pscustomobject]@{
        PSTypeName  = 'Playtest.CheckResult'
        Name        = $Check.Name
        Outcome     = $outcome
        Degraded    = [bool]$ctx.Degraded
        Attempts    = [int]$ctx.Attempts
        MaxAttempts = [int]$ctx.MaxAttempts
        Detector    = $detector
        Detectors   = @($ctx.Detectors)
        Message     = $message
        Text        = (Format-PlaytestOutcome -Outcome $outcome -Degraded ([bool]$ctx.Degraded) -Attempts ([int]$ctx.MaxAttempts) -Detector $detector)
        DurationMs  = [int]($ended - $started).TotalMilliseconds
        EvidenceDir = $evidenceDir
        Owner       = $ctx.Owner
        Notes       = @($ctx.TeardownNotes)
    }
    Set-Content -LiteralPath (Join-Path $evidenceDir 'check.json') -Encoding utf8 -Value (ConvertTo-PlaytestJson ([ordered]@{
        name = $result.Name; outcome = $result.Outcome; text = $result.Text
        degraded = $result.Degraded; retries = $result.Attempts; worstAttempts = $result.MaxAttempts
        detector = $result.Detector; detectors = $result.Detectors
        message = $result.Message; detail = $detail
        durationMs = $result.DurationMs; lockOwner = $result.Owner
        teardownNotes = $result.Notes
        startedUtc = (Get-PlaytestStamp $started); endedUtc = (Get-PlaytestStamp $ended)
    }))
    $script:PlaytestContext = $null
    return $result
}

function Invoke-PlaytestSuite {
    <#
    .SYNOPSIS
        Run every registered check, each under its own lock, and write the bundle.

    .DESCRIPTION
        The lock is released and re-taken PER CHECK, which is the decision that
        buys each check the state-hygiene reset that hangs off a new lock: two
        checks under one lock get no reset between them, so the second would run
        on the first one's leftovers. The cost is the reset time, and the risk is
        that another agent takes the rig between checks. That risk is reported as
        inconclusive with detector 'rig-unavailable', never as a failure.

        Exit codes, which the runner returns:
            0  every check passed (degraded allowed)
            1  at least one check failed
            2  no failures, but at least one inconclusive
        Distinct codes so "the mod is broken" and "the rig was flaky" are not the
        same signal.
    #>
    param(
        [string] $Name = 'playtest',
        [string] $Only = '*',
        [string] $EvidenceRoot,
        [int] $LockWaitSeconds = 0
    )
    $script:PlaytestSuiteName = $Name
    $root = if ($EvidenceRoot) { $EvidenceRoot } else { $script:PlaytestEvidenceRoot }
    if (-not $root) { throw "No evidence root. Pass -EvidenceRoot, or set one with Initialize-PlaytestLib." }
    $bundle = New-PlaytestEvidenceBundle -Root $root -SuiteName $Name

    $selected = @($script:PlaytestChecks | Where-Object { $_.Name -like $Only })
    Write-Host ''
    Write-Host "playtest suite '$Name': $(@($selected).Count) check(s), evidence in $root"

    $before = Get-PlaytestSaveInventory
    Write-PlaytestSaveInventory -BundleRoot $root -When 'before' -Inventory $before | Out-Null

    $results = @()
    $i = 0
    foreach ($check in $selected) {
        $i++
        Write-Host ''
        Write-Host "== [$i/$(@($selected).Count)] $($check.Name) $('=' * [Math]::Max(3, 50 - $check.Name.Length))"
        $r = Invoke-PlaytestCheck -Check $check -BundleRoot $root -Index $i -LockWaitSeconds $LockWaitSeconds
        $results += $r
        Write-Host "[Playtest] $($check.Name): $($r.Text)"
        if ($r.Message) { Write-Host "[Playtest]   $($r.Message)" }
    }

    $after = Get-PlaytestSaveInventory
    Write-PlaytestSaveInventory -BundleRoot $root -When 'after' -Inventory $after | Out-Null
    $cmp = Compare-PlaytestSaveInventory -Before $before -After $after
    Set-Content -LiteralPath (Join-Path $root 'save-inventory.verdict.txt') -Encoding utf8 -Value (@(
        "# The developer save folder (tier 1) is off limits to the rig. This is a listing hash on either side of the run."
        "before   : $($cmp.Before)"
        "after    : $($cmp.After)"
        "verdict  : $(if ($cmp.Identical) { 'IDENTICAL' } else { 'CHANGED' })"
        ''
        '# added'
        (@($cmp.Added) -join "`n")
        ''
        '# removed'
        (@($cmp.Removed) -join "`n")
    ) -join "`n")
    if (-not $cmp.Identical) {
        Write-Warning "[Playtest] The developer's save folder listing CHANGED across this run. Nothing in the rig may write there. See $(Join-Path $root 'save-inventory.verdict.txt')."
    }

    $failed = @($results | Where-Object { $_.Outcome -eq 'fail' }).Count
    $inc    = @($results | Where-Object { $_.Outcome -eq 'inconclusive' }).Count
    $passed = @($results | Where-Object { $_.Outcome -eq 'pass' }).Count
    $exit   = if ($failed -gt 0) { 1 } elseif ($inc -gt 0) { 2 } else { 0 }

    $run = [ordered]@{
        suite = $Name
        startedUtc = $bundle.StartedUtc
        endedUtc = (Get-PlaytestStamp)
        passed = $passed; failed = $failed; inconclusive = $inc
        exitCode = $exit
        tier1SaveFolder = [ordered]@{
            root = $before.root; identical = $cmp.Identical
            before = $cmp.Before; after = $cmp.After
        }
        checks = @($results | ForEach-Object {
            [ordered]@{
                name = $_.Name; outcome = $_.Outcome; text = $_.Text
                degraded = $_.Degraded; retries = $_.Attempts; worstAttempts = $_.MaxAttempts
                detector = $_.Detector; detectors = $_.Detectors
                message = $_.Message; durationMs = $_.DurationMs
                evidence = $_.EvidenceDir; lockOwner = $_.Owner; teardownNotes = $_.Notes
            }
        })
    }
    Set-Content -LiteralPath (Join-Path $root 'run.json') -Encoding utf8 -Value (ConvertTo-PlaytestJson $run)
    Set-Content -LiteralPath (Join-Path $root 'run.md') -Encoding utf8 -Value (@(
        "# Playtest run: $Name"
        ''
        "Started $($bundle.StartedUtc), ended $(Get-PlaytestStamp)."
        ''
        "| Check | Outcome | Retries | Detectors | Evidence |"
        "|---|---|---|---|---|"
        (@($results | ForEach-Object {
            "| $($_.Name) | $($_.Text) | $($_.Attempts) | $((@($_.Detectors)) -join ', ') | $(Split-Path -Leaf $_.EvidenceDir) |"
        }) -join "`n")
        ''
        "Passed $passed, failed $failed, inconclusive $inc. Exit code $exit."
        ''
        "Developer save folder (tier 1): $(if ($cmp.Identical) { 'identical before and after' } else { 'CHANGED, see save-inventory.verdict.txt' })."
    ) -join "`n")

    Write-Host ''
    Write-Host ('-' * 64)
    Write-Host "passed $passed, failed $failed, inconclusive $inc"
    foreach ($r in $results) { Write-Host ("  {0,-14} {1}" -f $r.Text, $r.Name) }
    Write-Host "evidence: $root"
    return [pscustomobject]@{
        PSTypeName = 'Playtest.RunResult'
        Suite = $Name; Passed = $passed; Failed = $failed; Inconclusive = $inc
        ExitCode = $exit; Results = $results; EvidenceRoot = $root
        Tier1Identical = $cmp.Identical
    }
}
