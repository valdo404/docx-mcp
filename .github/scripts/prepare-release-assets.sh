#!/bin/bash
# Prepare release assets from build artifacts
# Expects artifacts to be downloaded in ./artifacts/

set -e

mkdir -p release-assets

# Windows installers
for arch in x64 arm64; do
    if [[ -d "artifacts/windows-${arch}-installer" ]]; then
        echo "Copying Windows $arch installer..."
        cp artifacts/windows-${arch}-installer/*.exe release-assets/ 2>/dev/null || true
    fi
done

# macOS installers
for arch in x64 arm64; do
    if [[ -d "artifacts/macos-${arch}-pkg" ]]; then
        echo "Copying macOS $arch installer..."
        cp artifacts/macos-${arch}-pkg/*.pkg release-assets/ 2>/dev/null || true
    fi
done

echo ""
echo "Release assets:"
ls -la release-assets/
