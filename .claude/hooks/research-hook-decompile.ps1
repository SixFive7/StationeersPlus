# research-hook-decompile.ps1
# Curation reminder hook for decompiled-content access. Fires on:
#   (a) Glob against rocketstation_Data/Managed/ (direct DLL listings).
#   (b) Read or Glob against .work/decomp/ (canonical decompile output).
#   (c) Read or Glob of any *.decompiled.cs file (suffix safety net).
#   (d) Bash commands containing ilspycmd or ICSharpCode.Decompiler.
#   (e) Bash commands whose text contains .work/decomp/, *.decompiled.cs,
#       or rocketstation_Data/Managed/ (catches cat/grep/rg/head/xxd/etc.
#       inspection of decompile content without enumerating tools).
#
# Reminds the agent about Rule 2 (curate findings into Research/) and
# injects the current game version so the agent has the right stamp
# value for any subsequent Research/ write.
#
# Source-confirmed gaps (do not try to "fix" with extra if rules):
#   * Grep matchers are intentionally NOT registered. Per the Claude Code
#     source (src/tools/GrepTool/GrepTool.ts preparePermissionMatcher),
#     Grep `if` rules match against the search regex argument, not the
#     path or glob. There is no settings.json syntax to make Grep fire
#     on path-based criteria. If Grep coverage is required later, the
#     only fix is in-script stdin filtering (the hook script reads the
#     full tool_input from stdin and decides for itself).
#   * Read against rocketstation_Data/Managed/ is also NOT registered.
#     All files there are binary; the Read tool rejects binaries
#     pre-flight, so PostToolUse never fires. Glob covers the realistic
#     access pattern for that directory.
#   * Bash matchers fail OPEN for compound commands. Per
#     src/tools/BashTool/BashTool.tsx preparePermissionMatcher, when the
#     bash AST parser cannot represent the command (pipes, &&, loops,
#     heredocs), the matcher returns () => true and every Bash `if` rule
#     fires. This is intentional security-favouring behaviour (so a
#     compound can't bypass Bash(git push *) deny rules). Result: the
#     decompile reminder also fires on every "too-complex" shell call;
#     ignore it on non-decompile work.

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
