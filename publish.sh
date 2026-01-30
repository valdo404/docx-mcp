#!/usr/bin/env bash
set -euo pipefail

# Build NativeAOT binaries for all supported platforms.
# Requires .NET 10 SDK.
#
# Usage:
#   ./publish.sh              # Build for current platform
#   ./publish.sh all          # Build for all platforms (cross-compile)
#   ./publish.sh macos-arm64  # Build for specific target

PROJECT="src/DocxMcp/DocxMcp.csproj"
OUTPUT_DIR="dist"
CONFIG="Release"

declare -A TARGETS=(
    ["macos-arm64"]="osx-arm64"
    ["macos-x64"]="osx-x64"
    ["linux-x64"]="linux-x64"
    ["linux-arm64"]="linux-arm64"
    ["windows-x64"]="win-x64"
    ["windows-arm64"]="win-arm64"
)

publish_target() {
    local name="$1"
    local rid="${TARGETS[$name]}"
    local out="$OUTPUT_DIR/$name"

    echo "==> Publishing $name ($rid)..."
    mkdir -p "$out"

    # On macOS, NativeAOT needs Homebrew libraries (openssl, brotli, etc.)
    if [[ "$(uname -s)" == "Darwin" ]]; then
        export LIBRARY_PATH="/opt/homebrew/lib:${LIBRARY_PATH:-}"
    fi

    dotnet publish "$PROJECT" \
        --configuration "$CONFIG" \
        --runtime "$rid" \
        --self-contained true \
        --output "$out" \
        -p:PublishAot=true \
        -p:OptimizationPreference=Size

    local binary
    if [[ "$name" == windows-* ]]; then
        binary="$out/docx-mcp.exe"
    else
        binary="$out/docx-mcp"
    fi

    if [[ -f "$binary" ]]; then
        local size
        size=$(du -sh "$binary" | cut -f1)
        echo "    Built: $binary ($size)"
    else
        echo "    WARNING: Binary not found at $binary"
    fi
}

main() {
    local target="${1:-current}"

    echo "docx-mcp NativeAOT publisher"
    echo "=============================="

    if [[ "$target" == "all" ]]; then
        for name in "${!TARGETS[@]}"; do
            publish_target "$name"
        done
    elif [[ "$target" == "current" ]]; then
        # Detect current platform
        local arch rid_name
        arch="$(uname -m)"
        case "$(uname -s)-$arch" in
            Darwin-arm64) rid_name="macos-arm64" ;;
            Darwin-x86_64) rid_name="macos-x64" ;;
            Linux-x86_64) rid_name="linux-x64" ;;
            Linux-aarch64) rid_name="linux-arm64" ;;
            *) echo "Unsupported platform: $(uname -s)-$arch"; exit 1 ;;
        esac
        publish_target "$rid_name"
    elif [[ -n "${TARGETS[$target]+x}" ]]; then
        publish_target "$target"
    else
        echo "Unknown target: $target"
        echo "Available: ${!TARGETS[*]} all current"
        exit 1
    fi

    echo ""
    echo "Done. Binaries are in $OUTPUT_DIR/"
}

main "$@"
