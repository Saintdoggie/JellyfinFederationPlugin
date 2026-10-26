#!/usr/bin/env bash
set -euo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
DOTNET=${DOTNET:-dotnet}
VERSION=$($DOTNET msbuild "$ROOT/Plex/Jellyfin.Plugin.Federation.Plex.csproj" -nologo -getProperty:Version)
OUT_DIR="$ROOT/artifacts/plex-addin"
ARCHIVE="$OUT_DIR/Jellyfin.Plugin.Federation.Plex-$VERSION.zip"

$DOTNET build "$ROOT/Plex/Jellyfin.Plugin.Federation.Plex.csproj" --configuration Release --nologo
mkdir -p "$OUT_DIR"
STAGING=$(mktemp -d)
trap 'rm -rf -- "$STAGING"' EXIT
cp "$ROOT/Plex/bin/Release/net10.0/Jellyfin.Plugin.Federation.Plex.dll" "$STAGING/"
cp "$ROOT/Plex/INSTALL.md" "$STAGING/"

ARCHIVE="$ARCHIVE" STAGING="$STAGING" python3 - <<'PY'
import hashlib
import os
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

archive = Path(os.environ["ARCHIVE"])
staging = Path(os.environ["STAGING"])
with ZipFile(archive, "w", compression=ZIP_DEFLATED, compresslevel=9) as package:
    for name in ("Jellyfin.Plugin.Federation.Plex.dll", "INSTALL.md"):
        package.write(staging / name, arcname=name)

with ZipFile(archive) as package:
    assert package.testzip() is None
    assert set(package.namelist()) == {"Jellyfin.Plugin.Federation.Plex.dll", "INSTALL.md"}

digest = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(archive.suffix + ".sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
print(f"Created {archive}")
print(f"SHA256 {digest}")
PY
