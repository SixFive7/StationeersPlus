# todo-content-hook.ps1
# Fires after Read / Edit / Write on any TODO.md file (matched via the `if`
# field in .claude/settings.json). Injects the open-issues-only reminder so a
# TODO.md never accumulates completed items.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[TODO hygiene reminder] You just touched a TODO.md. These files track OPEN ISSUES ONLY. When a task is done, REMOVE it; do not mark it done, do not leave a checked-off `- [x]` entry, and do not add a completed-work section. Completed work lives in git history. If you find existing `- [x]` items while editing, delete them.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
