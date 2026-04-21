# research-hook-decompile.ps1
# Hook A: fires after Read | Grep | Glob against any file under
# rocketstation_Data/Managed/. Reminds the agent about Rule 2 (curate findings
# into Research/ on every decompiled-code touch) and injects the current game
# version so the agent has the right stamp value for any subsequent Research/
# write.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$helperPath = Join-Path -Path $PSScriptRoot -ChildPath 'get-game-version.ps1'
. $helperPath

try {
    $version = Get-GameVersionString
} catch {
    [Console]::Error.WriteLine("[research-hook-decompile] $($_.Exception.Message)")
    exit 1
}

$message = @"
[Research curation reminder] You just touched decompiled game code. Current game version: $version.

Research/WORKFLOW.md Rule 2 ("curate decompiled-code findings into Research/ on every touch") applies: any game-internals finding you produce this turn must land in a page under Research/<category>/ in this same response, with version stamps set to $version. Do not postpone. Read Research/WORKFLOW.md in full if you have not yet this conversation.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName    = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
