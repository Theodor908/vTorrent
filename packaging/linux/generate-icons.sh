#!/bin/bash
#
# generate-icons.sh - Generate Linux icons from vTorrent source assets
#
# This script converts the existing SVG/ICO icons from Assets/Images/ to
# the PNG formats required for Linux desktop integration.
#
# Source icons:
#   - Assets/Images/dark_logo.svg (main application icon)
#   - Assets/Images/dark_logo*.ico (fallback for various sizes)
#   - Assets/Images/logo256x256.ico (256x256 fallback)
#   - Assets/Images/mutate_logo256x256.ico (for MIME type icons)
#
# Output:
#   - packaging/linux/icons/vtorrent-*.png (various sizes)
#   - packaging/linux/icons/vtorrent.svg (scalable)
#   - packaging/linux/icons/hicolor/ (ready-to-install structure)
#
# Requirements:
#   One of: Inkscape, ImageMagick (convert), or rsvg-convert
#
# Usage:
#   ./generate-icons.sh
#   ./generate-icons.sh --use-imagemagick
#   ./generate-icons.sh --use-inkscape
#   ./generate-icons.sh --use-rsvg
#   ./generate-icons.sh --use-ico   # Use ICO files instead of SVG
#   ./generate-icons.sh --install   # Install to system (requires root)
#

set -e

# Get script directory and project root
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
SOURCE_DIR="$PROJECT_ROOT/Assets/Images"
OUTPUT_DIR="$SCRIPT_DIR/icons"
HICOLOR_DIR="$OUTPUT_DIR/hicolor"

# Source files
DARK_LOGO_SVG="$SOURCE_DIR/dark_logo.svg"
LOGO_256_ICO="$SOURCE_DIR/logo256x256.ico"
LOGO_128_ICO="$SOURCE_DIR/dark_logo128x128.ico"
LOGO_32_ICO="$SOURCE_DIR/dark_logo32x32.ico"
LOGO_16_ICO="$SOURCE_DIR/dark_logo16x16.ico"
MUTATE_LOGO_ICO="$SOURCE_DIR/mutate_logo256x256.ico"

# Required icon sizes
SIZES=(16 32 48 64 128 256 512)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Parse arguments
USE_IMAGEMAGICK=false
USE_INKSCAPE=false
USE_RSVG=false
USE_ICO=false
DO_INSTALL=false
OPTIMIZE=false

while [[ "$#" -gt 0 ]]; do
    case $1 in
        --use-imagemagick) USE_IMAGEMAGICK=true ;;
        --use-inkscape) USE_INKSCAPE=true ;;
        --use-rsvg) USE_RSVG=true ;;
        --use-ico) USE_ICO=true ;;
        --install) DO_INSTALL=true ;;
        --optimize) OPTIMIZE=true ;;
        -h|--help)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --use-imagemagick    Use ImageMagick for conversion"
            echo "  --use-inkscape       Use Inkscape for SVG conversion"
            echo "  --use-rsvg           Use rsvg-convert for SVG conversion"
            echo "  --use-ico            Use ICO files instead of SVG (fallback)"
            echo "  --install            Install icons to system (requires root)"
            echo "  --optimize           Optimize PNG files with optipng"
            echo "  -h, --help           Show this help message"
            exit 0
            ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
    shift
done

echo -e "${CYAN}vTorrent Linux Icon Generator${NC}"
echo -e "${CYAN}==============================${NC}"
echo ""

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

echo "Checking available tools..."
HAS_INKSCAPE=false
HAS_IMAGEMAGICK=false
HAS_RSVG=false
HAS_OPTIPNG=false

check_tool "inkscape" && HAS_INKSCAPE=true
check_tool "convert" && HAS_IMAGEMAGICK=true
check_tool "rsvg-convert" && HAS_RSVG=true
check_tool "optipng" && HAS_OPTIPNG=true
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
    echo -e "  ${YELLOW}[MISSING]${NC} logo256x256.ico (fallback)"
fi

if [[ -f "$LOGO_128_ICO" ]]; then
    echo -e "  ${GREEN}[OK]${NC} dark_logo128x128.ico"
else
    echo -e "  ${YELLOW}[MISSING]${NC} dark_logo128x128.ico (fallback)"
fi

if [[ -f "$MUTATE_LOGO_ICO" ]]; then
    echo -e "  ${GREEN}[OK]${NC} mutate_logo256x256.ico"
else
    echo -e "  ${YELLOW}[MISSING]${NC} mutate_logo256x256.ico (for MIME icons)"
fi
echo ""

# Determine conversion tool
CONVERTER=""
if $USE_INKSCAPE && $HAS_INKSCAPE; then
    CONVERTER="inkscape"
elif $USE_RSVG && $HAS_RSVG; then
    CONVERTER="rsvg"
elif $USE_IMAGEMAGICK && $HAS_IMAGEMAGICK; then
    CONVERTER="imagemagick"
elif $HAS_INKSCAPE; then
    CONVERTER="inkscape"
elif $HAS_RSVG; then
    CONVERTER="rsvg"
elif $HAS_IMAGEMAGICK; then
    CONVERTER="imagemagick"
else
    echo -e "${RED}Error: No SVG conversion tool available.${NC}"
    echo "Please install one of: Inkscape, ImageMagick (with SVG support), or librsvg"
    echo ""
    echo "On Ubuntu/Debian: sudo apt install inkscape"
    echo "On Fedora: sudo dnf install inkscape"
    echo "On Arch: sudo pacman -S inkscape"
    exit 1
fi

echo -e "Using converter: ${CYAN}${CONVERTER}${NC}"
echo ""

# Create output directories
mkdir -p "$OUTPUT_DIR"

# Function to convert SVG to PNG
svg_to_png() {
    local svg_path="$1"
    local png_path="$2"
    local size="$3"

    case $CONVERTER in
        inkscape)
            inkscape "$svg_path" --export-type=png --export-width="$size" --export-height="$size" --export-filename="$png_path" 2>/dev/null
            ;;
        rsvg)
            rsvg-convert -w "$size" -h "$size" "$svg_path" -o "$png_path"
            ;;
        imagemagick)
            convert -background none -resize "${size}x${size}" "$svg_path" "$png_path"
            ;;
    esac
}

# Function to convert ICO to PNG
ico_to_png() {
    local ico_path="$1"
    local png_path="$2"
    local size="$3"

    if $HAS_IMAGEMAGICK; then
        convert "${ico_path}[0]" -resize "${size}x${size}" "$png_path"
    else
        echo -e "${RED}Error: ImageMagick required for ICO conversion${NC}"
        return 1
    fi
}

# Function to get best ICO source for a given size
get_ico_source() {
    local size="$1"

    if [[ $size -le 16 ]] && [[ -f "$LOGO_16_ICO" ]]; then
        echo "$LOGO_16_ICO"
    elif [[ $size -le 32 ]] && [[ -f "$LOGO_32_ICO" ]]; then
        echo "$LOGO_32_ICO"
    elif [[ $size -le 128 ]] && [[ -f "$LOGO_128_ICO" ]]; then
        echo "$LOGO_128_ICO"
    elif [[ -f "$LOGO_256_ICO" ]]; then
        echo "$LOGO_256_ICO"
    else
        echo ""
    fi
}

# Generate icons
echo -e "${YELLOW}Generating icons...${NC}"
SUCCESS_COUNT=0
FAIL_COUNT=0

for size in "${SIZES[@]}"; do
    png_path="$OUTPUT_DIR/vtorrent-${size}x${size}.png"

    if $USE_ICO || ! [[ -f "$DARK_LOGO_SVG" ]]; then
        # Use ICO source
        ico_source=$(get_ico_source $size)
        if [[ -n "$ico_source" ]] && [[ -f "$ico_source" ]]; then
            if ico_to_png "$ico_source" "$png_path" "$size"; then
                echo -e "  ${GREEN}[OK]${NC} vtorrent-${size}x${size}.png (from ICO)"
                ((SUCCESS_COUNT++))
            else
                echo -e "  ${RED}[FAIL]${NC} vtorrent-${size}x${size}.png"
                ((FAIL_COUNT++))
            fi
        else
            echo -e "  ${RED}[SKIP]${NC} vtorrent-${size}x${size}.png (no ICO source)"
            ((FAIL_COUNT++))
        fi
    else
        # Use SVG source
        if svg_to_png "$DARK_LOGO_SVG" "$png_path" "$size"; then
            echo -e "  ${GREEN}[OK]${NC} vtorrent-${size}x${size}.png"
            ((SUCCESS_COUNT++))
        else
            echo -e "  ${RED}[FAIL]${NC} vtorrent-${size}x${size}.png"
            ((FAIL_COUNT++))
        fi
    fi
done

# Copy SVG for scalable icon
if [[ -f "$DARK_LOGO_SVG" ]]; then
    cp "$DARK_LOGO_SVG" "$OUTPUT_DIR/vtorrent.svg"
    echo -e "  ${GREEN}[OK]${NC} vtorrent.svg (scalable)"
    ((SUCCESS_COUNT++))
fi

# Generate MIME type icons (for .torrent files)
echo ""
echo -e "${YELLOW}Generating MIME type icons...${NC}"
MIME_SIZES=(16 32 48 64 128 256)

for size in "${MIME_SIZES[@]}"; do
    png_path="$OUTPUT_DIR/application-x-bittorrent-${size}x${size}.png"

    if [[ -f "$MUTATE_LOGO_ICO" ]] && $HAS_IMAGEMAGICK; then
        if ico_to_png "$MUTATE_LOGO_ICO" "$png_path" "$size"; then
            echo -e "  ${GREEN}[OK]${NC} application-x-bittorrent-${size}x${size}.png"
        else
            echo -e "  ${RED}[FAIL]${NC} application-x-bittorrent-${size}x${size}.png"
        fi
    elif [[ -f "$DARK_LOGO_SVG" ]]; then
        if svg_to_png "$DARK_LOGO_SVG" "$png_path" "$size"; then
            echo -e "  ${GREEN}[OK]${NC} application-x-bittorrent-${size}x${size}.png"
        else
            echo -e "  ${RED}[FAIL]${NC} application-x-bittorrent-${size}x${size}.png"
        fi
    else
        echo -e "  ${YELLOW}[SKIP]${NC} application-x-bittorrent-${size}x${size}.png (no source)"
    fi
done

# Optimize PNGs if requested
if $OPTIMIZE && $HAS_OPTIPNG; then
    echo ""
    echo -e "${YELLOW}Optimizing PNG files...${NC}"
    for png in "$OUTPUT_DIR"/*.png; do
        if [[ -f "$png" ]]; then
            optipng -quiet -o7 "$png"
            echo -e "  ${GREEN}[OK]${NC} Optimized $(basename "$png")"
        fi
    done
fi

# Create hicolor theme structure
echo ""
echo -e "${YELLOW}Creating hicolor theme structure...${NC}"
rm -rf "$HICOLOR_DIR"

for size in "${SIZES[@]}"; do
    dir="$HICOLOR_DIR/${size}x${size}/apps"
    mkdir -p "$dir"

    src="$OUTPUT_DIR/vtorrent-${size}x${size}.png"
    if [[ -f "$src" ]]; then
        cp "$src" "$dir/vTorrent.png"
        echo -e "  ${GREEN}[OK]${NC} hicolor/${size}x${size}/apps/vTorrent.png"
    fi
done

# Scalable
mkdir -p "$HICOLOR_DIR/scalable/apps"
if [[ -f "$OUTPUT_DIR/vtorrent.svg" ]]; then
    cp "$OUTPUT_DIR/vtorrent.svg" "$HICOLOR_DIR/scalable/apps/vTorrent.svg"
    echo -e "  ${GREEN}[OK]${NC} hicolor/scalable/apps/vTorrent.svg"
fi

# MIME type icons in hicolor
for size in "${MIME_SIZES[@]}"; do
    dir="$HICOLOR_DIR/${size}x${size}/mimetypes"
    mkdir -p "$dir"

    src="$OUTPUT_DIR/application-x-bittorrent-${size}x${size}.png"
    if [[ -f "$src" ]]; then
        cp "$src" "$dir/application-x-bittorrent.png"
        echo -e "  ${GREEN}[OK]${NC} hicolor/${size}x${size}/mimetypes/application-x-bittorrent.png"
    fi
done

# Install to system if requested
if $DO_INSTALL; then
    echo ""
    echo -e "${YELLOW}Installing icons to system...${NC}"

    if [[ $EUID -ne 0 ]]; then
        echo -e "${RED}Error: Installation requires root privileges.${NC}"
        echo "Run with: sudo $0 --install"
        exit 1
    fi

    SYSTEM_ICONS="/usr/share/icons/hicolor"

    for size in "${SIZES[@]}"; do
        dir="$SYSTEM_ICONS/${size}x${size}/apps"
        mkdir -p "$dir"

        src="$OUTPUT_DIR/vtorrent-${size}x${size}.png"
        if [[ -f "$src" ]]; then
            cp "$src" "$dir/vTorrent.png"
            echo -e "  ${GREEN}[OK]${NC} Installed ${size}x${size}"
        fi
    done

    # Scalable
    mkdir -p "$SYSTEM_ICONS/scalable/apps"
    if [[ -f "$OUTPUT_DIR/vtorrent.svg" ]]; then
        cp "$OUTPUT_DIR/vtorrent.svg" "$SYSTEM_ICONS/scalable/apps/vTorrent.svg"
        echo -e "  ${GREEN}[OK]${NC} Installed scalable"
    fi

    # Update icon cache
    if command -v gtk-update-icon-cache &> /dev/null; then
        gtk-update-icon-cache -f -t "$SYSTEM_ICONS"
        echo -e "  ${GREEN}[OK]${NC} Updated icon cache"
    fi
fi

# Summary
echo ""
echo -e "${CYAN}==============================${NC}"
echo -e "${CYAN}Icon generation complete!${NC}"
echo ""
echo "Output directory: $OUTPUT_DIR"
echo "Hicolor structure: $HICOLOR_DIR"
echo ""
echo -e "Generated: ${GREEN}$SUCCESS_COUNT${NC} icons"
if [[ $FAIL_COUNT -gt 0 ]]; then
    echo -e "Failed: ${RED}$FAIL_COUNT${NC} icons"
fi

echo ""
echo "To install icons system-wide:"
echo "  sudo cp -r $HICOLOR_DIR/* /usr/share/icons/hicolor/"
echo "  sudo gtk-update-icon-cache -f -t /usr/share/icons/hicolor"
echo ""
echo "Or use: sudo $0 --install"
