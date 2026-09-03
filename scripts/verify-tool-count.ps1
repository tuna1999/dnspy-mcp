<#
.SYNOPSIS
    Guard against CLAUDE.md / tool-count contract drift.

.DESCRIPTION
    Counts MCP tools discovered by the same reflection rules ToolRegistry uses
    ([Description] on a public string method in dnSpy.MCP.Tools*) and
    cross-checks against the count advertised in CLAUDE.md. Fails (exit 1) on
    mismatch so CI catches a tool added in code but not documented (or vice
    versa) — the same drift that once let load_assembly/close_assembly be
    advertised but unimplemented.

    Scans BOTH Core tools (instance methods on sealed classes with McpContext ctor)
    and Extension-only tools (static methods on static classes, e.g. TreeViewTools).

    Usage in CI:
        pwsh scripts/verify-tool-count.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$coreToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP.Core/Tools'
$extensionToolsDir = Join-Path $RepoRoot 'src/dnSpy.MCP/Tools'
$claudeMd = Join-Path $RepoRoot 'CLAUDE.md'

if (-not (Test-Path $coreToolsDir)) { throw "Core tools dir not found: $coreToolsDir" }
if (-not (Test-Path $extensionToolsDir)) { throw "Extension tools dir not found: $extensionToolsDir" }
if (-not (Test-Path $claudeMd)) { throw "CLAUDE.md not found: $claudeMd" }

# A tool = a [Description(...)] attribute whose next non-blank line declares a
# `public string Method(` (instance, Core) OR `public static string Method(` (static, Extension).
# This mirrors ToolRegistry.DiscoverTools() which accepts both BindingFlags.Instance
# and BindingFlags.Static depending on whether the class has ctor(McpContext).
$toolNames = New-Object System.Collections.Generic.List[string]

# Regex matches BOTH `public string X(` and `public static string X(`.
# The (?:static\s+)? group makes 'static' optional.
$methodRegex = 'public\s+(?:static\s+)?string\s+(\w+)\s*\('

function Scan-ToolDir {
    param([string]$DirPath)
    Get-ChildItem -Path $DirPath -Filter *.cs | ForEach-Object {
        $lines = Get-Content -LiteralPath $_.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch '^\s*\[Description\(') { continue }
            $j = $i + 1
            while ($j -lt $lines.Count -and $lines[$j].Trim() -eq '') { $j++ }
            if ($j -lt $lines.Count -and $lines[$j] -match $methodRegex) {
                $method = $Matches[1]
                # snake_case conversion mirroring ToolRegistry.ToSnakeCase exactly:
                # '_' before an uppercase at a word boundary — after lower/digit, or when the
                # last uppercase of an acronym is followed by a lowercase. Trailing acronyms
                # stay clamped: "RefreshUI" -> "refresh_ui", not "refresh_u_i".
                $sb = New-Object System.Text.StringBuilder($method.Length + 10)
                for ($k = 0; $k -lt $method.Length; $k++) {
                    $ch = $method[$k]
                    if ($k -gt 0 -and [char]::IsUpper($ch)) {
                        $prev = $method[$k - 1]
                        $next = if ($k + 1 -lt $method.Length) { $method[$k + 1] } else { [char]0 }
                        $isBoundary = ([char]::IsLower($prev) -or [char]::IsDigit($prev)) -or
                                      ([char]::IsUpper($prev) -and [char]::IsLower($next))
                        if ($isBoundary) { [void]$sb.Append('_') }
                    }
                    [void]$sb.Append([char]::ToLowerInvariant($ch))
                }
                $toolNames.Add($sb.ToString())
            }
        }
    }
}

Scan-ToolDir $coreToolsDir
Scan-ToolDir $extensionToolsDir

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
