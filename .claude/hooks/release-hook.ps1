# release-hook.ps1
# Fires after Edit / Write on any mod's Plugin.cs. Release commits are
# identified by a PluginVersion bump in that file, but hooks cannot match on
# content; this fires on every Plugin.cs edit and the reminder is short.
# The rules it points at are short and worth re-reading when any plugin
# code changes, so the over-fire is accepted intentionally.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Release workflow reminder] You just edited a mod's Plugin.cs. If this is a release commit (PluginVersion bump), the release rules apply: one mod per release commit, exactly Plugin.cs + About.xml touched, tag format `mods/<ModName>/v<X.Y.Z>`, commit message `<ModName> v<X.Y.Z>: <summary>`. Full rules in Mods/Template/RELEASE.md. If this is not a release commit, ignore this reminder.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
