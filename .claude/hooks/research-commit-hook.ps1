# research-commit-hook.ps1 -- saved with UTF-8 BOM
#
# PreToolUse hook on Bash. Fires for every `git commit` invocation. If the
# commit message contains the autonomous-research prefix `Research:`, every
# staged path must be under the central `Research/` directory; otherwise the
# commit is denied. Enforces the rule in CLAUDE.md
# ("Workflow: research commits are autonomous, code commits are not").

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

function Emit-Allow {
    @{
        hookSpecificOutput = @{
            hookEventName      = 'PreToolUse'
            permissionDecision = 'allow'
        }
    } | ConvertTo-Json -Depth 5 -Compress | Write-Output
    exit 0
}

function Emit-Deny([string]$reason) {
    @{
        hookSpecificOutput = @{
            hookEventName            = 'PreToolUse'
            permissionDecision       = 'deny'
            permissionDecisionReason = $reason
        }
    } | ConvertTo-Json -Depth 5 -Compress | Write-Output
    exit 0
}

# Read tool input from stdin.
$stdin = [Console]::In.ReadToEnd()
if (-not $stdin) { Emit-Allow }

try {
    $payload = $stdin | ConvertFrom-Json
} catch {
    Emit-Allow
}

$cmd = $payload.tool_input.command
if (-not $cmd) { Emit-Allow }

# Only act on `git commit`. Allow `git commit-tree` and similar to pass.
if ($cmd -notmatch '\bgit\s+(?:[^|;&\r\n]*\s)?commit(\s|$)') { Emit-Allow }

# Only act when the commit message uses the autonomous-research prefix.
# Filenames cannot contain ':' on Windows, so the literal `Research:` token
# is specific enough to identify the prefix in practice.
if ($cmd -notmatch '\bResearch:') { Emit-Allow }

# Read the staged set.
$staged = git diff --cached --name-only 2>$null
if ($LASTEXITCODE -ne 0) { Emit-Allow }
if (-not $staged) { Emit-Allow }

$offenders = @()
foreach ($line in ($staged -split "`r?`n")) {
    $p = $line.Trim()
    if (-not $p) { continue }
    # git outputs forward slashes; accept either separator defensively.
    if ($p -notmatch '^Research[/\\]') {
        $offenders += $p
    }
}

if ($offenders.Count -eq 0) { Emit-Allow }

$listLines = $offenders | ForEach-Object { "  - $_" }
$list = $listLines -join "`n"

$header = @'
Blocked: a `git commit` with a `Research:` prefix message must have every staged path under `Research/`. The following staged paths are outside `Research/`:

'@

$footer = @'


This guard enforces the rule in CLAUDE.md ("Workflow: research commits are autonomous, code commits are not"). To proceed:

  1. Unstage non-research paths: `git restore --staged <path>`
  2. Re-run the commit; only `Research/` paths must remain staged.

If the intent was a mixed code+research commit, drop the `Research:` prefix; this hook only fires on that prefix.
'@

Emit-Deny ($header + $list + $footer)
