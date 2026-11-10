#!/usr/bin/env bash
# Builds the installable "Jellyfin Federation Plex" plugin archive (one DLL that
# carries the add-in, plus meta.json). It does not touch manifest.json, tags or
# releases: publishing is a separate, deliberate step.
set -euo pipefail

ROOT=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
DOTNET=${DOTNET:-dotnet}
PROJECT="$ROOT/PlexPlugin/Jellyfin.Plugin.FederationPlex.csproj"
VERSION=$($DOTNET msbuild "$PROJECT" -nologo -getProperty:Version)
CORE_VERSION=$($DOTNET msbuild "$ROOT/JellyfinFederationPlugin.csproj" -nologo -getProperty:Version)
if [ "$VERSION" != "$CORE_VERSION" ]; then
  echo "Plex plugin $VERSION and core $CORE_VERSION must be the same version" >&2
  exit 1
fi

OUT_DIR="$ROOT/artifacts/plex-plugin"
ARCHIVE="$OUT_DIR/jellyfin-federation-plex_$VERSION.zip"
$DOTNET build "$PROJECT" --configuration Release --nologo
mkdir -p "$OUT_DIR"

ARCHIVE="$ARCHIVE" BIN="$ROOT/PlexPlugin/bin/Release/net10.0" python3 - <<'PY'
import hashlib
import os
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

archive = Path(os.environ["ARCHIVE"])
binaries = Path(os.environ["BIN"])
names = ("Jellyfin.Plugin.FederationPlex.dll", "meta.json")
with ZipFile(archive, "w", compression=ZIP_DEFLATED, compresslevel=9) as package:
    for name in names:
        package.write(binaries / name, arcname=name)

with ZipFile(archive) as package:
    assert package.testzip() is None
    assert set(package.namelist()) == set(names)

md5 = hashlib.md5(archive.read_bytes()).hexdigest()
sha = hashlib.sha256(archive.read_bytes()).hexdigest()
archive.with_suffix(archive.suffix + ".sha256").write_text(f"{sha}  {archive.name}\n", encoding="utf-8")
print(f"Created {archive}")
print(f"MD5 (manifest checksum) {md5}")
print(f"SHA256 {sha}")
PY
