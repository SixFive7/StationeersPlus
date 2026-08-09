# rig-lock-hook.ps1 -- saved with UTF-8 BOM
# Fires PreToolUse when a command invokes either TestRig launcher
# (dedicated-server.ps1, client-rig.ps1), or when any command or file
# Read/Edit/Write touches the TestRig/ tree.
#
# Covers BOTH halves. There is one lock for the whole rig, so there is one
# reminder for the whole rig. The client half used to have no hook at all: it
# only tripped this reminder by accident, because it sat under DedicatedServer/
# and matched that path pattern. The TestRig restructure removed the accident,
# which is why the matchers are rig-wide now.
#
# Injects the session-lock rules plus the client-rig hazards that have no
# equivalent on the server (hard links into the developer's real install,
# processes sharing PlayerCookie-v2.xml and the PlayerPrefs key with the
# developer's own client, an endpoint that retargets a live client's save root),
# and points at the single sources of truth (TestRig/session.lock.template,
# TestRig/CLAUDE.md).
#
# Does NOT block. Reminder only (additionalContext, no permissionDecision), so
# the normal permission flow for the underlying command is unchanged.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[TestRig session-lock reminder] The test rig is a shared single-instance resource and ONE lock at TestRig/session.lock covers BOTH halves (TestRig/DedicatedServer/ and TestRig/ClientRig/). They are not independent: they share the developer's one game install, PlayerCookie-v2.xml and the HKCU Rocketwerkz PlayerPrefs key with each other AND with the developer's own client.

Acquire once from either launcher (`-Lock -Purpose "<reason>"`), then pass `-As <id>` on every mutating command on BOTH launchers. Mutating on the dedicated server: -Bootstrap, -DeployMods, -SyncMods, -Start, -Save, -SendCommand, -Stop. Mutating on the client rig: -Provision, -Start, -Stop, -Remove, -Broadcast, -Call. Read-only -Status/-Logs/-List/-Snapshot never need it; -Wait does not need it but refreshes one you already hold.

The lock expires on a timer (refresh about once a minute while actively testing; never poll-refresh just to hold it for an absent human, and never spawn a background refresher). A busy rig stays live regardless of the timer: a player connected to the running server, or ANY client instance process alive. That means leaving instances running holds the whole rig, so always `client-rig.ps1 -Stop -All -As <id>` before `-Unlock`. Re-check ownership with `-Status -As <id>` after any idle gap.

Breaking another session's live lock is `-BreakLock` and is human-gated: only on the user's explicit say-so. It is NOT `-Force`. On both launchers `-Force` means the routine "override a refusal inside my own session" (client rig: `-Provision -Force` rebuilds an instance you own) and never breaks a lock. `-TimeoutSeconds` is process-teardown grace (default 30) on both; the client rig's readiness barrier is `-WaitSeconds` (default 300).

Client-rig hazards with no server equivalent: a provision hard-links ~1,050 files out of the developer's REAL install (read-only, always; anything the game writes to must be a real copy, never a link), `-Remove` deletes an instance's save root at TestRig/ClientRig/data/<instance>/userdata/, `-Stop -All` ends every instance including another session's live test, and `POST /savepath` retargets a RUNNING client's save root (it refuses the real user-data folder only while the caller omits force=true). Never focus, raise or activate a game window; instances run on a Win32 desktop that is created and never switched to.

Server-side: -Stop the server before any -DeployMods or -SyncMods. Windows holds an exclusive file lock on loaded plugin DLLs, so deploying onto a running server either fails or leaves a half-written DLL the next -Start picks up as broken plugin bytes (the launcher enforces this check too).

Save tiers: TestRig/DedicatedServer/data/saves/ and TestRig/ClientRig/data/<instance>/userdata/ are both tier 3 (agent-managed, but owned by whoever holds the lock). The developer's client save folder is tier 1 and off-limits unconditionally.

Full rules: TestRig/session.lock.template + TestRig/CLAUDE.md + the half's own manual (TestRig/DedicatedServer/CLAUDE.md, TestRig/ClientRig/README.md).
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
