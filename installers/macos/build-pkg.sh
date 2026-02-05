#!/bin/bash
# =============================================================================
# macOS Package Builder for DocX MCP Server
# Creates a signed .pkg installer with optional notarization
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------
APP_NAME="DocX MCP Server"
APP_IDENTIFIER="com.docxmcp.server"
CLI_IDENTIFIER="com.docxmcp.cli"
INSTALL_LOCATION="/usr/local/bin"

# Default values (can be overridden via environment or arguments)
VERSION="${VERSION:-0.0.0}"
ARCH="${ARCH:-arm64}"
SIGNING_IDENTITY="${SIGNING_IDENTITY:-}"
INSTALLER_SIGNING_IDENTITY="${INSTALLER_SIGNING_IDENTITY:-}"
NOTARIZE="${NOTARIZE:-false}"
APPLE_ID="${APPLE_ID:-}"
APPLE_TEAM_ID="${APPLE_TEAM_ID:-}"
NOTARYTOOL_PASSWORD="${NOTARYTOOL_PASSWORD:-}"

# Paths
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
DIST_DIR="${PROJECT_ROOT}/dist"
BUILD_DIR="${DIST_DIR}/pkg-build-${ARCH}"
OUTPUT_DIR="${DIST_DIR}/installers"

# Binaries
BINARY_DIR="${DIST_DIR}/macos-${ARCH}"
MCP_BINARY="${BINARY_DIR}/docx-mcp"
CLI_BINARY="${BINARY_DIR}/docx-cli"
STORAGE_BINARY="${BINARY_DIR}/docx-mcp-storage"

# -----------------------------------------------------------------------------
# Helper Functions
# -----------------------------------------------------------------------------
log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"
}

error() {
    echo "[ERROR] $*" >&2
    exit 1
}

cleanup() {
    log "Cleaning up build directory..."
    rm -rf "${BUILD_DIR}"
}

# -----------------------------------------------------------------------------
# Argument Parsing
# -----------------------------------------------------------------------------
usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Build a signed macOS .pkg installer for DocX MCP Server

Options:
    -v, --version VERSION       Application version (default: ${VERSION})
    -a, --arch ARCH             Architecture: arm64 or x64 (default: ${ARCH})
    -s, --sign IDENTITY         Developer ID Application certificate identity
    -i, --installer-sign ID     Developer ID Installer certificate identity
    -n, --notarize              Enable notarization (requires Apple ID credentials)
    --apple-id EMAIL            Apple ID for notarization
    --team-id TEAM_ID           Apple Developer Team ID
    --password PASSWORD         App-specific password or keychain reference
    -h, --help                  Show this help message

Environment Variables:
    VERSION                     Application version
    ARCH                        Target architecture
    SIGNING_IDENTITY            Developer ID Application certificate
    INSTALLER_SIGNING_IDENTITY  Developer ID Installer certificate
    NOTARIZE                    Set to 'true' to enable notarization
    APPLE_ID                    Apple ID email
    APPLE_TEAM_ID               Developer Team ID
    NOTARYTOOL_PASSWORD         App-specific password

Examples:
    # Unsigned build
    ./build-pkg.sh -v 1.0.0 -a arm64

    # Signed build
    ./build-pkg.sh -v 1.0.0 -a arm64 -s "Developer ID Application: Company (TEAMID)"

    # Signed and notarized
    ./build-pkg.sh -v 1.0.0 -a arm64 \\
        -s "Developer ID Application: Company (TEAMID)" \\
        -i "Developer ID Installer: Company (TEAMID)" \\
        -n --apple-id "dev@company.com" --team-id "TEAMID" --password "@keychain:AC_PASSWORD"

EOF
    exit 0
}

while [[ $# -gt 0 ]]; do
    case $1 in
        -v|--version) VERSION="$2"; shift 2 ;;
        -a|--arch) ARCH="$2"; shift 2 ;;
        -s|--sign) SIGNING_IDENTITY="$2"; shift 2 ;;
        -i|--installer-sign) INSTALLER_SIGNING_IDENTITY="$2"; shift 2 ;;
        -n|--notarize) NOTARIZE="true"; shift ;;
        --apple-id) APPLE_ID="$2"; shift 2 ;;
        --team-id) APPLE_TEAM_ID="$2"; shift 2 ;;
        --password) NOTARYTOOL_PASSWORD="$2"; shift 2 ;;
        -h|--help) usage ;;
        *) error "Unknown option: $1" ;;
    esac
done

# Update paths after argument parsing
BUILD_DIR="${DIST_DIR}/pkg-build-${ARCH}"
BINARY_DIR="${DIST_DIR}/macos-${ARCH}"
MCP_BINARY="${BINARY_DIR}/docx-mcp"
CLI_BINARY="${BINARY_DIR}/docx-cli"
STORAGE_BINARY="${BINARY_DIR}/docx-mcp-storage"

# -----------------------------------------------------------------------------
# Validation
# -----------------------------------------------------------------------------
log "Validating configuration..."

[[ -f "${MCP_BINARY}" ]] || error "MCP binary not found: ${MCP_BINARY}"

if [[ "${NOTARIZE}" == "true" ]]; then
    [[ -n "${SIGNING_IDENTITY}" ]] || error "Code signing identity required for notarization"
    [[ -n "${INSTALLER_SIGNING_IDENTITY}" ]] || error "Installer signing identity required for notarization"
    [[ -n "${APPLE_ID}" ]] || error "Apple ID required for notarization"
    [[ -n "${APPLE_TEAM_ID}" ]] || error "Team ID required for notarization"
    [[ -n "${NOTARYTOOL_PASSWORD}" ]] || error "Notarytool password required for notarization"
fi

# -----------------------------------------------------------------------------
# Preparation
# -----------------------------------------------------------------------------
log "Building DocX MCP Server ${VERSION} for macOS ${ARCH}"
log "Output directory: ${OUTPUT_DIR}"

trap cleanup EXIT
mkdir -p "${BUILD_DIR}" "${OUTPUT_DIR}"

# Package structure
PKG_ROOT="${BUILD_DIR}/root"
PKG_SCRIPTS="${BUILD_DIR}/scripts"
mkdir -p "${PKG_ROOT}${INSTALL_LOCATION}"
mkdir -p "${PKG_SCRIPTS}"

# -----------------------------------------------------------------------------
# Code Signing
# -----------------------------------------------------------------------------
sign_binary() {
    local binary="$1"
    local identifier="$2"

    if [[ -n "${SIGNING_IDENTITY}" ]]; then
        log "Signing ${binary}..."
        codesign --force --options runtime --timestamp \
            --sign "${SIGNING_IDENTITY}" \
            --identifier "${identifier}" \
            "${binary}"

        log "Verifying signature..."
        codesign --verify --verbose=2 "${binary}"
    else
        log "Skipping code signing (no identity provided)"
    fi
}

# -----------------------------------------------------------------------------
# Build Package
# -----------------------------------------------------------------------------
log "Copying binaries..."
cp "${MCP_BINARY}" "${PKG_ROOT}${INSTALL_LOCATION}/"
chmod 755 "${PKG_ROOT}${INSTALL_LOCATION}/docx-mcp"

if [[ -f "${CLI_BINARY}" ]]; then
    cp "${CLI_BINARY}" "${PKG_ROOT}${INSTALL_LOCATION}/"
    chmod 755 "${PKG_ROOT}${INSTALL_LOCATION}/docx-cli"
fi

if [[ -f "${STORAGE_BINARY}" ]]; then
    cp "${STORAGE_BINARY}" "${PKG_ROOT}${INSTALL_LOCATION}/"
    chmod 755 "${PKG_ROOT}${INSTALL_LOCATION}/docx-mcp-storage"
fi

# Sign binaries before packaging
sign_binary "${PKG_ROOT}${INSTALL_LOCATION}/docx-mcp" "${APP_IDENTIFIER}"
if [[ -f "${PKG_ROOT}${INSTALL_LOCATION}/docx-cli" ]]; then
    sign_binary "${PKG_ROOT}${INSTALL_LOCATION}/docx-cli" "${CLI_IDENTIFIER}"
fi
if [[ -f "${PKG_ROOT}${INSTALL_LOCATION}/docx-mcp-storage" ]]; then
    sign_binary "${PKG_ROOT}${INSTALL_LOCATION}/docx-mcp-storage" "${APP_IDENTIFIER}.storage"
fi

# Create postinstall script
cat > "${PKG_SCRIPTS}/postinstall" <<'SCRIPT'
#!/bin/bash
# Post-installation script for DocX MCP Server

# Ensure binaries are executable
chmod 755 /usr/local/bin/docx-mcp 2>/dev/null || true
chmod 755 /usr/local/bin/docx-cli 2>/dev/null || true
chmod 755 /usr/local/bin/docx-mcp-storage 2>/dev/null || true

# Create sessions directory for current user
if [[ -n "${USER}" ]] && [[ "${USER}" != "root" ]]; then
    USER_HOME=$(eval echo "~${USER}")
    mkdir -p "${USER_HOME}/.docx-mcp/sessions" 2>/dev/null || true
    chown -R "${USER}" "${USER_HOME}/.docx-mcp" 2>/dev/null || true
fi

echo "DocX MCP Server installed successfully!"
echo "Run 'docx-mcp --help' or 'docx-cli --help' for usage information."

exit 0
SCRIPT
chmod 755 "${PKG_SCRIPTS}/postinstall"

# Build component package
COMPONENT_PKG="${BUILD_DIR}/DocxMcp-component.pkg"
log "Building component package..."
pkgbuild \
    --root "${PKG_ROOT}" \
    --scripts "${PKG_SCRIPTS}" \
    --identifier "${APP_IDENTIFIER}" \
    --version "${VERSION}" \
    --install-location "/" \
    "${COMPONENT_PKG}"

# Create distribution XML for nice installer UI
DISTRIBUTION_XML="${BUILD_DIR}/distribution.xml"
cat > "${DISTRIBUTION_XML}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<installer-gui-script minSpecVersion="2">
    <title>${APP_NAME}</title>
    <organization>${APP_IDENTIFIER}</organization>
    <domains enable_localSystem="true" enable_currentUserHome="false"/>
    <options customize="never" require-scripts="false" hostArchitectures="${ARCH}"/>

    <welcome file="welcome.html"/>
    <license file="license.html"/>
    <conclusion file="conclusion.html"/>

    <choices-outline>
        <line choice="default">
            <line choice="com.docxmcp.pkg"/>
        </line>
    </choices-outline>

    <choice id="default"/>
    <choice id="com.docxmcp.pkg" visible="false">
        <pkg-ref id="${APP_IDENTIFIER}"/>
    </choice>

    <pkg-ref id="${APP_IDENTIFIER}" version="${VERSION}" onConclusion="none">DocxMcp-component.pkg</pkg-ref>
</installer-gui-script>
EOF

# Create resources for installer UI
RESOURCES_DIR="${BUILD_DIR}/resources"
mkdir -p "${RESOURCES_DIR}"

cat > "${RESOURCES_DIR}/welcome.html" <<EOF
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; padding: 20px; }
        h1 { color: #1a73e8; }
        .version { color: #666; font-size: 14px; }
        ul { line-height: 1.8; }
    </style>
</head>
<body>
    <h1>DocX MCP Server</h1>
    <p class="version">Version ${VERSION}</p>
    <p>Welcome to the DocX MCP Server installer.</p>
    <p>This package will install:</p>
    <ul>
        <li><strong>docx-mcp</strong> - MCP server for AI-powered Word document manipulation</li>
        <li><strong>docx-cli</strong> - Command-line interface for direct operations</li>
        <li><strong>docx-mcp-storage</strong> - gRPC storage server (auto-launched by MCP/CLI)</li>
    </ul>
    <p>The tools will be installed to <code>/usr/local/bin</code> and will be available from the terminal immediately after installation.</p>
</body>
</html>
EOF

cat > "${RESOURCES_DIR}/license.html" <<EOF
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, monospace; padding: 20px; font-size: 12px; }
        pre { white-space: pre-wrap; }
    </style>
</head>
<body>
<pre>
$(cat "${PROJECT_ROOT}/LICENSE" 2>/dev/null || echo "MIT License - See project repository for full license text.")
</pre>
</body>
</html>
EOF

cat > "${RESOURCES_DIR}/conclusion.html" <<EOF
<!DOCTYPE html>
<html>
<head>
    <meta charset="UTF-8">
    <style>
        body { font-family: -apple-system, BlinkMacSystemFont, sans-serif; padding: 20px; }
        h1 { color: #34a853; }
        pre {
            background: #2d2d2d;
            color: #f8f8f2;
            padding: 12px 16px;
            border-radius: 8px;
            font-family: 'SF Mono', Menlo, Monaco, monospace;
            font-size: 13px;
            overflow-x: auto;
        }
    </style>
</head>
<body>
    <h1>Installation Complete</h1>
    <p>DocX MCP Server has been installed successfully!</p>

    <p><strong>Quick Start:</strong></p>
    <p>Open Terminal and run:</p>
    <pre>docx-mcp --help
docx-cli --help</pre>

    <p>For documentation and updates, visit:</p>
    <p><a href="https://github.com/valdo404/docx-mcp">https://github.com/valdo404/docx-mcp</a></p>
</body>
</html>
EOF

# Build final product package
OUTPUT_PKG="${OUTPUT_DIR}/docx-mcp-${VERSION}-${ARCH}.pkg"
log "Building product package..."

PRODUCTBUILD_ARGS=(
    --distribution "${DISTRIBUTION_XML}"
    --resources "${RESOURCES_DIR}"
    --package-path "${BUILD_DIR}"
)

if [[ -n "${INSTALLER_SIGNING_IDENTITY}" ]]; then
    PRODUCTBUILD_ARGS+=(--sign "${INSTALLER_SIGNING_IDENTITY}")
fi

PRODUCTBUILD_ARGS+=("${OUTPUT_PKG}")

productbuild "${PRODUCTBUILD_ARGS[@]}"

log "Package created: ${OUTPUT_PKG}"

# -----------------------------------------------------------------------------
# Notarization
# -----------------------------------------------------------------------------
if [[ "${NOTARIZE}" == "true" ]]; then
    log "Submitting package for notarization..."

    xcrun notarytool submit "${OUTPUT_PKG}" \
        --apple-id "${APPLE_ID}" \
        --team-id "${APPLE_TEAM_ID}" \
        --password "${NOTARYTOOL_PASSWORD}" \
        --wait

    log "Stapling notarization ticket..."
    xcrun stapler staple "${OUTPUT_PKG}"

    log "Verifying notarization..."
    xcrun stapler validate "${OUTPUT_PKG}"

    log "Package notarized successfully!"
fi

# -----------------------------------------------------------------------------
# Verification
# -----------------------------------------------------------------------------
log "Package details:"
pkgutil --payload-files "${OUTPUT_PKG}" | head -20

if [[ -n "${INSTALLER_SIGNING_IDENTITY}" ]]; then
    log "Signature verification:"
    pkgutil --check-signature "${OUTPUT_PKG}"
fi

log "Build complete!"
log "Output: ${OUTPUT_PKG}"

# Don't cleanup on success - keep for debugging
trap - EXIT
