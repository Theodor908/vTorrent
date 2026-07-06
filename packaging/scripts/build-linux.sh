#!/bin/bash

# ============================================================================
# vTorrent Linux Build Script
# ============================================================================
#
# This script builds and packages vTorrent for Linux, creating AppImage
# and/or Debian (.deb) packages.
#
# Usage:
#   ./build-linux.sh [options]
#
# Options:
#   -c, --configuration   Build configuration: Debug or Release (default: Release)
#   -a, --arch            Target architecture: x64 or arm64 (default: x64)
#   --all-arch            Build for all architectures (x64 and arm64)
#   --appimage            Create AppImage package
#   --deb                 Create Debian package
#   --all                 Create both AppImage and Debian packages
#   --clean               Clean build artifacts before building
#   --skip-publish        Skip dotnet publish (use existing publish output)
#   --version VERSION     Set version number (default: 1.0.0)
#   -h, --help            Show this help message
#
# Examples:
#   ./build-linux.sh
#   ./build-linux.sh -c Debug
#   ./build-linux.sh --appimage --deb
#   ./build-linux.sh --all-arch --all
#   ./build-linux.sh --version 1.2.3 --all
#
# Requirements:
#   - .NET 10.0 SDK
#   - For AppImage: appimagetool (https://github.com/AppImage/AppImageKit)
#   - For Debian: dpkg-deb
#   - For icon generation: ImageMagick (convert) or Inkscape
#
# ============================================================================

set -euo pipefail

# ============================================================================
# Configuration
# ============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MAIN_PROJECT="$PROJECT_ROOT/vTorrent.csproj"
OUTPUT_DIR="$PROJECT_ROOT/dist/linux"
PUBLISH_DIR="$OUTPUT_DIR/publish"
LINUX_PACKAGING="$PROJECT_ROOT/packaging/linux"
ASSETS_DIR="$PROJECT_ROOT/Assets"
APP_NAME="vTorrent"
APP_NAME_LOWER="vtorrent"
APP_VERSION="1.0.0"
TARGET_FRAMEWORK="net10.0"

# Default options
CONFIGURATION="Release"
ARCHITECTURES=("x64")
BUILD_ALL_ARCH=false
CREATE_APPIMAGE=false
CREATE_DEB=false
CLEAN=false
SKIP_PUBLISH=false

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
GRAY='\033[0;37m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# Track errors for final summary
ERRORS=()
WARNINGS=()

# ============================================================================
# Helper Functions
# ============================================================================

print_header() {
    echo ""
    echo -e "${CYAN}============================================================================${NC}"
    echo -e "${CYAN}${BOLD} $1${NC}"
    echo -e "${CYAN}============================================================================${NC}"
    echo ""
}

print_step() {
    echo -e "${YELLOW}[*] $1${NC}"
}

print_success() {
    echo -e "${GREEN}[+] $1${NC}"
}

print_error() {
    echo -e "${RED}[!] $1${NC}"
    ERRORS+=("$1")
}

print_warning() {
    echo -e "${YELLOW}[!] $1${NC}"
    WARNINGS+=("$1")
}

print_info() {
    echo -e "${GRAY}    $1${NC}"
}

die() {
    print_error "$1"
    exit 1
}

show_help() {
    cat << 'EOF'
vTorrent Linux Build Script

Usage:
  ./build-linux.sh [options]

Options:
  -c, --configuration   Build configuration: Debug or Release (default: Release)
  -a, --arch            Target architecture: x64 or arm64 (default: x64)
  --all-arch            Build for all architectures (x64 and arm64)
  --appimage            Create AppImage package
  --deb                 Create Debian package
  --all                 Create both AppImage and Debian packages
  --clean               Clean build artifacts before building
  --skip-publish        Skip dotnet publish (use existing publish output)
  --version VERSION     Set version number (default: 1.0.0)
  -h, --help            Show this help message

Examples:
  ./build-linux.sh
      Build vTorrent in Release mode for x64.

  ./build-linux.sh -c Debug
      Build vTorrent in Debug mode for x64.

  ./build-linux.sh --appimage --deb
      Build and create both AppImage and Debian packages.

  ./build-linux.sh --all-arch --all
      Build for all architectures and create all package types.

  ./build-linux.sh --version 2.0.0 --all
      Build version 2.0.0 with all package types.

Requirements:
  - .NET 10.0 SDK
  - For AppImage: appimagetool (https://github.com/AppImage/AppImageKit)
  - For Debian: dpkg-deb
  - For icon generation: ImageMagick (convert) or Inkscape

Output Structure:
  dist/linux/
  ├── publish/
  │   ├── linux-x64/          # Published application files
  │   └── linux-arm64/        # (if --all-arch specified)
  ├── vTorrent-1.0.0-x86_64.AppImage
  ├── vTorrent-1.0.0-aarch64.AppImage
  ├── vtorrent_1.0.0_amd64.deb
  └── vtorrent_1.0.0_arm64.deb
EOF
}

# ============================================================================
# Prerequisite Checks
# ============================================================================

check_prerequisites() {
    print_step "Checking prerequisites..."

    local has_critical_errors=false

    # Check for dotnet
    if ! command -v dotnet &> /dev/null; then
        print_error ".NET SDK not found. Please install .NET 10.0 SDK."
        has_critical_errors=true
    else
        local dotnet_version
        dotnet_version=$(dotnet --version 2>/dev/null || echo "unknown")
        print_info "Found .NET SDK: $dotnet_version"

        # Check if .NET 10.0 SDK is available
        if ! dotnet --list-sdks 2>/dev/null | grep -q "^10\."; then
            print_warning ".NET 10.0 SDK not detected. Build may fail."
        fi
    fi

    # Check for AppImage tools if needed
    if [ "$CREATE_APPIMAGE" = true ]; then
        if ! command -v appimagetool &> /dev/null; then
            print_warning "appimagetool not found. AppDir will be created but AppImage will not be built."
            print_info "Install from: https://github.com/AppImage/AppImageKit/releases"
            print_info "Or run:"
            print_info "  wget https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
            print_info "  chmod +x appimagetool-x86_64.AppImage"
            print_info "  sudo mv appimagetool-x86_64.AppImage /usr/local/bin/appimagetool"
        else
            print_info "Found appimagetool"
        fi
    fi

    # Check for dpkg-deb if needed
    if [ "$CREATE_DEB" = true ]; then
        if ! command -v dpkg-deb &> /dev/null; then
            print_warning "dpkg-deb not found. Debian package creation will be skipped."
            print_info "Install with: sudo apt-get install dpkg"
        else
            print_info "Found dpkg-deb"
        fi
    fi

    # Check for icon conversion tools
    local has_icon_tool=false
    if command -v convert &> /dev/null; then
        print_info "Found ImageMagick (convert)"
        has_icon_tool=true
    fi
    if command -v inkscape &> /dev/null; then
        print_info "Found Inkscape"
        has_icon_tool=true
    fi
    if command -v rsvg-convert &> /dev/null; then
        print_info "Found rsvg-convert"
        has_icon_tool=true
    fi
    if [ "$has_icon_tool" = false ]; then
        print_warning "No icon conversion tool found (ImageMagick, Inkscape, or rsvg-convert)."
        print_info "Icons will be copied from ICO files if available."
    fi

    # Check for main project file
    if [ ! -f "$MAIN_PROJECT" ]; then
        print_error "Main project file not found: $MAIN_PROJECT"
        has_critical_errors=true
    else
        print_info "Found main project: $MAIN_PROJECT"
    fi

    # Check for packaging files
    if [ ! -f "$LINUX_PACKAGING/vTorrent.desktop" ]; then
        print_warning "Desktop file not found: $LINUX_PACKAGING/vTorrent.desktop"
    fi
    if [ ! -f "$LINUX_PACKAGING/AppImage/AppRun" ]; then
        print_warning "AppRun script not found: $LINUX_PACKAGING/AppImage/AppRun"
    fi

    if [ "$has_critical_errors" = true ]; then
        die "Prerequisites check failed. Please resolve the critical issues above."
    fi

    print_success "Prerequisites check completed."
}

# ============================================================================
# Clean Build Artifacts
# ============================================================================

clean_build() {
    print_step "Cleaning build artifacts..."

    # Clean output directory
    if [ -d "$OUTPUT_DIR" ]; then
        rm -rf "$OUTPUT_DIR"
        print_info "Removed: $OUTPUT_DIR"
    fi

    # Clean bin and obj directories for linux builds
    local dirs_to_clean=(
        "$PROJECT_ROOT/bin/Release/$TARGET_FRAMEWORK/linux-x64"
        "$PROJECT_ROOT/bin/Release/$TARGET_FRAMEWORK/linux-arm64"
        "$PROJECT_ROOT/bin/Debug/$TARGET_FRAMEWORK/linux-x64"
        "$PROJECT_ROOT/bin/Debug/$TARGET_FRAMEWORK/linux-arm64"
    )

    for dir in "${dirs_to_clean[@]}"; do
        if [ -d "$dir" ]; then
            rm -rf "$dir"
            print_info "Removed: $dir"
        fi
    done

    print_success "Clean completed."
}

# ============================================================================
# Publish Application
# ============================================================================

publish_architecture() {
    local arch=$1
    local runtime_id="linux-$arch"

    print_step "Publishing for $arch ($runtime_id) with $TARGET_FRAMEWORK..."

    local publish_path="$PUBLISH_DIR/$runtime_id"
    mkdir -p "$publish_path"

    # Run dotnet publish
    if ! dotnet publish "$MAIN_PROJECT" \
        --configuration "$CONFIGURATION" \
        --runtime "$runtime_id" \
        --self-contained true \
        --output "$publish_path" \
        -p:TargetFramework="$TARGET_FRAMEWORK" \
        -p:PublishSingleFile=false \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=embedded \
        -p:PublishTrimmed=false; then
        print_error "Failed to publish for $arch"
        return 1
    fi

    # Make main binary executable
    if [ -f "$publish_path/vTorrent" ]; then
        chmod +x "$publish_path/vTorrent"
    else
        print_error "Published binary not found: $publish_path/vTorrent"
        return 1
    fi

    # Calculate size
    local size
    size=$(du -sh "$publish_path" 2>/dev/null | cut -f1)
    print_success "Published to: $publish_path ($size)"
}

# ============================================================================
# Icon Generation
# ============================================================================

generate_icons() {
    local target_dir=$1
    local sizes=("16" "32" "48" "64" "128" "256")

    print_info "Generating icons..."

    # Create icon directories
    for size in "${sizes[@]}"; do
        mkdir -p "$target_dir/usr/share/icons/hicolor/${size}x${size}/apps"
    done

    # Source files
    local svg_source="$ASSETS_DIR/Images/dark_logo.svg"
    local ico_256="$ASSETS_DIR/Images/logo256x256.ico"
    local ico_128="$ASSETS_DIR/Images/dark_logo128x128.ico"
    local ico_32="$ASSETS_DIR/Images/dark_logo32x32.ico"
    local ico_16="$ASSETS_DIR/Images/dark_logo16x16.ico"

    # Determine conversion method
    local converter=""
    if command -v inkscape &> /dev/null; then
        converter="inkscape"
    elif command -v rsvg-convert &> /dev/null; then
        converter="rsvg"
    elif command -v convert &> /dev/null; then
        converter="imagemagick"
    fi

    local icons_generated=false

    # Try SVG conversion first
    if [ -f "$svg_source" ] && [ -n "$converter" ]; then
        for size in "${sizes[@]}"; do
            local output_png="$target_dir/usr/share/icons/hicolor/${size}x${size}/apps/vTorrent.png"

            case $converter in
                inkscape)
                    if inkscape "$svg_source" --export-type=png --export-width="$size" --export-height="$size" --export-filename="$output_png" 2>/dev/null; then
                        icons_generated=true
                    fi
                    ;;
                rsvg)
                    if rsvg-convert -w "$size" -h "$size" "$svg_source" -o "$output_png" 2>/dev/null; then
                        icons_generated=true
                    fi
                    ;;
                imagemagick)
                    if convert -background none -resize "${size}x${size}" "$svg_source" "$output_png" 2>/dev/null; then
                        icons_generated=true
                    fi
                    ;;
            esac
        done
    fi

    # Fallback to ICO conversion
    if [ "$icons_generated" = false ] && command -v convert &> /dev/null; then
        print_info "Using ICO files for icon generation..."

        for size in "${sizes[@]}"; do
            local output_png="$target_dir/usr/share/icons/hicolor/${size}x${size}/apps/vTorrent.png"
            local ico_source=""

            # Select best ICO source for size
            if [ "$size" -le 16 ] && [ -f "$ico_16" ]; then
                ico_source="$ico_16"
            elif [ "$size" -le 32 ] && [ -f "$ico_32" ]; then
                ico_source="$ico_32"
            elif [ "$size" -le 128 ] && [ -f "$ico_128" ]; then
                ico_source="$ico_128"
            elif [ -f "$ico_256" ]; then
                ico_source="$ico_256"
            fi

            if [ -n "$ico_source" ]; then
                if convert "${ico_source}[0]" -resize "${size}x${size}" "$output_png" 2>/dev/null; then
                    icons_generated=true
                fi
            fi
        done
    fi

    # Copy main icon to root of target_dir for AppImage
    local main_icon=""
    for size in 256 128 64 48; do
        local icon_path="$target_dir/usr/share/icons/hicolor/${size}x${size}/apps/vTorrent.png"
        if [ -f "$icon_path" ]; then
            main_icon="$icon_path"
            break
        fi
    done

    if [ -n "$main_icon" ]; then
        cp "$main_icon" "$target_dir/vTorrent.png"
        print_info "Main icon set from ${main_icon##*/}"
    elif [ -f "$ico_256" ] && command -v convert &> /dev/null; then
        # Last resort: convert 256x256 ICO directly
        convert "${ico_256}[0]" "$target_dir/vTorrent.png" 2>/dev/null || true
    fi

    if [ "$icons_generated" = true ]; then
        print_info "Icons generated successfully"
    else
        print_warning "Could not generate icons automatically"
    fi
}

# ============================================================================
# Create Desktop File
# ============================================================================

setup_desktop_file() {
    local target_dir=$1
    local app_prefix=${2:-}  # Optional: path prefix for Exec

    print_info "Setting up desktop file..."

    mkdir -p "$target_dir/usr/share/applications"

    # Check if desktop file exists in packaging directory
    if [ -f "$LINUX_PACKAGING/vTorrent.desktop" ]; then
        cp "$LINUX_PACKAGING/vTorrent.desktop" "$target_dir/usr/share/applications/vTorrent.desktop"

        # Update Exec path if prefix specified
        if [ -n "$app_prefix" ]; then
            sed -i "s|Exec=vTorrent|Exec=${app_prefix}vTorrent|g" "$target_dir/usr/share/applications/vTorrent.desktop"
            sed -i "s|Exec=vTorrent --add-torrent|Exec=${app_prefix}vTorrent --add-torrent|g" "$target_dir/usr/share/applications/vTorrent.desktop"
            sed -i "s|Exec=vTorrent --add-magnet|Exec=${app_prefix}vTorrent --add-magnet|g" "$target_dir/usr/share/applications/vTorrent.desktop"
        fi

        print_info "Using vTorrent.desktop from packaging/linux/"
    else
        # Create default desktop file
        cat > "$target_dir/usr/share/applications/vTorrent.desktop" << EOF
[Desktop Entry]
Name=vTorrent
GenericName=BitTorrent Client
Comment=A modern BitTorrent client
Exec=${app_prefix}vTorrent %U
Icon=vTorrent
Terminal=false
Type=Application
Categories=Network;FileTransfer;P2P;
MimeType=application/x-bittorrent;x-scheme-handler/magnet;
StartupWMClass=vTorrent
StartupNotify=true
Keywords=torrent;bittorrent;download;p2p;magnet;file;sharing;
EOF
        print_info "Created default desktop file"
    fi
}

# ============================================================================
# Create AppImage
# ============================================================================

create_appimage() {
    local arch=$1
    local runtime_id="linux-$arch"

    print_header "Creating AppImage for $arch"

    local publish_path="$PUBLISH_DIR/$runtime_id"

    # Determine architecture naming
    local arch_suffix
    case $arch in
        x64) arch_suffix="x86_64" ;;
        arm64) arch_suffix="aarch64" ;;
        *) arch_suffix="$arch" ;;
    esac

    local appdir="$OUTPUT_DIR/${APP_NAME}.AppDir"
    local appimage_name="${APP_NAME}-${APP_VERSION}-${arch_suffix}.AppImage"

    # Clean previous AppDir
    rm -rf "$appdir"

    print_step "Setting up AppDir structure..."

    # Create AppDir structure according to specification
    mkdir -p "$appdir/usr/bin"
    mkdir -p "$appdir/usr/lib"
    mkdir -p "$appdir/usr/share/applications"
    mkdir -p "$appdir/usr/share/icons/hicolor"
    mkdir -p "$appdir/usr/share/metainfo"
    mkdir -p "$appdir/usr/share/mime/packages"

    # Copy application files
    print_info "Copying application files to usr/bin/..."
    cp -R "$publish_path"/* "$appdir/usr/bin/"

    # Ensure binary is executable
    chmod +x "$appdir/usr/bin/vTorrent"

    # Setup desktop file
    setup_desktop_file "$appdir" ""

    # Copy desktop file to AppDir root (required by AppImage spec)
    cp "$appdir/usr/share/applications/vTorrent.desktop" "$appdir/"

    # Generate icons
    generate_icons "$appdir"

    # Copy AppRun from packaging directory or create one
    print_info "Setting up AppRun..."
    if [ -f "$LINUX_PACKAGING/AppImage/AppRun" ]; then
        cp "$LINUX_PACKAGING/AppImage/AppRun" "$appdir/AppRun"
        chmod +x "$appdir/AppRun"
        print_info "Using AppRun from packaging/linux/AppImage/"
    else
        # Create default AppRun
        cat > "$appdir/AppRun" << 'APPRUN_EOF'
#!/bin/bash
# AppRun - Entry point for vTorrent AppImage

# Get the directory where this script (and the AppImage contents) reside
APPDIR="$(dirname "$(readlink -f "$0")")"

# Export application directory for internal use
export VTORRENT_APPDIR="$APPDIR"

# Set up library paths for bundled dependencies
export LD_LIBRARY_PATH="$APPDIR/usr/lib:$APPDIR/usr/lib/x86_64-linux-gnu:${LD_LIBRARY_PATH:-}"

# Set up XDG paths for proper desktop integration
export XDG_DATA_DIRS="$APPDIR/usr/share:${XDG_DATA_DIRS:-/usr/local/share:/usr/share}"

# Configure .NET runtime
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
export DOTNET_BUNDLE_EXTRACT_BASE_DIR="${XDG_CACHE_HOME:-$HOME/.cache}/vtorrent-bundle"

# Handle Wayland/X11 display
if [ -z "$DISPLAY" ] && [ -z "$WAYLAND_DISPLAY" ]; then
    echo "Warning: No display server detected. vTorrent requires a graphical environment." >&2
fi

# Execute the main application
BINARY="$APPDIR/usr/bin/vTorrent"

if [ ! -x "$BINARY" ]; then
    echo "Error: vTorrent binary not found or not executable at $BINARY" >&2
    exit 1
fi

exec "$BINARY" "$@"
APPRUN_EOF
        chmod +x "$appdir/AppRun"
        print_info "Created default AppRun script"
    fi

    # Copy metainfo if available
    if [ -f "$LINUX_PACKAGING/vTorrent.metainfo.xml" ]; then
        cp "$LINUX_PACKAGING/vTorrent.metainfo.xml" "$appdir/usr/share/metainfo/com.vtorrent.app.metainfo.xml"
        print_info "Copied metainfo file"
    else
        # Create AppStream metadata
        cat > "$appdir/usr/share/metainfo/com.vtorrent.app.metainfo.xml" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>com.vtorrent.app</id>
  <name>vTorrent</name>
  <summary>A modern BitTorrent client</summary>
  <metadata_license>MIT</metadata_license>
  <project_license>MIT</project_license>
  <description>
    <p>
      vTorrent is a modern, lightweight BitTorrent client with a clean user interface.
      It supports magnet links, DHT, peer exchange, and other BitTorrent protocol extensions.
    </p>
  </description>
  <launchable type="desktop-id">vTorrent.desktop</launchable>
  <url type="homepage">https://github.com/vtorrent/vtorrent</url>
  <provides>
    <binary>vTorrent</binary>
  </provides>
  <releases>
    <release version="${APP_VERSION}" date="$(date +%Y-%m-%d)"/>
  </releases>
  <content_rating type="oars-1.1"/>
</component>
EOF
    fi

    # Copy MIME type file if available
    if [ -f "$LINUX_PACKAGING/mime/vtorrent.xml" ]; then
        cp "$LINUX_PACKAGING/mime/vtorrent.xml" "$appdir/usr/share/mime/packages/"
    fi

    print_success "AppDir created: $appdir"

    # Build AppImage if appimagetool is available
    if command -v appimagetool &> /dev/null; then
        print_step "Building AppImage with appimagetool..."

        # Set ARCH environment variable for appimagetool
        if ARCH="$arch_suffix" appimagetool "$appdir" "$OUTPUT_DIR/$appimage_name" 2>&1; then
            if [ -f "$OUTPUT_DIR/$appimage_name" ]; then
                chmod +x "$OUTPUT_DIR/$appimage_name"
                local size
                size=$(du -sh "$OUTPUT_DIR/$appimage_name" 2>/dev/null | cut -f1)
                print_success "AppImage created: $OUTPUT_DIR/$appimage_name ($size)"
            else
                print_error "AppImage file not created"
            fi
        else
            print_error "appimagetool failed"
        fi
    else
        print_warning "appimagetool not available. AppDir is ready at: $appdir"
        print_info "To build AppImage manually:"
        print_info "  ARCH=$arch_suffix appimagetool $appdir $OUTPUT_DIR/$appimage_name"
    fi
}

# ============================================================================
# Create Debian Package
# ============================================================================

create_deb_package() {
    local arch=$1
    local runtime_id="linux-$arch"

    print_header "Creating Debian Package for $arch"

    # Check if dpkg-deb is available
    if ! command -v dpkg-deb &> /dev/null; then
        print_error "dpkg-deb not found. Skipping Debian package creation."
        return 1
    fi

    local publish_path="$PUBLISH_DIR/$runtime_id"

    # Determine Debian architecture naming
    local deb_arch
    case $arch in
        x64) deb_arch="amd64" ;;
        arm64) deb_arch="arm64" ;;
        *) deb_arch="$arch" ;;
    esac

    local deb_name="${APP_NAME_LOWER}_${APP_VERSION}_${deb_arch}"
    local deb_dir="$OUTPUT_DIR/$deb_name"

    # Clean previous deb directory
    rm -rf "$deb_dir"

    print_step "Setting up Debian package structure..."

    # Create Debian package structure according to specification
    mkdir -p "$deb_dir/DEBIAN"
    mkdir -p "$deb_dir/usr/bin"
    mkdir -p "$deb_dir/usr/lib/$APP_NAME_LOWER"
    mkdir -p "$deb_dir/usr/share/applications"
    mkdir -p "$deb_dir/usr/share/icons/hicolor"
    mkdir -p "$deb_dir/usr/share/doc/$APP_NAME_LOWER"
    mkdir -p "$deb_dir/usr/share/mime/packages"
    mkdir -p "$deb_dir/usr/share/metainfo"

    # Copy application files to lib directory
    print_info "Copying application files to usr/lib/$APP_NAME_LOWER/..."
    cp -R "$publish_path"/* "$deb_dir/usr/lib/$APP_NAME_LOWER/"

    # Ensure binary is executable
    chmod +x "$deb_dir/usr/lib/$APP_NAME_LOWER/vTorrent"

    # Create launcher wrapper script
    print_info "Creating launcher wrapper..."
    cat > "$deb_dir/usr/bin/$APP_NAME_LOWER" << EOF
#!/bin/bash
exec /usr/lib/$APP_NAME_LOWER/vTorrent "\$@"
EOF
    chmod +x "$deb_dir/usr/bin/$APP_NAME_LOWER"

    # Setup desktop file with proper path
    setup_desktop_file "$deb_dir" "/usr/bin/"

    # Fix desktop file to use lowercase executable name
    sed -i "s|Exec=/usr/bin/vTorrent|Exec=/usr/bin/$APP_NAME_LOWER|g" "$deb_dir/usr/share/applications/vTorrent.desktop"

    # Rename desktop file to lowercase for consistency
    mv "$deb_dir/usr/share/applications/vTorrent.desktop" "$deb_dir/usr/share/applications/$APP_NAME_LOWER.desktop"

    # Generate icons
    generate_icons "$deb_dir"

    # Copy MIME type file
    if [ -f "$LINUX_PACKAGING/mime/vtorrent.xml" ]; then
        cp "$LINUX_PACKAGING/mime/vtorrent.xml" "$deb_dir/usr/share/mime/packages/"
    else
        # Create basic MIME type file
        cat > "$deb_dir/usr/share/mime/packages/$APP_NAME_LOWER.xml" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<mime-info xmlns="http://www.freedesktop.org/standards/shared-mime-info">
  <mime-type type="application/x-bittorrent">
    <comment>BitTorrent file</comment>
    <glob pattern="*.torrent"/>
  </mime-type>
</mime-info>
EOF
    fi

    # Copy metainfo
    if [ -f "$LINUX_PACKAGING/vTorrent.metainfo.xml" ]; then
        cp "$LINUX_PACKAGING/vTorrent.metainfo.xml" "$deb_dir/usr/share/metainfo/com.vtorrent.app.metainfo.xml"
    fi

    # Create copyright file
    print_info "Creating copyright file..."
    if [ -f "$LINUX_PACKAGING/debian/copyright" ]; then
        cp "$LINUX_PACKAGING/debian/copyright" "$deb_dir/usr/share/doc/$APP_NAME_LOWER/"
    else
        cat > "$deb_dir/usr/share/doc/$APP_NAME_LOWER/copyright" << EOF
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: vTorrent
Source: https://github.com/vtorrent/vtorrent

Files: *
Copyright: $(date +%Y) vTorrent
License: MIT

License: MIT
 Permission is hereby granted, free of charge, to any person obtaining a copy
 of this software and associated documentation files (the "Software"), to deal
 in the Software without restriction, including without limitation the rights
 to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 copies of the Software, and to permit persons to whom the Software is
 furnished to do so, subject to the following conditions:
 .
 The above copyright notice and this permission notice shall be included in all
 copies or substantial portions of the Software.
 .
 THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 SOFTWARE.
EOF
    fi

    # Calculate installed size (in KB)
    local installed_size
    installed_size=$(du -sk "$deb_dir" 2>/dev/null | cut -f1)

    # Create control file
    print_info "Creating DEBIAN/control..."
    cat > "$deb_dir/DEBIAN/control" << EOF
Package: $APP_NAME_LOWER
Version: $APP_VERSION
Section: net
Priority: optional
Architecture: $deb_arch
Installed-Size: $installed_size
Depends: libc6, libgcc-s1, libstdc++6, libx11-6, libfontconfig1, libfreetype6, libxcursor1, libxrandr2, libxi6
Recommends: libgl1, xdg-utils, desktop-file-utils, shared-mime-info
Suggests: ffmpeg
Maintainer: vTorrent <support@vtorrent.app>
Homepage: https://github.com/vtorrent/vtorrent
Description: A modern BitTorrent client
 vTorrent is a modern, lightweight BitTorrent client with a clean
 user interface. It supports magnet links, DHT, peer exchange, and
 other BitTorrent protocol extensions.
 .
 Features:
  - Modern, clean user interface with light and dark themes
  - Magnet link support
  - DHT (Distributed Hash Table) for trackerless torrents
  - Peer exchange (PEX) support
  - UDP tracker support
  - Local peer discovery
EOF

    # Create postinst script
    print_info "Creating DEBIAN/postinst..."
    if [ -f "$LINUX_PACKAGING/debian/postinst" ]; then
        cp "$LINUX_PACKAGING/debian/postinst" "$deb_dir/DEBIAN/postinst"
    else
        cat > "$deb_dir/DEBIAN/postinst" << 'EOF'
#!/bin/bash
set -e

case "$1" in
    configure)
        # Update desktop database
        if command -v update-desktop-database >/dev/null 2>&1; then
            update-desktop-database -q /usr/share/applications 2>/dev/null || true
        fi

        # Update MIME database
        if command -v update-mime-database >/dev/null 2>&1; then
            update-mime-database /usr/share/mime 2>/dev/null || true
        fi

        # Update icon cache
        if command -v gtk-update-icon-cache >/dev/null 2>&1; then
            gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true
        fi

        # Register magnet link handler
        if command -v xdg-mime >/dev/null 2>&1; then
            xdg-mime default vtorrent.desktop x-scheme-handler/magnet 2>/dev/null || true
            xdg-mime default vtorrent.desktop application/x-bittorrent 2>/dev/null || true
        fi
        ;;
esac

exit 0
EOF
    fi
    chmod 755 "$deb_dir/DEBIAN/postinst"

    # Create prerm script
    print_info "Creating DEBIAN/prerm..."
    if [ -f "$LINUX_PACKAGING/debian/prerm" ]; then
        cp "$LINUX_PACKAGING/debian/prerm" "$deb_dir/DEBIAN/prerm"
    else
        cat > "$deb_dir/DEBIAN/prerm" << 'EOF'
#!/bin/bash
set -e

case "$1" in
    remove|upgrade|deconfigure)
        # Stop any running instances gracefully
        if command -v pkill >/dev/null 2>&1; then
            pkill -TERM -x vTorrent 2>/dev/null || true
            sleep 1
        fi
        ;;
esac

exit 0
EOF
    fi
    chmod 755 "$deb_dir/DEBIAN/prerm"

    # Create postrm script
    print_info "Creating DEBIAN/postrm..."
    if [ -f "$LINUX_PACKAGING/debian/postrm" ]; then
        cp "$LINUX_PACKAGING/debian/postrm" "$deb_dir/DEBIAN/postrm"
    else
        cat > "$deb_dir/DEBIAN/postrm" << 'EOF'
#!/bin/bash
set -e

case "$1" in
    remove)
        # Update desktop database
        if command -v update-desktop-database >/dev/null 2>&1; then
            update-desktop-database -q /usr/share/applications 2>/dev/null || true
        fi

        # Update MIME database
        if command -v update-mime-database >/dev/null 2>&1; then
            update-mime-database /usr/share/mime 2>/dev/null || true
        fi

        # Update icon cache
        if command -v gtk-update-icon-cache >/dev/null 2>&1; then
            gtk-update-icon-cache -f -t /usr/share/icons/hicolor 2>/dev/null || true
        fi
        ;;

    purge)
        # Note: User data is intentionally NOT removed
        echo "Note: User configuration in ~/.config/vTorrent is preserved."
        echo "To remove all user data, manually delete ~/.config/vTorrent and ~/.local/share/vTorrent"
        ;;
esac

exit 0
EOF
    fi
    chmod 755 "$deb_dir/DEBIAN/postrm"

    # Set correct permissions for DEBIAN directory
    chmod 755 "$deb_dir/DEBIAN"

    # Build the package
    print_step "Building Debian package with dpkg-deb..."

    if dpkg-deb --build --root-owner-group "$deb_dir" "$OUTPUT_DIR/${deb_name}.deb" 2>&1; then
        if [ -f "$OUTPUT_DIR/${deb_name}.deb" ]; then
            local size
            size=$(du -sh "$OUTPUT_DIR/${deb_name}.deb" 2>/dev/null | cut -f1)
            print_success "Debian package created: $OUTPUT_DIR/${deb_name}.deb ($size)"

            # Validate package if lintian is available
            if command -v lintian &> /dev/null; then
                print_info "Running lintian validation..."
                lintian --no-tag-display-limit "$OUTPUT_DIR/${deb_name}.deb" 2>&1 | head -20 || true
            fi
        else
            print_error "Debian package file not created"
        fi
    else
        print_error "dpkg-deb failed"
    fi

    # Clean up build directory
    rm -rf "$deb_dir"
}

# ============================================================================
# Build Summary
# ============================================================================

show_summary() {
    print_header "Build Summary"

    echo -e "${BOLD}Configuration:${NC}  $CONFIGURATION"
    echo -e "${BOLD}Target Framework:${NC} $TARGET_FRAMEWORK"
    echo -e "${BOLD}Version:${NC}        $APP_VERSION"
    echo -e "${BOLD}Architectures:${NC}  ${ARCHITECTURES[*]}"
    echo -e "${BOLD}Output:${NC}         $OUTPUT_DIR"
    echo ""

    # List outputs
    echo -e "${YELLOW}${BOLD}Generated Outputs:${NC}"

    local has_outputs=false

    # Published applications
    for arch in "${ARCHITECTURES[@]}"; do
        local runtime_id="linux-$arch"
        local publish_path="$PUBLISH_DIR/$runtime_id"
        if [ -d "$publish_path" ] && [ -f "$publish_path/vTorrent" ]; then
            local size
            size=$(du -sh "$publish_path" 2>/dev/null | cut -f1)
            print_info "Published ($arch): $publish_path ($size)"
            has_outputs=true
        fi
    done

    # AppImages
    for arch in "${ARCHITECTURES[@]}"; do
        local arch_suffix
        case $arch in
            x64) arch_suffix="x86_64" ;;
            arm64) arch_suffix="aarch64" ;;
            *) arch_suffix="$arch" ;;
        esac
        local appimage="$OUTPUT_DIR/${APP_NAME}-${APP_VERSION}-${arch_suffix}.AppImage"
        if [ -f "$appimage" ]; then
            local size
            size=$(du -sh "$appimage" 2>/dev/null | cut -f1)
            print_info "AppImage ($arch): $appimage ($size)"
            has_outputs=true
        fi

        # Check for AppDir if AppImage wasn't created
        local appdir="$OUTPUT_DIR/${APP_NAME}.AppDir"
        if [ -d "$appdir" ] && [ ! -f "$appimage" ]; then
            local size
            size=$(du -sh "$appdir" 2>/dev/null | cut -f1)
            print_info "AppDir ($arch): $appdir ($size) - Ready for appimagetool"
            has_outputs=true
        fi
    done

    # Debian packages
    for arch in "${ARCHITECTURES[@]}"; do
        local deb_arch
        case $arch in
            x64) deb_arch="amd64" ;;
            arm64) deb_arch="arm64" ;;
            *) deb_arch="$arch" ;;
        esac
        local deb_file="$OUTPUT_DIR/${APP_NAME_LOWER}_${APP_VERSION}_${deb_arch}.deb"
        if [ -f "$deb_file" ]; then
            local size
            size=$(du -sh "$deb_file" 2>/dev/null | cut -f1)
            print_info "Debian ($arch): $deb_file ($size)"
            has_outputs=true
        fi
    done

    if [ "$has_outputs" = false ]; then
        print_warning "No output files were generated"
    fi

    # Show warnings
    if [ ${#WARNINGS[@]} -gt 0 ]; then
        echo ""
        echo -e "${YELLOW}${BOLD}Warnings:${NC}"
        for warning in "${WARNINGS[@]}"; do
            echo -e "  ${YELLOW}- $warning${NC}"
        done
    fi

    # Show errors
    if [ ${#ERRORS[@]} -gt 0 ]; then
        echo ""
        echo -e "${RED}${BOLD}Errors:${NC}"
        for error in "${ERRORS[@]}"; do
            echo -e "  ${RED}- $error${NC}"
        done
        echo ""
        print_error "Build completed with errors"
        return 1
    fi

    echo ""
    print_success "Build completed successfully!"

    echo ""
    echo -e "${YELLOW}${BOLD}Installation Instructions:${NC}"
    echo ""
    echo "  AppImage:"
    echo "    chmod +x ${APP_NAME}-${APP_VERSION}-*.AppImage"
    echo "    ./${APP_NAME}-${APP_VERSION}-*.AppImage"
    echo ""
    echo "  Debian package:"
    echo "    sudo dpkg -i ${APP_NAME_LOWER}_${APP_VERSION}_*.deb"
    echo "    sudo apt-get install -f  # Install dependencies if needed"
    echo ""
    echo "  Direct run from publish:"
    echo "    ./dist/linux/publish/linux-x64/vTorrent"
}

# ============================================================================
# Parse Arguments
# ============================================================================

while [[ $# -gt 0 ]]; do
    case $1 in
        -c|--configuration)
            CONFIGURATION="$2"
            shift 2
            ;;
        -a|--arch)
            ARCHITECTURES=("$2")
            shift 2
            ;;
        --all-arch)
            BUILD_ALL_ARCH=true
            shift
            ;;
        --appimage)
            CREATE_APPIMAGE=true
            shift
            ;;
        --deb)
            CREATE_DEB=true
            shift
            ;;
        --all)
            CREATE_APPIMAGE=true
            CREATE_DEB=true
            shift
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        --skip-publish)
            SKIP_PUBLISH=true
            shift
            ;;
        --version)
            APP_VERSION="$2"
            shift 2
            ;;
        -h|--help)
            show_help
            exit 0
            ;;
        *)
            print_error "Unknown option: $1"
            echo "Use --help for usage information."
            exit 1
            ;;
    esac
done

# Set all architectures if requested
if [ "$BUILD_ALL_ARCH" = true ]; then
    ARCHITECTURES=("x64" "arm64")
fi

# ============================================================================
# Main Script Execution
# ============================================================================

main() {
    print_header "vTorrent Linux Build Script"

    echo -e "${BOLD}Build Configuration:${NC}"
    print_info "Project Root: $PROJECT_ROOT"
    print_info "Configuration: $CONFIGURATION"
    print_info "Target Framework: $TARGET_FRAMEWORK"
    print_info "Version: $APP_VERSION"
    print_info "Architectures: ${ARCHITECTURES[*]}"
    print_info "Create AppImage: $CREATE_APPIMAGE"
    print_info "Create Debian: $CREATE_DEB"
    print_info "Clean Build: $CLEAN"
    print_info "Skip Publish: $SKIP_PUBLISH"
    echo ""

    # Validate configuration
    if [ "$CONFIGURATION" != "Debug" ] && [ "$CONFIGURATION" != "Release" ]; then
        die "Invalid configuration: $CONFIGURATION (must be Debug or Release)"
    fi

    # Validate architectures
    for arch in "${ARCHITECTURES[@]}"; do
        if [ "$arch" != "x64" ] && [ "$arch" != "arm64" ]; then
            die "Invalid architecture: $arch (must be x64 or arm64)"
        fi
    done

    # Check prerequisites
    check_prerequisites

    # Clean if requested
    if [ "$CLEAN" = true ]; then
        clean_build
    fi

    # Create output directory
    mkdir -p "$OUTPUT_DIR"
    mkdir -p "$PUBLISH_DIR"

    # Publish for each architecture
    if [ "$SKIP_PUBLISH" = false ]; then
        print_header "Publishing vTorrent"
        for arch in "${ARCHITECTURES[@]}"; do
            publish_architecture "$arch" || true
        done
    else
        print_info "Skipping publish step (using existing output)"
    fi

    # Verify publish output exists
    for arch in "${ARCHITECTURES[@]}"; do
        local runtime_id="linux-$arch"
        local publish_path="$PUBLISH_DIR/$runtime_id"
        if [ ! -d "$publish_path" ] || [ ! -f "$publish_path/vTorrent" ]; then
            print_error "Published output not found for $arch at $publish_path"
            print_info "Run without --skip-publish to generate publish output"
        fi
    done

    # Create packages
    for arch in "${ARCHITECTURES[@]}"; do
        local runtime_id="linux-$arch"
        local publish_path="$PUBLISH_DIR/$runtime_id"

        # Skip if publish output doesn't exist
        if [ ! -d "$publish_path" ] || [ ! -f "$publish_path/vTorrent" ]; then
            continue
        fi

        if [ "$CREATE_APPIMAGE" = true ]; then
            create_appimage "$arch" || true
        fi

        if [ "$CREATE_DEB" = true ]; then
            create_deb_package "$arch" || true
        fi
    done

    # Show summary
    show_summary
}

# Run main function
main
