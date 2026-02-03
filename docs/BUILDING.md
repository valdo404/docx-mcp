# Building DocX MCP Server

This document describes how to build and release DocX MCP Server with signed installers.

## Quick Start (Development)

```bash
# Build for current platform
./publish.sh

# Build for all platforms
./publish.sh all
```

## CI/CD Pipeline

The GitHub Actions workflow automatically builds:

| Platform | Architectures | Outputs |
|----------|---------------|---------|
| Windows | x64, arm64 | `.exe` installer + `.zip` binaries |
| macOS | x64, arm64 | `.pkg` installer + `.dmg` + `.tar.gz` binaries |
| Linux | amd64, arm64 | Docker image (ghcr.io) |

## Secrets Configuration

To enable code signing and notarization, configure these secrets in your GitHub repository settings.

### macOS Code Signing & Notarization

For distributing macOS applications outside the App Store, you need:

1. **Apple Developer Program membership** ($99/year)
2. **Developer ID Application certificate** (for signing binaries)
3. **Developer ID Installer certificate** (for signing .pkg files)

#### Required Secrets

| Secret | Description |
|--------|-------------|
| `APPLE_CERTIFICATE` | Base64-encoded Developer ID Application certificate (.p12) |
| `APPLE_CERTIFICATE_PASSWORD` | Password for the .p12 file |
| `APPLE_INSTALLER_CERTIFICATE` | Base64-encoded Developer ID Installer certificate (.p12) |
| `APPLE_INSTALLER_CERTIFICATE_PASSWORD` | Password for the installer .p12 file |
| `APPLE_ID` | Your Apple ID email |
| `APPLE_TEAM_ID` | Your 10-character Apple Developer Team ID |
| `APPLE_APP_PASSWORD` | App-specific password for notarization |

#### How to Generate Apple Secrets

1. **Export certificates from Keychain Access:**
   ```bash
   # Open Keychain Access > My Certificates
   # Right-click "Developer ID Application: Your Name (TEAMID)"
   # Export as .p12 with a strong password

   # Repeat for "Developer ID Installer: Your Name (TEAMID)"
   ```

2. **Convert to Base64:**
   ```bash
   base64 -i DeveloperIDApplication.p12 | pbcopy
   # Paste into APPLE_CERTIFICATE secret

   base64 -i DeveloperIDInstaller.p12 | pbcopy
   # Paste into APPLE_INSTALLER_CERTIFICATE secret
   ```

3. **Create App-Specific Password:**
   - Go to https://appleid.apple.com/account/manage
   - Sign in and go to "App-Specific Passwords"
   - Generate a new password for "GitHub Actions"
   - Save as `APPLE_APP_PASSWORD` secret

4. **Find your Team ID:**
   - Go to https://developer.apple.com/account
   - Your Team ID is shown in the top-right or in Membership details

### Windows Code Signing (Optional)

Windows code signing prevents "Unknown Publisher" warnings.

#### Required Secrets

| Secret | Description |
|--------|-------------|
| `WINDOWS_CERTIFICATE` | Base64-encoded code signing certificate (.pfx) |
| `WINDOWS_CERTIFICATE_PASSWORD` | Password for the .pfx file |

#### Certificate Options

1. **OV (Organization Validation) Certificate** - ~$200-500/year
   - From: DigiCert, Sectigo, GlobalSign
   - Standard reputation, may trigger SmartScreen initially

2. **EV (Extended Validation) Certificate** - ~$300-700/year
   - Requires hardware token
   - Instant Windows SmartScreen reputation
   - More complex CI/CD setup (needs Azure SignTool or similar)

#### How to Generate Windows Secrets

```bash
# Convert .pfx to Base64
base64 -i certificate.pfx | pbcopy
# Paste into WINDOWS_CERTIFICATE secret
```

## Release Process

### Automatic Releases

Push a version tag to trigger a release:

```bash
git tag v1.0.0
git push origin v1.0.0
```

This will:
1. Run all tests
2. Build binaries for all platforms
3. Create signed installers (if secrets are configured)
4. Notarize macOS packages (if secrets are configured)
5. Create a GitHub Release with all assets

### Manual Release

Use the workflow dispatch feature:

1. Go to Actions > "Build & Release"
2. Click "Run workflow"
3. Check "Create GitHub Release"
4. Click "Run workflow"

## Local Installer Building

### macOS PKG

```bash
# Build unsigned PKG
VERSION=1.0.0 ARCH=arm64 ./installers/macos/build-pkg.sh

# Build signed & notarized PKG
VERSION=1.0.0 ARCH=arm64 \
  SIGNING_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
  INSTALLER_SIGNING_IDENTITY="Developer ID Installer: Your Name (TEAMID)" \
  NOTARIZE=true \
  APPLE_ID="your@email.com" \
  APPLE_TEAM_ID="TEAMID" \
  NOTARYTOOL_PASSWORD="@keychain:AC_PASSWORD" \
  ./installers/macos/build-pkg.sh
```

### macOS DMG

```bash
VERSION=1.0.0 ARCH=arm64 ./installers/macos/build-dmg.sh
```

### Windows Installer

Requires Inno Setup installed (via Chocolatey: `choco install innosetup`):

```powershell
iscc /DMyAppVersion=1.0.0 /DMyAppArch=x64 installers\windows\docx-mcp.iss
```

## Troubleshooting

### macOS: "app is damaged and can't be opened"

The binary wasn't signed or notarization failed. Check:
- Certificate is valid and not expired
- Notarization completed successfully
- Ticket was stapled to the package

To check notarization status:
```bash
xcrun stapler validate MyApp.pkg
```

### Windows: SmartScreen Warning

This is normal for new or OV certificates. Options:
- Use an EV certificate (instant reputation)
- Wait for reputation to build (requires many downloads)
- Users can click "More info" > "Run anyway"

### Build Fails: "Certificate not found"

Verify secrets are correctly set:
- Check Base64 encoding (no line breaks)
- Verify password is correct
- Ensure certificate hasn't expired

## Architecture Support

| Platform | x64 | arm64 |
|----------|-----|-------|
| Windows | Native | Cross-compiled |
| macOS | Native | Native (Apple Silicon) |
| Linux | Docker QEMU | Docker QEMU |

Note: Windows arm64 is cross-compiled from x64 runner. macOS builds run on Apple Silicon (macos-latest = M1).
