<#
.SYNOPSIS
    Guard against CLAUDE.md / tool-count contract drift.

.DESCRIPTION
    Counts MCP tools discovered by the same reflection rules ToolRegistry uses
    ([Description] on a public static string method in dnSpy.MCP.Tools.*) and
    cross-checks against the count advertised in CLAUDE.md. Fails (exit 1) on
    mismatch so CI catches a tool added in code but not documented (or vice
    versa) — the same drift that once let load_assembly/close_assembly be
    advertised but unimplemented.

    Usage in CI:
        pwsh scripts/verify-tool-count.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$toolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP/Tools'
$claudeMd = Join-Path $RepoRoot 'CLAUDE.md'

if (-not (Test-Path $toolsDir)) { throw "Tools dir not found: $toolsDir" }
if (-not (Test-Path $claudeMd)) { throw "CLAUDE.md not found: $claudeMd" }

# A tool = a [Description(...)] attribute whose next non-blank line declares a
# `public static string Method(`. This mirrors ToolRegistry.DiscoverTools().
$toolNames = New-Object System.Collections.Generic.List[string]
Get-ChildItem -Path $toolsDir -Filter *.cs | ForEach-Object {
    $lines = Get-Content -LiteralPath $_.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -notmatch '^\s*\[Description\(') { continue }
        $j = $i + 1
        while ($j -lt $lines.Count -and $lines[$j].Trim() -eq '') { $j++ }
        if ($j -lt $lines.Count -and $lines[$j] -match 'public static string (\w+)\s*\(') {
            $method = $Matches[1]
            # snake_case conversion mirroring ToolRegistry.ToSnakeCase exactly:
            # insert '_' before every uppercase letter at index > 0.
            $sb = New-Object System.Text.StringBuilder($method.Length + 10)
            for ($k = 0; $k -lt $method.Length; $k++) {
                $ch = $method[$k]
                if ($k -gt 0 -and [char]::IsUpper($ch)) { [void]$sb.Append('_') }
                [void]$sb.Append([char]::ToLowerInvariant($ch))
            }
            $toolNames.Add($sb.ToString())
        }
    }
}

# Count advertised tools in CLAUDE.md header ("## Available MCP Tools (NN)")
$claudeText = Get-Content -Raw -LiteralPath $claudeMd
$advertised = $null
if ($claudeText -match '## Available MCP Tools \((\d+)\)') {
    $advertised = [int]$Matches[1]
}

$actual = $toolNames.Count
Write-Host "Discovered tools ($actual):"
$toolNames | Sort-Object | ForEach-Object { Write-Host "  - $_" }
Write-Host ""
Write-Host "CLAUDE.md advertises: $(if ($null -eq $advertised) { '(header not found)' } else { $advertised })"

$failed = $false
if ($null -ne $advertised -and $advertised -ne $actual) {
    Write-Error "MISMATCH: CLAUDE.md advertises $advertised tools but $actual were discovered."
    $failed = $true
}

if ($failed) { exit 1 }
Write-Host ""
Write-Host "OK: tool counts consistent ($actual)."
exit 0
