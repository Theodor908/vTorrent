<#
.SYNOPSIS
    Creates a traditional Windows installer (.exe) using Inno Setup.

.DESCRIPTION
    This script:
    1. Publishes vTorrent as self-contained for Windows x64
    2. Runs Inno Setup to create the installer executable

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.PARAMETER SkipPublish
    Skip the dotnet publish step (use existing publish output)

.EXAMPLE
    .\build-installer.ps1
    .\build-installer.ps1 -Configuration Debug
    .\build-installer.ps1 -SkipPublish

.NOTES
    Requires Inno Setup to be installed. Download from: https://jrsoftware.org/isinfo.php
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"

# Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path "$ScriptDir\..\..").Path
$OutputDir = Join-Path $ProjectRoot "dist\windows"
$PublishDir = Join-Path $OutputDir "publish\x64"
$InstallerOutputDir = Join-Path $OutputDir "installer"
$IssFile = Join-Path $ScriptDir "vTorrent.iss"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " vTorrent Installer Builder" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Configuration: $Configuration"
Write-Host "  Project: $ProjectRoot"
Write-Host ""

# Find Inno Setup
function Find-InnoSetup {
    $paths = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 5\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 5\ISCC.exe"
    )

    foreach ($path in $paths) {
        if (Test-Path $path) {
            return $path
        }
    }

    # Check PATH
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($iscc) {
        return $iscc.Source
    }

    return $null
}

$InnoSetup = Find-InnoSetup
if (-not $InnoSetup) {
    Write-Host "[!] Inno Setup not found!" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please install Inno Setup from: https://jrsoftware.org/isdl.php" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "After installation, run this script again." -ForegroundColor Yellow
    exit 1
}

Write-Host "[+] Found Inno Setup: $InnoSetup" -ForegroundColor Green

# Step 1: Publish the application
if (-not $SkipPublish) {
    Write-Host ""
    Write-Host "[*] Publishing vTorrent..." -ForegroundColor Yellow

    # Clean previous publish
    if (Test-Path $PublishDir) {
        Remove-Item $PublishDir -Recurse -Force
    }

    $publishArgs = @(
        "publish"
        "$ProjectRoot\vTorrent.csproj"
        "--configuration", $Configuration
        "--runtime", "win-x64"
        "--self-contained", "true"
        "--output", $PublishDir
        "-p:PublishSingleFile=false"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:DebugType=embedded"
        "-p:TargetFramework=net10.0-windows10.0.17763.0"
    )

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[!] Publish failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "[+] Published to: $PublishDir" -ForegroundColor Green
} else {
    Write-Host "[*] Skipping publish (using existing output)" -ForegroundColor Yellow
    if (-not (Test-Path $PublishDir)) {
        Write-Host "[!] Publish directory not found: $PublishDir" -ForegroundColor Red
        Write-Host "    Run without -SkipPublish first." -ForegroundColor Yellow
        exit 1
    }
}

# Step 2: Create installer output directory
if (-not (Test-Path $InstallerOutputDir)) {
    New-Item -ItemType Directory -Path $InstallerOutputDir -Force | Out-Null
}

# Step 3: Run Inno Setup
Write-Host ""
Write-Host "[*] Building installer..." -ForegroundColor Yellow

& $InnoSetup $IssFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] Inno Setup failed!" -ForegroundColor Red
    exit 1
}

# Find the output file
$InstallerFile = Get-ChildItem $InstallerOutputDir -Filter "*.exe" | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($InstallerFile) {
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Green
    Write-Host " Build Complete!" -ForegroundColor Green
    Write-Host "============================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  Installer: $($InstallerFile.FullName)" -ForegroundColor White
    Write-Host "  Size: $([math]::Round($InstallerFile.Length / 1MB, 2)) MB" -ForegroundColor White
    Write-Host ""
    Write-Host "  To install: Double-click the .exe file" -ForegroundColor Cyan
    Write-Host ""
} else {
    Write-Host "[!] Installer file not found in output directory" -ForegroundColor Red
    exit 1
}
