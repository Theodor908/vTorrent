#Requires -Version 5.1
<#
.SYNOPSIS
    Generates MSIX package visual assets from existing vTorrent icons.

.DESCRIPTION
    This script copies and converts existing icons from Assets/Images/ to the
    packaging/windows/Assets/ folder in the formats required for MSIX packaging.

    Source icons used:
    - Assets/Images/dark_logo.svg (main logo, scalable - requires Inkscape for conversion)
    - Assets/Images/logo256x256.ico (256x256 icon)
    - Assets/Images/dark_logo16x16.ico, dark_logo32x32.ico, dark_logo128x128.ico
    - Assets/Images/mutate_logo256x256.ico (for torrent file icon)

.PARAMETER UseImageMagick
    Use ImageMagick for conversions. Default is to use System.Drawing.

.PARAMETER UseSvg
    Convert from SVG source (requires Inkscape installed).

.EXAMPLE
    .\copy-assets.ps1
    # Generates assets using existing ICO files

.EXAMPLE
    .\copy-assets.ps1 -UseSvg
    # Generates assets from SVG source (requires Inkscape)

.NOTES
    Author: vTorrent
    Requires: PowerShell 5.1+, optionally ImageMagick or Inkscape
#>

[CmdletBinding()]
param(
    [switch]$UseImageMagick,
    [switch]$UseSvg
)

$ErrorActionPreference = "Stop"

# Resolve paths
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = (Resolve-Path "$ScriptDir\..\..").Path
$SourceImagesDir = Join-Path $ProjectRoot "Assets\Images"
$TargetAssetsDir = Join-Path $ScriptDir "Assets"

# Source icon files
$SourceIcons = @{
    "Logo256"       = Join-Path $SourceImagesDir "logo256x256.ico"
    "Logo128"       = Join-Path $SourceImagesDir "dark_logo128x128.ico"
    "Logo32"        = Join-Path $SourceImagesDir "dark_logo32x32.ico"
    "Logo16"        = Join-Path $SourceImagesDir "dark_logo16x16.ico"
    "MutateLogo256" = Join-Path $SourceImagesDir "mutate_logo256x256.ico"
    "LogoSvg"       = Join-Path $SourceImagesDir "dark_logo.svg"
}

# Required MSIX assets and their sizes
$RequiredAssets = @{
    # Store Logo
    "StoreLogo.png"                    = @{ Width = 50; Height = 50; Source = "Logo256" }

    # Square 44x44 (taskbar, small tile) and scales
    "Square44x44Logo.png"              = @{ Width = 44; Height = 44; Source = "Logo256" }
    "Square44x44Logo.scale-100.png"    = @{ Width = 44; Height = 44; Source = "Logo256" }
    "Square44x44Logo.scale-125.png"    = @{ Width = 55; Height = 55; Source = "Logo256" }
    "Square44x44Logo.scale-150.png"    = @{ Width = 66; Height = 66; Source = "Logo256" }
    "Square44x44Logo.scale-200.png"    = @{ Width = 88; Height = 88; Source = "Logo256" }
    "Square44x44Logo.scale-400.png"    = @{ Width = 176; Height = 176; Source = "Logo256" }

    # Target sizes for taskbar (no padding)
    "Square44x44Logo.targetsize-16.png"  = @{ Width = 16; Height = 16; Source = "Logo16" }
    "Square44x44Logo.targetsize-24.png"  = @{ Width = 24; Height = 24; Source = "Logo32" }
    "Square44x44Logo.targetsize-32.png"  = @{ Width = 32; Height = 32; Source = "Logo32" }
    "Square44x44Logo.targetsize-48.png"  = @{ Width = 48; Height = 48; Source = "Logo128" }
    "Square44x44Logo.targetsize-256.png" = @{ Width = 256; Height = 256; Source = "Logo256" }

    # Square 71x71 (small start tile)
    "Square71x71Logo.png"              = @{ Width = 71; Height = 71; Source = "Logo256" }

    # Square 150x150 (medium tile) and scales
    "Square150x150Logo.png"            = @{ Width = 150; Height = 150; Source = "Logo256" }
    "Square150x150Logo.scale-100.png"  = @{ Width = 150; Height = 150; Source = "Logo256" }
    "Square150x150Logo.scale-125.png"  = @{ Width = 188; Height = 188; Source = "Logo256" }
    "Square150x150Logo.scale-150.png"  = @{ Width = 225; Height = 225; Source = "Logo256" }
    "Square150x150Logo.scale-200.png"  = @{ Width = 300; Height = 300; Source = "Logo256" }
    "Square150x150Logo.scale-400.png"  = @{ Width = 600; Height = 600; Source = "Logo256" }

    # Wide 310x150 (wide tile)
    "Wide310x150Logo.png"              = @{ Width = 310; Height = 150; Source = "Logo256"; Wide = $true }

    # Square 310x310 (large tile)
    "Square310x310Logo.png"            = @{ Width = 310; Height = 310; Source = "Logo256" }

    # Splash screen
    "SplashScreen.png"                 = @{ Width = 620; Height = 300; Source = "Logo256"; Splash = $true }

    # File association icons
    "TorrentFileIcon.png"              = @{ Width = 44; Height = 44; Source = "MutateLogo256" }
    "MagnetLinkIcon.png"               = @{ Width = 44; Height = 44; Source = "Logo128" }
}

function Test-ImageMagick {
    try {
        $null = & magick --version 2>$null
        return $true
    } catch {
        return $false
    }
}

function Test-Inkscape {
    try {
        $null = & inkscape --version 2>$null
        return $true
    } catch {
        return $false
    }
}

function Convert-IcoToPng-SystemDrawing {
    param(
        [string]$IcoPath,
        [string]$PngPath,
        [int]$Width,
        [int]$Height,
        [bool]$Wide = $false,
        [bool]$Splash = $false
    )

    Add-Type -AssemblyName System.Drawing

    try {
        $icon = New-Object System.Drawing.Icon($IcoPath)

        # Create bitmap at target size
        $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)

        # Set high quality rendering
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        # Fill with transparent background (or dark background for splash)
        if ($Splash) {
            $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 26, 26, 46))
            $graphics.FillRectangle($brush, 0, 0, $Width, $Height)
            $brush.Dispose()
        } else {
            $graphics.Clear([System.Drawing.Color]::Transparent)
        }

        # Calculate icon position (centered)
        if ($Wide -or $Splash) {
            # For wide tiles, center the icon and make it fit the height
            $iconSize = [Math]::Min($Width, $Height) - 20
            $iconX = ($Width - $iconSize) / 2
            $iconY = ($Height - $iconSize) / 2
        } else {
            $iconSize = [Math]::Min($Width, $Height)
            $iconX = 0
            $iconY = 0
        }

        $rect = New-Object System.Drawing.Rectangle($iconX, $iconY, $iconSize, $iconSize)
        $graphics.DrawIcon($icon, $rect)

        # Save as PNG
        $bitmap.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)

        $graphics.Dispose()
        $bitmap.Dispose()
        $icon.Dispose()

        return $true
    } catch {
        Write-Warning "Failed to convert $IcoPath to $PngPath using System.Drawing: $_"
        return $false
    }
}

function Convert-IcoToPng-ImageMagick {
    param(
        [string]$IcoPath,
        [string]$PngPath,
        [int]$Width,
        [int]$Height,
        [bool]$Wide = $false,
        [bool]$Splash = $false
    )

    try {
        if ($Wide) {
            # For wide logos, resize to fit height and center on transparent background
            $tempFile = [System.IO.Path]::GetTempFileName() + ".png"
            & magick convert "$IcoPath[0]" -resize "x$Height" -background transparent -gravity center -extent "${Width}x${Height}" $PngPath
        } elseif ($Splash) {
            # For splash, center on dark background
            & magick convert "$IcoPath[0]" -resize "x$($Height - 40)" -background "#1a1a2e" -gravity center -extent "${Width}x${Height}" $PngPath
        } else {
            # Standard resize
            & magick convert "$IcoPath[0]" -resize "${Width}x${Height}" -background transparent -gravity center -extent "${Width}x${Height}" $PngPath
        }
        return $LASTEXITCODE -eq 0
    } catch {
        Write-Warning "Failed to convert $IcoPath to $PngPath using ImageMagick: $_"
        return $false
    }
}

function Convert-SvgToPng-Inkscape {
    param(
        [string]$SvgPath,
        [string]$PngPath,
        [int]$Width,
        [int]$Height,
        [bool]$Wide = $false,
        [bool]$Splash = $false
    )

    try {
        if ($Wide -or $Splash) {
            # For wide/splash, export at height and we'll need to composite
            $iconSize = $Height - 20
            $tempFile = [System.IO.Path]::GetTempFileName() + ".png"
            & inkscape $SvgPath --export-type=png --export-width=$iconSize --export-height=$iconSize --export-filename=$tempFile

            if ($LASTEXITCODE -eq 0) {
                $bgColor = if ($Splash) { "#1a1a2e" } else { "transparent" }
                & magick convert $tempFile -background $bgColor -gravity center -extent "${Width}x${Height}" $PngPath
                Remove-Item $tempFile -ErrorAction SilentlyContinue
            }
        } else {
            & inkscape $SvgPath --export-type=png --export-width=$Width --export-height=$Height --export-filename=$PngPath
        }
        return $LASTEXITCODE -eq 0
    } catch {
        Write-Warning "Failed to convert $SvgPath to $PngPath using Inkscape: $_"
        return $false
    }
}

# Main script execution
Write-Host "vTorrent MSIX Asset Generator" -ForegroundColor Cyan
Write-Host "=============================" -ForegroundColor Cyan
Write-Host ""

# Check source files exist
Write-Host "Checking source icon files..." -ForegroundColor Yellow
foreach ($name in $SourceIcons.Keys) {
    $path = $SourceIcons[$name]
    if (Test-Path $path) {
        Write-Host "  [OK] $name : $path" -ForegroundColor Green
    } else {
        Write-Host "  [MISSING] $name : $path" -ForegroundColor Red
    }
}
Write-Host ""

# Check tools
$hasImageMagick = Test-ImageMagick
$hasInkscape = Test-Inkscape

Write-Host "Available tools:" -ForegroundColor Yellow
Write-Host "  ImageMagick: $(if ($hasImageMagick) { 'Available' } else { 'Not found' })" -ForegroundColor $(if ($hasImageMagick) { 'Green' } else { 'Gray' })
Write-Host "  Inkscape: $(if ($hasInkscape) { 'Available' } else { 'Not found' })" -ForegroundColor $(if ($hasInkscape) { 'Green' } else { 'Gray' })
Write-Host ""

# Determine conversion method
$useIM = $UseImageMagick -and $hasImageMagick
$useSvgSource = $UseSvg -and $hasInkscape

if ($UseSvg -and -not $hasInkscape) {
    Write-Warning "SVG conversion requested but Inkscape not found. Falling back to ICO source."
}
if ($UseImageMagick -and -not $hasImageMagick) {
    Write-Warning "ImageMagick requested but not found. Falling back to System.Drawing."
}

Write-Host "Conversion method: $(if ($useIM) { 'ImageMagick' } elseif ($useSvgSource) { 'Inkscape' } else { 'System.Drawing' })" -ForegroundColor Cyan
Write-Host ""

# Create target directory
if (-not (Test-Path $TargetAssetsDir)) {
    New-Item -ItemType Directory -Path $TargetAssetsDir -Force | Out-Null
    Write-Host "Created directory: $TargetAssetsDir" -ForegroundColor Yellow
}

# Generate assets
Write-Host "Generating MSIX assets..." -ForegroundColor Yellow
$successCount = 0
$failCount = 0

foreach ($assetName in $RequiredAssets.Keys) {
    $asset = $RequiredAssets[$assetName]
    $targetPath = Join-Path $TargetAssetsDir $assetName

    # Determine source file
    if ($useSvgSource) {
        $sourcePath = $SourceIcons["LogoSvg"]
    } else {
        $sourcePath = $SourceIcons[$asset.Source]
    }

    if (-not (Test-Path $sourcePath)) {
        Write-Host "  [SKIP] $assetName (source not found: $sourcePath)" -ForegroundColor Yellow
        $failCount++
        continue
    }

    $isWide = $asset.ContainsKey('Wide') -and $asset.Wide
    $isSplash = $asset.ContainsKey('Splash') -and $asset.Splash

    $success = $false
    if ($useSvgSource -and $hasInkscape) {
        $success = Convert-SvgToPng-Inkscape -SvgPath $sourcePath -PngPath $targetPath `
            -Width $asset.Width -Height $asset.Height -Wide $isWide -Splash $isSplash
    } elseif ($useIM) {
        $success = Convert-IcoToPng-ImageMagick -IcoPath $sourcePath -PngPath $targetPath `
            -Width $asset.Width -Height $asset.Height -Wide $isWide -Splash $isSplash
    } else {
        $success = Convert-IcoToPng-SystemDrawing -IcoPath $sourcePath -PngPath $targetPath `
            -Width $asset.Width -Height $asset.Height -Wide $isWide -Splash $isSplash
    }

    if ($success -and (Test-Path $targetPath)) {
        Write-Host "  [OK] $assetName ($($asset.Width)x$($asset.Height))" -ForegroundColor Green
        $successCount++
    } else {
        Write-Host "  [FAIL] $assetName" -ForegroundColor Red
        $failCount++
    }
}

Write-Host ""
Write-Host "=============================" -ForegroundColor Cyan
Write-Host "Asset generation complete!" -ForegroundColor Cyan
Write-Host "  Success: $successCount" -ForegroundColor Green
Write-Host "  Failed: $failCount" -ForegroundColor $(if ($failCount -gt 0) { 'Red' } else { 'Green' })
Write-Host ""
Write-Host "Generated assets are in: $TargetAssetsDir" -ForegroundColor Yellow

if ($failCount -gt 0) {
    Write-Host ""
    Write-Host "MANUAL STEPS REQUIRED:" -ForegroundColor Yellow
    Write-Host "Some assets could not be generated automatically." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Option 1: Install ImageMagick and run:" -ForegroundColor White
    Write-Host "  .\copy-assets.ps1 -UseImageMagick" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Option 2: Install Inkscape and run:" -ForegroundColor White
    Write-Host "  .\copy-assets.ps1 -UseSvg" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Option 3: Manually create PNG assets using the sizes in this script" -ForegroundColor White
    Write-Host "  or use an online ICO to PNG converter." -ForegroundColor Gray
}
