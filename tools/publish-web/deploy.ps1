# deploy.ps1
# Mirrors Web/site/ to the SMB share at \\10.20.30.250\nvme-system\containers\stationeers\.
#
# The SMB share is treated as a strict downstream copy; any file present there
# that is not in Web/site/ will be deleted by the /MIR pass. Never hand-edit
# the share; this script will overwrite or remove anything that drifts.
#
# Usage:
#   .\tools\publish-web\deploy.ps1            # mirror
#   .\tools\publish-web\deploy.ps1 -DryRun    # show what would change, no writes

[CmdletBinding()]
param(
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Resolve-Path (Join-Path $scriptDir '..\..')
$siteDir   = Join-Path $repoRoot 'Web\site'
$smbTarget = '\\10.20.30.250\nvme-system\containers\stationeers'

if (-not (Test-Path $siteDir)) {
    throw "Build output missing: $siteDir. Run .\tools\publish-web\build.ps1 first."
}

if (-not (Test-Path $smbTarget)) {
    throw "SMB target not reachable: $smbTarget. Check network and share availability."
}

# Sanity check: refuse to mirror an empty site (would wipe the share).
$siteFileCount = (Get-ChildItem -Path $siteDir -Recurse -File | Measure-Object).Count
if ($siteFileCount -lt 5) {
    throw "Web/site/ has only $siteFileCount files. Refusing to mirror; build is probably broken."
}

Write-Host "[publish-web] Source:  $siteDir ($siteFileCount files)"
Write-Host "[publish-web] Target:  $smbTarget"

$rcCommon = @('/MIR', '/R:2', '/W:3', '/NFL', '/NDL', '/NJH', '/NP')
$rcArgs   = @($siteDir, $smbTarget) + $rcCommon

if ($DryRun) {
    Write-Host "[publish-web] DRY RUN -- /L flag added; no files written"
    $rcArgs += '/L'
}

& robocopy @rcArgs
$rc = $LASTEXITCODE

# Robocopy exit codes: 0 = no change, 1 = copied OK, 2 = extras seen, 3 = copied + extras,
# 4 = mismatched files, 5/6/7 = combinations. >=8 = actual failure.
if ($rc -ge 8) {
    throw "robocopy mirror failed (exit $rc)"
}

Write-Host "[publish-web] Robocopy exit code: $rc"
switch ($rc) {
    0 { Write-Host "[publish-web] Done. No changes -- SMB already in sync." }
    1 { Write-Host "[publish-web] Done. Files copied." }
    2 { Write-Host "[publish-web] Done. Extra files removed from SMB." }
    3 { Write-Host "[publish-web] Done. Files copied and extras removed." }
    default { Write-Host "[publish-web] Done with exit $rc (see robocopy docs)." }
}

if ($DryRun) {
    Write-Host "[publish-web] (Dry run -- nothing was actually written.)"
}

# Robocopy exit codes 0..7 are success variants; PowerShell would otherwise
# propagate them as a non-zero process exit. Normalise to 0 since the script's
# own logic above already filtered out actual failures (>=8).
exit 0
