# web-stop-hook.ps1
# Fires on the Stop event (end of agent turn). Checks whether the publish flow
# has been completed. Three working-tree states warrant a reminder:
#   - Publishable source dirty, Web/site/ clean -> publish flow not started
#   - Source clean, Web/site/ dirty            -> Publish: commit + deploy missing
#   - Both dirty                                -> publish flow in progress but not finished
# All-clean: nothing to do.
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

if ($sourceChanged.Count -eq 0 -and $siteChanged.Count -eq 0) {
    # No publishable changes this turn; nothing to do.
    exit 0
}

# Build state-aware reminder lines.
$stateLines = @()
if ($sourceChanged.Count -gt 0) {
    $srcSummary = ($sourceChanged | Select-Object -First 5) -join ', '
    if ($sourceChanged.Count -gt 5) { $srcSummary += " (+$($sourceChanged.Count - 5) more)" }
    $stateLines += "  - Publishable source uncommitted: $srcSummary"
}
if ($siteChanged.Count -gt 0) {
    $stateLines += "  - Web/site/ uncommitted: $($siteChanged.Count) file(s)"
}
$stateBlock = $stateLines -join "`n"

$message = @"
[Web publish -- end of turn] The publish flow is incomplete. The public site at https://stationeers.huisman.io will lag git HEAD until you finish.

State:
$stateBlock

Run the publish flow to completion before ending the turn:

    # 1. Commit publishable source (Research: autonomous, or user-approved for other paths)
    # 2. Rebuild
    .\tools\publish-web\build.ps1
    # 3. Stage and commit Web/site/ with the autonomous Publish: prefix
    git add Web/site/
    git commit -m "Publish: <summary>"
    # 4. Deploy
    .\tools\publish-web\deploy.ps1

The Publish: commit is the second autonomous-commit lane (see CLAUDE.md "Workflow: site publish commits are autonomous"). If you genuinely do not want to publish (mid-investigation, throwaway change), say so explicitly.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
