<#
.SYNOPSIS
    Build script for vTorrent on Windows.

.DESCRIPTION
    This script builds and packages vTorrent for Windows as a self-contained application
    and optionally creates an MSIX package.

.PARAMETER Configuration
    Build configuration: Debug or Release. Default is Release.

.PARAMETER Platform
    Target platform: x64 or arm64. Default is x64.

.PARAMETER NoMsix
    Skip MSIX package creation.

.PARAMETER GenerateCertificate
    Generate a self-signed certificate for code signing.

.PARAMETER CertificateThumbprint
    Thumbprint of an existing certificate to use for signing.

.PARAMETER Clean
    Clean build artifacts before building.

.PARAMETER Help
    Show this help message.

.EXAMPLE
    .\build-windows.ps1
    Build vTorrent in Release mode for x64.

.EXAMPLE
    .\build-windows.ps1 -Configuration Debug -NoMsix
    Build vTorrent in Debug mode without MSIX packaging.

.EXAMPLE
    .\build-windows.ps1 -GenerateCertificate
    Generate a self-signed certificate and build with signing.
#>

[CmdletBinding()]
param(
    [Parameter()]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [Parameter()]
    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64",

    [Parameter()]
    [switch]$NoMsix,

    [Parameter()]
    [switch]$GenerateCertificate,

    [Parameter()]
    [string]$CertificateThumbprint,

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
$MainProject = "$ProjectRoot\vTorrent.csproj"
$PackagingProject = "$ProjectRoot\packaging\windows\vTorrent.Windows.Packaging.csproj"
$OutputDir = "$ProjectRoot\dist\windows"
$PublishDir = "$OutputDir\publish"
$MsixOutputDir = "$OutputDir\msix"

# Runtime identifier mapping
$RuntimeIdentifiers = @{
    "x64" = "win-x64"
    "arm64" = "win-arm64"
}

$RuntimeId = $RuntimeIdentifiers[$Platform]

# ============================================================================
# Helper Functions
# ============================================================================

function Write-Header {
    param([string]$Message)
    Write-Host ""
    Write-Host "============================================================================" -ForegroundColor Cyan
    Write-Host " $Message" -ForegroundColor Cyan
    Write-Host "============================================================================" -ForegroundColor Cyan
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

function Write-Error {
    param([string]$Message)
    Write-Host "[!] $Message" -ForegroundColor Red
}

function Write-Info {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Gray
}

function Test-Command {
    param([string]$Command)
    $null = Get-Command $Command -ErrorAction SilentlyContinue
    return $?
}

function Show-Help {
    Get-Help $MyInvocation.PSCommandPath -Detailed
}

function Test-Prerequisites {
    Write-Step "Checking prerequisites..."

    $hasErrors = $false

    # Check for dotnet
    if (-not (Test-Command "dotnet")) {
        Write-Error ".NET SDK not found. Please install .NET 10.0 SDK or later."
        $hasErrors = $true
    } else {
        $dotnetVersion = dotnet --version
        Write-Info "Found .NET SDK: $dotnetVersion"
    }

    # Check for main project file
    if (-not (Test-Path $MainProject)) {
        Write-Error "Main project file not found: $MainProject"
        $hasErrors = $true
    } else {
        Write-Info "Found main project: $MainProject"
    }

    # Check for packaging project if MSIX is requested
    if (-not $NoMsix) {
        if (-not (Test-Path $PackagingProject)) {
            Write-Error "Packaging project not found: $PackagingProject"
            Write-Info "Use -NoMsix flag to skip MSIX packaging."
            $hasErrors = $true
        } else {
            Write-Info "Found packaging project: $PackagingProject"
        }
    }

    if ($hasErrors) {
        throw "Prerequisites check failed. Please resolve the issues above."
    }

    Write-Success "All prerequisites satisfied."
}

function New-SelfSignedCodeSigningCertificate {
    Write-Step "Generating self-signed certificate..."

    # Check if running as administrator for certificate installation
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isAdmin) {
        Write-Error "Administrator privileges required to generate and install certificates."
        Write-Info "Please run this script as Administrator or use an existing certificate."
        throw "Insufficient privileges for certificate generation."
    }

    # Generate certificate
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject "CN=vTorrent" `
        -KeyUsage DigitalSignature `
        -FriendlyName "vTorrent Development Certificate" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") `
        -NotAfter (Get-Date).AddYears(5)

    $thumbprint = $cert.Thumbprint
    Write-Success "Certificate generated with thumbprint: $thumbprint"

    # Export certificate for user installation
    $certPath = "$OutputDir\vTorrent_DevCert.cer"
    $null = New-Item -ItemType Directory -Force -Path $OutputDir
    Export-Certificate -Cert $cert -FilePath $certPath | Out-Null
    Write-Info "Certificate exported to: $certPath"

    # Install to Trusted People store for local testing
    $trustedPeopleStore = New-Object System.Security.Cryptography.X509Certificates.X509Store("TrustedPeople", "CurrentUser")
    $trustedPeopleStore.Open("ReadWrite")
    $trustedPeopleStore.Add($cert)
    $trustedPeopleStore.Close()
    Write-Success "Certificate installed to Trusted People store."

    return $thumbprint
}

function Invoke-Clean {
    Write-Step "Cleaning build artifacts..."

    # Clean output directories
    if (Test-Path $OutputDir) {
        Remove-Item -Recurse -Force $OutputDir
        Write-Info "Removed: $OutputDir"
    }

    # Clean bin and obj directories
    $dirsToClean = @(
        "$ProjectRoot\bin",
        "$ProjectRoot\obj",
        "$ProjectRoot\packaging\windows\bin",
        "$ProjectRoot\packaging\windows\obj",
        "$ProjectRoot\packaging\windows\AppPackages"
    )

    foreach ($dir in $dirsToClean) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
            Write-Info "Removed: $dir"
        }
    }

    Write-Success "Clean completed."
}

function Invoke-Publish {
    Write-Header "Publishing vTorrent"

    Write-Step "Publishing self-contained application..."
    Write-Info "Configuration: $Configuration"
    Write-Info "Platform: $Platform ($RuntimeId)"
    Write-Info "Output: $PublishDir\$RuntimeId"

    $publishArgs = @(
        "publish"
        $MainProject
        "--configuration", $Configuration
        "--runtime", $RuntimeId
        "--self-contained", "true"
        "--output", "$PublishDir\$RuntimeId"
        "-p:PublishSingleFile=false"
        "-p:IncludeNativeLibrariesForSelfExtract=true"
        "-p:DebugType=embedded"
    )

    # Use specific target framework for Windows builds
    $publishArgs += "-p:TargetFramework=net10.0-windows10.0.17763.0"

    Write-Info "Running: dotnet $($publishArgs -join ' ')"

    & dotnet @publishArgs

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Success "Published to: $PublishDir\$RuntimeId"

    # List output files
    $outputFiles = Get-ChildItem -Path "$PublishDir\$RuntimeId" -File | Select-Object -First 10
    Write-Info "Output files (first 10):"
    foreach ($file in $outputFiles) {
        Write-Info "  - $($file.Name) ($([math]::Round($file.Length / 1MB, 2)) MB)"
    }
}

function Invoke-MsixBuild {
    Write-Header "Building MSIX Package"

    Write-Step "Building MSIX package..."

    $null = New-Item -ItemType Directory -Force -Path $MsixOutputDir

    $buildArgs = @(
        "build"
        $PackagingProject
        "--configuration", $Configuration
        "-p:Platform=$Platform"
        "-p:AppxPackageDir=$MsixOutputDir\"
        "-p:GenerateAppxPackageOnBuild=true"
    )

    # Add certificate thumbprint if provided
    if ($CertificateThumbprint) {
        $buildArgs += "-p:PackageCertificateThumbprint=$CertificateThumbprint"
        $buildArgs += "-p:AppxPackageSigningEnabled=true"
        Write-Info "Using certificate: $CertificateThumbprint"
    } elseif ($Configuration -eq "Debug") {
        $buildArgs += "-p:AppxPackageSigningEnabled=false"
        Write-Info "Building unsigned package (Debug mode)"
    }

    Write-Info "Running: dotnet $($buildArgs -join ' ')"

    & dotnet @buildArgs

    if ($LASTEXITCODE -ne 0) {
        throw "MSIX build failed with exit code $LASTEXITCODE"
    }

    # Find the generated MSIX
    $msixFiles = Get-ChildItem -Path $MsixOutputDir -Filter "*.msix" -Recurse
    if ($msixFiles) {
        Write-Success "MSIX packages created:"
        foreach ($msix in $msixFiles) {
            Write-Info "  - $($msix.FullName)"

            # Copy to output directory root for easy access
            Copy-Item $msix.FullName -Destination $MsixOutputDir -Force
        }
    }

    # Also look for .appx files
    $appxFiles = Get-ChildItem -Path $MsixOutputDir -Filter "*.appx" -Recurse
    if ($appxFiles) {
        Write-Success "APPX packages created:"
        foreach ($appx in $appxFiles) {
            Write-Info "  - $($appx.FullName)"
        }
    }
}

function Show-Summary {
    Write-Header "Build Summary"

    Write-Host "Configuration:  " -NoNewline
    Write-Host $Configuration -ForegroundColor White

    Write-Host "Platform:       " -NoNewline
    Write-Host "$Platform ($RuntimeId)" -ForegroundColor White

    Write-Host "Output:         " -NoNewline
    Write-Host $OutputDir -ForegroundColor White

    Write-Host ""
    Write-Host "Outputs:" -ForegroundColor Yellow

    # Published application
    $publishPath = "$PublishDir\$RuntimeId"
    if (Test-Path $publishPath) {
        $exePath = "$publishPath\vTorrent.exe"
        if (Test-Path $exePath) {
            $exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
            Write-Info "Self-contained app: $publishPath"
            Write-Info "  vTorrent.exe: $exeSize MB"
        }
    }

    # MSIX package
    if (-not $NoMsix) {
        $msixFiles = Get-ChildItem -Path $MsixOutputDir -Filter "*.msix" -ErrorAction SilentlyContinue
        if ($msixFiles) {
            foreach ($msix in $msixFiles) {
                $msixSize = [math]::Round($msix.Length / 1MB, 2)
                Write-Info "MSIX package: $($msix.FullName)"
                Write-Info "  Size: $msixSize MB"
            }
        }
    }

    Write-Host ""
    Write-Success "Build completed successfully!"

    # Installation instructions
    Write-Host ""
    Write-Host "Installation:" -ForegroundColor Yellow
    Write-Info "1. Self-contained app: Run vTorrent.exe directly from $publishPath"
    if (-not $NoMsix) {
        Write-Info "2. MSIX package: Double-click the .msix file to install"
        Write-Info "   Note: For unsigned packages, enable Developer Mode in Windows Settings"
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

    Write-Header "vTorrent Windows Build Script"
    Write-Info "Project Root: $ProjectRoot"
    Write-Info "Configuration: $Configuration"
    Write-Info "Platform: $Platform"
    Write-Info "MSIX: $(-not $NoMsix)"

    # Check prerequisites
    Test-Prerequisites

    # Clean if requested
    if ($Clean) {
        Invoke-Clean
    }

    # Generate certificate if requested
    if ($GenerateCertificate) {
        $CertificateThumbprint = New-SelfSignedCodeSigningCertificate
    }

    # Create output directory
    $null = New-Item -ItemType Directory -Force -Path $OutputDir

    # Publish the application
    Invoke-Publish

    # Build MSIX package
    if (-not $NoMsix) {
        Invoke-MsixBuild
    }

    # Show summary
    Show-Summary

    exit 0
}
catch {
    Write-Error $_.Exception.Message
    Write-Host ""
    Write-Host "Stack trace:" -ForegroundColor DarkGray
    Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    exit 1
}
