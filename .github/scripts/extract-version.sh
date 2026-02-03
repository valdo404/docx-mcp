#!/bin/bash
# Extract version from git tags
# Output: VERSION, TAG, PRERELEASE to GITHUB_OUTPUT

set -e

SHORT_SHA=$(git rev-parse --short HEAD)

# Check if current commit has a version tag
CURRENT_TAG=$(git tag --points-at HEAD | grep -E '^v[0-9]' | head -1)

if [[ -n "$CURRENT_TAG" ]]; then
    # Current commit is tagged with a version
    VERSION="${CURRENT_TAG#v}"
    TAG="$CURRENT_TAG"
    PRERELEASE="false"
    echo "Detected tagged release: $TAG"
else
    # Find the latest version tag
    LATEST_TAG=$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || echo "v0.0.0")
    VERSION="${LATEST_TAG#v}-dev.${SHORT_SHA}"
    TAG="v$VERSION"
    PRERELEASE="true"
    echo "Detected dev release: $TAG (based on $LATEST_TAG)"
fi

# Output to GitHub Actions
if [[ -n "$GITHUB_OUTPUT" ]]; then
    echo "VERSION=$VERSION" >> "$GITHUB_OUTPUT"
    echo "TAG=$TAG" >> "$GITHUB_OUTPUT"
    echo "PRERELEASE=$PRERELEASE" >> "$GITHUB_OUTPUT"
fi

# Also output for local testing
echo "VERSION=$VERSION"
echo "TAG=$TAG"
echo "PRERELEASE=$PRERELEASE"
