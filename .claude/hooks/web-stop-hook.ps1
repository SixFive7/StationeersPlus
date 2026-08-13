# web-stop-hook.ps1
# Fires on the Stop event (end of agent turn). Checks whether the publish flow
# has been completed. Three working-tree states warrant a reminder:
#   - Publishable source dirty, Web/site/ clean -> publish flow not started
#   - Source clean, Web/site/ dirty            -> Publish: commit + deploy missing
#   - Both dirty                                -> publish flow in progress but not finished
# All-clean: nothing to do.
#
# This is the enforcement point. The earlier per-edit hooks are signals; this
# one fires at the natural commit point.
#
# It fires once per distinct STATE, not once per turn. It records a signature
# (each dirty publishable path plus its last-write time, and the Web/site/ file
# count) and stays silent until that signature changes. Change a watched file and
# the reminder returns; leave it alone and it does not.
#
# The debounce is not a nicety. Publishable source outside the two autonomous
# commit lanes (anything under tools/, Web/content/, Web/overrides/) cannot be
# cleared by the agent on its own: it needs the developer to approve a commit.
# Without a debounce that produces a reminder the agent cannot act on and cannot
# silence, repeated verbatim at every single turn end for as long as the file
# sits there. That happened, for dozens of turns, over one two-line edit.
#
# The signature lives outside the repository, so it never shows up as an
# untracked file and never becomes something this hook reports on.

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

# --- signature and debounce --------------------------------------------------
# The signature is every dirty source path with its last-write time, plus the
# Web/site/ file count. Editing a watched file moves its ticks and the reminder
# returns. Committing it drops the path entirely, which exits above. Rebuilding
# Web/site/ moves the count, which is a genuinely different state (the publish is
# now half finished) and worth saying once more.
$parts = @()
foreach ($p in ($sourceChanged | Sort-Object -Unique)) {
    $ticks = 'D'   # deleted or otherwise unreadable
    try {
        $item = Get-Item -LiteralPath $p -ErrorAction Stop
        $ticks = [string]$item.LastWriteTimeUtc.Ticks
    } catch { }
    $parts += "$p=$ticks"
}
$parts += "site=$($siteChanged.Count)"
$signature = ($parts -join ';')

$stampDir  = Join-Path $env:LOCALAPPDATA 'claude-code-hooks'
$stampFile = Join-Path $stampDir 'stationeersplus-web-publish.stamp'
try {
    if (Test-Path -LiteralPath $stampFile) {
        $previous = (Get-Content -LiteralPath $stampFile -Raw -ErrorAction Stop).Trim()
        if ($previous -eq $signature) { exit 0 }
    }
    if (-not (Test-Path -LiteralPath $stampDir)) {
        New-Item -ItemType Directory -Path $stampDir -Force | Out-Null
    }
    Set-Content -LiteralPath $stampFile -Value $signature -Encoding UTF8 -NoNewline
} catch {
    # A stamp we cannot read or write only costs a repeated reminder. Carry on.
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
