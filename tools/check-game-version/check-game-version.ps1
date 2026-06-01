# check-game-version.ps1
# Advisory check: is the installed Stationeers newer than what this repo last
# decompiled, and what is the latest public build on Steam?
#
# Reports three things and changes no state:
#   1. The installed client's Assembly-CSharp.dll version (the four-part string
#      the rest of the repo keys decompiles and Research stamps on).
#   2. Whether the newest .work/decomp/<version>/ folder matches that installed
#      version, so a stale decompile cache is obvious before you trust it.
#   3. The latest public build id and update time for the game (Steam appid
#      544550) and the dedicated server (appid 600760), from the public
#      api.steamcmd.net endpoint.
#
# api.steamcmd.net reports a Steam *build id*, not the four-part assembly
# version, so item 3 is a "did something ship since I last looked" signal, not a
# direct equality check against item 1. Treat it as advisory.
#
# Usage:
#   .\tools\check-game-version\check-game-version.ps1
#   .\tools\check-game-version\check-game-version.ps1 -SkipSteam   # offline / local-only
#
# Read-only. No secrets. Hits one public third-party endpoint unless -SkipSteam.
# Exits 0 unless the install path or the game DLL cannot be resolved.

[CmdletBinding()]
param(
    [switch]$SkipSteam
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# tools/check-game-version/check-game-version.ps1 -> repo root is two levels up.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = (Resolve-Path (Join-Path $scriptDir '..\..')).Path

function Get-StationeersPath {
    if ($env:StationeersPath -and $env:StationeersPath.Trim().Length -gt 0) {
        return $env:StationeersPath.Trim()
    }
    $propsPath = Join-Path $repoRoot 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Cannot resolve the Stationeers install: `$env:StationeersPath is unset and '$propsPath' does not exist. Copy Directory.Build.props.template to Directory.Build.props and set <StationeersPath>. See DEV.md."
    }
    [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    $node = $props.SelectSingleNode('//StationeersPath')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Cannot resolve the Stationeers install: '$propsPath' has no <StationeersPath> element. See DEV.md."
    }
    return $node.InnerText.Trim()
}

function Get-InstalledGameVersion {
    param([string]$StationeersPath)
    $dll = Join-Path $StationeersPath 'rocketstation_Data\Managed\Assembly-CSharp.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "Assembly-CSharp.dll not found at '$dll'. Check <StationeersPath>. See DEV.md."
    }
    return [Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
}

function Get-LatestDecompVersion {
    $decompRoot = Join-Path $repoRoot '.work\decomp'
    if (-not (Test-Path -LiteralPath $decompRoot)) { return $null }
    $dirs = Get-ChildItem -LiteralPath $decompRoot -Directory -ErrorAction SilentlyContinue
    if (-not $dirs) { return $null }
    # Folder names are four-part version strings; sort by parsed version where possible.
    $parsed = foreach ($d in $dirs) {
        $v = $null
        if ([version]::TryParse($d.Name, [ref]$v)) {
            [pscustomobject]@{ Name = $d.Name; Version = $v }
        } else {
            [pscustomobject]@{ Name = $d.Name; Version = $null }
        }
    }
    $withVersion = @($parsed | Where-Object { $null -ne $_.Version } | Sort-Object Version)
    if ($withVersion.Count -gt 0) { return $withVersion[-1].Name }
    return @($parsed)[-1].Name
}

function Get-SteamBuild {
    param([string]$AppId)
    $url  = "https://api.steamcmd.net/v1/info/$AppId"
    $resp = Invoke-RestMethod -Uri $url -TimeoutSec 20 -ErrorAction Stop
    $public  = $resp.data.$AppId.depots.branches.public
    $buildId = $public.buildid
    $when    = $null
    $timeRaw = $public.timeupdated
    if ($timeRaw) {
        try { $when = [DateTimeOffset]::FromUnixTimeSeconds([long]$timeRaw).UtcDateTime } catch { $when = $null }
    }
    return [pscustomobject]@{ BuildId = $buildId; Updated = $when }
}

Write-Host ""
Write-Host "Stationeers version check" -ForegroundColor Cyan
Write-Host "========================="

$stationeersPath = Get-StationeersPath
$installed = Get-InstalledGameVersion -StationeersPath $stationeersPath
Write-Host ("Installed client (Assembly-CSharp) : {0}" -f $installed)

$decomp = Get-LatestDecompVersion
if ($null -eq $decomp) {
    Write-Host "Local decompile cache (.work/decomp) : none"
} elseif ($decomp -eq $installed) {
    Write-Host ("Local decompile cache (.work/decomp) : {0}  (matches)" -f $decomp) -ForegroundColor Green
} else {
    Write-Host ("Local decompile cache (.work/decomp) : {0}  (STALE; installed is {1})" -f $decomp, $installed) -ForegroundColor Yellow
    Write-Host "  -> Re-decompile against the installed DLL before trusting cached .decompiled.cs. See CLAUDE.md 'Decompilation artifacts'." -ForegroundColor Yellow
}

if ($SkipSteam) {
    Write-Host ""
    Write-Host "Skipping Steam build lookup (-SkipSteam)."
} else {
    Write-Host ""
    Write-Host "Latest public builds on Steam (advisory; build id is not the assembly version):"
    $apps = @(
        [pscustomobject]@{ Id = '544550'; Label = 'Game' },
        [pscustomobject]@{ Id = '600760'; Label = 'Dedicated server' }
    )
    foreach ($app in $apps) {
        try {
            $b = Get-SteamBuild -AppId $app.Id
            $updatedText = if ($b.Updated) { $b.Updated.ToString('yyyy-MM-dd HH:mm') + ' UTC' } else { 'unknown' }
            Write-Host ("  {0,-16} (appid {1}) : build {2}, updated {3}" -f $app.Label, $app.Id, $b.BuildId, $updatedText)
        } catch {
            Write-Host ("  {0,-16} (appid {1}) : lookup failed ({2})" -f $app.Label, $app.Id, $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    Write-Host ""
    Write-Host "If a Steam build looks newer than your install, update the game, then rebuild mods and re-decompile as needed."
}

Write-Host ""
exit 0
