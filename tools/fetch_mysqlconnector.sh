#!/usr/bin/env bash
set -euo pipefail
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PKG_VERSION="2.3.7"
PKG_ID="MySqlConnector"
PKG_DIR="$ROOT_DIR/packages"
PKG_PATH="$PKG_DIR/$PKG_ID.$PKG_VERSION.nupkg"
EXTRACT_DIR="$PKG_DIR/$PKG_ID.$PKG_VERSION"
URL="https://www.nuget.org/api/v2/package/$PKG_ID/$PKG_VERSION"

mkdir -p "$PKG_DIR"

if [ ! -f "$PKG_PATH" ]; then
  echo "Downloading $PKG_ID $PKG_VERSION from nuget.org..."
  python - <<'PY'
import sys
import urllib.request
url = sys.argv[1]
path = sys.argv[2]
with urllib.request.urlopen(url) as resp:
    data = resp.read()
open(path, 'wb').write(data)
print('Downloaded {} bytes to {}'.format(len(data), path))
PY
 "$URL" "$PKG_PATH"
else
  echo "Using existing $PKG_PATH"
fi

mkdir -p "$EXTRACT_DIR"

echo "Extracting package into $EXTRACT_DIR..."
python - <<'PY'
import os
import sys
import zipfile
pkg_path = sys.argv[1]
dest = sys.argv[2]
with zipfile.ZipFile(pkg_path, 'r') as zf:
    zf.extractall(dest)
print('Extracted contents to {}'.format(dest))
PY
 "$PKG_PATH" "$EXTRACT_DIR"

echo "Done. Ensure your project references point to $EXTRACT_DIR."
