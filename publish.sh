#!/usr/bin/env bash
set -euo pipefail

# Build NativeAOT binaries with embedded Rust storage for all supported platforms.
# Requires .NET 10 SDK and Rust toolchain.
#
# Usage:
#   ./publish.sh              # Build for current platform
#   ./publish.sh all          # Build for all platforms (cross-compile)
#   ./publish.sh macos-arm64  # Build for specific target
#   ./publish.sh rust         # Build only Rust staticlib for current platform

SERVER_PROJECT="src/DocxMcp/DocxMcp.csproj"
CLI_PROJECT="src/DocxMcp.Cli/DocxMcp.Cli.csproj"
STORAGE_CRATE="crates/docx-storage-local"
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

# Rust target triples for cross-compilation
declare -A RUST_TARGETS=(
    ["macos-arm64"]="aarch64-apple-darwin"
    ["macos-x64"]="x86_64-apple-darwin"
    ["linux-x64"]="x86_64-unknown-linux-gnu"
    ["linux-arm64"]="aarch64-unknown-linux-gnu"
    ["windows-x64"]="x86_64-pc-windows-msvc"
    ["windows-arm64"]="aarch64-pc-windows-msvc"
)

# Staticlib names per platform
rust_staticlib_name() {
    local name="$1"
    if [[ "$name" == windows-* ]]; then
        echo "docx_storage_local.lib"
    else
        echo "libdocx_storage_local.a"
    fi
}

publish_project() {
    local project="$1"
    local binary_name="$2"
    local rid="$3"
    local out="$4"
    local rust_lib_path="$5"

    local extra_args=()
    if [[ -n "$rust_lib_path" ]]; then
        extra_args+=("-p:RustStaticLibPath=$rust_lib_path")
    fi

    dotnet publish "$project" \
        --configuration "$CONFIG" \
        --runtime "$rid" \
        --self-contained true \
        --output "$out" \
        -p:PublishAot=true \
        -p:OptimizationPreference=Size \
        "${extra_args[@]}"

    local binary
    if [[ "$out" == *windows* ]]; then
        binary="$out/${binary_name}.exe"
    else
        binary="$out/$binary_name"
    fi

    if [[ -f "$binary" ]]; then
        local size
        size=$(du -sh "$binary" | cut -f1)
        echo "    Built: $binary ($size)"
    else
        echo "    WARNING: Binary not found at $binary"
    fi
}

# Build Rust staticlib (for embedding into .NET binaries)
build_rust_staticlib() {
    local name="$1"
    local rust_target="${RUST_TARGETS[$name]}"
    local current_target

    # Detect current Rust target
    local arch
    arch="$(uname -m)"
    case "$(uname -s)-$arch" in
        Darwin-arm64) current_target="aarch64-apple-darwin" ;;
        Darwin-x86_64) current_target="x86_64-apple-darwin" ;;
        Linux-x86_64) current_target="x86_64-unknown-linux-gnu" ;;
        Linux-aarch64) current_target="aarch64-unknown-linux-gnu" ;;
        *) current_target="" ;;
    esac

    local lib_name
    lib_name=$(rust_staticlib_name "$name")

    if [[ "$rust_target" == "$current_target" ]]; then
        # Native build
        echo "    Building Rust staticlib (native)..." >&2
        cargo build --release --package docx-storage-local --lib
        echo "target/release/$lib_name"
    else
        # Cross-compile (requires target installed)
        if rustup target list --installed | grep -q "$rust_target"; then
            echo "    Building Rust staticlib (cross: $rust_target)..." >&2
            cargo build --release --package docx-storage-local --lib --target "$rust_target"
            echo "target/$rust_target/release/$lib_name"
        else
            echo "    SKIP: Rust target $rust_target not installed (run: rustup target add $rust_target)" >&2
            echo ""
            return 0
        fi
    fi
}

# Build standalone Rust binary (for remote server use)
build_rust_binary() {
    local name="$1"
    local out="$2"
    local rust_target="${RUST_TARGETS[$name]}"
    local current_target

    local arch
    arch="$(uname -m)"
    case "$(uname -s)-$arch" in
        Darwin-arm64) current_target="aarch64-apple-darwin" ;;
        Darwin-x86_64) current_target="x86_64-apple-darwin" ;;
        Linux-x86_64) current_target="x86_64-unknown-linux-gnu" ;;
        Linux-aarch64) current_target="aarch64-unknown-linux-gnu" ;;
        *) current_target="" ;;
    esac

    local binary_name="docx-storage-local"
    [[ "$name" == windows-* ]] && binary_name="docx-storage-local.exe"

    if [[ "$rust_target" == "$current_target" ]]; then
        echo "    Building Rust storage binary (native)..."
        cargo build --release --package docx-storage-local
        cp "target/release/$binary_name" "$out/" 2>/dev/null || \
            cp "target/release/docx-storage-local" "$out/$binary_name"
    else
        if rustup target list --installed | grep -q "$rust_target"; then
            echo "    Building Rust storage binary (cross: $rust_target)..."
            cargo build --release --package docx-storage-local --target "$rust_target"
            cp "target/$rust_target/release/$binary_name" "$out/" 2>/dev/null || \
                cp "target/$rust_target/release/docx-storage-local" "$out/$binary_name"
        else
            echo "    SKIP: Rust binary target $rust_target not installed"
            return 0
        fi
    fi

    if [[ -f "$out/$binary_name" ]]; then
        local size
        size=$(du -sh "$out/$binary_name" | cut -f1)
        echo "    Built: $out/$binary_name ($size)"
    fi
}

publish_target() {
    local name="$1"
    local rid="${TARGETS[$name]}"
    local out="$OUTPUT_DIR/$name"

    mkdir -p "$out"

    # On macOS, NativeAOT needs Homebrew libraries (openssl, brotli, etc.)
    if [[ "$(uname -s)" == "Darwin" ]]; then
        export LIBRARY_PATH="/opt/homebrew/lib:${LIBRARY_PATH:-}"
    fi

    # 1. Build Rust staticlib
    echo "==> Building Rust staticlib ($name)..."
    local rust_lib_path
    rust_lib_path=$(build_rust_staticlib "$name")

    if [[ -z "$rust_lib_path" ]]; then
        echo "    SKIP: Could not build Rust staticlib for $name"
        return 0
    fi

    local abs_rust_lib_path
    abs_rust_lib_path="$(pwd)/$rust_lib_path"

    if [[ -f "$abs_rust_lib_path" ]]; then
        local size
        size=$(du -sh "$abs_rust_lib_path" | cut -f1)
        echo "    Staticlib: $abs_rust_lib_path ($size)"
    fi

    # 2. Build .NET with embedded Rust
    echo "==> Publishing docx-mcp ($name / $rid) [embedded storage]..."
    publish_project "$SERVER_PROJECT" "docx-mcp" "$rid" "$out" "$abs_rust_lib_path"

    echo "==> Publishing docx-cli ($name / $rid) [embedded storage]..."
    publish_project "$CLI_PROJECT" "docx-cli" "$rid" "$out" "$abs_rust_lib_path"

    # 3. (Optional) Build standalone Rust binary for remote server use
    echo "==> Publishing docx-storage-local binary ($name) [standalone]..."
    build_rust_binary "$name" "$out"
}

publish_rust_only() {
    local rid_name="$1"
    local out="$OUTPUT_DIR/$rid_name"
    mkdir -p "$out"

    echo "==> Building Rust staticlib ($rid_name)..."
    local rust_lib_path
    rust_lib_path=$(build_rust_staticlib "$rid_name")

    if [[ -n "$rust_lib_path" && -f "$rust_lib_path" ]]; then
        local size
        size=$(du -sh "$rust_lib_path" | cut -f1)
        echo "    Staticlib: $rust_lib_path ($size)"
        cp "$rust_lib_path" "$out/"
    fi

    echo "==> Building Rust binary ($rid_name)..."
    build_rust_binary "$rid_name" "$out"
}

detect_current_platform() {
    local arch
    arch="$(uname -m)"
    case "$(uname -s)-$arch" in
        Darwin-arm64) echo "macos-arm64" ;;
        Darwin-x86_64) echo "macos-x64" ;;
        Linux-x86_64) echo "linux-x64" ;;
        Linux-aarch64) echo "linux-arm64" ;;
        *) echo ""; return 1 ;;
    esac
}

main() {
    local target="${1:-current}"

    echo "docx-mcp NativeAOT publisher (embedded storage)"
    echo "================================================="

    if [[ "$target" == "all" ]]; then
        for name in "${!TARGETS[@]}"; do
            publish_target "$name"
        done
    elif [[ "$target" == "rust" ]]; then
        # Build only Rust artifacts for current platform
        local rid_name
        rid_name=$(detect_current_platform) || { echo "Unsupported platform"; exit 1; }
        publish_rust_only "$rid_name"
    elif [[ "$target" == "current" ]]; then
        # Detect current platform
        local rid_name
        rid_name=$(detect_current_platform) || { echo "Unsupported platform: $(uname -s)-$(uname -m)"; exit 1; }
        publish_target "$rid_name"
    elif [[ -n "${TARGETS[$target]+x}" ]]; then
        publish_target "$target"
    else
        echo "Unknown target: $target"
        echo "Available: ${!TARGETS[*]} all current rust"
        exit 1
    fi

    echo ""
    echo "Done. Binaries are in $OUTPUT_DIR/"
}

main "$@"
