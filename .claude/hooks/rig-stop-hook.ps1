# rig-stop-hook.ps1 -- saved with UTF-8 BOM
#
# Fires on the Stop event (end of agent turn). Modelled on web-stop-hook.ps1,
# which is the repo's other Stop hook and the right shape: read git status,
# classify the dirty paths, emit a state-aware reminder, block nothing.
#
# What it watches: the shared rig safety code. testrig.ps1 and the playtest
# harness both dot-source rig-lock.ps1, rig-reset.ps1 and lib/, so a regression
# in any of them reaches every rig action at once. Four offline suites cover it,
# and until now nothing anywhere reminded anyone to run them: the instruction
# lived in prose only.
#
# DEBOUNCE, AND WHY. web-stop-hook fires every turn while its condition holds,
# which is right for a publish flow that is finished within the turn. It is wrong
# here: rig safety code stays dirty across many turns of a single piece of work,
# so an unconditional version would inject the same reminder every turn for days
# and be tuned out by the third one. So this hook fires once per distinct STATE of
# the watched files. It records a signature (path plus last-write time, or a
# deletion marker) and stays silent until that signature changes. Edit again, get
# reminded again. Run the suites and change nothing, stay silent.
#
# The signature lives outside the repository, so it never shows up as an untracked
# file and never collides with the rig's own state files or .work/ conventions.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    $changed = & git status --porcelain 2>$null
    if ($LASTEXITCODE -ne 0) { exit 0 }
} catch { exit 0 }

if (-not $changed) { exit 0 }

# Porcelain lines are "XY path", and a rename is "R  old -> new". Take the
# destination in that case; quoted paths (spaces, non-ASCII) keep their quotes,
# which only affects the display string, not the match.
$paths = @()
foreach ($line in $changed) {
    if ($line -match '^..\s+(.+)$') {
        $p = $matches[1]
        if ($p -match '^(.*?)\s+->\s+(.+)$') { $p = $matches[2] }
        $paths += $p.Trim('"')
    }
}

# The watched set. The suites themselves are excluded on purpose: a turn that
# only edits a suite does not need to be told the suite exists.
$safetyPatterns = @(
    '^TestRig/rig-lock\.ps1$',
    '^TestRig/rig-reset\.ps1$',
    '^TestRig/testrig\.ps1$',
    '^TestRig/lib/',
    '^TestRig/playtest/playtest-lib\.ps1$'
)

$dirty = @()
foreach ($p in $paths) {
    $u = ($p -replace '\\', '/')
    foreach ($pat in $safetyPatterns) {
        if ($u -match $pat) { $dirty += $u; break }
    }
}

if ($dirty.Count -eq 0) { exit 0 }
$dirty = @($dirty | Sort-Object -Unique)

# --- signature and debounce --------------------------------------------------
$parts = @()
foreach ($p in $dirty) {
    $ticks = 'D'   # deleted or otherwise unreadable
    try {
        $item = Get-Item -LiteralPath $p -ErrorAction Stop
        $ticks = [string]$item.LastWriteTimeUtc.Ticks
    } catch { }
    $parts += "$p=$ticks"
}
$signature = ($parts -join ';')

$stampDir  = Join-Path $env:LOCALAPPDATA 'claude-code-hooks'
$stampFile = Join-Path $stampDir 'stationeersplus-rig-safety.stamp'
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

$list = ($dirty | Select-Object -First 6) -join ', '
if ($dirty.Count -gt 6) { $list += " (+$($dirty.Count - 6) more)" }

$message = @"
[TestRig -- end of turn] Shared rig safety code is uncommitted: $list

testrig.ps1 and the playtest harness both dot-source rig-lock.ps1, rig-reset.ps1 and lib/, so a regression in any of them reaches every rig action at once. Four offline suites cover this code and none of them needs a game running:

    pwsh -NoProfile -File TestRig/rig-lock.tests.ps1
    pwsh -NoProfile -File TestRig/rig-reset.tests.ps1
    pwsh -NoProfile -File TestRig/testrig.tests.ps1
    pwsh -NoProfile -File TestRig/playtest/playtest-lib.tests.ps1

1,413 assertions (284 / 377 / 353 / 399), about two and a half minutes for all four. They must all pass before this work is called done, and a behaviour change belongs in its suite in the same turn as the code. If you have already run them since the last edit, ignore this. This fires once per change, not once per turn, so it will stay quiet until you touch these files again.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
