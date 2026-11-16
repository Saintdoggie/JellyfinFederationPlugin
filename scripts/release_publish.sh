#!/bin/bash
# Package, commit, push and publish one plugin release. Run from the repository
# root after the gate in AGENTS.md passed on a finished normal Release build.
#
#   python3 scripts/release_prepare.py prepare OLD NEW "changelog sentence"   (before the build)
#   scripts/release_publish.sh NEW "Release title" notes.md commit-message.txt
#
# It builds the zip from bin/Release/net10.0, checks the version inside it, writes
# the checksum into manifest.json, refuses a diff that carries personal data,
# commits everything not ignored, pushes HEAD to master (never forced, and only
# when master has nothing new) and creates the GitHub release with the zip.
set -euo pipefail
V=$1; TITLE=$2; NOTES=$3; MSG=$4
ROOT=$(git rev-parse --show-toplevel); cd "$ROOT"
D=$ROOT/artifacts/releases/$V; Z=$D/Jellyfin.Federation.zip
mkdir -p "$D"; test ! -e "$Z" || { echo "$Z exists already"; exit 1; }
(cd bin/Release/net10.0 && zip -qr "$Z" logo.png meta.json Jellyfin.Plugin.Federation.dll native)
zip -q "$Z" native/LICENSE-msquic.txt
unzip -l "$Z" | tail -3
unzip -p "$Z" meta.json | grep -q "\"version\": \"$V\""
python3 -I scripts/release_prepare.py checksum "$V" "$Z"
# Personal data must never reach the public repository (see AGENTS.md).
DENY=$HOME/.config/federation-release-denylist   # one extended regex: names, domains, addresses that must not be committed
test -s "$DENY" || { echo "write your denylist regex to $DENY first"; exit 1; }
if git diff HEAD | grep -E "^\+" | grep -qiE -f "$DENY"; then echo "personal data in diff"; exit 1; fi
git add -A -- .
git status --short
git commit -q -F "$MSG"
git fetch -q origin; test -z "$(git log --oneline HEAD..origin/master)" || { echo "master moved; merge first"; exit 1; }
git push origin HEAD:master 2>&1 | tail -1
gh release create "$V" "$Z" --target master --title "$TITLE" --notes-file "$NOTES"
echo "asset md5: $(curl -sL -m 60 "https://github.com/Saintdoggie/JellyfinFederationPlugin/releases/download/$V/Jellyfin.Federation.zip" | md5sum)"
