# build.ps1
# Builds the public documentation site at Web/site/ from:
#   - Web/content/         (landing page, section intros)
#   - Research/            (knowledge-base markdown, excluding agent-internal meta-docs)
#   - tools/<name>/        (each folder containing index.html becomes a published tool)
#
# Usage:
#   .\tools\publish-web\build.ps1            # full rebuild
#   .\tools\publish-web\build.ps1 -KeepStage # keep Web/_staging/ for debugging
#
# Does not touch the SMB share. See deploy.ps1 for that.

[CmdletBinding()]
param(
    [switch]$KeepStage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

# Resolve repo root relative to this script (tools/publish-web/build.ps1 -> repo root is two levels up).
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot  = Resolve-Path (Join-Path $scriptDir '..\..')
$webDir    = Join-Path $repoRoot 'Web'
$contentDir = Join-Path $webDir  'content'
$stagingDir = Join-Path $webDir  '_staging'
$siteDir   = Join-Path $webDir   'site'
$researchSrc = Join-Path $repoRoot 'Research'
$toolsSrc  = Join-Path $repoRoot 'tools'
$mkdocsCfg = Join-Path $webDir 'mkdocs.yml'

Write-Host "[publish-web] Repo root: $repoRoot"
Write-Host "[publish-web] Staging:   $stagingDir"
Write-Host "[publish-web] Output:    $siteDir"

# --- 1. Wipe staging --------------------------------------------------------
if (Test-Path $stagingDir) {
    Write-Host "[publish-web] Wiping previous staging..."
    Remove-Item -Recurse -Force $stagingDir
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

# --- 2. Copy Web/content/* into staging -------------------------------------
Write-Host "[publish-web] Copying Web/content/ -> staging..."
$rcArgs = @($contentDir, $stagingDir, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
& robocopy @rcArgs | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "robocopy of Web/content failed (exit $LASTEXITCODE)"
}

# --- 3. Copy Research/ into staging/research/, excluding agent-internal docs -
$researchStage = Join-Path $stagingDir 'research'
if (-not (Test-Path $researchStage)) {
    New-Item -ItemType Directory -Path $researchStage | Out-Null
}
Write-Host "[publish-web] Copying Research/ -> staging/research/ (excluding INDEX.md, CLAUDE.md, WORKFLOW.md)..."
$researchExclude = @('INDEX.md', 'CLAUDE.md', 'WORKFLOW.md')
$rcArgs = @($researchSrc, $researchStage, '/E', '/XF') + $researchExclude + @('/NFL', '/NDL', '/NJH', '/NJS', '/NP')
& robocopy @rcArgs | Out-Null
if ($LASTEXITCODE -ge 8) {
    throw "robocopy of Research failed (exit $LASTEXITCODE)"
}

# --- 4. Copy tools/<name>/index.html (and same-folder siblings) into staging/tools/<name>/
Write-Host "[publish-web] Scanning tools/ for index.html..."
$toolsStage = Join-Path $stagingDir 'tools'
if (-not (Test-Path $toolsStage)) {
    New-Item -ItemType Directory -Path $toolsStage | Out-Null
}

$toolDirs = @(Get-ChildItem -Path $toolsSrc -Directory | Where-Object {
    Test-Path (Join-Path $_.FullName 'index.html')
})

foreach ($t in $toolDirs) {
    $dst = Join-Path $toolsStage $t.Name
    Write-Host "[publish-web]   tools/$($t.Name)/ -> staging/tools/$($t.Name)/"
    $rcArgs = @($t.FullName, $dst, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP')
    & robocopy @rcArgs | Out-Null
    if ($LASTEXITCODE -ge 8) {
        throw "robocopy of tools/$($t.Name) failed (exit $LASTEXITCODE)"
    }
}

if ($toolDirs.Count -eq 0) {
    Write-Warning "[publish-web] No tools found under tools/*/index.html"
}

# Surface each discovered tool in two places so it isn't orphaned:
#   1. A "Available tools" list appended to staging/tools/index.md, so a
#      visitor landing on /tools/ sees a clickable list in the body.
#   2. A staging/tools/.pages file picked up by the awesome-pages plugin, so
#      each tool's index.html appears in the sidebar nav. MkDocs doesn't
#      auto-discover non-markdown files for nav; without this the sidebar on
#      the tools page would show only the overview.
$titleMap = @{
    'phase-diagram' = 'Phase Diagram'
}
function Get-ToolTitle($name) {
    if ($titleMap.ContainsKey($name)) { return $titleMap[$name] }
    return ($name -split '-' | ForEach-Object { if ($_) { $_.Substring(0,1).ToUpper() + $_.Substring(1) } }) -join ' '
}

$sortedTools = @($toolDirs | Sort-Object Name)

$toolsIndexMd = Join-Path $toolsStage 'index.md'
if (Test-Path $toolsIndexMd) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine()
    [void]$sb.AppendLine('## Available tools')
    [void]$sb.AppendLine()
    foreach ($t in $sortedTools) {
        $title = Get-ToolTitle $t.Name
        [void]$sb.AppendLine("- [$title](./$($t.Name)/)")
    }
    Add-Content -Path $toolsIndexMd -Value $sb.ToString() -Encoding UTF8
    Write-Host "[publish-web] Appended $($sortedTools.Count) tool link(s) to tools/index.md"
}

if ($sortedTools.Count -gt 0) {
    # awesome-pages treats raw URLs in .pages as docs-root-relative, not
    # current-folder-relative. So the path here is "tools/<name>/index.html",
    # not "<name>/index.html". With the wrong shape, MkDocs emits "../<name>/"
    # links that resolve one level too high.
    $pagesYaml = New-Object System.Text.StringBuilder
    [void]$pagesYaml.AppendLine('nav:')
    [void]$pagesYaml.AppendLine('  - index.md')
    foreach ($t in $sortedTools) {
        $title = Get-ToolTitle $t.Name
        [void]$pagesYaml.AppendLine("  - '$title': tools/$($t.Name)/index.html")
    }
    $pagesPath = Join-Path $toolsStage '.pages'
    Set-Content -Path $pagesPath -Value $pagesYaml.ToString() -Encoding UTF8 -NoNewline
    Write-Host "[publish-web] Wrote staging/tools/.pages with $($sortedTools.Count) tool entry/entries"
}

# --- 5. Run mkdocs ----------------------------------------------------------
Write-Host "[publish-web] Running mkdocs build..."
Push-Location $webDir
try {
    # Not using --strict: Research pages cite mod source files via relative
    # paths like ../../Mods/Foo/RESEARCH.md, which are valid in the repo but
    # don't resolve on the public site. Treating those as build failures would
    # block every publish; treating them as informational is the right call.
    & python -m mkdocs build --clean --config-file $mkdocsCfg
    if ($LASTEXITCODE -ne 0) {
        throw "mkdocs build failed (exit $LASTEXITCODE)"
    }
} finally {
    Pop-Location
}

# --- 6. Tidy up -------------------------------------------------------------
if (-not $KeepStage) {
    Write-Host "[publish-web] Removing staging dir (pass -KeepStage to retain)..."
    Remove-Item -Recurse -Force $stagingDir
}

$pageCount = (Get-ChildItem -Path $siteDir -Recurse -Filter '*.html' | Measure-Object).Count
Write-Host "[publish-web] Done. $pageCount HTML pages in $siteDir"
Write-Host "[publish-web] Next: run .\tools\publish-web\deploy.ps1 to mirror to SMB."
exit 0
