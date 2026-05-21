# dedi-lock-hook.ps1
# Fires PreToolUse when a command invokes dedicated-server.ps1, or when any
# command or file edit touches the DedicatedServer/ tree (notably data/saves/).
# Injects the session-lock reminder and points at the single source of truth,
# DedicatedServer/session.lock.template.
#
# Does NOT block. Reminder only (additionalContext, no permissionDecision), so
# the normal permission flow for the underlying command is unchanged.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Dedicated-server session-lock reminder] The dedicated server is a shared single-instance resource. Before any mutating command (-Bootstrap, -DeployMods, -SyncMods, -Start, -Save, -SendCommand, -Stop) you must hold the session lock: acquire once with `dedicated-server.ps1 -Lock -Purpose "<reason>"`, then pass `-As <id>` on every mutating command. Direct edits under DedicatedServer/data/saves/ also belong to whoever holds the lock; do not clobber another session's save. The lock expires on a timer (refresh about once a minute while actively testing; never poll-refresh just to hold it for an absent human, and never spawn a background refresher). A connected player keeps it live, and you must re-check ownership with `-Status -As <id>` after any idle gap. Breaking another session's live lock with -Force is human-gated: only on the user's explicit say-so. Full rules: DedicatedServer/session.lock.template.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
