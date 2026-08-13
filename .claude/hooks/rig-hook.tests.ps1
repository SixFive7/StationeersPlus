# rig-hook.tests.ps1 -- saved with UTF-8 BOM
#
# Offline branch tests for rig-hook.ps1. Feeds the hook synthetic PreToolUse and
# PostToolUse payloads on stdin and asserts which branch it takes. No game, no
# rig, no lock, no network, and it never invokes testrig.ps1 for real.
#
#     pwsh -NoProfile -File .claude/hooks/rig-hook.tests.ps1
#
# WHY THIS EXISTS. Every hook defect this repository has hit was silent: a matcher
# that decoded to a regex nothing could satisfy, a header comment naming a safety
# net that never fired, a restructure that orphaned five matchers with no error
# anywhere. "It looks right" is not evidence about a hook. This file is.
#
# WHAT IT DOES AND DOES NOT COVER. It exercises the hook SCRIPT's decision logic
# against the payload shape the harness delivers. It does NOT exercise the `if`
# rules in settings.json, which only Claude Code can evaluate; those were checked
# by making real matching and non-matching tool calls and watching what appeared.
# If you change an `if` rule, this suite will not catch a mistake in it.
#
# Nothing runs this automatically. Run it after touching rig-hook.ps1.
$ErrorActionPreference = 'Stop'
$hook = Join-Path $PSScriptRoot 'rig-hook.ps1'

function Invoke-Hook {
    param([string] $Event, [string] $Tool, [hashtable] $ToolInput)
    $payload = @{ hook_event_name = $Event; tool_name = $Tool; tool_input = $ToolInput } | ConvertTo-Json -Depth 5 -Compress
    $tmp = [System.IO.Path]::GetTempFileName()
    Set-Content -LiteralPath $tmp -Value $payload -Encoding UTF8 -NoNewline
    $out = (Get-Content -LiteralPath $tmp -Raw | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $hook) 2>&1
    Remove-Item $tmp -Force
    if (-not $out) { return @{ kind = 'silent'; text = '' } }
    $j = ($out -join "`n") | ConvertFrom-Json
    $h = $j.hookSpecificOutput
    $kind = 'context'
    if ($h.PSObject.Properties.Name -contains 'permissionDecision') { $kind = $h.permissionDecision }
    $text = if ($kind -eq 'context') { $h.additionalContext } else { $h.permissionDecisionReason }
    return @{ kind = $kind; text = $text }
}

$cases = @(
    @{ n='status (read-only verb)';        e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 status'};                                                     want='silent' }
    @{ n='logs -Target all';               e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 logs -Target all -Tail 50'};                                  want='silent' }
    @{ n='stop -Target all';               e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 stop -Target all -As k3'};                                    want='ask' }
    @{ n='stop -Target clients';           e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 stop -Target clients -As k3'};                                want='ask' }
    @{ n='stop -Target host1 (narrow)';    e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 stop -Target host1 -As k3'};                                  want='silent' }
    @{ n='unlock -BreakLock';              e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 unlock -As k3 -BreakLock'};                                   want='ask' }
    @{ n='remove';                         e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 remove -Target client1 -As k3'};                              want='ask' }
    @{ n='remove, segment-start form';     e='PreToolUse'; t='Bash';       i=@{command='./TestRig/testrig.ps1 remove -Target client1'};                                                          want='ask' }
    @{ n='remove via PowerShell tool';     e='PreToolUse'; t='PowerShell'; i=@{command="& 'c:\Source\SixFive7\StationeersPlus\TestRig\testrig.ps1' remove -Target c1 -As k3"};                  want='ask' }
    @{ n='MISFIRE: git commit message';    e='PreToolUse'; t='Bash';       i=@{command='git commit -m "TestRig: fold dedicated-server.ps1 and client-rig.ps1 into testrig.ps1"'};               want='silent' }
    @{ n='MISFIRE: ls the tree';           e='PreToolUse'; t='Bash';       i=@{command='ls -la TestRig/'};                                                                                      want='silent' }
    @{ n='MISFIRE: grep for BreakLock';    e='PreToolUse'; t='Bash';       i=@{command='grep -n BreakLock TestRig/testrig.ps1'};                                                                want='silent' }
    @{ n='MISFIRE: grep the override';     e='PreToolUse'; t='Bash';       i=@{command='grep -rn requireIsolatedSavePath TestRig/ClientRig/dev-plugins'};                                       want='silent' }
    @{ n='MISFIRE: compound fail-open';    e='PreToolUse'; t='Bash';       i=@{command='ls TestRig | wc -l'};                                                                                   want='silent' }
    @{ n='MISFIRE: wc -l on the launcher'; e='PreToolUse'; t='Bash';       i=@{command='wc -l TestRig/testrig.ps1'};                                                                            want='silent' }
    @{ n='mutating, no -As';               e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 start -Target server -Load W -Map Mars'};                     want='context' }
    @{ n='mutating, has -As';              e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 deploy SprayPaintPlus -As k3'};                               want='silent' }
    @{ n='lock itself (excluded)';         e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 lock -Purpose "network paint"'};                              want='silent' }
    @{ n='DENY savepath force, launcher';  e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 call -Target c1 -As k3 -Path /savepath -Body ''{"path":"D:/x","force":true}'''}; want='deny' }
    @{ n='DENY savepath force, curl';      e='PreToolUse'; t='Bash';       i=@{command='curl -s -X POST http://127.0.0.1:27701/savepath -d ''{"path":"D:/x","force":true}'''};                  want='deny' }
    @{ n='DENY savepath force, irm';       e='PreToolUse'; t='PowerShell'; i=@{command='Invoke-RestMethod -Method Post -Uri http://127.0.0.1:27701/savepath -Body ''{"force": true}'''};        want='deny' }
    @{ n='DENY host isolation override';   e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 call -Target h1 -As k3 -Path /host -Body ''{"world":"Lunar","requireIsolatedSavePath":false}'''}; want='deny' }
    @{ n='savepath WITHOUT force (ok)';    e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 call -Target c1 -As k3 -Path /savepath -Body ''{"path":"D:/x"}'''}; want='silent' }
    @{ n='host WITH isolation kept (ok)';  e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 call -Target h1 -As k3 -Path /host -Body ''{"world":"Lunar","requireIsolatedSavePath":true}'''}; want='silent' }
    @{ n='AUTHOR rig-lock.ps1';            e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\rig-lock.ps1'};                                            want='context' }
    @{ n='AUTHOR lib/client.ps1';          e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\lib\client.ps1'};                                          want='context' }
    @{ n='AUTHOR playtest-lib.ps1';        e='PostToolUse'; t='Write';     i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\playtest\playtest-lib.ps1'};                               want='context' }
    @{ n='AUTHOR testrig.ps1';             e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\testrig.ps1'};                                             want='context' }
    @{ n='a suite file (excluded)';        e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\rig-lock.tests.ps1'};                                      want='silent' }
    @{ n='a dev-plugin .cs (excluded)';    e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\ClientRig\dev-plugins\ClientDriver\ClientDriver\Plugin.cs'}; want='silent' }
    @{ n='a rig doc (excluded)';           e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\MANUAL.md'};                                               want='silent' }
    @{ n='a playtest check (excluded)';    e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\playtest\checks\SprayPaintPlus\01-first-use-notice-cap.playtest.ps1'}; want='silent' }
)

$pass = 0; $fail = 0
foreach ($c in $cases) {
    $r = Invoke-Hook -Event $c.e -Tool $c.t -ToolInput $c.i
    $ok = ($r.kind -eq $c.want)
    if ($ok) { $pass++ } else { $fail++ }
    $words = if ($r.text) { ($r.text -split '\s+' | Where-Object { $_ }).Count } else { 0 }
    '{0}  {1,-32} got={2,-8} want={3,-8} words={4}' -f $(if ($ok) { 'pass' } else { 'FAIL' }), $c.n, $r.kind, $c.want, $words
}
''
"assertions: $pass passed, $fail failed"
if ($fail -gt 0) { exit 1 }
