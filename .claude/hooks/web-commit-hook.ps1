# web-commit-hook.ps1
# Fires PreToolUse on Bash(*git commit*). Checks the currently staged paths.
# If any source path (Research/, tools/, Web/content/, Web/mkdocs.yml,
# Web/overrides/) is staged but no Web/site/ path is staged, warns the agent
# that the committed site will diverge from source.
#
# Skips `Research:` and `Publish:` autonomous-lane commits -- those have
# dedicated enforcer hooks (research-commit-hook.ps1, site-commit-hook.ps1)
# that block mixed staging by design. Warning about "no Web/site/ staged" on
# a Research: commit would be misleading because the autonomous Publish:
# commit that follows handles the rebuild in the same turn.
#
# Does NOT block the commit. Just injects a reminder.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Skip autonomous-lane commits. Their scope is enforced by the dedicated hooks.
$stdin = [Console]::In.ReadToEnd()
if ($stdin) {
    try {
        $payload = $stdin | ConvertFrom-Json
        $cmd = $payload.tool_input.command
        if ($cmd -and ($cmd -match '\bResearch:' -or $cmd -match '\bPublish:')) {
            exit 0
        }
    } catch {
        # Fall through to the normal check if we can't parse stdin.
    }
}

try {
    $staged = & git diff --cached --name-only 2>$null
    if ($LASTEXITCODE -ne 0) {
        # Not in a git repo or git unavailable -- nothing to check.
        exit 0
    }
} catch {
    exit 0
}

if (-not $staged) {
    exit 0
}

$sourcePatterns = @(
    '^Research/',
    '^tools/',
    '^Web/content/',
    '^Web/overrides/',
    '^Web/mkdocs\.yml$',
    '^Web/requirements\.txt$'
)
$sitePattern = '^Web/site/'

$sourceTouched = @()
$siteTouched   = $false

foreach ($path in $staged) {
    foreach ($p in $sourcePatterns) {
        if ($path -match $p) {
            $sourceTouched += $path
            break
        }
    }
    if ($path -match $sitePattern) {
        $siteTouched = $true
    }
}

if ($sourceTouched.Count -eq 0) {
    # No publishable source changes in this commit; nothing to warn about.
    exit 0
}

if ($siteTouched) {
    # Both source and site are staged together; that's the correct shape.
    exit 0
}

$sourceList = ($sourceTouched | Select-Object -First 8) -join "`n  - "
$more = if ($sourceTouched.Count -gt 8) { "`n  (+$($sourceTouched.Count - 8) more)" } else { '' }

$message = @"
[Web commit] This commit touches publishable source but no files under Web/site/ are staged. Committing will leave the committed site out of sync with its source.

Source paths in this commit:
  - $sourceList$more

After this commit lands, run the autonomous publish lane in the same turn:

    .\tools\publish-web\build.ps1
    git add Web/site/
    git commit -m "Publish: <summary>"   # autonomous; site-commit-hook enforces Web/site/-only scope
    .\tools\publish-web\deploy.ps1

The Publish: commit is the second autonomous-commit lane (see CLAUDE.md "Workflow: site publish commits are autonomous"). Do not leave Web/site/ lagging git HEAD across turn boundaries -- the SMB mirror should always match what's in git.

(This is a reminder, not a block. Proceed if you have a reason.)
"@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
