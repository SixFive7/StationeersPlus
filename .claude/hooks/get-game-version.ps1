# get-game-version.ps1
# Shared helper used by research-hook-*.ps1.
# Resolves the current Stationeers Assembly-CSharp.dll version string,
# or writes an error to stderr and exits non-zero if the version cannot be
# determined.
#
# Resolution order:
#   1. $env:StationeersPath (preferred; set by developer's environment).
#   2. The <StationeersPath> element in the repo-root Directory.Build.props
#      (gitignored per-developer file). The committed .template is NOT read
#      because it carries a placeholder value.
#
# Output on success: four-part version string on stdout (e.g. "0.2.6228.27061").
# Output on failure: human-readable message on stderr, exit code 1.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-StationeersPath {
    if ($env:StationeersPath -and $env:StationeersPath.Trim().Length -gt 0) {
        return $env:StationeersPath
    }

    # Repo root is the hook's working directory (Claude Code runs hooks from
    # the project root). Directory.Build.props sits there when the developer
    # has copied the .template.
    $propsPath = Join-Path -Path (Get-Location) -ChildPath 'Directory.Build.props'
    if (-not (Test-Path -LiteralPath $propsPath)) {
        throw "Cannot resolve game version: `$env:StationeersPath is unset and Directory.Build.props does not exist at repo root. Copy Directory.Build.props.template to Directory.Build.props and fill in <StationeersPath>."
    }

    try {
        [xml]$props = Get-Content -LiteralPath $propsPath -Raw
    } catch {
        throw "Cannot resolve game version: failed to parse Directory.Build.props as XML ($($_.Exception.Message))."
    }

    $node = $props.SelectSingleNode('//StationeersPath')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "Cannot resolve game version: Directory.Build.props has no <StationeersPath> element."
    }

    return $node.InnerText.Trim()
}

function Get-GameVersionString {
    $stationeersPath = Get-StationeersPath
    $dllPath = Join-Path -Path $stationeersPath -ChildPath 'rocketstation_Data\Managed\Assembly-CSharp.dll'

    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Cannot resolve game version: Assembly-CSharp.dll not found at '$dllPath'."
    }

    try {
        $asmName = [Reflection.AssemblyName]::GetAssemblyName($dllPath)
    } catch {
        throw "Cannot resolve game version: GetAssemblyName threw on '$dllPath' ($($_.Exception.Message))."
    }

    return $asmName.Version.ToString()
}
