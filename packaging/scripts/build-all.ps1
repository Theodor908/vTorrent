<#
.SYNOPSIS
    Cross-platform build script for vTorrent.

.DESCRIPTION
    This script orchestrates building vTorrent for all supported platforms.
    When run on Windows, it can use WSL for Linux builds and provide instructions
    for macOS builds.

.PARAMETER Platforms
    Platforms to build for: Windows, Linux, macOS, or All. Default is All.

.PARAMETER Configuration
    Build configuration: Debug or Release. Default is Release.

.PARAMETER WindowsPlatform
    Windows target platform: x64 or arm64. Default is x64.

.PARAMETER LinuxArch
    Linux architecture: x64, arm64, or all. Default is x64.

.PARAMETER SkipMsix
    Skip MSIX package creation for Windows.

.PARAMETER SkipAppImage
    Skip AppImage creation for Linux.

.PARAMETER SkipDeb
    Skip Debian package creation for Linux.

.PARAMETER UseWSL
    Use WSL for Linux builds when running on Windows.

.PARAMETER WSLDistro
    WSL distribution name to use. Default is the default WSL distribution.

.PARAMETER Clean
    Clean build artifacts before building.

.PARAMETER Help
    Show this help message.

.EXAMPLE
    .\build-all.ps1
    Build for all platforms using default settings.

.EXAMPLE
    .\build-all.ps1 -Platforms Windows
    Build only for Windows.

.EXAMPLE
    .\build-all.ps1 -Platforms Linux -UseWSL
    Build for Linux using WSL.

.EXAMPLE
    .\build-all.ps1 -Configuration Debug -Clean
    Clean and build in Debug mode for all platforms.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet("Windows", "Linux", "macOS", "All")]
    [string[]]$Platforms = @("All"),

    [Parameter()]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter()]
    [ValidateSet("x64", "arm64")]
    [string]$WindowsPlatform = "x64",

    [Parameter()]
    [ValidateSet("x64", "arm64", "all")]
    [string]$LinuxArch = "x64",

    [Parameter()]
    [switch]$SkipMsix,

    [Parameter()]
    [switch]$SkipAppImage,

    [Parameter()]
    [switch]$SkipDeb,

    [Parameter()]
    [switch]$UseWSL,

    [Parameter()]
    [string]$WSLDistro,

    [Parameter()]
    [switch]$Clean,

    [Parameter()]
    [switch]$Help
)

# ============================================================================
# Configuration
# ============================================================================

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path "$ScriptDir\..\..").Path
$DistDir = "$ProjectRoot\dist"

# Build scripts
$WindowsScript = "$ScriptDir\build-windows.ps1"
$LinuxScript = "$ScriptDir\build-linux.sh"
$MacOSScript = "$ScriptDir\build-macos.sh"

# ============================================================================
# Helper Functions
# ============================================================================

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================================" -ForegroundColor Magenta
    Write-Host " $Message" -ForegroundColor Magenta
    Write-Host "============================================================================" -ForegroundColor Magenta
    Write-Host ""
}

function Write-SubHeader {
    param([string]$Message)
    Write-Host ""
    Write-Host "----------------------------------------------------------------------------" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "----------------------------------------------------------------------------" -ForegroundColor Cyan
    Write-Host ""
}

function Write-Step {
    param([string]$Message)
    Write-Host "[*] $Message" -ForegroundColor Yellow
}

function Write-Success {
    param([string]$Message)
    Write-Host "[+] $Message" -ForegroundColor Green
}

function Write-ErrorMessage {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Write-Warning {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor DarkYellow
}

function Show-Help {
    Get-Help $MyInvocation.PSCommandPath -Detailed
}

function Test-WSLAvailable {
    try {
        $wslOutput = wsl --list --quiet 2>&1
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

function Get-WSLPath {
    param([string]$WindowsPath)

    # Convert Windows path to WSL path
    $drive = $WindowsPath.Substring(0, 1).ToLower()
    $rest = $WindowsPath.Substring(2).Replace('\', '/')
    return "/mnt/$drive$rest"
}

function Invoke-WindowsBuild {
    Write-SubHeader "Building for Windows"

    if (-not (Test-Path $WindowsScript)) {
        Write-ErrorMessage "Windows build script not found: $WindowsScript"
        return $false
    }

    $args = @(
        "-Configuration", $Configuration
        "-Platform", $WindowsPlatform
    )

    if ($SkipMsix) {
        $args += "-NoMsix"
    }

    if ($Clean) {
        $args += "-Clean"
    }

    Write-Step "Running Windows build script..."
    Write-Info "Arguments: $($args -join ' ')"

    & $WindowsScript @args

    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "Windows build failed with exit code $LASTEXITCODE"
        return $false
    }

    Write-Success "Windows build completed successfully."
    return $true
}

function Invoke-LinuxBuild {
    Write-SubHeader "Building for Linux"

    if (-not $UseWSL) {
        Write-Warning "Linux builds require WSL on Windows. Use -UseWSL flag to enable."
        Write-Info "To build for Linux manually:"
        Write-Info "  1. Copy the project to a Linux machine or WSL"
        Write-Info "  2. Run: ./packaging/scripts/build-linux.sh"
        return $false
    }

    if (-not (Test-WSLAvailable)) {
        Write-ErrorMessage "WSL is not available. Please install WSL first."
        Write-Info "Install WSL: wsl --install"
        return $false
    }

    if (-not (Test-Path $LinuxScript)) {
        Write-ErrorMessage "Linux build script not found: $LinuxScript"
        return $false
    }

    # Convert paths for WSL
    $wslProjectRoot = Get-WSLPath $ProjectRoot
    $wslScript = "$wslProjectRoot/packaging/scripts/build-linux.sh"

    # Build WSL command
    $wslArgs = @()

    if ($WSLDistro) {
        $wslArgs += "-d"
        $wslArgs += $WSLDistro
    }

    # Build the bash command arguments
    $bashArgs = @("-c", $Configuration)

    if ($LinuxArch -eq "all") {
        $bashArgs += "--all-arch"
    }
    else {
        $bashArgs += @("-a", $LinuxArch)
    }

    if (-not $SkipAppImage) {
        $bashArgs += "--appimage"
    }

    if (-not $SkipDeb) {
        $bashArgs += "--deb"
    }

    if ($Clean) {
        $bashArgs += "--clean"
    }

    $bashCommand = "cd '$wslProjectRoot' && chmod +x '$wslScript' && '$wslScript' $($bashArgs -join ' ')"

    Write-Step "Running Linux build in WSL..."
    Write-Info "Command: wsl $($wslArgs -join ' ') bash -c `"$bashCommand`""

    if ($wslArgs.Count -gt 0) {
        & wsl @wslArgs bash -c $bashCommand
    }
    else {
        & wsl bash -c $bashCommand
    }

    if ($LASTEXITCODE -ne 0) {
        Write-ErrorMessage "Linux build failed with exit code $LASTEXITCODE"
        return $false
    }

    Write-Success "Linux build completed successfully."
    return $true
}

function Invoke-MacOSBuild {
    Write-SubHeader "Building for macOS"

    Write-Warning "macOS builds cannot be run directly on Windows."
    Write-Info ""
    Write-Info "To build for macOS:"
    Write-Info "  1. Copy the project to a macOS machine"
    Write-Info "  2. Make the script executable: chmod +x packaging/scripts/build-macos.sh"
    Write-Info "  3. Run: ./packaging/scripts/build-macos.sh"
    Write-Info ""
    Write-Info "Options:"
    Write-Info "  -c, --configuration   Build configuration (Debug/Release)"
    Write-Info "  -s, --sign            Sign with Developer ID"
    Write-Info "  -d, --dmg             Create DMG disk image"
    Write-Info "  -n, --notarize        Notarize the application"
    Write-Info ""
    Write-Info "Example:"
    Write-Info "  ./build-macos.sh -c Release -s 'Developer ID Application: Your Name' -d"

    return $true
}

function Show-Summary {
    param(
        [hashtable]$Results
    )

    Write-Header "Build Summary"

    Write-Host "Configuration: " -NoNewline
    Write-Host $Configuration -ForegroundColor White

    Write-Host ""
    Write-Host "Platform Results:" -ForegroundColor Yellow

    foreach ($platform in $Results.Keys) {
        $status = $Results[$platform]
        $statusText = if ($status -eq "Success") { "Success" } elseif ($status -eq "Skipped") { "Skipped" } else { "Failed" }
        $statusColor = switch ($status) {
            "Success" { "Green" }
            "Skipped" { "DarkYellow" }
            default { "Red" }
        }

        Write-Host "  $platform`: " -NoNewline
        Write-Host $statusText -ForegroundColor $statusColor
    }

    Write-Host ""
    Write-Host "Output Directories:" -ForegroundColor Yellow

    # Windows
    $windowsOutput = "$DistDir\windows"
    if (Test-Path $windowsOutput) {
        Write-Info "Windows: $windowsOutput"
        $files = Get-ChildItem -Path $windowsOutput -Recurse -File | Where-Object { $_.Extension -in ".exe", ".msix" }
        foreach ($file in $files) {
            $size = [math]::Round($file.Length / 1MB, 2)
            Write-Info "  - $($file.Name) ($size MB)"
        }
    }

    # Linux
    $linuxOutput = "$DistDir\linux"
    if (Test-Path $linuxOutput) {
        Write-Info "Linux: $linuxOutput"
        $files = Get-ChildItem -Path $linuxOutput -File | Where-Object { $_.Extension -in ".AppImage", ".deb" }
        foreach ($file in $files) {
            $size = [math]::Round($file.Length / 1MB, 2)
            Write-Info "  - $($file.Name) ($size MB)"
        }
    }

    # macOS
    $macosOutput = "$DistDir\macos"
    if (Test-Path $macosOutput) {
        Write-Info "macOS: $macosOutput"
    }

    Write-Host ""

    $failedCount = ($Results.Values | Where-Object { $_ -eq "Failed" }).Count
    if ($failedCount -gt 0) {
        Write-ErrorMessage "Build completed with $failedCount failure(s)."
    }
    else {
        Write-Success "Build completed successfully!"
    }
}

# ============================================================================
# Main Script
# ============================================================================

try {
    # Show help if requested
    if ($Help) {
        Show-Help
        exit 0
    }

    Write-Header "vTorrent Cross-Platform Build"
    Write-Info "Project Root: $ProjectRoot"
    Write-Info "Configuration: $Configuration"
    Write-Info "Platforms: $($Platforms -join ', ')"

    # Expand "All" to specific platforms
    if ($Platforms -contains "All") {
        $Platforms = @("Windows", "Linux", "macOS")
    }

    # Clean if requested
    if ($Clean) {
        Write-Step "Cleaning all build artifacts..."
        if (Test-Path $DistDir) {
            Remove-Item -Recurse -Force $DistDir
            Write-Info "Removed: $DistDir"
        }
    }

    # Create dist directory
    $null = New-Item -ItemType Directory -Force -Path $DistDir

    # Track results
    $results = @{}

    # Build for each platform
    foreach ($platform in $Platforms) {
        switch ($platform) {
            "Windows" {
                $success = Invoke-WindowsBuild
                $results["Windows"] = if ($success) { "Success" } else { "Failed" }
            }
            "Linux" {
                if ($UseWSL) {
                    $success = Invoke-LinuxBuild
                    $results["Linux"] = if ($success) { "Success" } else { "Failed" }
                }
                else {
                    Invoke-LinuxBuild | Out-Null
                    $results["Linux"] = "Skipped"
                }
            }
            "macOS" {
                Invoke-MacOSBuild | Out-Null
                $results["macOS"] = "Skipped"
            }
        }
    }

    # Show summary
    Show-Summary -Results $results

    # Exit with appropriate code
    $failedCount = ($results.Values | Where-Object { $_ -eq "Failed" }).Count
    exit $failedCount
}
catch {
    Write-ErrorMessage $_.Exception.Message
    Write-Host ""
    Write-Host "Stack trace:" -ForegroundColor DarkGray
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
