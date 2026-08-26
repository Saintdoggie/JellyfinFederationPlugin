#!/usr/bin/env bash
# One-line installer for Federation Companion (macOS/Linux).
#
#   curl -fsSL https://raw.githubusercontent.com/Saintdoggie/JellyfinFederationPlugin/master/Companion/install.sh | bash
#
# Downloads the self-contained build for this machine's OS/arch from the
# "companion-latest" GitHub release, extracts it, and launches it - no .NET
# install required, since the build already bundles its own runtime.
set -euo pipefail

REPO="Saintdoggie/JellyfinFederationPlugin"
TAG="companion-latest"
INSTALL_DIR="${FEDERATION_COMPANION_DIR:-$HOME/FederationCompanion}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Darwin)
    case "$arch" in
      arm64) rid="osx-arm64" ;;
      *) rid="osx-x64" ;;
    esac
    ;;
  Linux)
    rid="linux-x64"
    ;;
  *)
    echo "Unsupported OS: $os. Download a build manually from https://github.com/$REPO/releases/tag/$TAG" >&2
    exit 1
    ;;
esac

url="https://github.com/$REPO/releases/download/$TAG/FederationCompanion-$rid.zip"
archive="$(mktemp -t federation-companion-XXXXXX.zip)"

echo "Downloading Federation Companion ($rid)..."
curl -fsSL -o "$archive" "$url"

mkdir -p "$INSTALL_DIR"
unzip -oq "$archive" -d "$INSTALL_DIR"
rm -f "$archive"
chmod +x "$INSTALL_DIR/FederationCompanion"

echo "Installed to $INSTALL_DIR"
echo "Starting Federation Companion - open the URL it prints in your browser."
cd "$INSTALL_DIR"
exec ./FederationCompanion
