#!/bin/bash

# ============================================================================
# vTorrent macOS Build Structure Test
# ============================================================================
#
# This script validates that all required files and directories exist for the
# macOS build process. It can be run on any platform to verify the structure.
#
# Usage:
#   ./test-macos-build-structure.sh
#
# ============================================================================

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

# Script location
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"

# Counters
PASSED=0
FAILED=0
WARNINGS=0

print_header() {
    echo ""
    echo -e "${CYAN}============================================================================${NC}"
    echo -e "${CYAN} $1${NC}"
    echo -e "${CYAN}============================================================================${NC}"
    echo ""
}

check_exists() {
    local path="$1"
    local description="$2"
    local required="${3:-true}"

    if [ -e "$path" ]; then
        echo -e "${GREEN}[PASS]${NC} $description"
        echo -e "       ${CYAN}$path${NC}"
        PASSED=$((PASSED + 1))
        return 0
    else
        if [ "$required" = "true" ]; then
            echo -e "${RED}[FAIL]${NC} $description"
            echo -e "       ${RED}Missing: $path${NC}"
            FAILED=$((FAILED + 1))
            return 1
        else
            echo -e "${YELLOW}[WARN]${NC} $description (optional)"
            echo -e "       ${YELLOW}Not found: $path${NC}"
            WARNINGS=$((WARNINGS + 1))
            return 0
        fi
    fi
}

check_file_contains() {
    local file="$1"
    local pattern="$2"
    local description="$3"

    if [ -f "$file" ]; then
        if grep -q "$pattern" "$file"; then
            echo -e "${GREEN}[PASS]${NC} $description"
            PASSED=$((PASSED + 1))
            return 0
        else
            echo -e "${RED}[FAIL]${NC} $description"
            echo -e "       ${RED}Pattern not found: $pattern${NC}"
            FAILED=$((FAILED + 1))
            return 1
        fi
    else
        echo -e "${RED}[FAIL]${NC} $description - file not found"
        FAILED=$((FAILED + 1))
        return 1
    fi
}

check_executable() {
    local file="$1"
    local description="$2"

    if [ -f "$file" ]; then
        if [ -x "$file" ]; then
            echo -e "${GREEN}[PASS]${NC} $description is executable"
            PASSED=$((PASSED + 1))
            return 0
        else
            echo -e "${YELLOW}[WARN]${NC} $description exists but is not executable"
            echo -e "       ${YELLOW}Run: chmod +x $file${NC}"
            WARNINGS=$((WARNINGS + 1))
            return 0
        fi
    else
        echo -e "${RED}[FAIL]${NC} $description not found"
        FAILED=$((FAILED + 1))
        return 1
    fi
}

# ============================================================================
# Main Tests
# ============================================================================

print_header "vTorrent macOS Build Structure Test"

echo "Project Root: $PROJECT_ROOT"
echo ""

# Test 1: Project Structure
print_header "1. Project Structure"

check_exists "$PROJECT_ROOT/vTorrent.csproj" "Main project file"
check_exists "$PROJECT_ROOT/packaging" "Packaging directory"
check_exists "$PROJECT_ROOT/packaging/macos" "macOS packaging directory"
check_exists "$PROJECT_ROOT/packaging/scripts" "Scripts directory"

# Test 2: Build Scripts
print_header "2. Build Scripts"

check_exists "$PROJECT_ROOT/packaging/scripts/build-macos.sh" "macOS build script"
check_executable "$PROJECT_ROOT/packaging/scripts/build-macos.sh" "macOS build script"

# Test 3: macOS Packaging Files
print_header "3. macOS Packaging Files"

check_exists "$PROJECT_ROOT/packaging/macos/Info.plist" "Info.plist"
check_exists "$PROJECT_ROOT/packaging/macos/entitlements.plist" "Entitlements plist"
check_exists "$PROJECT_ROOT/packaging/macos/Assets" "Assets directory" "false"
check_exists "$PROJECT_ROOT/packaging/macos/Assets/AppIcon.icns" "Application icon" "false"

# Test 4: Info.plist Content Verification
print_header "4. Info.plist Content Verification"

INFO_PLIST="$PROJECT_ROOT/packaging/macos/Info.plist"

if [ -f "$INFO_PLIST" ]; then
    check_file_contains "$INFO_PLIST" "CFBundleExecutable" "CFBundleExecutable key exists"
    check_file_contains "$INFO_PLIST" "<string>vTorrent</string>" "CFBundleExecutable value is 'vTorrent'"
    check_file_contains "$INFO_PLIST" "CFBundleIdentifier" "CFBundleIdentifier key exists"
    check_file_contains "$INFO_PLIST" "com.vtorrent.app" "Bundle identifier is correct"
    check_file_contains "$INFO_PLIST" "CFBundleIconFile" "CFBundleIconFile key exists"
    check_file_contains "$INFO_PLIST" "AppIcon.icns" "Icon file reference exists"
    check_file_contains "$INFO_PLIST" "LSMinimumSystemVersion" "Minimum system version defined"
    check_file_contains "$INFO_PLIST" "CFBundleURLSchemes" "URL schemes defined (magnet)"
    check_file_contains "$INFO_PLIST" "CFBundleDocumentTypes" "Document types defined (.torrent)"
else
    echo -e "${RED}[SKIP]${NC} Info.plist tests - file not found"
    FAILED=$((FAILED + 1))
fi

# Test 5: Build Script Content Verification
print_header "5. Build Script Content Verification"

BUILD_SCRIPT="$PROJECT_ROOT/packaging/scripts/build-macos.sh"

if [ -f "$BUILD_SCRIPT" ]; then
    check_file_contains "$BUILD_SCRIPT" "net10.0" "Target framework is net10.0"
    check_file_contains "$BUILD_SCRIPT" "osx-x64" "x64 runtime identifier"
    check_file_contains "$BUILD_SCRIPT" "osx-arm64" "arm64 runtime identifier"
    check_file_contains "$BUILD_SCRIPT" "lipo" "Universal binary creation with lipo"
    check_file_contains "$BUILD_SCRIPT" "dist/macos" "Output directory is dist/macos"
    check_file_contains "$BUILD_SCRIPT" "Info.plist" "Info.plist handling"
    check_file_contains "$BUILD_SCRIPT" "chmod +x" "Executable permissions"
    check_file_contains "$BUILD_SCRIPT" "\.app/Contents/MacOS" "Correct .app bundle structure"
    check_file_contains "$BUILD_SCRIPT" "\.app/Contents/Resources" "Resources directory in bundle"
    check_file_contains "$BUILD_SCRIPT" "codesign" "Code signing support"
    check_file_contains "$BUILD_SCRIPT" "hdiutil" "DMG creation support"
    check_file_contains "$BUILD_SCRIPT" "notarytool" "Notarization support"
else
    echo -e "${RED}[SKIP]${NC} Build script tests - file not found"
    FAILED=$((FAILED + 1))
fi

# Test 6: Project File Verification
print_header "6. Project File Verification"

CSPROJ="$PROJECT_ROOT/vTorrent.csproj"

if [ -f "$CSPROJ" ]; then
    check_file_contains "$CSPROJ" "net10.0" "net10.0 target framework"
    check_file_contains "$CSPROJ" "osx-x64" "osx-x64 runtime identifier"
    check_file_contains "$CSPROJ" "osx-arm64" "osx-arm64 runtime identifier"
else
    echo -e "${RED}[SKIP]${NC} Project file tests - file not found"
    FAILED=$((FAILED + 1))
fi

# Test 7: Entitlements Verification
print_header "7. Entitlements Verification"

ENTITLEMENTS="$PROJECT_ROOT/packaging/macos/entitlements.plist"

if [ -f "$ENTITLEMENTS" ]; then
    check_file_contains "$ENTITLEMENTS" "com.apple.security.cs.allow-jit" "JIT entitlement"
    check_file_contains "$ENTITLEMENTS" "com.apple.security.network.client" "Network client entitlement"
    check_file_contains "$ENTITLEMENTS" "com.apple.security.network.server" "Network server entitlement"
    check_file_contains "$ENTITLEMENTS" "com.apple.security.files" "File access entitlements"
else
    echo -e "${RED}[SKIP]${NC} Entitlements tests - file not found"
    FAILED=$((FAILED + 1))
fi

# Test 8: Expected Output Directories
print_header "8. Expected Output Paths (Verification Only)"

echo -e "${CYAN}The following paths will be created during build:${NC}"
echo ""
echo "  dist/macos/                        - Main output directory"
echo "  dist/macos/publish/osx-x64/        - Intel x64 publish output"
echo "  dist/macos/publish/osx-arm64/      - Apple Silicon publish output"
echo "  dist/macos/publish/universal/      - Universal binary"
echo "  dist/macos/vTorrent.app/           - Application bundle"
echo "  dist/macos/vTorrent.app/Contents/  - Bundle contents"
echo "  dist/macos/vTorrent.app/Contents/MacOS/     - Executable and libs"
echo "  dist/macos/vTorrent.app/Contents/Resources/ - Icons and resources"
echo "  dist/macos/vTorrent.app/Contents/Info.plist - Bundle metadata"
echo "  dist/macos/vTorrent.dmg            - Disk image (with -d flag)"
echo ""

# ============================================================================
# Summary
# ============================================================================

print_header "Test Summary"

TOTAL=$((PASSED + FAILED))

echo -e "Passed:   ${GREEN}$PASSED${NC}"
echo -e "Failed:   ${RED}$FAILED${NC}"
echo -e "Warnings: ${YELLOW}$WARNINGS${NC}"
echo -e "Total:    $TOTAL"
echo ""

if [ $FAILED -eq 0 ]; then
    echo -e "${GREEN}============================================================================${NC}"
    echo -e "${GREEN} All required structure tests passed!${NC}"
    echo -e "${GREEN}============================================================================${NC}"
    echo ""
    echo "The macOS build structure is correctly configured."
    echo ""
    echo "To build for macOS, run:"
    echo "  cd $PROJECT_ROOT"
    echo "  ./packaging/scripts/build-macos.sh"
    echo ""
    echo "Options:"
    echo "  ./packaging/scripts/build-macos.sh -d          # Create DMG"
    echo "  ./packaging/scripts/build-macos.sh --clean     # Clean build"
    echo "  ./packaging/scripts/build-macos.sh -h          # Show help"
    exit 0
else
    echo -e "${RED}============================================================================${NC}"
    echo -e "${RED} Some structure tests failed!${NC}"
    echo -e "${RED}============================================================================${NC}"
    echo ""
    echo "Please fix the issues above before attempting to build."
    exit 1
fi
