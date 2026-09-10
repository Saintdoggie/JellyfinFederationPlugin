#!/usr/bin/env bash
# Install the self-contained Companion for the current user. No sudo required.
set -euo pipefail
umask 077
REPO="Saintdoggie/JellyfinFederationPlugin"
TAG="companion-latest"
INSTALL_DIR="${FEDERATION_COMPANION_DIR:-$HOME/FederationCompanion}"
case "$(uname -s)/$(uname -m)" in
  Linux/x86_64) rid="linux-x64" ;;
  Darwin/arm64) rid="osx-arm64" ;;
  Darwin/x86_64) rid="osx-x64" ;;
  *) echo "No prebuilt Companion for this OS/architecture. See Companion/README.md for source builds." >&2; exit 1 ;;
esac
for utility in curl unzip; do
  command -v "$utility" >/dev/null || { echo "Install $utility first using your package manager." >&2; exit 1; }
done
if [[ "$INSTALL_DIR" == *$'\n'* || "$INSTALL_DIR" == *$'\r'* ]]; then
  echo "Install path cannot contain line breaks." >&2; exit 1
fi
if [[ -e "$INSTALL_DIR/FederationCompanion" ]]; then
  echo "Companion is already installed. Use Update in its dashboard to update safely." >&2
  exit 1
fi
work="$(mktemp -d -t federation-companion-XXXXXX)"
trap 'rm -rf -- "$work"' EXIT
asset="FederationCompanion-$rid.zip"
base="https://github.com/$REPO/releases/download/$TAG"
echo "Installing Companion in $INSTALL_DIR for your user."
echo "It runs a local dashboard and saves private credentials and logs beside the app."
echo "The optional media folder uses rclone, FUSE, bandwidth and a disk cache."
echo "No autostart, public access or system permissions are enabled by this installer."
curl --fail --show-error --location --proto '=https' --max-time 300 -o "$work/$asset" "$base/$asset"
curl --fail --show-error --location --proto '=https' --max-time 60 -o "$work/SHA256SUMS" "$base/SHA256SUMS"
expected="$(awk -v name="$asset" '$2 == name {print $1}' "$work/SHA256SUMS")"
if command -v sha256sum >/dev/null; then
  actual="$(sha256sum "$work/$asset")"
else
  actual="$(shasum -a 256 "$work/$asset")"
fi
[[ "$expected" =~ ^[a-fA-F0-9]{64}$ && "${actual%% *}" == "$expected" ]] || { echo "Archive checksum did not match. Nothing installed." >&2; exit 1; }
mkdir -p "$INSTALL_DIR"
unzip -oq "$work/$asset" -d "$INSTALL_DIR"
chmod +x "$INSTALL_DIR/FederationCompanion"
if [[ "$rid" == linux-* ]]; then
  launcher_dir="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
  mkdir -p "$launcher_dir"
  # Escape the Desktop Entry string layer as well as Exec's quoted argument.
  escaped="${INSTALL_DIR//\\/\\\\\\\\}"
  escaped="${escaped//\"/\\\\\"}"
  escaped="${escaped//\$/\\\\\$}"
  escaped="${escaped//\`/\\\\\`}"
  escaped="${escaped//%/%%}"
  cat > "$launcher_dir/federation-companion.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Federation Companion
Comment=Connect your Plex and Jellyfin friends
Exec="$escaped/FederationCompanion" --open
Terminal=false
Categories=AudioVideo;Network;
EOF
  echo "Added Federation Companion to your application menu."
  echo "Linux media mounting needs FUSE; Plex running as another user needs explicit mount access."
fi
if [[ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" || "$rid" == osx-* ]]; then
  nohup "$INSTALL_DIR/FederationCompanion" --open >/dev/null 2>&1 < /dev/null &
  echo "Dashboard opened. Enable Start Companion when I sign in there if wanted."
else
  echo "Headless install ready. Run $INSTALL_DIR/FederationCompanion --no-browser in a terminal for first setup."
  echo "See Companion/README.md for a systemd user service and SSH dashboard access."
fi
