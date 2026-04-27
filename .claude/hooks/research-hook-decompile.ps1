# research-hook-decompile.ps1
# Hook A: fires after Read | Grep | Glob against (a) any file under
# rocketstation_Data/Managed/ (direct DLL reads), (b) any file under
# .work/decomp/ (canonical decompile output location), (c) any file ending
# in *.decompiled.cs (suffix safety net for derived artifacts anywhere in
# the tree), and after Bash commands invoking a decompiler (ilspycmd,
# ICSharpCode.Decompiler). Reminds the agent about Rule 2 (curate findings
# into Research/ on every decompiled-code touch) and injects the current
# game version so the agent has the right stamp value for any subsequent
# Research/ write.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$helperPath = Join-Path -Path $PSScriptRoot -ChildPath 'get-game-version.ps1'
. $helperPath

try {
    $version = Get-GameVersionString
} catch {
    [Console]::Error.WriteLine("[research-hook-decompile] $($_.Exception.Message)")
    exit 1
}

$message = @'
[Research curation reminder] You just touched decompiled game code. Current game version: {0}.

Research/WORKFLOW.md Rule 2 ("curate decompiled-code findings into Research/ on every touch") applies: any game-internals finding you produce this turn must land in a page under Research/<category>/ in this same response. Do not postpone. Read Research/WORKFLOW.md in full if you have not yet this conversation.

This hook fires for: (a) direct reads of game DLLs under rocketstation_Data/Managed/, (b) reads of files under .work/decomp/<game-version>/, (c) reads of any *.decompiled.cs file anywhere in the tree, and (d) Bash invocations of a decompiler (ilspycmd, ICSharpCode.Decompiler).

The canonical decompile output path is .work/decomp/<game-version>/<source-name>.decompiled.cs (see CLAUDE.md, "Decompilation artifacts" section). Decompiles outside that path are forbidden.

Version stamping: prefer the <game-version> segment of the path you read over the current game version {0} when they differ. The path segment records when the file was decompiled; {0} is only "right now". If the path has no version segment (e.g., a direct DLL read or a stray *.decompiled.cs without a version folder), use {0}. Treat a mismatch as a signal that the decompile is stale: regenerate it under the current version before relying on its content.
'@ -f $version

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
