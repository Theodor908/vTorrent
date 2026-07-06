#!/bin/bash
#
# generate-icns.sh - Generate macOS .icns icon files from vTorrent source assets
#
# This script converts the existing SVG/ICO icons from Assets/Images/ to
# the macOS .icns format required for the application bundle.
#
# Source icons:
#   - Assets/Images/dark_logo.svg (main application icon)
#   - Assets/Images/mutate_logo256x256.ico (torrent file icon variant)
#
# Output:
#   - packaging/macos/Assets/AppIcon.icns
#   - packaging/macos/Assets/TorrentIcon.icns
#
# Requirements:
#   - macOS with iconutil (built-in)
#   - One of: Inkscape, ImageMagick (convert), or rsvg-convert for SVG conversion
#
# Usage:
#   ./generate-icns.sh
#   ./generate-icns.sh --use-imagemagick
#   ./generate-icns.sh --use-rsvg
#

set -e

# Get script directory and project root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SOURCE_DIR="$PROJECT_ROOT/Assets/Images"
OUTPUT_DIR="$SCRIPT_DIR/Assets"

# Source files
DARK_LOGO_SVG="$SOURCE_DIR/dark_logo.svg"
LOGO_256_ICO="$SOURCE_DIR/logo256x256.ico"
MUTATE_LOGO_ICO="$SOURCE_DIR/mutate_logo256x256.ico"
DARK_LOGO_128_ICO="$SOURCE_DIR/dark_logo128x128.ico"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
USE_IMAGEMAGICK=false
USE_RSVG=false

while [[ "$#" -gt 0 ]]; do
    case $1 in
        --use-imagemagick) USE_IMAGEMAGICK=true ;;
        --use-rsvg) USE_RSVG=true ;;
        -h|--help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --use-imagemagick    Use ImageMagick for SVG conversion"
            echo "  --use-rsvg           Use rsvg-convert for SVG conversion"
            echo "  -h, --help           Show this help message"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
    shift
done

echo -e "${CYAN}vTorrent macOS Icon Generator${NC}"
echo -e "${CYAN}==============================${NC}"
echo ""

# Check if we're on macOS
if [[ "$(uname)" != "Darwin" ]]; then
    echo -e "${YELLOW}Warning: This script is designed for macOS.${NC}"
    echo -e "${YELLOW}The 'iconutil' command is only available on macOS.${NC}"
    echo ""
    echo "For cross-platform development:"
    echo "  1. Generate icons on a Mac and commit them"
    echo "  2. Or use a tool like 'png2icns' or online converters"
    echo ""
fi

# Check for required tools
check_tool() {
    if command -v "$1" &> /dev/null; then
        echo -e "  ${GREEN}[OK]${NC} $1"
        return 0
    else
        echo -e "  ${RED}[NOT FOUND]${NC} $1"
        return 1
    fi
}

echo "Checking required tools..."
HAS_ICONUTIL=false
HAS_INKSCAPE=false
HAS_IMAGEMAGICK=false
HAS_RSVG=false
HAS_SIPS=false

check_tool "iconutil" && HAS_ICONUTIL=true
check_tool "inkscape" && HAS_INKSCAPE=true
check_tool "convert" && HAS_IMAGEMAGICK=true
check_tool "rsvg-convert" && HAS_RSVG=true
check_tool "sips" && HAS_SIPS=true
echo ""

# Check source files
echo "Checking source files..."
if [[ -f "$DARK_LOGO_SVG" ]]; then
    echo -e "  ${GREEN}[OK]${NC} dark_logo.svg"
else
    echo -e "  ${RED}[MISSING]${NC} dark_logo.svg"
fi

if [[ -f "$LOGO_256_ICO" ]]; then
    echo -e "  ${GREEN}[OK]${NC} logo256x256.ico"
else
    echo -e "  ${YELLOW}[MISSING]${NC} logo256x256.ico (optional fallback)"
fi

if [[ -f "$MUTATE_LOGO_ICO" ]]; then
    echo -e "  ${GREEN}[OK]${NC} mutate_logo256x256.ico"
else
    echo -e "  ${YELLOW}[MISSING]${NC} mutate_logo256x256.ico (optional)"
fi
echo ""

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Function to convert SVG to PNG at specific size
svg_to_png() {
    local svg_path="$1"
    local png_path="$2"
    local size="$3"

    if $USE_RSVG && $HAS_RSVG; then
        rsvg-convert -w "$size" -h "$size" "$svg_path" -o "$png_path"
    elif $USE_IMAGEMAGICK && $HAS_IMAGEMAGICK; then
        convert -background none -resize "${size}x${size}" "$svg_path" "$png_path"
    elif $HAS_INKSCAPE; then
        inkscape "$svg_path" --export-type=png --export-width="$size" --export-height="$size" --export-filename="$png_path" 2>/dev/null
    elif $HAS_IMAGEMAGICK; then
        convert -background none -resize "${size}x${size}" "$svg_path" "$png_path"
    elif $HAS_RSVG; then
        rsvg-convert -w "$size" -h "$size" "$svg_path" -o "$png_path"
    else
        echo -e "${RED}Error: No SVG conversion tool available.${NC}"
        echo "Please install one of: Inkscape, ImageMagick, or librsvg"
        return 1
    fi
}

# Function to convert ICO to PNG at specific size (fallback)
ico_to_png() {
    local ico_path="$1"
    local png_path="$2"
    local size="$3"

    if $HAS_IMAGEMAGICK; then
        convert "${ico_path}[0]" -resize "${size}x${size}" "$png_path"
    elif $HAS_SIPS; then
        # sips can't read ICO directly, so this is a limited fallback
        echo -e "${YELLOW}Warning: sips cannot convert ICO files directly.${NC}"
        return 1
    else
        echo -e "${RED}Error: No ICO conversion tool available.${NC}"
        return 1
    fi
}

# Function to create iconset from source
create_iconset() {
    local name="$1"
    local source_svg="$2"
    local source_ico="$3"  # fallback
    local iconset_dir="$OUTPUT_DIR/${name}.iconset"

    echo -e "${YELLOW}Creating ${name}.icns...${NC}"

    # Create iconset directory
    rm -rf "$iconset_dir"
    mkdir -p "$iconset_dir"

    # Required sizes for macOS iconset
    # Format: filename size
    local sizes=(
        "icon_16x16.png 16"
        "icon_16x16@2x.png 32"
        "icon_32x32.png 32"
        "icon_32x32@2x.png 64"
        "icon_128x128.png 128"
        "icon_128x128@2x.png 256"
        "icon_256x256.png 256"
        "icon_256x256@2x.png 512"
        "icon_512x512.png 512"
        "icon_512x512@2x.png 1024"
    )

    local use_svg=false
    local use_ico=false

    if [[ -f "$source_svg" ]]; then
        use_svg=true
    elif [[ -f "$source_ico" ]]; then
        use_ico=true
    else
        echo -e "${RED}Error: No source file found for ${name}${NC}"
        return 1
    fi

    for entry in "${sizes[@]}"; do
        local filename=$(echo "$entry" | cut -d' ' -f1)
        local size=$(echo "$entry" | cut -d' ' -f2)
        local png_path="$iconset_dir/$filename"

        if $use_svg; then
            if svg_to_png "$source_svg" "$png_path" "$size"; then
                echo -e "  ${GREEN}[OK]${NC} $filename (${size}x${size})"
            else
                echo -e "  ${RED}[FAIL]${NC} $filename"
            fi
        elif $use_ico; then
            if ico_to_png "$source_ico" "$png_path" "$size"; then
                echo -e "  ${GREEN}[OK]${NC} $filename (${size}x${size})"
            else
                echo -e "  ${RED}[FAIL]${NC} $filename"
            fi
        fi
    done

    # Generate icns file
    if $HAS_ICONUTIL; then
        if iconutil -c icns "$iconset_dir" -o "$OUTPUT_DIR/${name}.icns"; then
            echo -e "  ${GREEN}[OK]${NC} ${name}.icns created"
        else
            echo -e "  ${RED}[FAIL]${NC} Failed to create ${name}.icns"
            return 1
        fi

        # Cleanup iconset directory
        rm -rf "$iconset_dir"
    else
        echo -e "${YELLOW}Note: iconutil not available. Iconset directory preserved.${NC}"
        echo "  Copy $iconset_dir to a Mac and run:"
        echo "  iconutil -c icns ${name}.iconset -o ${name}.icns"
    fi

    return 0
}

# Generate AppIcon.icns from dark_logo.svg
echo ""
if create_iconset "AppIcon" "$DARK_LOGO_SVG" "$LOGO_256_ICO"; then
    echo -e "${GREEN}AppIcon.icns generated successfully!${NC}"
else
    echo -e "${RED}Failed to generate AppIcon.icns${NC}"
fi

# Generate TorrentIcon.icns (use same source or mutate variant)
echo ""
if create_iconset "TorrentIcon" "$DARK_LOGO_SVG" "$MUTATE_LOGO_ICO"; then
    echo -e "${GREEN}TorrentIcon.icns generated successfully!${NC}"
else
    echo -e "${RED}Failed to generate TorrentIcon.icns${NC}"
fi

echo ""
echo -e "${CYAN}==============================${NC}"
echo -e "${CYAN}Icon generation complete!${NC}"
echo ""
echo "Output directory: $OUTPUT_DIR"
echo ""

if [[ -f "$OUTPUT_DIR/AppIcon.icns" ]]; then
    echo -e "  ${GREEN}[OK]${NC} AppIcon.icns"
else
    echo -e "  ${RED}[MISSING]${NC} AppIcon.icns"
fi

if [[ -f "$OUTPUT_DIR/TorrentIcon.icns" ]]; then
    echo -e "  ${GREEN}[OK]${NC} TorrentIcon.icns"
else
    echo -e "  ${RED}[MISSING]${NC} TorrentIcon.icns"
fi

echo ""
echo "Copy these .icns files to your app bundle's Resources folder:"
echo "  vTorrent.app/Contents/Resources/AppIcon.icns"
echo "  vTorrent.app/Contents/Resources/TorrentIcon.icns"
