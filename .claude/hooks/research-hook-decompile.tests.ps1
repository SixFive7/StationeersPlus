# research-hook-decompile.tests.ps1 -- saved with UTF-8 BOM
#
# Offline branch tests for research-hook-decompile.ps1. Feeds the hook synthetic
# PostToolUse payloads on stdin and asserts whether it speaks or stays quiet. No
# game, no decompiler, no network.
#
#     pwsh -NoProfile -File .claude/hooks/research-hook-decompile.tests.ps1
#
# WHY THIS EXISTS. The hook shipped for months with a defect nothing could see: it
# never read stdin at all, so it could not tell a decompile from a directory listing,
# and seven Bash registrations in settings.json meant one unparseable command bought
# seven copies of a reminder about work that had never touched a decompile. Both
# halves of that failure were invisible from reading the file. This suite pins the
# filtering half. The registration half is pinned by the case count below: if a real
# call ever produces more than one injection, the `if` rules regressed, not this.
#
# WHAT IT DOES AND DOES NOT COVER. It exercises the hook SCRIPT's decision against
# the payload shape the harness actually delivers (captured from live PostToolUse
# calls, tool_response included). It does NOT exercise the `if` rules in
# settings.json, which only Claude Code can evaluate; those are checked by making
# real matching and non-matching tool calls and counting the injections that appear.
#
# THE FIVE TRIGGERS THAT MUST NEVER GO QUIET, per Research/WORKFLOW.md Rule 2:
# a *.decompiled.cs read anywhere, a read under .work/decomp/, a Glob of the game
# DLLs under rocketstation_Data/Managed/, a Bash decompiler invocation, and a Bash
# command whose text names a decompile path or suffix. Every one has a case here.
#
# Nothing runs this automatically. Run it after touching research-hook-decompile.ps1.
$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'research-hook-decompile.ps1'

$version  = '0.2.6428.27798'
$repo     = 'c:\Source\SixFive7\StationeersPlus'
$decompNt = "$repo\.work\decomp\$version"
$managed  = 'E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed'

function Invoke-Hook {
    param([hashtable] $Payload, [string] $RawText)

    $text = if ($PSBoundParameters.ContainsKey('RawText')) { $RawText }
            else { $Payload | ConvertTo-Json -Depth 6 -Compress }

    $tmp = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $tmp -Value $text -Encoding UTF8 -NoNewline
    $out = Get-Content -LiteralPath $tmp -Raw | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook
    $code = $LASTEXITCODE
    Remove-Item $tmp -Force

    if (-not $out) { return @{ kind = 'silent'; text = ''; code = $code } }
    $j = ($out -join "`n") | ConvertFrom-Json
    return @{ kind = 'context'; text = $j.hookSpecificOutput.additionalContext; code = $code }
}

# A PostToolUse payload with the fields the harness really sends. The envelope keys
# outside tool_input are load-bearing for one case: a token there must not fire.
function New-Payload {
    # NOT $Input: that is a PowerShell automatic variable (the pipeline enumerator),
    # and binding a parameter to it hands you an ArrayListEnumeratorSimple.
    param([string] $Tool, [hashtable] $ToolInput, $Response = @{ stdout = ''; stderr = '' })
    return @{
        session_id      = '5f1673be-b732-4a23-855a-72af43200618'
        transcript_path = 'C:\Users\jori\.claude\projects\x\y.jsonl'
        cwd             = $repo
        permission_mode = 'bypassPermissions'
        hook_event_name = 'PostToolUse'
        tool_name       = $Tool
        tool_input      = $ToolInput
        tool_response   = $Response
        tool_use_id     = 'toolu_test'
    }
}

$cases = @(
    # === TRUE FIRES. All five triggers. Losing any of these loses the rule. ======
    @{ n='(a) read *.decompiled.cs, canonical'; want='context'
       p=(New-Payload 'Read' @{ file_path="$decompNt\NetworkBase.decompiled.cs" }) }
    @{ n='(a) read *.decompiled.cs, stray'; want='context'
       p=(New-Payload 'Read' @{ file_path="$repo\Mods\SprayPaintPlus\Assembly-CSharp.decompiled.cs" }) }
    @{ n='(b) read non-.cs under .work/decomp'; want='context'
       p=(New-Payload 'Read' @{ file_path="$decompNt\il-dump.txt" }) }
    @{ n='(b) glob under .work/decomp'; want='context'
       p=(New-Payload 'Glob' @{ pattern='.work/decomp/**/*.cs' }) }
    @{ n='(c) glob the game DLLs'; want='context'
       p=(New-Payload 'Glob' @{ pattern='**/rocketstation_Data/Managed/Assembly-CSharp.dll' }) }
    @{ n='(c) glob, dir in path arg'; want='context'
       p=(New-Payload 'Glob' @{ pattern='*.dll'; path=$managed }) }
    @{ n='(d) bash ilspycmd'; want='context'
       p=(New-Payload 'Bash' @{ command="ilspycmd `"$managed/Assembly-CSharp.dll`" -o .work/decomp/$version"; description='Decompile' }) }
    @{ n='(d) bash ICSharpCode.Decompiler'; want='context'
       p=(New-Payload 'Bash' @{ command='dotnet run --project ICSharpCode.Decompiler.Console' }) }
    @{ n='(e) bash cat a decompile'; want='context'
       p=(New-Payload 'Bash' @{ command="cat .work/decomp/$version/NetworkBase.decompiled.cs" }) }
    @{ n='(e) bash grep through a pipe'; want='context'
       p=(New-Payload 'Bash' @{ command="grep -n OnFinishedLoad .work/decomp/$version/*.decompiled.cs | head -20" }) }
    @{ n='(e) bash ls the Managed folder'; want='context'
       p=(New-Payload 'Bash' @{ command="ls -la `"$managed/`"" }) }
    @{ n='(e) bash strings on a game DLL'; want='context'
       p=(New-Payload 'Bash' @{ command="strings `"$managed/Assembly-CSharp.dll`"" }) }
    @{ n='(e) windows separators in path'; want='context'
       p=(New-Payload 'Bash' @{ command="xxd `"$decompNt\Client.decompiled.cs`" | head" }) }
    @{ n='(e) relevant AND loop-shaped'; want='context'
       p=(New-Payload 'Bash' @{ command='for f in .work/decomp/*/*.decompiled.cs; do wc -l $f; done' }) }

    # === FALSE FIRES. The whole point of the stdin filter. ======================
    # The reproducer: a loop is unparseable, so every Bash `if` rule fires regardless
    # of content. The script has to be the thing that says no.
    @{ n='MISFIRE: the loop reproducer'; want='silent'
       p=(New-Payload 'Bash' @{ command='for i in a b; do echo $i; done' } @{ stdout="a`nb"; stderr='' }) }
    @{ n='MISFIRE: git status'; want='silent'
       p=(New-Payload 'Bash' @{ command='git status --short' } @{ stdout=' M CLAUDE.md'; stderr='' }) }
    @{ n='MISFIRE: wc -l'; want='silent'
       p=(New-Payload 'Bash' @{ command='wc -l CLAUDE.md' }) }
    @{ n='MISFIRE: heredoc'; want='silent'
       p=(New-Payload 'Bash' @{ command="git commit -F - <<'EOF'`nResearch: page`nEOF" }) }
    @{ n='MISFIRE: run testrig.exe'; want='silent'
       p=(New-Payload 'Bash' @{ command='TestRig/testrig.exe status' }) }
    @{ n='MISFIRE: bare word decomp'; want='silent'
       p=(New-Payload 'Bash' @{ command='grep -rn decompress tools/' }) }
    @{ n='MISFIRE: path names decomp, is not one'; want='silent'
       p=(New-Payload 'Read' @{ file_path="$repo\tools\decompress-notes.md" }) }
    # A token in the OUTPUT is not a touch. `git status` listing a decompile path,
    # or a grep printing one, must not be read as having decompiled anything.
    @{ n='MISFIRE: token in tool_response only'; want='silent'
       p=(New-Payload 'Bash' @{ command='git status --short' } @{ stdout="?? .work/decomp/$version/Client.decompiled.cs"; stderr='' }) }
    @{ n='MISFIRE: token in description only'; want='silent'
       p=(New-Payload 'Bash' @{ command='ls tools/'; description='Look for the .decompiled.cs helper' }) }
    @{ n='MISFIRE: no tool_input at all'; want='silent'
       p=@{ hook_event_name='PostToolUse'; tool_name='Bash'; tool_response=@{ stdout=".work/decomp/$version/x.decompiled.cs" } } }

    # === EDGES =================================================================
    # Token present but the payload will not parse: fire. The gap is narrow and a
    # lost curation reminder costs more than a spare one.
    @{ n='EDGE: unparseable payload, token'; want='context'
       raw='{"tool_input":{"command":"cat .work/decomp/x/y.decompiled.cs"' }
    @{ n='EDGE: unparseable payload, no token'; want='silent'
       raw='{"tool_input":{"command":"echo hello"' }
    @{ n='EDGE: empty stdin'; want='silent'; raw='' }
    @{ n='EDGE: whitespace stdin'; want='silent'; raw="  `n  " }
)

$pass = 0; $fail = 0
foreach ($c in $cases) {
    $r = if ($c.ContainsKey('raw')) { Invoke-Hook -RawText $c.raw } else { Invoke-Hook -Payload $c.p }
    $ok = ($r.kind -eq $c.want)
    if ($ok) { $pass++ } else { $fail++ }
    $words = if ($r.text) { ($r.text -split '\s+' | Where-Object { $_ }).Count } else { 0 }
    $note  = if ($r.code -ne 0) { "  EXIT=$($r.code) (game version unresolvable?)" } else { '' }
    '{0}  {1,-38} got={2,-8} want={3,-8} words={4}{5}' -f $(if ($ok) { 'pass' } else { 'FAIL' }), $c.n, $r.kind, $c.want, $words, $note
}
''
"assertions: $pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
