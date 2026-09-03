<#
.SYNOPSIS
    Sync dnSpy dependency DLLs from a local dnSpy installation into deps/.

.DESCRIPTION
    Copies the required dnSpy contract + decompiler DLLs from a dnSpy install
    into the repo's deps/ folder. Idempotent — skips files that already match
    by size + last-write-time.

    Both win32 and win64 dnSpy installs ship identical AnyCPU managed DLLs;
    this script defaults to win64 because that's the user's primary install.

.PARAMETER DnSpyBin
    Path to the dnSpy bin/ folder containing the DLLs.
    Default: D:\ProgramFiles\StandaloneTools\RETools\dnSpy\win64\bin

.EXAMPLE
    pwsh scripts/sync-deps.ps1
    pwsh scripts/sync-deps.ps1 -DnSpyBin "C:\dnSpy\bin"
#>
[CmdletBinding()]
param(
    [string]$DnSpyBin = "D:\ProgramFiles\StandaloneTools\RETools\dnSpy\win64\bin"
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$depsDir = Join-Path $repoRoot 'deps'

if (-not (Test-Path $DnSpyBin)) {
    throw "dnSpy bin folder not found: $DnSpyBin. Pass -DnSpyBin <path> to override."
}
if (-not (Test-Path $depsDir)) {
    New-Item -ItemType Directory -Path $depsDir | Out-Null
}

# DLLs required by Core + Extension + Headless.
# Core/Extension already use the first 4; Headless adds the Decompiler set.
$requiredDlls = @(
    'dnSpy.Contracts.DnSpy.dll',
    'dnSpy.Contracts.Logic.dll',
    'dnlib.dll',
    'ICSharpCode.Decompiler.dll',
    # Headless-only:
    'dnSpy.Decompiler.dll',
    'dnSpy.Decompiler.ILSpy.Core.dll',
    'ICSharpCode.NRefactory.dll',
    'ICSharpCode.NRefactory.CSharp.dll'
)

$copied = 0
$skipped = 0
foreach ($dll in $requiredDlls) {
    $src = Join-Path $DnSpyBin $dll
    $dst = Join-Path $depsDir $dll

    if (-not (Test-Path $src)) {
        Write-Warning "Missing in dnSpy install: $dll (skipped)"
        continue
    }

    # Idempotent: skip if exists with same size + last-write-time
    if (Test-Path $dst) {
        $srcItem = Get-Item $src
        $dstItem = Get-Item $dst
        if ($srcItem.Length -eq $dstItem.Length -and
            $srcItem.LastWriteTimeUtc -eq $dstItem.LastWriteTimeUtc) {
            $skipped++
            continue
        }
    }

    Copy-Item $src $dst -Force
    $copied++
    Write-Host "  Copied: $dll"
}

Write-Host ""
Write-Host "Sync complete: $copied copied, $skipped up-to-date, $($requiredDlls.Count) total expected."
Write-Host "deps/ contents:"
Get-ChildItem $depsDir -Filter *.dll | ForEach-Object { Write-Host "  $($_.Name) ($($_.Length) bytes)" }
