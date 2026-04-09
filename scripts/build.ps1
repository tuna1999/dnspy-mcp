# Build and deploy dnSpy MCP extension
# Supports standalone (src/dnSpy.MCP/) layout
# Usage: .\build.ps1 [-Clean] [-Deploy] [-DeployDir <path>] [-Configuration <Debug|Release>]
#
# Examples:
#   .\build.ps1                              # Build Release only (default)
#   .\build.ps1 -Clean                      # Clean + build Release
#   .\build.ps1 -Deploy                     # Build + deploy to build\Extensions\
#   .\build.ps1 -Clean -Deploy              # Clean + build + deploy
#   .\build.ps1 -Configuration Debug         # Build Debug
#   .\build.ps1 -DeployDir "D:\tools\dnSpy\Extensions"  # Deploy to dnSpy folder

param(
    [switch]$Clean,                # Clean build artifacts before building
    [switch]$Deploy,               # Deploy extension after building
    [string]$DeployDir = "",    # Custom deploy path (default: build\Extensions\)
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"  # Build configuration (default: Release)
)

$ErrorActionPreference = "Stop"
$WorkspaceRoot = Split-Path -Parent (Split-Path $MyInvocation.MyCommand.Path -Parent)

# Use custom deploy dir or default
if ([string]::IsNullOrWhiteSpace($DeployDir)) {
    $DeployDir = Join-Path $WorkspaceRoot "build\Extensions"
    $IsLocal = $true
} else {
    $IsLocal = $false
    $DeployDir = $DeployDir.TrimEnd('\', '/')
}

# Project paths
$StandaloneDir = Join-Path $WorkspaceRoot "src\dnSpy.MCP"
$ProjectDir = $StandaloneDir
$ProjectFile = Join-Path $ProjectDir "dnSpy.MCP.csproj"
$BinDir = Join-Path $ProjectDir "bin\$Configuration\net8.0-windows"
$ObjDir = Join-Path $ProjectDir "obj"

Write-Host "=== dnSpy MCP ===" -ForegroundColor Cyan
Write-Host "  Project: $ProjectDir"
Write-Host "  Config:  $Configuration"
if ($Deploy) {
    Write-Host "  Deploy:  $DeployDir"
}
Write-Host ""

# Step 0: Clean
if ($Clean) {
    Write-Host "[Clean] Cleaning..." -ForegroundColor Yellow

    # Clean project obj/
    if (Test-Path $ObjDir) {
        Get-ChildItem $ObjDir -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    }
    # Clean project bin/<Config>/
    if (Test-Path $BinDir) {
        Get-ChildItem $BinDir -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse -ErrorAction SilentlyContinue
    }
    Write-Host "  Done." -ForegroundColor DarkGray
}

# Step 1: Build
$prefix = if ($Clean) { "[Build]" } else { "[Build]" }
Write-Host "$prefix Building..." -ForegroundColor Yellow
if (-not (Test-Path $ProjectFile)) {
    Write-Host "  ERROR: Project not found: $ProjectFile" -ForegroundColor Red
    exit 1
}

$buildOutput = & dotnet build $ProjectFile -c $Configuration 2>&1
$buildText = $buildOutput | Out-String

if ($buildText -notmatch "Build succeeded") {
    Write-Host "[BUILD FAILED]" -ForegroundColor Red
    $buildOutput | Where-Object { $_ -match "error CS" } | ForEach-Object {
        Write-Host "  $_" -ForegroundColor Red
    }
    exit 1
}

$errorCount = if ($buildText -match "(\d+) Error\(s\)") { $Matches[1] } else { "?" }
Write-Host "  OK ($errorCount errors)" -ForegroundColor Green

# Step 2: Deploy
if ($Deploy) {
    Write-Host "[Deploy] Copying..." -ForegroundColor Yellow

    if (-not (Test-Path $BinDir)) {
        Write-Host "  ERROR: Bin directory not found: $BinDir" -ForegroundColor Red
        exit 1
    }
    if (-not (Test-Path $DeployDir)) {
        New-Item -ItemType Directory -Path $DeployDir -Force | Out-Null
    }

    # Clean extension files in deploy dir
    $oldExt = Get-ChildItem "$DeployDir\dnSpy.MCP.x.*" -ErrorAction SilentlyContinue
    foreach ($f in $oldExt) {
        Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
    }
    # Clean old MCP/ASP.NET Core DLLs (local deploy only)
    if ($IsLocal) {
        $oldPatterns = @(
            "ModelContextProtocol*.dll", "Microsoft.AspNetCore*.dll",
            "Microsoft.AspNetCore.App.*", "Microsoft.Extensions.*.dll",
            "aspnetcorev2_inprocess.dll"
        )
        foreach ($pattern in $oldPatterns) {
            $files = Get-ChildItem $DeployDir -Filter $pattern -ErrorAction SilentlyContinue
            foreach ($f in $files) {
                Remove-Item $f.FullName -Force -ErrorAction SilentlyContinue
            }
        }
    }

    # Copy extension files
    Copy-Item "$BinDir\dnSpy.MCP.x.dll" $DeployDir -Force
    Copy-Item "$BinDir\dnSpy.MCP.x.pdb" $DeployDir -Force -ErrorAction SilentlyContinue
    Copy-Item "$BinDir\dnSpy.MCP.x.deps.json" $DeployDir -Force -ErrorAction SilentlyContinue

    # Verify
    $dll = Get-Item (Join-Path $DeployDir "dnSpy.MCP.x.dll") -ErrorAction SilentlyContinue
    if ($dll) {
        Write-Host "  Done ($([Math]::Round($dll.Length/1KB, 1)) KB)" -ForegroundColor Green
    }
}

# Summary
Write-Host ""
Write-Host "[Ready]" -ForegroundColor Yellow
if ($Deploy) {
    Write-Host "  Deploy:  $DeployDir\dnSpy.MCP.x.dll"
    if ($IsLocal) {
        Write-Host "  Log:     $WorkspaceRoot\build\Extensions\mcp-server.log"
    }
}
Write-Host "  Port:    5150"
Write-Host ""
Write-Host "Done!" -ForegroundColor Green
