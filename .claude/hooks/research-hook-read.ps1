# research-hook-read.ps1
# Hook B: fires after Read against any file under Research/. Injects the
# current game version so subsequent Edit / Write operations in this turn
# carry the correct stamp without the agent having to guess.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$helperPath = Join-Path -Path $PSScriptRoot -ChildPath 'get-game-version.ps1'
. $helperPath

try {
    $version = Get-GameVersionString
} catch {
    [Console]::Error.WriteLine("[research-hook-read] $($_.Exception.Message)")
    exit 1
}

$message = '[Research version] Current game version: {0}. Use this value for any created_in / verified_in / section-stamp updates you make in Research/ this turn.' -f $version

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
