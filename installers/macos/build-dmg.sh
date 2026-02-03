#!/bin/bash
# =============================================================================
# macOS DMG Builder for DocX MCP Server
# Creates a beautiful drag-and-drop DMG installer
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------
APP_NAME="DocX MCP Server"
DMG_TITLE="DocX MCP Server"
VOLUME_NAME="DocX MCP Server"

VERSION="${VERSION:-0.0.0}"
ARCH="${ARCH:-arm64}"
SIGNING_IDENTITY="${SIGNING_IDENTITY:-}"
NOTARIZE="${NOTARIZE:-false}"
APPLE_ID="${APPLE_ID:-}"
APPLE_TEAM_ID="${APPLE_TEAM_ID:-}"
NOTARYTOOL_PASSWORD="${NOTARYTOOL_PASSWORD:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
DIST_DIR="${PROJECT_ROOT}/dist"
BUILD_DIR="${DIST_DIR}/dmg-build-${ARCH}"
OUTPUT_DIR="${DIST_DIR}/installers"

BINARY_DIR="${DIST_DIR}/macos-${ARCH}"
MCP_BINARY="${BINARY_DIR}/docx-mcp"
CLI_BINARY="${BINARY_DIR}/docx-cli"

# -----------------------------------------------------------------------------
# Helper Functions
# -----------------------------------------------------------------------------
log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"; }
error() { echo "[ERROR] $*" >&2; exit 1; }

cleanup() {
    log "Cleaning up..."
    hdiutil detach "/Volumes/${VOLUME_NAME}" 2>/dev/null || true
    rm -rf "${BUILD_DIR}"
}

# -----------------------------------------------------------------------------
# Argument Parsing
# -----------------------------------------------------------------------------
while [[ $# -gt 0 ]]; do
    case $1 in
        -v|--version) VERSION="$2"; shift 2 ;;
        -a|--arch) ARCH="$2"; shift 2 ;;
        -s|--sign) SIGNING_IDENTITY="$2"; shift 2 ;;
        -n|--notarize) NOTARIZE="true"; shift ;;
        --apple-id) APPLE_ID="$2"; shift 2 ;;
        --team-id) APPLE_TEAM_ID="$2"; shift 2 ;;
        --password) NOTARYTOOL_PASSWORD="$2"; shift 2 ;;
        -h|--help)
            echo "Usage: $(basename "$0") [-v VERSION] [-a ARCH] [-s IDENTITY] [-n] [notarization options]"
            exit 0
            ;;
        *) error "Unknown option: $1" ;;
    esac
done

# Update paths
BUILD_DIR="${DIST_DIR}/dmg-build-${ARCH}"
BINARY_DIR="${DIST_DIR}/macos-${ARCH}"
MCP_BINARY="${BINARY_DIR}/docx-mcp"
CLI_BINARY="${BINARY_DIR}/docx-cli"

# -----------------------------------------------------------------------------
# Validation
# -----------------------------------------------------------------------------
[[ -f "${MCP_BINARY}" ]] || error "MCP binary not found: ${MCP_BINARY}"

# -----------------------------------------------------------------------------
# Build DMG
# -----------------------------------------------------------------------------
log "Building DMG for DocX MCP Server ${VERSION} (${ARCH})"

trap cleanup EXIT
rm -rf "${BUILD_DIR}"
mkdir -p "${BUILD_DIR}" "${OUTPUT_DIR}"

DMG_CONTENT="${BUILD_DIR}/content"
mkdir -p "${DMG_CONTENT}"

# Copy and sign binaries
cp "${MCP_BINARY}" "${DMG_CONTENT}/"
chmod 755 "${DMG_CONTENT}/docx-mcp"

if [[ -f "${CLI_BINARY}" ]]; then
    cp "${CLI_BINARY}" "${DMG_CONTENT}/"
    chmod 755 "${DMG_CONTENT}/docx-cli"
fi

# Sign binaries
if [[ -n "${SIGNING_IDENTITY}" ]]; then
    log "Signing binaries..."
    codesign --force --options runtime --timestamp \
        --sign "${SIGNING_IDENTITY}" \
        "${DMG_CONTENT}/docx-mcp"

    if [[ -f "${DMG_CONTENT}/docx-cli" ]]; then
        codesign --force --options runtime --timestamp \
            --sign "${SIGNING_IDENTITY}" \
            "${DMG_CONTENT}/docx-cli"
    fi
fi

# Create README for DMG
cat > "${DMG_CONTENT}/README.txt" <<EOF
DocX MCP Server ${VERSION}
==========================

Installation Instructions:
1. Open Terminal
2. Run: sudo cp docx-mcp docx-cli /usr/local/bin/
3. Run: chmod +x /usr/local/bin/docx-mcp /usr/local/bin/docx-cli

Quick Start:
  docx-mcp --help
  docx-cli --help

For more information:
  https://github.com/valdo404/docx-mcp
EOF

# Create symbolic link to /usr/local/bin for drag-and-drop
ln -s /usr/local/bin "${DMG_CONTENT}/Install Here (drag files)"

# Create install script
cat > "${DMG_CONTENT}/install.command" <<'SCRIPT'
#!/bin/bash
# DocX MCP Server Installer

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Installing DocX MCP Server..."
echo ""

sudo cp "${SCRIPT_DIR}/docx-mcp" /usr/local/bin/
sudo chmod 755 /usr/local/bin/docx-mcp

if [[ -f "${SCRIPT_DIR}/docx-cli" ]]; then
    sudo cp "${SCRIPT_DIR}/docx-cli" /usr/local/bin/
    sudo chmod 755 /usr/local/bin/docx-cli
fi

echo ""
echo "Installation complete!"
echo ""
echo "Run 'docx-mcp --help' or 'docx-cli --help' for usage."
echo ""
read -p "Press Enter to close..."
SCRIPT
chmod 755 "${DMG_CONTENT}/install.command"

if [[ -n "${SIGNING_IDENTITY}" ]]; then
    codesign --force --options runtime --timestamp \
        --sign "${SIGNING_IDENTITY}" \
        "${DMG_CONTENT}/install.command"
fi

# Create temporary DMG
TEMP_DMG="${BUILD_DIR}/temp.dmg"
OUTPUT_DMG="${OUTPUT_DIR}/docx-mcp-${VERSION}-${ARCH}.dmg"

log "Creating DMG image..."
hdiutil create -srcfolder "${DMG_CONTENT}" \
    -volname "${VOLUME_NAME}" \
    -fs HFS+ \
    -format UDRW \
    "${TEMP_DMG}"

# Mount and customize
log "Customizing DMG appearance..."
MOUNT_POINT=$(hdiutil attach -readwrite -noverify "${TEMP_DMG}" | grep "/Volumes/" | tail -1 | awk '{print $3}')

# Set DMG window properties using AppleScript
osascript <<EOF
tell application "Finder"
    tell disk "${VOLUME_NAME}"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set bounds of container window to {400, 100, 900, 450}
        set theViewOptions to the icon view options of container window
        set arrangement of theViewOptions to not arranged
        set icon size of theViewOptions to 80
        close
    end tell
end tell
EOF

# Wait for Finder to finish
sync
sleep 2

# Unmount
hdiutil detach "${MOUNT_POINT}"

# Convert to compressed, read-only DMG
log "Creating final DMG..."
hdiutil convert "${TEMP_DMG}" \
    -format UDZO \
    -imagekey zlib-level=9 \
    -o "${OUTPUT_DMG}"

# Sign DMG
if [[ -n "${SIGNING_IDENTITY}" ]]; then
    log "Signing DMG..."
    codesign --force --sign "${SIGNING_IDENTITY}" "${OUTPUT_DMG}"
fi

# Notarize
if [[ "${NOTARIZE}" == "true" ]]; then
    log "Submitting DMG for notarization..."
    xcrun notarytool submit "${OUTPUT_DMG}" \
        --apple-id "${APPLE_ID}" \
        --team-id "${APPLE_TEAM_ID}" \
        --password "${NOTARYTOOL_PASSWORD}" \
        --wait

    log "Stapling notarization ticket..."
    xcrun stapler staple "${OUTPUT_DMG}"
fi

log "Build complete!"
log "Output: ${OUTPUT_DMG}"

trap - EXIT
