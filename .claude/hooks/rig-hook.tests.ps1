# rig-hook.tests.ps1 -- saved with UTF-8 BOM
#
# Offline branch tests for rig-hook.ps1. Feeds the hook synthetic PreToolUse and
# PostToolUse payloads on stdin and asserts which branch it takes. No game, no
# rig, no lock, no network, and it never invokes testrig.exe or testrig.ps1 for real.
#
#     pwsh -NoProfile -File .claude/hooks/rig-hook.tests.ps1
#
# WHY THIS EXISTS. Every hook defect this repository has hit was silent: a matcher
# that decoded to a regex nothing could satisfy, a header comment naming a safety
# net that never fired, a restructure that orphaned five matchers with no error
# anywhere. The C# port added the sharpest one yet: every rig matcher named
# testrig.ps1, so when the launcher became testrig.exe the whole hook went quiet,
# tier-1 deny branch included, and the suite stayed green because every case in it
# was written against the old spelling. "It looks right" is not evidence about a
# hook. This file is, and only as far as its cases reach.
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
    # --- the binary: read-only and non-destructive verbs stay silent -----------
    @{ n='exe status';                     e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe status'};                                                                            want='silent' }
    @{ n='exe logs --target all';          e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe logs --target all --tail 50'};                                                       want='silent' }
    @{ n='exe lock (excluded)';            e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe lock --purpose "network paint"'};                                                    want='silent' }
    @{ n='exe create --force (not deny)';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe create --target c1 --as k3 --force'};                                                want='silent' }

    # --- the binary: the three ask gates ---------------------------------------
    @{ n='exe stop --target all';          e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe stop --target all --as k3'};                                                         want='ask' }
    @{ n='exe stop --target clients';      e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe stop --target clients --as k3'};                                                     want='ask' }
    @{ n='exe stop --target=all (equals)'; e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe stop --target=all --as k3'};                                                         want='ask' }
    @{ n='exe stop --target:all (colon)';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe stop --target:clients --as k3'};                                                     want='ask' }
    @{ n='exe stop one instance (narrow)'; e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe stop --target hostie --as k3'};                                                      want='silent' }
    @{ n='exe unlock --break-lock';        e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe unlock --as k3 --break-lock'};                                                       want='ask' }
    @{ n='exe remove';                     e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe remove --target hostie --as k3'};                                                    want='ask' }
    @{ n='exe remove, segment-start form'; e='PreToolUse'; t='Bash';       i=@{command='./TestRig/testrig.exe remove --target hostie'};                                                          want='ask' }
    @{ n='exe remove, quoted abs path';    e='PreToolUse'; t='Bash';       i=@{command='"c:/Source/SixFive7/StationeersPlus/TestRig/testrig.exe" remove --target hostie --as k3'};               want='ask' }
    @{ n='exe remove after && chain';      e='PreToolUse'; t='Bash';       i=@{command='cd /c/Source/SixFive7/StationeersPlus && ./TestRig/testrig.exe remove --target hostie --as k3'};         want='ask' }
    @{ n='exe remove via PowerShell tool'; e='PreToolUse'; t='PowerShell'; i=@{command="& 'c:\Source\SixFive7\StationeersPlus\TestRig\testrig.exe' remove --target c1 --as k3"};                  want='ask' }
    @{ n='bare testrig on PATH, remove';   e='PreToolUse'; t='Bash';       i=@{command='testrig remove --target c1 --as k3'};                                                                    want='ask' }
    @{ n='exe remove --force (ask, deny)'; e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe remove --target c1 --as k3 --force'};                                                want='ask' }

    # --- the DELETED PowerShell launcher is still recognised -------------------
    # testrig.ps1 no longer exists; it went with the rest of the PowerShell rig. The
    # spelling stays matched on purpose, because the script is recoverable from git
    # history and a hook that stopped recognising it would go quiet on exactly the
    # command that should not have been typed. These cases pin that.
    @{ n='ps1 stop -Target all';           e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 stop -Target all -As k3'};                                     want='ask' }
    @{ n='ps1 unlock -BreakLock';          e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 unlock -As k3 -BreakLock'};                                    want='ask' }
    @{ n='ps1 remove';                     e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 remove -Target client1 -As k3'};                               want='ask' }
    @{ n='ps1 status (read-only)';         e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 status'};                                                      want='silent' }

    # --- misfires: mentioning the rig is not driving it ------------------------
    @{ n='MISFIRE: git commit message';    e='PreToolUse'; t='Bash';       i=@{command='git commit -m "TestRig: retire the hooks the binary supersedes, testrig.exe"'};                          want='silent' }
    @{ n='MISFIRE: ls the tree';           e='PreToolUse'; t='Bash';       i=@{command='ls -la TestRig/'};                                                                                       want='silent' }
    @{ n='MISFIRE: grep for break-lock';   e='PreToolUse'; t='Bash';       i=@{command='grep -n break-lock TestRig/testrig.ps1'};                                                                want='silent' }
    @{ n='MISFIRE: grep the override';     e='PreToolUse'; t='Bash';       i=@{command='grep -rn requireIsolatedSavePath TestRig/ClientRig/dev-plugins'};                                        want='silent' }
    @{ n='MISFIRE: compound fail-open';    e='PreToolUse'; t='Bash';       i=@{command='ls TestRig | wc -l'};                                                                                    want='silent' }
    @{ n='MISFIRE: wc -l on the binary';   e='PreToolUse'; t='Bash';       i=@{command='wc -c TestRig/testrig.exe'};                                                                             want='silent' }
    @{ n='MISFIRE: dotnet test';           e='PreToolUse'; t='Bash';       i=@{command='dotnet test TestRig/src/TestRig.slnx'};                                                                  want='silent' }
    @{ n='MISFIRE: dotnet publish';        e='PreToolUse'; t='Bash';       i=@{command='dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64'};                       want='silent' }

    # --- the tier-1 deny, on both the launcher and raw HTTP --------------------
    @{ n='DENY savepath force, exe call';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe call --target c1 --as k3 --path /savepath --body ''{"path":"D:/x","force":true}'''};  want='deny' }
    @{ n='DENY savepath force, ps1 call';  e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 call -Target c1 -As k3 -Path /savepath -Body ''{"path":"D:/x","force":true}'''}; want='deny' }
    @{ n='DENY savepath force, curl';      e='PreToolUse'; t='Bash';       i=@{command='curl -s -X POST http://127.0.0.1:27701/savepath -d ''{"path":"D:/x","force":true}'''};                   want='deny' }
    @{ n='DENY savepath force, irm';       e='PreToolUse'; t='PowerShell'; i=@{command='Invoke-RestMethod -Method Post -Uri http://127.0.0.1:27701/savepath -Body ''{"force": true}'''};         want='deny' }
    @{ n='DENY host isolation, exe call';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe call --target h1 --as k3 --path /host --body ''{"world":"Lunar","requireIsolatedSavePath":false}'''}; want='deny' }
    @{ n='DENY host isolation, curl';      e='PreToolUse'; t='Bash';       i=@{command='curl -s -X POST http://127.0.0.1:27701/host -d ''{"world":"Lunar","requireIsolatedSavePath":false}'''};  want='deny' }
    @{ n='savepath WITHOUT force (ok)';    e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe call --target c1 --as k3 --path /savepath --body ''{"path":"D:/x"}'''};              want='silent' }
    @{ n='host WITH isolation kept (ok)';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe call --target h1 --as k3 --path /host --body ''{"world":"Lunar","requireIsolatedSavePath":true}'''}; want='silent' }
    @{ n='force on a non-savepath route';  e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe call --target c1 --as k3 --path /input/gameplay --body ''{"force":true}'''};         want='silent' }

    # --- RETIRED BRANCHES. These must now be silent; the binary does the job. ---
    # 'session': testrig.exe asserts the lock centrally and exits 5 naming --as.
    @{ n='RETIRED: mutating, no --as';     e='PreToolUse'; t='Bash';       i=@{command='TestRig/testrig.exe start --target server --load W --map Mars'};                                          want='silent' }
    @{ n='RETIRED: ps1 mutating, no -As';  e='PreToolUse'; t='Bash';       i=@{command='pwsh -NoProfile -File TestRig/testrig.ps1 deploy SprayPaintPlus'};                                       want='silent' }
    # 'author': rig-stop-hook.ps1 watches TestRig/src/ and names dotnet test. The two
    # PowerShell paths below no longer exist on disk; the hook never stats a path, so
    # they still prove the branch is gone rather than merely unreachable.
    @{ n='RETIRED: edit rig-lock.ps1';     e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\rig-lock.ps1'};                                              want='silent' }
    @{ n='RETIRED: edit lib/client.ps1';   e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\lib\client.ps1'};                                            want='silent' }
    @{ n='RETIRED: edit CliApp.cs';        e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\src\TestRig.Cli\CliApp.cs'};                                 want='silent' }
    @{ n='RETIRED: edit a rig doc';        e='PostToolUse'; t='Edit';      i=@{file_path='c:\Source\SixFive7\StationeersPlus\TestRig\MANUAL.md'};                                                 want='silent' }
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
