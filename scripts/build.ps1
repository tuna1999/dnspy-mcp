# Build and deploy dnSpy MCP extension
# Supports standalone (src/dnSpy.MCP/) layout
# Usage: .\build.ps1 [-DnSpyPath <path>] [-Clean] [-Deploy] [-DeployDir <path>] [-Configuration <Debug|Release>]
#
# Required:
#   -DnSpyPath  Path to dnSpy installation folder (must contain dnSpy.exe in its bin/ folder)
#
# Examples:
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy"              # Build Release, DLLs from dnSpy/bin
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Clean       # Clean + build Release
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Deploy      # Build + deploy to dnSpy\Extensions
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -Configuration Debug  # Build Debug
#   .\build.ps1 -DnSpyPath "D:\tools\dnSpy" -DeployDir "D:\tools\dnSpy\Extensions"

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$DnSpyPath,           # Path to dnSpy installation (e.g. D:\tools\dnSpy)
    [switch]$Clean,               # Clean build artifacts before building
    [switch]$Deploy,              # Deploy extension after building
    [string]$DeployDir = "",      # Custom deploy path (default: <DnSpyPath>\Extensions)
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"  # Build configuration (default: Release)
)

$ErrorActionPreference = "Stop"
$WorkspaceRoot = Split-Path -Parent (Split-Path $MyInvocation.MyCommand.Path -Parent)

# Resolve dnSpy bin path (DLLs must be in <DnSpyPath>/bin/)
$DnSpyPath = $DnSpyPath.TrimEnd('\', '/')
$DnSpyBin = Join-Path $DnSpyPath "bin"

# Validate required DLLs
$RequiredDlls = @(
    "dnSpy.Contracts.DnSpy.dll",
    "dnSpy.Contracts.Logic.dll",
    "ICSharpCode.Decompiler.dll",
    "dnlib.dll"
)
$missingDlls = $RequiredDlls | Where-Object { -not (Test-Path (Join-Path $DnSpyBin $_)) }
if ($missingDlls.Count -gt 0) {
    Write-Host "[ERROR] Missing required DLLs in: $DnSpyBin" -ForegroundColor Red
    $missingDlls | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Ensure -DnSpyPath points to a valid dnSpy installation." -ForegroundColor Yellow
    Write-Host "The following DLLs must exist in the dnSpy bin folder:" -ForegroundColor Yellow
    $RequiredDlls | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
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
$StandaloneDir = Join-Path $WorkspaceRoot "src\dnSpy.MCP"
$ProjectDir = $StandaloneDir
$ProjectFile = Join-Path $ProjectDir "dnSpy.MCP.csproj"
$BinDir = Join-Path $ProjectDir "bin\$Configuration\$projectTfm"
$ObjDir = Join-Path $ProjectDir "obj"

Write-Host "=== dnSpy MCP ===" -ForegroundColor Cyan
Write-Host "  Project:  $ProjectDir"
Write-Host "  Config:   $Configuration"
Write-Host "  DnSpyBin: $DnSpyBin"
if ($Deploy) {
    Write-Host "  Deploy:   $DeployDir"
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

$buildOutput = & dotnet build $ProjectFile -c $Configuration -p:DnSpyBin="$DnSpyBin" 2>&1
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
Write-Host "  Port:    5150"
Write-Host ""
Write-Host "Done!" -ForegroundColor Green
