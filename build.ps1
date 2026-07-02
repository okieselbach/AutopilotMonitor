<#
.SYNOPSIS
    Build script for Autopilot Monitor solution.

.DESCRIPTION
    Builds all .NET projects and optionally the web UI.
    Run this after cloning or when you want to rebuild everything.

.PARAMETER Configuration
    Build configuration: Debug or Release (default: Debug)

.PARAMETER IncludeWeb
    Also build the web UI (requires Node.js)

.PARAMETER Clean
    Clean before building

.EXAMPLE
    .\build.ps1
    Builds all .NET projects in Debug mode

.EXAMPLE
    .\build.ps1 -Configuration Release -IncludeWeb
    Builds everything in Release mode including web UI
#>

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$IncludeWeb,

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host " Autopilot Monitor Build Script" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

$rootPath = $PSScriptRoot
$slnPath = Join-Path $rootPath "AutopilotMonitor.sln"

# Check prerequisites
Write-Host "[1/4] Checking prerequisites..." -ForegroundColor Yellow

# Check .NET SDK
try {
    $dotnetVersion = & dotnet --version
    Write-Host "  ✓ .NET SDK version: $dotnetVersion" -ForegroundColor Green
}
catch {
    Write-Host "  ✗ .NET SDK not found. Please install .NET 8 SDK." -ForegroundColor Red
    exit 1
}

# Check Node.js if building web
if ($IncludeWeb) {
    try {
        $nodeVersion = & node --version
        Write-Host "  ✓ Node.js version: $nodeVersion" -ForegroundColor Green
    }
    catch {
        Write-Host "  ✗ Node.js not found. Please install Node.js 18+." -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# Clean if requested
if ($Clean) {
    Write-Host "[2/4] Cleaning solution..." -ForegroundColor Yellow
    & dotnet clean $slnPath -c $Configuration
    Write-Host "  ✓ Clean completed" -ForegroundColor Green
    Write-Host ""
}
else {
    Write-Host "[2/4] Skipping clean (use -Clean to clean first)" -ForegroundColor Gray
    Write-Host ""
}

# Restore packages
Write-Host "[3/4] Restoring NuGet packages..." -ForegroundColor Yellow
& dotnet restore $slnPath

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Restore failed" -ForegroundColor Red
    exit 1
}

Write-Host "  ✓ Restore completed" -ForegroundColor Green
Write-Host ""

# Build solution
Write-Host "[4/4] Building solution ($Configuration)..." -ForegroundColor Yellow
& dotnet build $slnPath -c $Configuration --no-restore

if ($LASTEXITCODE -ne 0) {
    Write-Host "  ✗ Build failed" -ForegroundColor Red
    exit 1
}

Write-Host "  ✓ Build completed" -ForegroundColor Green
Write-Host ""

# Build web if requested
if ($IncludeWeb) {
    Write-Host "[BONUS] Building web UI..." -ForegroundColor Yellow
    $webPath = Join-Path $rootPath "src\Web\autopilot-monitor-web"

    Push-Location $webPath
    try {
        Write-Host "  Installing npm packages..." -ForegroundColor Gray
        & npm install --silent

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ npm install failed" -ForegroundColor Red
            exit 1
        }

        Write-Host "  Building Next.js app..." -ForegroundColor Gray
        & npm run build

        if ($LASTEXITCODE -ne 0) {
            Write-Host "  ✗ Web build failed" -ForegroundColor Red
            exit 1
        }

        Write-Host "  ✓ Web build completed" -ForegroundColor Green
    }
    finally {
        Pop-Location
    }
    Write-Host ""
}

# Summary
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " Build Completed Successfully!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output locations:" -ForegroundColor White
Write-Host "  Agent:     src\Agent\AutopilotMonitor.Agent.V2\bin\$Configuration\net48\" -ForegroundColor Gray
Write-Host "  Functions: src\Backend\AutopilotMonitor.Functions\bin\$Configuration\net8.0\" -ForegroundColor Gray

if ($IncludeWeb) {
    Write-Host "  Web UI:    src\Web\autopilot-monitor-web\.next\" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start Azurite: azurite --silent --location c:\azurite" -ForegroundColor Gray
Write-Host "  2. Start Functions: cd src\Backend\AutopilotMonitor.Functions && func start" -ForegroundColor Gray

if ($IncludeWeb) {
    Write-Host "  3. Start Web UI: cd src\Web\autopilot-monitor-web && npm run dev" -ForegroundColor Gray
    Write-Host "  4. Test Agent: cd src\Agent\AutopilotMonitor.Agent.V2\bin\$Configuration\net48 && .\AutopilotMonitor.Agent.exe --console" -ForegroundColor Gray
}
else {
    Write-Host "  3. Test Agent: cd src\Agent\AutopilotMonitor.Agent.V2\bin\$Configuration\net48 && .\AutopilotMonitor.Agent.exe --console" -ForegroundColor Gray
}

