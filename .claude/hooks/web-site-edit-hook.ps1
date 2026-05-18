# web-site-edit-hook.ps1
# Fires on Edit|Write to Web/site/**. Web/site/ is build output, not source.
# Direct edits there get clobbered on the next build and create silent drift
# between the committed site and what the build would actually produce.
#
# Injects a reminder pointing the agent at the right source location.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Web site edit] You are editing Web/site/, which is BUILD OUTPUT, not source.

The next time .\tools\publish-web\build.ps1 runs, this edit will be overwritten because mkdocs --clean wipes Web/site/ before writing the new build. If you are trying to change what appears on the site, edit the corresponding source file instead:

- Landing page or section intros -> Web/content/**
- Research pages -> Research/**
- Tool HTML files -> tools/**
- Theme or template -> Web/overrides/**, Web/mkdocs.yml

If you really need to edit Web/site/ directly (for example, to test a one-shot fix), be intentional about it and accept that the change is throwaway. Then run .\tools\publish-web\deploy.ps1 to push the manual edit to SMB.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
