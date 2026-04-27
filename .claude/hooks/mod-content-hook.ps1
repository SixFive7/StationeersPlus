# mod-content-hook.ps1
# Fires after Read / Edit / Write on mod-facing README.md or About.xml files
# (matched via the `if` field in .claude/settings.json). Injects a reminder
# that the layout rules for these files live in Mods/Template/LAYOUT.md so
# repo-root CLAUDE.md can stay short.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Mod content reminder] You just touched a mod README.md or About.xml. Layout rules for these files live in Mods/Template/LAYOUT.md and are the single source of truth. Read that file before editing unless you have already read it this conversation. Covers: README / Description / InGameDescription sync, tagline rule, ChangeLog plain-text format and 8000-char cap, About.xml element order and XML-escape safety, per-element size caps, Reporting Issues section placement, InGameDescription <line-height=40%> wrap, and preview image 16:9 dimensions.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
