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
  if ! python - <<'PY'
import os
import sys
import urllib.error
import urllib.request

url = sys.argv[1]
path = sys.argv[2]

try:
    with urllib.request.urlopen(url) as resp:
        data = resp.read()
    if len(data) < 1024:
        raise RuntimeError("Downloaded content is too small ({} bytes)".format(len(data)))
    with open(path, 'wb') as fh:
        fh.write(data)
    print('Downloaded {} bytes to {}'.format(len(data), path))
except Exception as exc:
    if os.path.exists(path):
        os.remove(path)
    raise SystemExit('Download failed: {}'.format(exc))
PY
   "$URL" "$PKG_PATH"; then
    echo "ERROR: Unable to download $PKG_ID $PKG_VERSION. Please download it manually into $PKG_DIR and re-run this script."
    exit 1
  fi
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

if not os.path.isfile(pkg_path):
    raise SystemExit('Package not found: {}'.format(pkg_path))

with zipfile.ZipFile(pkg_path, 'r') as zf:
    zf.extractall(dest)
print('Extracted contents to {}'.format(dest))
PY
 "$PKG_PATH" "$EXTRACT_DIR"

echo "Done. Ensure your project references point to $EXTRACT_DIR."
