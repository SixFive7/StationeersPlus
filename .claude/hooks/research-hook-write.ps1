# research-hook-write.ps1
# Hook C: fires after Edit | Write against any file under Research/. Catches
# multi-edit sessions and edits without a prior read. Re-injects the current
# game version plus a reminder to verify the stamps written in the edit
# match that version, and to append to Verification History when content
# changed.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$helperPath = Join-Path -Path $PSScriptRoot -ChildPath 'get-game-version.ps1'
. $helperPath

try {
    $version = Get-GameVersionString
} catch {
    [Console]::Error.WriteLine("[research-hook-write] $($_.Exception.Message)")
    exit 1
}

$message = @"
[Research write backstop] Current game version: $version.

Verify the verified_in and section-stamp values you just wrote match $version. If the edit changed factual content (not just wording), append a dated entry to the page's Verification History section per Research/CLAUDE.md. Cosmetic edits do not require a restamp.
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName    = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
