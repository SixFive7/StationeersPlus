# playwright-config-hook.ps1
# Fires BEFORE Read / Edit / Write on the Playwright MCP configuration trio
# (matched via the `if` field in .claude/settings.json). Injects the full
# architectural context and rules so the agent has them before interacting
# with the file, so edits are informed, not retroactively corrected, and
# readers know the file is part of a trio before consuming just one piece.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Force UTF-8 stdout so multi-byte chars (em-dash etc.) survive the pipe to
# Claude Code's JSON parser. Without this, additionalContext is silently dropped.
$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Single-quoted here-string: no escape processing, so backticks in the body
# (e.g. `npx`, `pdf`) render literally instead of being interpreted as
# PowerShell escape sequences.
$message = @'
[Playwright MCP config reminder] You are about to touch one of the three files that make up the Playwright MCP configuration.

Architecture: three files, clear separation of concerns:
  1. .mcp.json                     Bootstraps the MCP server for Claude Code. Contains only `--config` pointing to #2 plus the single CLI-only flag `--output-mode` (which is not in the config schema). Keep this file minimal.
  2. playwright/config.json        Every config-schema setting we deviate from upstream defaults on. Canonical source of Playwright-level configuration. Absent keys = upstream defaults.
  3. playwright/README.md          SINGLE SOURCE OF TRUTH for rules, conventions, and the full reference table (every setting with its location, function, value, and motivation, including settings we left at default, which are documented so that future changes know the intentional reasoning).

Rules live exclusively in playwright/README.md under the "Rules" section at the top. Read it before editing if you have not already this conversation. It covers:
  - HAR archive rename convention (recordHar.path is static; rename playwright/output/network.har to playwright/output/har/network-{timestamp}-{task}.har if the trace is worth keeping).
  - Screenshot filename discipline: explicit `filename` on `browser_take_screenshot` resolves against CWD (repo root), NOT outputDir. Prepend `playwright/output/` (e.g. `playwright/output/foo.png`) to land artifacts in the output dir. Auto-naming (omitting `filename`) does honour outputDir.
  - Single-Playwright-instance-per-repo rule (Chrome SingletonLock enforces this structurally).
  - Profile reset procedure (delete playwright/profile/ to force fresh logins).
  - Debugging pointers (playwright/output/session-{timestamp}/session.md + playwright/output/network.har; Markdown trace, not JSONL despite older notes).
  - Upgrade procedure (diff `npx @playwright/mcp@latest --help` against the table; new flags get new rows).
  - Design rationale for capability gating: e.g. browser_pdf_save is not exposed because its HTML-to-PDF render is a screenshot, not a server-served file. If you feel the urge to enable `pdf`, read row #56 first.

Drift rule: when you change a VALUE in playwright/config.json or .mcp.json, update the matching row in playwright/README.md (Value column, and Motivation if the reasoning shifts) in the SAME commit. Motivation only lives in the markdown; it cannot recover itself.

This hook is defined in .claude/settings.json and runs .claude/hooks/playwright-config-hook.ps1. To change the hook behaviour or matcher, edit those files.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        permissionDecision = 'allow'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
