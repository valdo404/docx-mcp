#!/bin/bash
# Generate release notes markdown
# Usage: ./generate-release-notes.sh <version> <docker_image>

set -e

VERSION="${1:-0.0.0-dev}"
DOCKER_IMAGE="${2:-valdo404/docx-mcp}"

cat > release-notes.md << EOF
## DocX MCP Server $VERSION

### Installation

#### Windows
Download and run the \`.exe\` installer for your architecture.

> **Note**: Windows SmartScreen may show a warning for unsigned binaries. Click "More info" → "Run anyway".

#### macOS
Download and run the \`.pkg\` installer for your architecture.

> **Note**: macOS may block unsigned packages. Right-click → Open, or run:
> \`\`\`bash
> xattr -cr docx-mcp-*.pkg
> \`\`\`

#### Linux / Docker
\`\`\`bash
docker pull ${DOCKER_IMAGE}:${VERSION}
\`\`\`

### Checksums
\`\`\`
EOF

# Add checksums
cd release-assets
if command -v sha256sum &> /dev/null; then
    sha256sum * >> ../release-notes.md 2>/dev/null || true
else
    shasum -a 256 * >> ../release-notes.md 2>/dev/null || true
fi
cd ..

echo '```' >> release-notes.md

echo "Generated release-notes.md"
