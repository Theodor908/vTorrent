#!/bin/bash
#
# create-dmg.sh - Create a styled DMG installer for vTorrent
#
# Usage: ./create-dmg.sh [app_path] [output_path]
#   app_path   - Path to vTorrent.app bundle (default: ./vTorrent.app)
#   output_path - Output DMG path (default: ./vTorrent-1.0.0.dmg)
#

set -e

# Configuration
APP_NAME="vTorrent"
VERSION="1.0.0"
VOLUME_NAME="${APP_NAME} ${VERSION}"
DMG_FILENAME="${APP_NAME}-${VERSION}.dmg"

# Paths
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_PATH="${1:-${SCRIPT_DIR}/../../bin/Release/net8.0/osx-x64/publish/${APP_NAME}.app}"
OUTPUT_DIR="${2:-${SCRIPT_DIR}/../../dist}"
OUTPUT_DMG="${OUTPUT_DIR}/${DMG_FILENAME}"

# DMG Settings
DMG_WINDOW_WIDTH=600
DMG_WINDOW_HEIGHT=400
DMG_ICON_SIZE=128
APP_ICON_X=150
APP_ICON_Y=200
APPLICATIONS_ICON_X=450
APPLICATIONS_ICON_Y=200

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if running on macOS
if [[ "$(uname)" != "Darwin" ]]; then
    log_error "This script must be run on macOS"
    exit 1
fi

# Check if app exists
if [[ ! -d "$APP_PATH" ]]; then
    log_error "Application bundle not found at: $APP_PATH"
    log_info "Please build the application first or specify the correct path"
    exit 1
fi

log_info "Creating DMG for ${APP_NAME} ${VERSION}"
log_info "Source: ${APP_PATH}"

# Create output directory
mkdir -p "$OUTPUT_DIR"

# Clean up any existing DMG
TEMP_DMG="${OUTPUT_DIR}/${APP_NAME}-temp.dmg"
if [[ -f "$OUTPUT_DMG" ]]; then
    log_info "Removing existing DMG..."
    rm -f "$OUTPUT_DMG"
fi
if [[ -f "$TEMP_DMG" ]]; then
    rm -f "$TEMP_DMG"
fi

# Create temporary directory for DMG contents
TEMP_DIR=$(mktemp -d)
log_info "Creating temporary directory: ${TEMP_DIR}"

# Copy app to temporary directory
log_info "Copying application bundle..."
cp -R "$APP_PATH" "${TEMP_DIR}/${APP_NAME}.app"

# Create Applications symlink
log_info "Creating Applications symlink..."
ln -s /Applications "${TEMP_DIR}/Applications"

# Create background directory and placeholder
BACKGROUND_DIR="${TEMP_DIR}/.background"
mkdir -p "$BACKGROUND_DIR"

# Create a simple background image placeholder (or copy existing one)
BACKGROUND_SOURCE="${SCRIPT_DIR}/Assets/dmg-background.png"
if [[ -f "$BACKGROUND_SOURCE" ]]; then
    cp "$BACKGROUND_SOURCE" "${BACKGROUND_DIR}/background.png"
    log_info "Using custom background image"
else
    log_warn "No background image found at ${BACKGROUND_SOURCE}"
    log_info "Creating placeholder background..."
    # Create a simple background using sips or leave empty
    # The DMG will work without a background
fi

# Calculate DMG size (app size + 50MB buffer)
APP_SIZE=$(du -sm "$APP_PATH" | cut -f1)
DMG_SIZE=$((APP_SIZE + 50))
log_info "Application size: ${APP_SIZE}MB, DMG size: ${DMG_SIZE}MB"

# Create temporary DMG
log_info "Creating temporary DMG..."
hdiutil create -srcfolder "$TEMP_DIR" \
    -volname "$VOLUME_NAME" \
    -fs HFS+ \
    -fsargs "-c c=64,a=16,e=16" \
    -format UDRW \
    -size ${DMG_SIZE}m \
    "$TEMP_DMG"

# Mount the temporary DMG
log_info "Mounting temporary DMG..."
MOUNT_DIR="/Volumes/${VOLUME_NAME}"

# Unmount if already mounted
if [[ -d "$MOUNT_DIR" ]]; then
    hdiutil detach "$MOUNT_DIR" -quiet || true
fi

hdiutil attach "$TEMP_DMG" -readwrite -noverify -noautoopen

# Wait for mount
sleep 2

# Apply custom view settings using AppleScript
log_info "Applying DMG styling..."
osascript <<EOF
tell application "Finder"
    tell disk "${VOLUME_NAME}"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {100, 100, $((100 + DMG_WINDOW_WIDTH)), $((100 + DMG_WINDOW_HEIGHT))}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to ${DMG_ICON_SIZE}

        -- Set background if exists
        try
            set background picture of viewOptions to file ".background:background.png"
        end try

        -- Position icons
        set position of item "${APP_NAME}.app" of container window to {${APP_ICON_X}, ${APP_ICON_Y}}
        set position of item "Applications" of container window to {${APPLICATIONS_ICON_X}, ${APPLICATIONS_ICON_Y}}

        -- Hide hidden files
        try
            set position of item ".background" of container window to {1000, 1000}
        end try
        try
            set position of item ".fseventsd" of container window to {1000, 1000}
        end try

        close
        open
        update without registering applications
        delay 2
        close
    end tell
end tell
EOF

# Set custom icon for the DMG volume (if available)
VOLUME_ICON="${SCRIPT_DIR}/Assets/AppIcon.icns"
if [[ -f "$VOLUME_ICON" ]]; then
    log_info "Setting volume icon..."
    cp "$VOLUME_ICON" "${MOUNT_DIR}/.VolumeIcon.icns"
    SetFile -c icnC "${MOUNT_DIR}/.VolumeIcon.icns"
    SetFile -a C "$MOUNT_DIR"
fi

# Sync and unmount
log_info "Finalizing..."
sync
hdiutil detach "$MOUNT_DIR"

# Convert to compressed DMG
log_info "Converting to compressed DMG..."
hdiutil convert "$TEMP_DMG" \
    -format UDZO \
    -imagekey zlib-level=9 \
    -o "$OUTPUT_DMG"

# Clean up
log_info "Cleaning up..."
rm -f "$TEMP_DMG"
rm -rf "$TEMP_DIR"

# Verify DMG
log_info "Verifying DMG..."
hdiutil verify "$OUTPUT_DMG"

# Show result
DMG_FINAL_SIZE=$(du -h "$OUTPUT_DMG" | cut -f1)
log_info "DMG created successfully!"
log_info "Output: ${OUTPUT_DMG}"
log_info "Size: ${DMG_FINAL_SIZE}"

# Optional: Notarize the DMG (requires Apple Developer account)
echo ""
echo "========================================"
echo "  DMG Creation Complete!"
echo "========================================"
echo ""
echo "To notarize for distribution (requires Apple Developer account):"
echo ""
echo "1. Submit for notarization:"
echo "   xcrun notarytool submit \"$OUTPUT_DMG\" \\"
echo "       --apple-id \"your-apple-id@email.com\" \\"
echo "       --team-id \"YOUR_TEAM_ID\" \\"
echo "       --password \"app-specific-password\" \\"
echo "       --wait"
echo ""
echo "2. Staple the notarization ticket:"
echo "   xcrun stapler staple \"$OUTPUT_DMG\""
echo ""
echo "========================================"
