# web-publish-hook.ps1
# Fires on Edit|Write to a publishable source path:
#   - Research/**
#   - tools/**
#   - Web/content/**
#   - Web/mkdocs.yml, Web/overrides/**
# Injects a reminder that Web/site/ needs to be rebuilt and the SMB share
# re-deployed before the turn ends. Does not actually run the build; the
# Stop hook is the enforcement point. This hook is the early signal.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Web publish] You changed a file that feeds the public documentation site at https://stationeers.huisman.io.

Before ending the turn, run:

    .\tools\publish-web\build.ps1
    .\tools\publish-web\deploy.ps1

Build regenerates Web/site/ from Research/, tools/, and Web/content/. Deploy mirrors Web/site/ to the SMB share at \\10.20.30.250\nvme-system\containers\stationeers\. The SMB share is a strict downstream copy of Web/site/; never hand-edit it.

If multiple source files are changing this turn, you only need to run build/deploy once at the end -- not after every edit.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
