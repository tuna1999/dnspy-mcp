# Build and deploy dnSpy MCP (Core + Extension + Headless + Tests)
# Usage: .\build.ps1 -DnSpyPath <path> [-Clean] [-Deploy] [-DeployDir <path>] [-Configuration <Debug|Release>] [-PublishHeadless]
#
# Required:
#   -DnSpyPath  Path to dnSpy installation folder (must contain dnSpy.exe in its bin/ folder)
#
# Examples:
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy"                        # Build solution (Release)
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Clean                 # Clean + build solution
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy                # Build + deploy extension to dnSpy\bin\Extensions
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -PublishHeadless       # Also publish headless to publish/headless/
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Configuration Debug   # Debug configuration

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$DnSpyPath,           # Path to dnSpy installation (e.g. D:\tools\dnSpy)
    [switch]$Clean,               # Clean build artifacts before building
    [switch]$Deploy,              # Deploy extension after building
    [string]$DeployDir = "",      # Custom deploy path (default: <DnSpyPath>\bin\Extensions)
    [switch]$PublishHeadless,     # Publish standalone headless MCP server
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"  # Build configuration (default: Release)
)

$ErrorActionPreference = "Stop"
$WorkspaceRoot = Split-Path -Parent (Split-Path $MyInvocation.MyCommand.Path -Parent)

# Resolve dnSpy bin path (DLLs must be in <DnSpyPath>/bin/)
$DnSpyPath = $DnSpyPath.TrimEnd('\', '/')
$DnSpyBin = Join-Path $DnSpyPath "bin"

# Sync dnSpy dependency DLLs into deps/ (idempotent; Core/Extension/Headless
# all resolve references from deps/ via the <DnSpyBin> csproj property).
& (Join-Path $PSScriptRoot "sync-deps.ps1") -DnSpyBin $DnSpyBin
if (-not $?) {
    Write-Host "[ERROR] sync-deps.ps1 failed." -ForegroundColor Red
    exit 1
}

# Read project target framework from csproj (needed for both TFM check and BinDir)
$projectFileForTfm = Join-Path $WorkspaceRoot "src\dnSpy.MCP\dnSpy.MCP.csproj"
$csprojContent = Get-Content $projectFileForTfm -Raw
if ($csprojContent -match '<TargetFramework>([^<]+)</TargetFramework>') {
    $projectTfm = $Matches[1]
} else {
    $projectTfm = "net8.0-windows"
}

# Check dnSpy runtime version compatibility (only when runtimeconfig exists)
$runtimeConfig = Join-Path $DnSpyBin "dnSpy.runtimeconfig.json"
if (Test-Path $runtimeConfig) {
    $config = Get-Content $runtimeConfig -Raw | ConvertFrom-Json
    $dnSpyTfm = $config.runtimeOptions.tfm
    $dnSpyBase = $dnSpyTfm -replace '-.*$', ''
    $projectBase = $projectTfm -replace '-.*$', ''

    if ($dnSpyBase -ne $projectBase) {
        Write-Host "[ERROR] Framework version mismatch!" -ForegroundColor Red
        Write-Host ""
        Write-Host "  dnSpy runtime: $dnSpyBase (from $dnSpyTfm)" -ForegroundColor Red
        Write-Host "  Project target: $projectBase (from $projectTfm)" -ForegroundColor Red
        Write-Host ""
        Write-Host "Solution options:" -ForegroundColor Yellow
        Write-Host "  1. Upgrade project to $dnSpyBase in dnSpy.MCP.csproj (requires .NET $($dnSpyBase -replace 'net', '') SDK)" -ForegroundColor Yellow
        Write-Host "  2. Use dnSpyEx source build instead (supports net8.0)" -ForegroundColor Yellow
        Write-Host "     - Clone: https://github.com/dnSpyEx/dnSpy" -ForegroundColor Gray
        Write-Host "     - Copy src/dnSpy.MCP/ into dnSpy/Extensions/" -ForegroundColor Gray
        Write-Host "     - Build via dnSpy.sln" -ForegroundColor Gray
        exit 1
    }
}

# Use custom deploy dir or default to dnSpy Extensions
if ([string]::IsNullOrWhiteSpace($DeployDir)) {
    $DeployDir = Join-Path $DnSpyBin "Extensions"
} else {
    $DeployDir = $DeployDir.TrimEnd('\', '/')
}

# Project paths
$SolutionFile = Join-Path $WorkspaceRoot "dnspy_mcp.sln"
$ProjectDir = Join-Path $WorkspaceRoot "src\dnSpy.MCP"
$BinDir = Join-Path $ProjectDir "bin\$Configuration\$projectTfm"
$HeadlessPublishDir = Join-Path $WorkspaceRoot "publish\headless"

Write-Host "=== dnSpy MCP ===" -ForegroundColor Cyan
Write-Host "  Solution: $SolutionFile"
Write-Host "  Config:   $Configuration"
Write-Host "  DnSpyBin: $DnSpyBin"
if ($Deploy) {
    Write-Host "  Deploy:   $DeployDir"
}
if ($PublishHeadless) {
    Write-Host "  Publish:  $HeadlessPublishDir (headless)"
}
Write-Host ""

# Step 0: Clean (all src projects)
if ($Clean) {
    Write-Host "[Clean] Cleaning..." -ForegroundColor Yellow
    & dotnet clean $SolutionFile -c $Configuration --nologo -v q | Out-Null
    foreach ($dir in @("dnSpy.MCP", "dnSpy.MCP.Core", "dnSpy.MCP.Headless", "dnSpy.MCP.Tests")) {
        foreach ($sub in @("obj", "bin")) {
            $p = Join-Path $WorkspaceRoot "src\$dir\$sub"
            if (Test-Path $p) {
                Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    Write-Host "  Done." -ForegroundColor DarkGray
}

# Step 1: Build the whole solution (Core + Extension + Headless + Tests)
Write-Host "[Build] Building solution..." -ForegroundColor Yellow

$buildOutput = & dotnet build $SolutionFile -c $Configuration 2>&1
$buildText = $buildOutput | Out-String

if ($LASTEXITCODE -ne 0) {
    Write-Host "[BUILD FAILED]" -ForegroundColor Red
    $buildOutput | Where-Object { $_ -match "error CS|error MSB" } | ForEach-Object {
        Write-Host "  $_" -ForegroundColor Red
    }
    exit 1
}

Write-Host "  OK" -ForegroundColor Green

# Step 1b: Publish standalone headless MCP server (optional)
if ($PublishHeadless) {
    Write-Host "[Publish] Headless..." -ForegroundColor Yellow
    & dotnet publish (Join-Path $WorkspaceRoot "src\dnSpy.MCP.Headless\dnSpy.MCP.Headless.csproj") `
        -c $Configuration -o $HeadlessPublishDir --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[PUBLISH FAILED]" -ForegroundColor Red
        exit 1
    }
    Write-Host "  OK: $HeadlessPublishDir" -ForegroundColor Green
}

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
    # Clean old MCP/ASP.NET Core DLLs
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
Write-Host "  Deploy:  $DeployDir\dnSpy.MCP.x.dll"
if ($Deploy) {
    Write-Host "  Log:     $DnSpyBin\mcp-server.log"
}
if ($PublishHeadless) {
    Write-Host "  Headless: $HeadlessPublishDir\dnspy-mcp-headless.dll (run: dotnet dnspy-mcp-headless.dll --load <dll>)"
}
Write-Host "  Port:    5150"
Write-Host ""
Write-Host "Done!" -ForegroundColor Green
