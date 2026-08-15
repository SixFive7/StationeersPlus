# research-hook-decompile.ps1 -- saved with UTF-8 BOM
#
# Curation reminder for decompiled-content access. It enforces Research/WORKFLOW.md
# Rule 2: a finding pulled out of decompiled game code gets curated into
# Research/<category>/ in the same turn it was found.
#
# WHAT COUNTS AS A TOUCH. Five triggers, and the script decides all five for itself
# from tool_input:
#   (a) a Read of any file whose path ends in *.decompiled.cs, anywhere in the tree.
#   (b) a Read or Glob under .work/decomp/<game-version>/, the canonical output path.
#   (c) a Glob naming rocketstation_Data/Managed/, which is how a direct DLL listing
#       is seen. Read cannot serve here: every file there is binary and the Read tool
#       rejects binaries pre-flight, so its tool body never runs and PostToolUse
#       never fires. Coverage is PARTIAL by nature, because a Glob `if` rule only
#       sees the `pattern` argument, and a call that puts the directory in `path` and
#       *.dll in `pattern` never reaches this script. Do not describe it as complete.
#   (d) a Bash invocation of a decompiler: ilspycmd, ICSharpCode.Decompiler.
#   (e) a Bash command whose text names a decompile path or suffix. This is what
#       catches inspection through cat, grep, rg, head, tail, xxd, strings and every
#       other tool nobody wants to enumerate.
#
# WHY THE FILTERING HAPPENS HERE AND NOT IN settings.json. Bash `if` rules fail OPEN:
# per src/tools/BashTool/BashTool.tsx preparePermissionMatcher, when the AST parser
# cannot represent a command the matcher becomes () => true and EVERY Bash `if` rule
# in settings.json fires. It tracks command shape, not content, and a bare `for` loop
# or a heredoc is enough. This hook used to carry SEVEN Bash registrations, so one
# unparseable command bought seven copies of the reminder, about 1330 words, on work
# that had never been near a decompile. Measured, not theorised: a `for` loop over
# two echoes produced all seven. It is one registration now, with no `if` at all, and
# the decision below is the only thing that speaks.
#
# So the registration set is deliberately a CHEAP PRE-FILTER, never a decision:
#   Bash        no `if`. Every Bash call reaches this script and is judged here.
#   Read/Glob   path-shaped `if` rules, because those DO match precisely, and firing
#               a process on every Read would cost more than it saves. One rule per
#               tool, `*decomp*`, wide enough to cover both .work/decomp/ and the
#               .decompiled.cs suffix in a single registration. Two overlapping rules
#               used to double-fire on the canonical path, which is both of them at
#               once: measured at two injections for one read.
#
# TWO-STAGE MATCH, for speed. Stage one scans the raw stdin text and exits silently
# when no trigger token appears anywhere in the payload. That is the path almost
# every Bash call takes, and it costs one regex pass with no JSON parse. Stage two
# only runs when something matched: parse the payload and re-test against tool_input
# alone, so a token sitting in tool_response (grep output that happens to print a
# decompile path) does not fire. `description` is excluded for the same reason.
#
# FAIL TOWARDS FIRING, but only from the narrow gap. If a token IS present and the
# payload will not parse, the reminder goes out. Losing a true fire costs curated
# research; an extra reminder costs words.
#
# Grep is intentionally NOT registered, and cannot usefully be. Per the Claude Code
# source (src/tools/GrepTool/GrepTool.ts preparePermissionMatcher), a Grep `if` rule
# matches the SEARCH REGEX argument, not the path or the glob filter, so a
# path-shaped rule on Grep is inert. Registering Grep with no `if` at all would work
# the same way Bash does now, at the price of a process on every Grep.
#
# Tests: .claude/hooks/research-hook-decompile.tests.ps1. Run it after any edit here.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# --- what a decompile touch looks like, in text ------------------------------
# Written to survive JSON escaping: a Windows path arrives as .work\\decomp\\, so
# every separator is [\\/]+ rather than a single character.
$trigger = '(?i)(ilspycmd|ICSharpCode\.Decompiler|\.work[\\/]+decomp|\.decompiled\.cs|rocketstation_Data[\\/]+Managed)'

$stdin = [Console]::In.ReadToEnd()
if ([string]::IsNullOrWhiteSpace($stdin)) { exit 0 }

# Stage one: nothing anywhere in the payload, nothing to say. No parse.
if ($stdin -notmatch $trigger) { exit 0 }

function Get-Field {
    param($Obj, [string] $Name)
    if ($null -eq $Obj) { return $null }
    if ($Obj.PSObject.Properties.Name -contains $Name) { return $Obj.$Name }
    return $null
}

# Stage two: was the trigger in the INPUT, or only in the output we scanned past?
$hookEvent = 'PostToolUse'
$relevant  = $true   # unparseable payload with a token present: fire.

$data = $null
try { $data = $stdin | ConvertFrom-Json } catch { $data = $null }

if ($null -ne $data) {
    $ev = [string](Get-Field $data 'hook_event_name')
    if ($ev) { $hookEvent = $ev }

    $toolInput = Get-Field $data 'tool_input'
    if ($null -eq $toolInput) {
        $relevant = $false
    } else {
        # Every string the caller passed IN, minus `description`, which narrates the
        # call rather than performing it. Tool-agnostic on purpose: command for Bash,
        # file_path for Read, pattern and path for Glob, whatever a future tool adds.
        $relevant = $false
        foreach ($prop in $toolInput.PSObject.Properties) {
            if ($prop.Name -eq 'description') { continue }
            $value = $prop.Value
            if ($null -eq $value) { continue }
            $text = if ($value -is [string]) { $value } else { $value | ConvertTo-Json -Depth 4 -Compress }
            if ($text -match $trigger) { $relevant = $true; break }
        }
    }
}

if (-not $relevant) { exit 0 }

# --- only now is the version worth resolving ---------------------------------
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

This hook fires for: (a) reads of any *.decompiled.cs file anywhere in the tree, (b) reads of files under .work/decomp/<game-version>/, (c) Glob listings of the game DLLs under rocketstation_Data/Managed/, (d) Bash invocations of a decompiler (ilspycmd, ICSharpCode.Decompiler), and (e) Bash commands whose text names a decompile path or suffix.

The canonical decompile output path is .work/decomp/<game-version>/<source-name>.decompiled.cs (see CLAUDE.md, "Decompilation artifacts" section). Decompiles outside that path are forbidden.

Version stamping: prefer the <game-version> segment of the path you read over the current game version {0} when they differ. The path segment records when the file was decompiled; {0} is only "right now". If the path has no version segment (e.g., a direct DLL read or a stray *.decompiled.cs without a version folder), use {0}. Treat a mismatch as a signal that the decompile is stale: regenerate it under the current version before relying on its content.
'@ -f $version

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = $hookEvent
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
