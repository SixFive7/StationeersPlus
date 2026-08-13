# todo-content-hook.ps1
# Fires after Read / Edit / Write on any TODO.md or PLAYTEST.md file (matched
# via the `if` field in .claude/settings.json). Injects the open-issues-only
# reminder so neither file ever accumulates completed items.
#
# PLAYTEST.md carries the identical "remove, do not tick" rule and the same
# plain-bullet format, across 13 files against TODO.md's 16, and had no hook
# coverage of any kind. The two differ only in what an entry MEANS -- TODO.md is
# work not yet done, PLAYTEST.md is code already written and awaiting an in-game
# run -- so one message covers both with one extra paragraph.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Work-tracking hygiene reminder] You just touched a TODO.md or a PLAYTEST.md. Both track OPEN ITEMS ONLY. Entries are plain bullets (`- text`), not `- [ ]` checkboxes; when an item is finished, REMOVE it rather than ticking it off, and do not add a completed-work section. Completed work lives in git history. If you find a `- [ ]` or `- [x]` entry while editing, drop the brackets (open item) or remove the line entirely (done item).

The two files split by what an entry means. TODO.md is work not yet done: design, research, audits, features, refactors, bugs to investigate. PLAYTEST.md is code already written whose behaviour can only be confirmed by running the game, and an entry carries everything a tester needs (what changed, single-player vs multiplayer vs dedicated server, the save to set up, the exact steps, what to watch, the expected result). Move an item across the moment the implementation lands. Check the existing entries before adding: extend a pending test rather than duplicating it. Remove a playtest when a run confirms it, when a run shows it broken (and open a TODO.md item for the fix), or when the player says it is done.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
