<#
.SYNOPSIS
    Creates an MSIX package from the published vTorrent application.

.DESCRIPTION
    This script:
    1. Publishes vTorrent as self-contained
    2. Prepares the MSIX layout with manifest and assets
    3. Creates the MSIX package using makeappx.exe
    4. Optionally signs the package

.PARAMETER Configuration
    Build configuration (Debug or Release). Default: Release

.PARAMETER Platform
    Target platform (x64 or arm64). Default: x64

.PARAMETER Sign
    Sign the MSIX package with a self-signed certificate

.PARAMETER CertificateThumbprint
    Thumbprint of existing certificate to use for signing

.EXAMPLE
    .\build-msix.ps1
    .\build-msix.ps1 -Sign
    .\build-msix.ps1 -Configuration Debug -Platform arm64
#>

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "arm64")]
    [string]$Platform = "x64",

    [switch]$Sign,

    [string]$CertificateThumbprint = ""
)

$ErrorActionPreference = "Stop"

# Paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path "$ScriptDir\..\..").Path
$PackagingDir = $ScriptDir
$OutputDir = Join-Path $ProjectRoot "dist\windows"
$PublishDir = Join-Path $OutputDir "publish\$Platform"
$MsixLayoutDir = Join-Path $OutputDir "msix-layout\$Platform"
$MsixOutputDir = Join-Path $OutputDir "msix"

# Runtime identifier
$RuntimeId = "win-$Platform"

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host " vTorrent MSIX Builder" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Configuration: $Configuration"
Write-Host "  Platform: $Platform ($RuntimeId)"
Write-Host "  Project: $ProjectRoot"
Write-Host ""

# Find Windows SDK
function Find-WindowsSDK {
    $sdkPaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22621.0\x64",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.22000.0\x64",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.19041.0\x64"
    )

    foreach ($path in $sdkPaths) {
        $makeappx = Join-Path $path "makeappx.exe"
        if (Test-Path $makeappx) {
            return $path
        }
    }

    # Try to find any version
    $basePath = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $basePath) {
        $versions = Get-ChildItem $basePath -Directory | Where-Object { $_.Name -match "^10\." } | Sort-Object Name -Descending
        foreach ($ver in $versions) {
            $makeappx = Join-Path $ver.FullName "x64\makeappx.exe"
            if (Test-Path $makeappx) {
                return Join-Path $ver.FullName "x64"
            }
        }
    }

    return $null
}

$SdkBinPath = Find-WindowsSDK
if (-not $SdkBinPath) {
    Write-Host "[!] Windows SDK not found. Please install Windows SDK." -ForegroundColor Red
    Write-Host "    Download from: https://developer.microsoft.com/windows/downloads/windows-sdk/" -ForegroundColor Yellow
    exit 1
}

$MakeAppx = Join-Path $SdkBinPath "makeappx.exe"
$SignTool = Join-Path $SdkBinPath "signtool.exe"

Write-Host "[+] Found Windows SDK: $SdkBinPath" -ForegroundColor Green

# Step 1: Publish the application
Write-Host ""
Write-Host "[*] Publishing vTorrent..." -ForegroundColor Yellow

$publishArgs = @(
    "publish"
    "$ProjectRoot\vTorrent.csproj"
    "--configuration", $Configuration
    "--runtime", $RuntimeId
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

# Step 2: Create MSIX layout directory
Write-Host ""
Write-Host "[*] Creating MSIX layout..." -ForegroundColor Yellow

if (Test-Path $MsixLayoutDir) {
    Remove-Item $MsixLayoutDir -Recurse -Force
}
New-Item -ItemType Directory -Path $MsixLayoutDir -Force | Out-Null

# Copy published files
Copy-Item "$PublishDir\*" $MsixLayoutDir -Recurse -Force

# Copy manifest
$ManifestSource = Join-Path $PackagingDir "Package.appxmanifest"
$ManifestDest = Join-Path $MsixLayoutDir "AppxManifest.xml"
Copy-Item $ManifestSource $ManifestDest -Force

# Update manifest with correct executable name
$manifestContent = Get-Content $ManifestDest -Raw
$manifestContent = $manifestContent -replace 'Executable="vTorrent.exe"', 'Executable="vTorrent.exe"'
Set-Content $ManifestDest $manifestContent -NoNewline

# Copy assets
$AssetsSource = Join-Path $PackagingDir "Assets"
$AssetsDest = Join-Path $MsixLayoutDir "Assets"
if (Test-Path $AssetsSource) {
    if (-not (Test-Path $AssetsDest)) {
        New-Item -ItemType Directory -Path $AssetsDest -Force | Out-Null
    }
    Copy-Item "$AssetsSource\*" $AssetsDest -Recurse -Force
}

Write-Host "[+] MSIX layout created: $MsixLayoutDir" -ForegroundColor Green

# Step 3: Create MSIX package
Write-Host ""
Write-Host "[*] Creating MSIX package..." -ForegroundColor Yellow

if (-not (Test-Path $MsixOutputDir)) {
    New-Item -ItemType Directory -Path $MsixOutputDir -Force | Out-Null
}

$MsixFile = Join-Path $MsixOutputDir "vTorrent_1.0.0.0_$Platform.msix"

& $MakeAppx pack /d $MsixLayoutDir /p $MsixFile /o
if ($LASTEXITCODE -ne 0) {
    Write-Host "[!] makeappx failed!" -ForegroundColor Red
    exit 1
}

Write-Host "[+] MSIX created: $MsixFile" -ForegroundColor Green

# Step 4: Sign the package (optional)
if ($Sign) {
    Write-Host ""
    Write-Host "[*] Signing MSIX package..." -ForegroundColor Yellow

    if ([string]::IsNullOrEmpty($CertificateThumbprint)) {
        # Create self-signed certificate
        Write-Host "    Creating self-signed certificate..." -ForegroundColor Cyan

        $cert = Get-ChildItem -Path Cert:\CurrentUser\My | Where-Object { $_.Subject -eq "CN=vTorrent" } | Select-Object -First 1

        if (-not $cert) {
            $cert = New-SelfSignedCertificate `
                -Type Custom `
                -Subject "CN=vTorrent" `
                -KeyUsage DigitalSignature `
                -FriendlyName "vTorrent Dev Certificate" `
                -CertStoreLocation "Cert:\CurrentUser\My" `
                -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")

            Write-Host "    Created certificate: $($cert.Thumbprint)" -ForegroundColor Green

            # Install to Trusted Root for local testing
            Write-Host "    Installing to Trusted Root (requires elevation)..." -ForegroundColor Cyan
            try {
                $certBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
                $trustedRoot = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certBytes)
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store("Root", "CurrentUser")
                $store.Open("ReadWrite")
                $store.Add($trustedRoot)
                $store.Close()
                Write-Host "    Certificate installed to Trusted Root" -ForegroundColor Green
            } catch {
                Write-Host "    [!] Could not install to Trusted Root: $_" -ForegroundColor Yellow
                Write-Host "    You may need to manually trust the certificate to install the MSIX" -ForegroundColor Yellow
            }
        } else {
            Write-Host "    Using existing certificate: $($cert.Thumbprint)" -ForegroundColor Green
        }

        $CertificateThumbprint = $cert.Thumbprint
    }

    # Sign the package
    & $SignTool sign /fd SHA256 /sha1 $CertificateThumbprint /td SHA256 $MsixFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[!] Signing failed!" -ForegroundColor Red
        exit 1
    }

    Write-Host "[+] Package signed successfully" -ForegroundColor Green
}

# Summary
Write-Host ""
Write-Host "============================================" -ForegroundColor Green
Write-Host " Build Complete!" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Green
Write-Host ""
Write-Host "  MSIX Package: $MsixFile" -ForegroundColor White
Write-Host "  Size: $([math]::Round((Get-Item $MsixFile).Length / 1MB, 2)) MB" -ForegroundColor White
Write-Host ""

if (-not $Sign) {
    Write-Host "  [!] Package is UNSIGNED. To install:" -ForegroundColor Yellow
    Write-Host "      1. Run this script with -Sign flag, OR" -ForegroundColor Yellow
    Write-Host "      2. Enable Developer Mode in Windows Settings" -ForegroundColor Yellow
    Write-Host ""
}

Write-Host "  To install: Double-click the .msix file" -ForegroundColor Cyan
Write-Host "  Or use: Add-AppPackage -Path `"$MsixFile`"" -ForegroundColor Cyan
Write-Host ""
