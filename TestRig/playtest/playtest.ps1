<#
.SYNOPSIS
    Run a mod's playtest checks against the client rig.

.DESCRIPTION
    The composition root for the playtest harness. It is deliberately thin: it
    wires the two seams the library refuses to reach for by itself, loads the
    check files, and runs them.

      - the control-plane transport, from client-rig.ps1's Invoke-Control, which
        returns an OBJECT. The launcher's own -Call only prints JSON, so a
        harness built on it would be parsing its own stdout;
      - the launcher seam, one child pwsh per client-rig.ps1 action, so a lock, a
        start or a stop behaves exactly as it does when a human types it;
      - the rig registry, so an instance NAME resolves to a control-plane port;
      - the tier-1 save folder, for the read-only listing hash taken on either
        side of a run.

    Everything else lives in playtest-lib.ps1, which knows nothing about paths or
    processes and is therefore testable with no game running.

    THE OUTCOMES. Three, never two.
        pass          the check made its observation and the value was right
        fail          an Assert-Rig* verb read a value and it was wrong
        inconclusive  the rig, not the mod: a flake, a lost lock, a stale binary,
                      an unclassified throw
    Exit code 0 for all pass, 1 if anything failed, 2 if nothing failed but
    something was inconclusive.

.PARAMETER Suite
    A check file, or a directory of *.playtest.ps1 files. Default: the checks/
    folder beside this script.

.PARAMETER Only
    Wildcard over check names. Default: all of them.

.PARAMETER EvidenceRoot
    Where the run's evidence bundle goes. Default:
    <repo>/.work/<date>-playtest-<suite>/, which is the repository's gitignored
    scratch directory.

.PARAMETER LockWaitSeconds
    Queue this long for the rig when another session holds it, per check. Default
    0, which is the launcher's immediate refusal. It is a queue and not a
    reservation; no ordering fairness is promised.

.PARAMETER ListChecks
    Print the registered checks and exit. Drives nothing.

.PARAMETER ListFlakes
    Print the flake taxonomy and exit. Drives nothing.

.EXAMPLE
    pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -ListFlakes

.EXAMPLE
    pwsh -NoProfile -File TestRig/playtest/playtest.ps1 -Suite TestRig/playtest/checks/ExampleMod
#>
[CmdletBinding()]
param(
    [string] $Suite,
    [string] $Name,
    [string] $Only = '*',
    [string] $EvidenceRoot,
    [int]    $LockWaitSeconds = 0,
    [switch] $ListChecks,
    [switch] $ListFlakes
)

$ErrorActionPreference = 'Stop'

$PlaytestRoot   = $PSScriptRoot
$TestRigRoot    = Split-Path -Parent $PlaytestRoot
$RepoRoot       = Split-Path -Parent $TestRigRoot
$ClientRigScript = Join-Path $TestRigRoot 'ClientRig\client-rig.ps1'

. (Join-Path $PlaytestRoot 'playtest-lib.ps1')

if ($ListFlakes) {
    Write-Host 'Flake taxonomy, in resolution order (first match wins):'
    Write-Host ''
    foreach ($f in (Get-PlaytestFlakeTaxonomy)) {
        Write-Host ("  {0,-24} {1} (max {2} attempt(s), {3}s gap)" -f $f.Name, $f.Remedy, $f.MaxAttempts, $f.GapSeconds)
        Write-Host ("    {0}" -f $f.Summary)
        if ($f.Reference) { Write-Host ("    see: {0}" -f $f.Reference) }
        Write-Host ''
    }
    Write-Host 'Every one of these ends a check as INCONCLUSIVE, never as a failure.'
    return
}

# The launcher, dot-sourced rather than shelled out to, for exactly one function:
# Invoke-Control returns the parsed response. Doing this here and not in the
# library is what keeps the library offline-testable.
if (-not (Test-Path -LiteralPath $ClientRigScript)) {
    throw "client-rig.ps1 not found at $ClientRigScript. The playtest harness drives the client rig and cannot run without it."
}
. $ClientRigScript | Out-Null

$PwshExe = if ($PSHOME -and (Test-Path -LiteralPath (Join-Path $PSHOME 'pwsh.exe'))) { Join-Path $PSHOME 'pwsh.exe' }
           else { 'pwsh' }

$Tier1SaveRoot = try { Join-Path (Get-UserDataPath) 'saves' }
                 catch { Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Stationeers\saves' }

$suiteName = if ($Name) { $Name }
             elseif ($Suite) { Split-Path -Leaf ($Suite.TrimEnd('\', '/')) }
             else { 'playtest' }
$suiteSlug = ConvertTo-PlaytestSlug $suiteName

$evidence = if ($EvidenceRoot) { $EvidenceRoot }
            else { Join-Path (Join-Path $RepoRoot '.work') ("{0}-playtest-{1}" -f (Get-Date -Format 'yyyy-MM-dd'), $suiteSlug) }

Initialize-PlaytestLib `
    -RigHome $TestRigRoot `
    -EvidenceRoot $evidence `
    -Tier1SaveRoot $Tier1SaveRoot `
    -Registry { @(Read-Registry) } `
    -Transport {
        param([int] $Port, [string] $Path, [string] $BodyJson, [int] $TimeoutSec)
        # A non-2xx answer must arrive as a throw whose message carries the
        # response BODY: on this control plane the body is the only thing that
        # explains a refusal, and a bare "409" would leave every flake detector
        # guessing.
        try {
            return (Invoke-Control -Port $Port -Path $Path -BodyJson $BodyJson -TimeoutSec $TimeoutSec)
        }
        catch {
            $detail = ''
            try { $detail = "$($_.ErrorDetails.Message)" } catch { $detail = '' }
            if (-not $detail) { $detail = "$($_.Exception.Message)" }
            throw "$($_.Exception.Message) :: $detail"
        }
    } `
    -RigCommand {
        param([string[]] $ArgList)
        $outFile = [System.IO.Path]::GetTempFileName()
        $errFile = [System.IO.Path]::GetTempFileName()
        try {
            $full = @('-NoProfile', '-NonInteractive', '-File', $ClientRigScript) + $ArgList
            # -NoNewWindow inherits this console instead of allocating one, so
            # nothing flashes and nothing takes the developer's foreground. The
            # rig's never-touch-the-foreground rule applies to the harness too.
            $p = Start-Process -FilePath $PwshExe -ArgumentList $full -NoNewWindow -PassThru -Wait `
                    -RedirectStandardOutput $outFile -RedirectStandardError $errFile
            return [pscustomobject]@{
                ExitCode = [int]$p.ExitCode
                StdOut   = (Get-Content -Raw -LiteralPath $outFile -ErrorAction SilentlyContinue)
                StdErr   = (Get-Content -Raw -LiteralPath $errFile -ErrorAction SilentlyContinue)
            }
        }
        finally {
            Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $outFile
            Remove-Item -Force -ErrorAction SilentlyContinue -LiteralPath $errFile
        }
    }

# ---- load the checks -------------------------------------------------------

$suitePath = if ($Suite) { $Suite } else { Join-Path $PlaytestRoot 'checks' }
$files = @()
if (Test-Path -LiteralPath $suitePath -PathType Leaf) {
    $files = @($suitePath)
}
elseif (Test-Path -LiteralPath $suitePath -PathType Container) {
    $files = @(Get-ChildItem -LiteralPath $suitePath -Recurse -File -Filter '*.playtest.ps1' | Sort-Object FullName | ForEach-Object { $_.FullName })
}
if (@($files).Count -eq 0) {
    throw "No check files at '$suitePath'. A check file is named <something>.playtest.ps1 and calls Register-PlaytestCheck. See TestRig/playtest/README.md."
}

Clear-PlaytestChecks
foreach ($f in $files) {
    Write-Host "[Playtest] loading $f"
    . $f
}

$checks = @(Get-PlaytestChecks | Where-Object { $_.Name -like $Only })
if ($ListChecks) {
    Write-Host ''
    Write-Host "Registered checks in '$suitePath':"
    foreach ($c in @(Get-PlaytestChecks)) {
        $selected = if ($c.Name -like $Only) { ' ' } else { '-' }
        Write-Host ("  {0} {1,-40} {2}" -f $selected, $c.Name, $c.Summary)
        foreach ($i in $c.Instances) {
            Write-Host ("      {0} ({1}){2}" -f $i.Name, $i.Role, $(if ($i.World) { " world $($i.World)" } elseif ($i.Save) { " save $($i.Save)" } else { '' }))
        }
    }
    return
}
if (@($checks).Count -eq 0) {
    throw "No check matched -Only '$Only'. Run with -ListChecks to see what is registered."
}

$run = Invoke-PlaytestSuite -Name $suiteName -Only $Only -EvidenceRoot $evidence -LockWaitSeconds $LockWaitSeconds
exit $run.ExitCode
