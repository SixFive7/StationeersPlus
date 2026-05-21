# mod-content-hook.ps1
# Fires after Read / Edit / Write on mod-facing README.md or About.xml files
# (matched via the `if` field in .claude/settings.json). Injects a reminder
# that the layout rules for these files live in Mods/Template/LAYOUT.md so
# repo-root CLAUDE.md can stay short.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$message = @'
[Mod content reminder] You just touched a mod README.md, CHANGELOG.md, or About.xml. Layout rules live in Mods/Template/LAYOUT.md (single source of truth); read it before editing unless you already have this conversation.

Changelog is split across two files: the full version history lives in the per-mod CHANGELOG.md (newest entry on top, prepended every release); the About.xml <ChangeLog> carries every change since the mod's last ACTUAL Workshop release (normally just the current version; several versions if publishes were skipped; the full history if the mod has never been published, like InspectorPlus). Steam records one change note per publish and keeps earlier ones on the Change Notes tab, so the body only spans the gap since the last real publish; reset it to the unpublished span after each publish.

Both files use ONE entry format: a "v<X.Y.Z>: summary" heading (## prefix in CHANGELOG.md only) plus "- " bullets that each end in a period. Plain ASCII, no em/en dashes or "--". Each version's CHANGELOG.md entry matches its block in the About.xml <ChangeLog> (which may stack several unpublished blocks, newest first); same wording per block (About.xml is plain text and XML-escapes < and >; CHANGELOG.md is Markdown and backticks tag-like tokens).

About.xml size caps enforced by StationeersLaunchPad at publish time (Steam.ValidateForWorkshop blocks the Workshop upload if exceeded, no truncation): <Name> 128, <Description> 8000, <ChangeLog> 8000 characters, About/thumb.png 1 MB. <InGameDescription> ~1450 chars is a visual settings-panel limit only, not a publish blocker. ModID/Author/Version/Tags have no cap.

LAYOUT.md also covers README / Description / InGameDescription sync, the tagline rule, element order and XML-escape safety, Reporting Issues placement, the InGameDescription <line-height=40%> wrap, and preview image 16:9 dimensions.
'@

$payload = @{
    hookSpecificOutput = @{
        hookEventName     = 'PostToolUse'
        additionalContext = $message
    }
} | ConvertTo-Json -Depth 5 -Compress

Write-Output $payload
exit 0
