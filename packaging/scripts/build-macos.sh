#!/bin/bash

# ============================================================================
# vTorrent macOS Build Script
# ============================================================================
#
# This script builds and packages vTorrent for macOS, creating a universal
# binary .app bundle and optionally a DMG disk image.
#
# Target Framework: net10.0
# Runtime Identifiers: osx-x64, osx-arm64
#
# Usage:
#   ./build-macos.sh [options]
#
# Options:
#   -c, --configuration   Build configuration: Debug or Release (default: Release)
#   -s, --sign            Sign the application with specified identity
#   -d, --dmg             Create a DMG disk image
#   -n, --notarize        Notarize the application (requires --sign)
#   --apple-id            Apple ID for notarization
#   --team-id             Team ID for notarization
#   --password            App-specific password for notarization
#   --clean               Clean build artifacts before building
#   --skip-universal      Skip universal binary creation (build x64 only)
#   -h, --help            Show this help message
#
# Examples:
#   ./build-macos.sh
#   ./build-macos.sh -c Debug
#   ./build-macos.sh -s "Developer ID Application: Your Name" -d
#   ./build-macos.sh -s "Developer ID" -d -n --apple-id your@email.com --team-id XXXXX
#
# ============================================================================

set -e

# ============================================================================
# Configuration
# ============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
MAIN_PROJECT="$PROJECT_ROOT/vTorrent.csproj"
OUTPUT_DIR="$PROJECT_ROOT/dist/macos"
PUBLISH_DIR="$OUTPUT_DIR/publish"
APP_NAME="vTorrent"
APP_BUNDLE="$OUTPUT_DIR/$APP_NAME.app"
DMG_NAME="vTorrent.dmg"

# .NET Configuration
TARGET_FRAMEWORK="net10.0"
RUNTIME_X64="osx-x64"
RUNTIME_ARM64="osx-arm64"

# Default options
CONFIGURATION="Release"
SIGN_IDENTITY=""
CREATE_DMG=false
NOTARIZE=false
APPLE_ID=""
TEAM_ID=""
APP_PASSWORD=""
CLEAN=false
SKIP_UNIVERSAL=false

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
BLUE='\033[0;34m'
MAGENTA='\033[0;35m'
GRAY='\033[0;37m'
BOLD='\033[1m'
NC='\033[0m' # No Color

# ============================================================================
# Helper Functions
# ============================================================================

print_header() {
    echo ""
    echo -e "${CYAN}${BOLD}============================================================================${NC}"
    echo -e "${CYAN}${BOLD} $1${NC}"
    echo -e "${CYAN}${BOLD}============================================================================${NC}"
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
}

print_info() {
    echo -e "${GRAY}    $1${NC}"
}

print_warning() {
    echo -e "${MAGENTA}[!] $1${NC}"
}

show_help() {
    cat << EOF
${BOLD}vTorrent macOS Build Script${NC}

${YELLOW}Usage:${NC}
  ./build-macos.sh [options]

${YELLOW}Options:${NC}
  -c, --configuration   Build configuration: Debug or Release (default: Release)
  -s, --sign            Sign the application with specified identity
  -d, --dmg             Create a DMG disk image
  -n, --notarize        Notarize the application (requires --sign)
  --apple-id            Apple ID for notarization
  --team-id             Team ID for notarization
  --password            App-specific password for notarization
  --clean               Clean build artifacts before building
  --skip-universal      Skip universal binary creation (build current arch only)
  -h, --help            Show this help message

${YELLOW}Examples:${NC}
  ./build-macos.sh
      Build vTorrent universal binary in Release mode.

  ./build-macos.sh -c Debug
      Build vTorrent in Debug mode.

  ./build-macos.sh -s "Developer ID Application: Your Name" -d
      Build, sign with the specified identity, and create a DMG.

  ./build-macos.sh -s "Developer ID" -d -n --apple-id your@email.com --team-id XXXXX
      Build, sign, create DMG, and notarize the application.

${YELLOW}Build Configuration:${NC}
  Target Framework:      $TARGET_FRAMEWORK
  Runtime Identifiers:   $RUNTIME_X64, $RUNTIME_ARM64
  Output Directory:      dist/macos/

${YELLOW}Requirements:${NC}
  - .NET 10.0 SDK
  - Xcode Command Line Tools (for lipo, codesign, hdiutil)
  - Apple Developer certificate (for signing)
  - notarytool (for notarization, included in Xcode 13+)
EOF
}

check_prerequisites() {
    print_step "Checking prerequisites..."

    local has_errors=false

    # Check for dotnet
    if ! command -v dotnet &> /dev/null; then
        print_error ".NET SDK not found. Please install .NET 10.0 SDK."
        has_errors=true
    else
        local dotnet_version=$(dotnet --version)
        print_info "Found .NET SDK: $dotnet_version"

        # Verify .NET 10.0 is available
        if ! dotnet --list-sdks | grep -q "^10\."; then
            print_warning ".NET 10.0 SDK not detected. Build may fail."
        else
            print_info ".NET 10.0 SDK available"
        fi
    fi

    # Check for lipo (for universal binary)
    if ! command -v lipo &> /dev/null; then
        print_error "lipo not found. Please install Xcode Command Line Tools."
        has_errors=true
    else
        print_info "Found lipo"
    fi

    # Check for codesign (if signing requested)
    if [ -n "$SIGN_IDENTITY" ]; then
        if ! command -v codesign &> /dev/null; then
            print_error "codesign not found. Please install Xcode Command Line Tools."
            has_errors=true
        else
            print_info "Found codesign"
        fi
    fi

    # Check for hdiutil (if DMG requested)
    if [ "$CREATE_DMG" = true ]; then
        if ! command -v hdiutil &> /dev/null; then
            print_error "hdiutil not found. This tool is required for DMG creation."
            has_errors=true
        else
            print_info "Found hdiutil"
        fi
    fi

    # Check for notarytool (if notarization requested)
    if [ "$NOTARIZE" = true ]; then
        if ! command -v xcrun &> /dev/null; then
            print_error "xcrun not found. Please install Xcode."
            has_errors=true
        else
            print_info "Found xcrun (for notarytool)"
        fi

        if [ -z "$APPLE_ID" ] || [ -z "$TEAM_ID" ]; then
            print_error "Notarization requires --apple-id and --team-id"
            has_errors=true
        fi
    fi

    # Check for main project file
    if [ ! -f "$MAIN_PROJECT" ]; then
        print_error "Main project file not found: $MAIN_PROJECT"
        has_errors=true
    else
        print_info "Found main project: $MAIN_PROJECT"
    fi

    # Check for Info.plist
    local info_plist="$PROJECT_ROOT/packaging/macos/Info.plist"
    if [ ! -f "$info_plist" ]; then
        print_warning "Info.plist not found at: $info_plist"
        print_info "A default Info.plist will be generated"
    else
        print_info "Found Info.plist: $info_plist"
    fi

    if [ "$has_errors" = true ]; then
        print_error "Prerequisites check failed. Please resolve the issues above."
        exit 1
    fi

    print_success "All prerequisites satisfied."
}

clean_build() {
    print_step "Cleaning build artifacts..."

    # Clean output directory
    if [ -d "$OUTPUT_DIR" ]; then
        rm -rf "$OUTPUT_DIR"
        print_info "Removed: $OUTPUT_DIR"
    fi

    # Clean bin and obj directories
    local dirs_to_clean=(
        "$PROJECT_ROOT/bin"
        "$PROJECT_ROOT/obj"
    )

    for dir in "${dirs_to_clean[@]}"; do
        if [ -d "$dir" ]; then
            rm -rf "$dir"
            print_info "Removed: $dir"
        fi
    done

    print_success "Clean completed."
}

publish_architecture() {
    local arch=$1
    local runtime_id=$2

    print_step "Publishing for $arch ($runtime_id) with $TARGET_FRAMEWORK..."

    local publish_path="$PUBLISH_DIR/$runtime_id"

    # Publish with explicit target framework
    dotnet publish "$MAIN_PROJECT" \
        --configuration "$CONFIGURATION" \
        --runtime "$runtime_id" \
        --self-contained true \
        --output "$publish_path" \
        -p:TargetFramework="$TARGET_FRAMEWORK" \
        -p:PublishSingleFile=false \
        -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:DebugType=embedded \
        -p:PublishReadyToRun=true

    if [ $? -eq 0 ]; then
        print_success "Published to: $publish_path"
    else
        print_error "Failed to publish for $arch"
        exit 1
    fi
}

create_universal_binary() {
    print_header "Creating Universal Binary"

    print_step "Merging x64 and arm64 binaries with lipo..."

    local x64_dir="$PUBLISH_DIR/$RUNTIME_X64"
    local arm64_dir="$PUBLISH_DIR/$RUNTIME_ARM64"
    local universal_dir="$PUBLISH_DIR/universal"

    # Verify both architectures were published
    if [ ! -d "$x64_dir" ]; then
        print_error "x64 publish directory not found: $x64_dir"
        exit 1
    fi

    if [ ! -d "$arm64_dir" ]; then
        print_error "arm64 publish directory not found: $arm64_dir"
        exit 1
    fi

    mkdir -p "$universal_dir"

    # Count files for progress
    local total_files=$(find "$x64_dir" -type f | wc -l)
    local processed=0

    print_info "Processing $total_files files..."

    # Find all files and merge them
    find "$x64_dir" -type f | while read x64_file; do
        local relative_path="${x64_file#$x64_dir/}"
        local arm64_file="$arm64_dir/$relative_path"
        local universal_file="$universal_dir/$relative_path"

        # Create directory structure
        mkdir -p "$(dirname "$universal_file")"

        if [ -f "$arm64_file" ]; then
            # Check if file is a Mach-O binary
            if file "$x64_file" | grep -q "Mach-O"; then
                # Merge with lipo
                lipo -create "$x64_file" "$arm64_file" -output "$universal_file" 2>/dev/null || {
                    # If lipo fails, just copy x64 version (might be same architecture)
                    cp "$x64_file" "$universal_file"
                }
            else
                # Copy non-binary files from x64
                cp "$x64_file" "$universal_file"
            fi
        else
            # File only exists in x64, copy it
            cp "$x64_file" "$universal_file"
        fi

        processed=$((processed + 1))
    done

    # Copy any files that only exist in arm64
    find "$arm64_dir" -type f | while read arm64_file; do
        local relative_path="${arm64_file#$arm64_dir/}"
        local universal_file="$universal_dir/$relative_path"

        if [ ! -f "$universal_file" ]; then
            mkdir -p "$(dirname "$universal_file")"
            cp "$arm64_file" "$universal_file"
        fi
    done

    print_success "Universal binary created at: $universal_dir"

    # Verify universal binary
    local main_binary="$universal_dir/vTorrent"
    if [ -f "$main_binary" ]; then
        print_info "Verifying universal binary:"
        local lipo_output=$(lipo -info "$main_binary" 2>&1)
        print_info "$lipo_output"

        # Check if it's actually universal
        if echo "$lipo_output" | grep -q "x86_64" && echo "$lipo_output" | grep -q "arm64"; then
            print_success "Universal binary verified: contains both x86_64 and arm64"
        else
            print_warning "Binary may not be universal. Check lipo output above."
        fi
    fi
}

create_app_bundle() {
    print_header "Creating App Bundle"

    print_step "Creating .app bundle structure..."

    local source_dir
    if [ "$SKIP_UNIVERSAL" = true ]; then
        # Use whichever architecture was built
        if [ -d "$PUBLISH_DIR/$RUNTIME_X64" ]; then
            source_dir="$PUBLISH_DIR/$RUNTIME_X64"
        elif [ -d "$PUBLISH_DIR/$RUNTIME_ARM64" ]; then
            source_dir="$PUBLISH_DIR/$RUNTIME_ARM64"
        else
            print_error "No publish directory found"
            exit 1
        fi
    else
        source_dir="$PUBLISH_DIR/universal"
    fi

    local contents_dir="$APP_BUNDLE/Contents"
    local macos_dir="$contents_dir/MacOS"
    local resources_dir="$contents_dir/Resources"
    local macos_packaging="$PROJECT_ROOT/packaging/macos"

    # Remove existing bundle
    if [ -d "$APP_BUNDLE" ]; then
        rm -rf "$APP_BUNDLE"
        print_info "Removed existing app bundle"
    fi

    # Create bundle structure
    mkdir -p "$macos_dir"
    mkdir -p "$resources_dir"

    print_info "Bundle structure created:"
    print_info "  $APP_BUNDLE/"
    print_info "    Contents/"
    print_info "      MacOS/"
    print_info "      Resources/"

    # Copy application files
    print_step "Copying application files..."
    cp -R "$source_dir"/* "$macos_dir/"
    print_info "Copied $(find "$macos_dir" -type f | wc -l) files to MacOS/"

    # Copy Info.plist from packaging directory
    print_step "Setting up Info.plist..."
    if [ -f "$macos_packaging/Info.plist" ]; then
        cp "$macos_packaging/Info.plist" "$contents_dir/Info.plist"
        print_success "Using Info.plist from packaging/macos/"

        # Verify CFBundleExecutable
        local bundle_exec=$(grep -A1 "CFBundleExecutable" "$contents_dir/Info.plist" | grep "<string>" | sed 's/.*<string>\(.*\)<\/string>.*/\1/')
        print_info "CFBundleExecutable: $bundle_exec"

        if [ "$bundle_exec" != "vTorrent" ]; then
            print_warning "CFBundleExecutable should be 'vTorrent', found: '$bundle_exec'"
        fi
    else
        create_info_plist "$contents_dir/Info.plist"
        print_info "Created default Info.plist"
    fi

    # Copy entitlements from packaging directory
    print_step "Setting up entitlements..."
    if [ -f "$macos_packaging/entitlements.plist" ]; then
        cp "$macos_packaging/entitlements.plist" "$OUTPUT_DIR/vTorrent.entitlements"
        print_success "Using entitlements.plist from packaging/macos/"
    else
        create_entitlements "$OUTPUT_DIR/vTorrent.entitlements"
        print_info "Created default entitlements"
    fi

    # Copy icon if available - check multiple locations
    print_step "Setting up icons..."
    local icon_copied=false

    # First check for AppIcon.icns (referenced in existing Info.plist)
    if [ -f "$macos_packaging/Assets/AppIcon.icns" ]; then
        cp "$macos_packaging/Assets/AppIcon.icns" "$resources_dir/AppIcon.icns"
        print_success "Copied AppIcon.icns"
        icon_copied=true
    # Then check for vTorrent.icns
    elif [ -f "$macos_packaging/Assets/vTorrent.icns" ]; then
        cp "$macos_packaging/Assets/vTorrent.icns" "$resources_dir/AppIcon.icns"
        print_success "Copied vTorrent.icns as AppIcon.icns"
        icon_copied=true
    else
        # Try to find any icns file in Assets
        local any_icns=$(find "$PROJECT_ROOT/Assets" -name "*.icns" 2>/dev/null | head -1)
        if [ -n "$any_icns" ]; then
            cp "$any_icns" "$resources_dir/AppIcon.icns"
            print_success "Copied icon from: $any_icns"
            icon_copied=true
        fi
    fi

    if [ "$icon_copied" = false ]; then
        print_warning "No .icns icon found, app will use default icon"
        print_info "Run packaging/macos/generate-icns.sh to generate icons"
    fi

    # Copy TorrentIcon if available (for document type association)
    if [ -f "$macos_packaging/Assets/TorrentIcon.icns" ]; then
        cp "$macos_packaging/Assets/TorrentIcon.icns" "$resources_dir/TorrentIcon.icns"
        print_info "Copied TorrentIcon.icns for document associations"
    fi

    # Set proper permissions on executable
    print_step "Setting executable permissions..."
    chmod +x "$macos_dir/vTorrent"

    # Also set permissions on any other native binaries
    find "$macos_dir" -type f -name "*.dylib" -exec chmod +x {} \; 2>/dev/null || true
    find "$macos_dir" -type f -perm +111 -exec chmod +x {} \; 2>/dev/null || true

    print_success "App bundle created at: $APP_BUNDLE"

    # Verify bundle structure
    print_info "Bundle verification:"
    if [ -f "$contents_dir/Info.plist" ]; then
        print_info "  [OK] Info.plist exists"
    else
        print_error "  [FAIL] Info.plist missing"
    fi

    if [ -x "$macos_dir/vTorrent" ]; then
        print_info "  [OK] vTorrent executable exists and is executable"
    else
        print_error "  [FAIL] vTorrent executable missing or not executable"
    fi

    if [ -f "$resources_dir/AppIcon.icns" ]; then
        print_info "  [OK] AppIcon.icns exists"
    else
        print_info "  [WARN] AppIcon.icns missing (using system default)"
    fi
}

create_info_plist() {
    local plist_path=$1

    cat > "$plist_path" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleDisplayName</key>
    <string>vTorrent</string>
    <key>CFBundleExecutable</key>
    <string>vTorrent</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon.icns</string>
    <key>CFBundleIdentifier</key>
    <string>com.vtorrent.app</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>vTorrent</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.0.0</string>
    <key>CFBundleVersion</key>
    <string>1</string>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.utilities</string>
    <key>LSMinimumSystemVersion</key>
    <string>10.15</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright 2024 vTorrent. All rights reserved.</string>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key>
            <string>BitTorrent File</string>
            <key>CFBundleTypeRole</key>
            <string>Viewer</string>
            <key>LSHandlerRank</key>
            <string>Owner</string>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>torrent</string>
            </array>
            <key>LSItemContentTypes</key>
            <array>
                <string>org.bittorrent.torrent</string>
            </array>
        </dict>
    </array>
    <key>CFBundleURLTypes</key>
    <array>
        <dict>
            <key>CFBundleURLName</key>
            <string>Magnet Link</string>
            <key>CFBundleURLSchemes</key>
            <array>
                <string>magnet</string>
            </array>
        </dict>
    </array>
    <key>UTExportedTypeDeclarations</key>
    <array>
        <dict>
            <key>UTTypeIdentifier</key>
            <string>org.bittorrent.torrent</string>
            <key>UTTypeDescription</key>
            <string>BitTorrent File</string>
            <key>UTTypeConformsTo</key>
            <array>
                <string>public.data</string>
            </array>
            <key>UTTypeTagSpecification</key>
            <dict>
                <key>public.filename-extension</key>
                <array>
                    <string>torrent</string>
                </array>
                <key>public.mime-type</key>
                <string>application/x-bittorrent</string>
            </dict>
        </dict>
    </array>
</dict>
</plist>
EOF
}

create_entitlements() {
    local entitlements_path=$1

    cat > "$entitlements_path" << 'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>com.apple.security.cs.allow-jit</key>
    <true/>
    <key>com.apple.security.cs.allow-unsigned-executable-memory</key>
    <true/>
    <key>com.apple.security.cs.disable-library-validation</key>
    <true/>
    <key>com.apple.security.network.client</key>
    <true/>
    <key>com.apple.security.network.server</key>
    <true/>
    <key>com.apple.security.files.user-selected.read-write</key>
    <true/>
    <key>com.apple.security.files.downloads.read-write</key>
    <true/>
</dict>
</plist>
EOF
}

sign_app() {
    print_header "Signing Application"

    if [ -z "$SIGN_IDENTITY" ]; then
        print_info "No signing identity provided, skipping signing."
        return
    fi

    print_step "Signing with identity: $SIGN_IDENTITY"

    local entitlements="$OUTPUT_DIR/vTorrent.entitlements"

    # Sign all binaries in the bundle
    print_info "Signing embedded binaries..."

    # Find and sign all Mach-O binaries
    find "$APP_BUNDLE/Contents/MacOS" -type f -perm +111 | while read binary; do
        print_info "Signing: $(basename "$binary")"
        codesign --force --options runtime \
            --entitlements "$entitlements" \
            --sign "$SIGN_IDENTITY" \
            --timestamp \
            "$binary"
    done

    # Sign dylibs
    find "$APP_BUNDLE/Contents/MacOS" -type f -name "*.dylib" | while read dylib; do
        print_info "Signing: $(basename "$dylib")"
        codesign --force --options runtime \
            --entitlements "$entitlements" \
            --sign "$SIGN_IDENTITY" \
            --timestamp \
            "$dylib"
    done

    # Sign the main bundle
    print_info "Signing main bundle..."
    codesign --force --options runtime \
        --entitlements "$entitlements" \
        --sign "$SIGN_IDENTITY" \
        --timestamp \
        --deep \
        "$APP_BUNDLE"

    print_success "Application signed successfully."

    # Verify signature
    print_step "Verifying signature..."
    codesign --verify --verbose=2 "$APP_BUNDLE"
    print_success "Signature verification passed."

    # Check Gatekeeper assessment
    print_step "Checking Gatekeeper assessment..."
    if spctl --assess --verbose=4 "$APP_BUNDLE" 2>&1; then
        print_success "Gatekeeper assessment passed."
    else
        print_warning "Gatekeeper assessment may require notarization for distribution."
    fi
}

create_dmg() {
    print_header "Creating DMG"

    print_step "Creating DMG disk image..."

    local dmg_path="$OUTPUT_DIR/$DMG_NAME"
    local temp_dmg="$OUTPUT_DIR/temp.dmg"
    local volume_name="vTorrent"

    # Remove existing DMG
    if [ -f "$dmg_path" ]; then
        rm "$dmg_path"
    fi

    # Create a temporary directory for DMG contents
    local dmg_contents="$OUTPUT_DIR/dmg-contents"
    mkdir -p "$dmg_contents"

    # Copy app bundle
    cp -R "$APP_BUNDLE" "$dmg_contents/"

    # Create symbolic link to Applications folder
    ln -sf /Applications "$dmg_contents/Applications"

    # Create DMG
    print_info "Creating disk image..."
    hdiutil create -volname "$volume_name" \
        -srcfolder "$dmg_contents" \
        -ov -format UDRW \
        "$temp_dmg"

    # Convert to compressed DMG
    print_info "Compressing disk image..."
    hdiutil convert "$temp_dmg" \
        -format UDZO \
        -imagekey zlib-level=9 \
        -o "$dmg_path"

    # Clean up
    rm "$temp_dmg"
    rm -rf "$dmg_contents"

    print_success "DMG created: $dmg_path"

    # Sign DMG if signing identity provided
    if [ -n "$SIGN_IDENTITY" ]; then
        print_step "Signing DMG..."
        codesign --force --sign "$SIGN_IDENTITY" --timestamp "$dmg_path"
        print_success "DMG signed."
    fi
}

notarize_app() {
    print_header "Notarizing Application"

    if [ "$NOTARIZE" != true ]; then
        return
    fi

    if [ -z "$SIGN_IDENTITY" ]; then
        print_error "Notarization requires a signed application. Use -s flag."
        exit 1
    fi

    print_step "Submitting for notarization..."

    local target_file
    if [ "$CREATE_DMG" = true ]; then
        target_file="$OUTPUT_DIR/$DMG_NAME"
    else
        # Create a zip for notarization
        target_file="$OUTPUT_DIR/vTorrent.zip"
        ditto -c -k --keepParent "$APP_BUNDLE" "$target_file"
    fi

    print_info "Submitting: $target_file"

    # Submit for notarization
    local notarize_output
    if [ -n "$APP_PASSWORD" ]; then
        notarize_output=$(xcrun notarytool submit "$target_file" \
            --apple-id "$APPLE_ID" \
            --team-id "$TEAM_ID" \
            --password "$APP_PASSWORD" \
            --wait 2>&1)
    else
        print_info "Using keychain for notarization credentials..."
        notarize_output=$(xcrun notarytool submit "$target_file" \
            --apple-id "$APPLE_ID" \
            --team-id "$TEAM_ID" \
            --keychain-profile "vTorrent-notarize" \
            --wait 2>&1)
    fi

    echo "$notarize_output"

    if echo "$notarize_output" | grep -q "status: Accepted"; then
        print_success "Notarization successful!"

        # Staple the notarization ticket
        print_step "Stapling notarization ticket..."
        if [ "$CREATE_DMG" = true ]; then
            xcrun stapler staple "$OUTPUT_DIR/$DMG_NAME"
        else
            xcrun stapler staple "$APP_BUNDLE"
        fi
        print_success "Notarization ticket stapled."
    else
        print_error "Notarization failed. Check the output above for details."
        exit 1
    fi
}

verify_build() {
    print_header "Build Verification"

    local all_passed=true

    print_step "Verifying build outputs..."

    # Check app bundle exists
    if [ -d "$APP_BUNDLE" ]; then
        print_info "[PASS] App bundle exists: $APP_BUNDLE"
    else
        print_error "[FAIL] App bundle missing: $APP_BUNDLE"
        all_passed=false
    fi

    # Check Info.plist
    if [ -f "$APP_BUNDLE/Contents/Info.plist" ]; then
        print_info "[PASS] Info.plist exists"
    else
        print_error "[FAIL] Info.plist missing"
        all_passed=false
    fi

    # Check executable
    if [ -x "$APP_BUNDLE/Contents/MacOS/vTorrent" ]; then
        print_info "[PASS] vTorrent executable is executable"
    else
        print_error "[FAIL] vTorrent is not executable"
        all_passed=false
    fi

    # Verify executable architecture
    if [ -f "$APP_BUNDLE/Contents/MacOS/vTorrent" ]; then
        local arch_info=$(lipo -info "$APP_BUNDLE/Contents/MacOS/vTorrent" 2>&1)
        print_info "Executable architecture: $arch_info"
    fi

    # Check DMG if created
    if [ "$CREATE_DMG" = true ]; then
        if [ -f "$OUTPUT_DIR/$DMG_NAME" ]; then
            print_info "[PASS] DMG created: $OUTPUT_DIR/$DMG_NAME"
        else
            print_error "[FAIL] DMG creation failed"
            all_passed=false
        fi
    fi

    if [ "$all_passed" = true ]; then
        print_success "All verification checks passed!"
    else
        print_error "Some verification checks failed!"
        return 1
    fi
}

show_summary() {
    print_header "Build Summary"

    echo -e "${BOLD}Configuration:${NC}  $CONFIGURATION"
    echo -e "${BOLD}Framework:${NC}      $TARGET_FRAMEWORK"
    echo -e "${BOLD}Output:${NC}         $OUTPUT_DIR"
    echo ""
    echo -e "${YELLOW}${BOLD}Build Outputs:${NC}"

    # App bundle
    if [ -d "$APP_BUNDLE" ]; then
        local app_size=$(du -sh "$APP_BUNDLE" | cut -f1)
        echo -e "  ${GREEN}[+]${NC} App bundle: $APP_BUNDLE (${CYAN}$app_size${NC})"
    fi

    # DMG
    if [ -f "$OUTPUT_DIR/$DMG_NAME" ]; then
        local dmg_size=$(du -sh "$OUTPUT_DIR/$DMG_NAME" | cut -f1)
        echo -e "  ${GREEN}[+]${NC} DMG: $OUTPUT_DIR/$DMG_NAME (${CYAN}$dmg_size${NC})"
    fi

    # Publish directories
    echo ""
    echo -e "${YELLOW}${BOLD}Publish Directories:${NC}"
    if [ -d "$PUBLISH_DIR/$RUNTIME_X64" ]; then
        local size=$(du -sh "$PUBLISH_DIR/$RUNTIME_X64" | cut -f1)
        echo -e "  ${GRAY}[-]${NC} x64: $PUBLISH_DIR/$RUNTIME_X64 ($size)"
    fi
    if [ -d "$PUBLISH_DIR/$RUNTIME_ARM64" ]; then
        local size=$(du -sh "$PUBLISH_DIR/$RUNTIME_ARM64" | cut -f1)
        echo -e "  ${GRAY}[-]${NC} arm64: $PUBLISH_DIR/$RUNTIME_ARM64 ($size)"
    fi
    if [ -d "$PUBLISH_DIR/universal" ]; then
        local size=$(du -sh "$PUBLISH_DIR/universal" | cut -f1)
        echo -e "  ${GREEN}[-]${NC} universal: $PUBLISH_DIR/universal ($size)"
    fi

    echo ""
    echo -e "${YELLOW}${BOLD}Build Configuration:${NC}"
    echo -e "  Signed:      $([ -n "$SIGN_IDENTITY" ] && echo "${GREEN}Yes${NC}" || echo "${GRAY}No${NC}")"
    echo -e "  Notarized:   $([ "$NOTARIZE" = true ] && echo "${GREEN}Yes${NC}" || echo "${GRAY}No${NC}")"
    echo -e "  Universal:   $([ "$SKIP_UNIVERSAL" != true ] && echo "${GREEN}Yes${NC}" || echo "${GRAY}No${NC}")"

    echo ""
    print_success "Build completed successfully!"

    echo ""
    echo -e "${YELLOW}${BOLD}Installation:${NC}"
    if [ -f "$OUTPUT_DIR/$DMG_NAME" ]; then
        echo "  1. Open $DMG_NAME"
        echo "  2. Drag vTorrent to Applications folder"
    else
        echo "  1. Drag vTorrent.app to Applications folder"
    fi

    echo ""
    echo -e "${YELLOW}${BOLD}Testing:${NC}"
    echo "  Run: open $APP_BUNDLE"
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
        -s|--sign)
            SIGN_IDENTITY="$2"
            shift 2
            ;;
        -d|--dmg)
            CREATE_DMG=true
            shift
            ;;
        -n|--notarize)
            NOTARIZE=true
            shift
            ;;
        --apple-id)
            APPLE_ID="$2"
            shift 2
            ;;
        --team-id)
            TEAM_ID="$2"
            shift 2
            ;;
        --password)
            APP_PASSWORD="$2"
            shift 2
            ;;
        --clean)
            CLEAN=true
            shift
            ;;
        --skip-universal)
            SKIP_UNIVERSAL=true
            shift
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

# ============================================================================
# Main Script
# ============================================================================

print_header "vTorrent macOS Build Script"
echo -e "${BOLD}Build Configuration:${NC}"
print_info "Project Root:   $PROJECT_ROOT"
print_info "Configuration:  $CONFIGURATION"
print_info "Framework:      $TARGET_FRAMEWORK"
print_info "Runtimes:       $RUNTIME_X64, $RUNTIME_ARM64"
print_info "Sign:           $([ -n "$SIGN_IDENTITY" ] && echo "$SIGN_IDENTITY" || echo "No")"
print_info "DMG:            $CREATE_DMG"
print_info "Notarize:       $NOTARIZE"
print_info "Universal:      $([ "$SKIP_UNIVERSAL" != true ] && echo "Yes" || echo "No")"

# Validate configuration
if [ "$CONFIGURATION" != "Debug" ] && [ "$CONFIGURATION" != "Release" ]; then
    print_error "Invalid configuration: $CONFIGURATION (must be Debug or Release)"
    exit 1
fi

# Check prerequisites
check_prerequisites

# Clean if requested
if [ "$CLEAN" = true ]; then
    clean_build
fi

# Create output directory
mkdir -p "$OUTPUT_DIR"
mkdir -p "$PUBLISH_DIR"

# Publish for architectures
print_header "Publishing vTorrent"

if [ "$SKIP_UNIVERSAL" = true ]; then
    # Detect current architecture and build for it
    current_arch=$(uname -m)
    if [ "$current_arch" = "arm64" ]; then
        publish_architecture "Apple Silicon" "$RUNTIME_ARM64"
    else
        publish_architecture "Intel x64" "$RUNTIME_X64"
    fi
else
    # Build for both architectures
    publish_architecture "Intel x64" "$RUNTIME_X64"
    publish_architecture "Apple Silicon" "$RUNTIME_ARM64"

    # Create universal binary
    create_universal_binary
fi

# Create app bundle
create_app_bundle

# Sign application
sign_app

# Create DMG
if [ "$CREATE_DMG" = true ]; then
    create_dmg
fi

# Notarize
notarize_app

# Verify build
verify_build

# Show summary
show_summary
