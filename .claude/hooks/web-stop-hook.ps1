# web-stop-hook.ps1
# Fires on the Stop event (end of agent turn). Checks whether any publishable
# source path (Research/, tools/, Web/content/, Web/mkdocs.yml, etc.) has
# uncommitted changes that have not been reflected in Web/site/. If so, injects
# a reminder asking the agent to run build + deploy.
#
# This is the enforcement point. The earlier per-edit hooks are signals; this
# one fires once per turn at the natural commit point.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    $changed = & git status --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) {
        exit 0
    }
} catch {
    exit 0
}

if (-not $changed) {
    # Nothing changed at all; nothing to publish.
    exit 0
}

# git status --porcelain lines: "XY path" where XY is 2-char status.
# Strip the status prefix and check the path.
$paths = $changed | ForEach-Object {
    if ($_ -match '^..\s+(.+)$') { $matches[1] } else { $null }
} | Where-Object { $_ }

$sourcePatterns = @(
    '^Research/',
    '^tools/',
    '^Web/content/',
    '^Web/overrides/',
    '^Web/mkdocs\.yml$',
    '^Web/requirements\.txt$'
)
$sitePattern = '^Web/site/'

$sourceChanged = @()
$siteChanged   = @()
foreach ($p in $paths) {
    foreach ($pat in $sourcePatterns) {
        if ($p -match $pat) { $sourceChanged += $p; break }
    }
    if ($p -match $sitePattern) { $siteChanged += $p }
}

if ($sourceChanged.Count -eq 0) {
    # No publishable source changes this turn; nothing to do.
    exit 0
}

if ($siteChanged.Count -gt 0) {
    # Source AND site changed -- the agent likely already ran build/deploy.
    # Skip the reminder; trust the shape.
    exit 0
}

$srcSummary = ($sourceChanged | Select-Object -First 5) -join ', '
if ($sourceChanged.Count -gt 5) { $srcSummary += " (+$($sourceChanged.Count - 5) more)" }

$message = @"
[Web publish -- end of turn] You modified publishable source this turn but Web/site/ has not been updated. The public site at https://stationeers.huisman.io will not reflect your changes until you run:

    .\tools\publish-web\build.ps1
    .\tools\publish-web\deploy.ps1

Source paths changed: $srcSummary

If you genuinely do not want to publish (for example, you are mid-investigation and the change is throwaway), say so explicitly. Otherwise run the two commands now before ending the turn.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
