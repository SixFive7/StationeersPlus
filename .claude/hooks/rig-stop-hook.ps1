# rig-stop-hook.ps1 -- saved with UTF-8 BOM
#
# Fires on the Stop event (end of agent turn). Modelled on web-stop-hook.ps1,
# which is the repo's other Stop hook and the right shape: read git status,
# classify the dirty paths, emit a state-aware reminder, block nothing.
#
# What it watches: TestRig/src/, the source of testrig.exe. Every rig action on
# either half, and the playtest harness with it, goes through that one binary, so a
# regression there reaches all of it at once. The suite is offline and covers it.
#
# It used to watch the PowerShell libraries (rig-lock.ps1, rig-reset.ps1, lib/,
# playtest-lib.ps1) and name their four suites. Those are retained-not-live now:
# TestRig/CLAUDE.md says to read them and never run them. Watching them would
# remind an agent to run suites against code that no longer drives anything.
#
# It also absorbed rig-hook.ps1's per-edit version of this reminder, which said the
# same thing 97 words at a time on every touch of a shared safety file. Once per
# change beats once per edit, and the debounce below is what buys that.
#
# DEBOUNCE, AND WHY. web-stop-hook fires every turn while its condition holds,
# which is right for a publish flow that is finished within the turn. It is wrong
# here: rig source stays dirty across many turns of a single piece of work, so an
# unconditional version would inject the same reminder every turn for days and be
# tuned out by the third one. So this hook fires once per distinct STATE of the
# watched files. It records a signature (path plus last-write time, or a deletion
# marker) and stays silent until that signature changes. Edit again, get reminded
# again. Run the suite and change nothing, stay silent.
#
# The signature lives outside the repository, and that is load bearing rather than
# tidy: a stamp inside the tree would show up in git status, match its own watch
# patterns, and change the very state it records, so the debounce could never
# engage. testrig.exe is deliberately NOT watched for the same reason in reverse:
# a rebuild would move its timestamp and re-fire a reminder the rebuild answered.

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

# The watched set. The test project is included rather than carved out: dotnet test
# runs the whole solution in one command, so "you are already in the suite" is not a
# reason to stay quiet about running it.
$safetyPatterns = @(
    '^TestRig/src/'
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
[TestRig -- end of turn] Rig source is uncommitted: $list

testrig.exe is built from TestRig/src/, and every rig action on both halves plus the playtest harness runs through it, so a regression here reaches all of them at once. The suite is offline: no game, no network, and it never touches the real session.lock.

    dotnet test TestRig/src/TestRig.slnx

A behaviour change belongs in its tests in the same turn as the code. The binary also embeds a SHA-256 digest of this tree and exits 7 when the two disagree, so the work is not finished until it is rebuilt and testrig.exe is committed in the same commit as the source:

    dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64

If you have already done both since the last edit, ignore this. It fires once per change, not once per turn, so it will stay quiet until you touch these files again.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'Stop'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
