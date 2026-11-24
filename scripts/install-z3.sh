#!/usr/bin/env bash
set -euo pipefail

DEST="${1:-$HOME/.verifiable/z3}"
FALLBACK_VERSION="${Z3_VERSION:-z3-5.3.3}"

if [ -x "$DEST/z3" ]; then
  VER="$("$DEST/z3" --version)"
  echo "Z3 already installed at $DEST/z3 ($VER)"
  echo "VERIFIABLE_Z3_PATH=$DEST/z3"
  exit 0
fi

OS="$(uname -s)"
ARCH="$(uname -m)"

case "${OS}:${ARCH}" in
  Linux:x86_64)
    PATTERN="x64-glibc.*\.zip$"
    FALLBACK_ASSET="${FALLBACK_VERSION}-x64-glibc-2.39.zip"
    ;;
  Darwin:arm64)
    PATTERN="arm64-osx.*\.zip$"
    FALLBACK_ASSET="${FALLBACK_VERSION}-arm64-osx-13.3.zip"
    ;;
  Darwin:x86_64)
    PATTERN="x64-osx.*\.zip$"
    FALLBACK_ASSET="${FALLBACK_VERSION}-x64-osx-13.3.zip"
    ;;
  *)
    echo "Unsupported platform ${OS}:${ARCH}. Please install Z3 manually and set VERIFIABLE_Z3_PATH." >&2
    exit 1
    ;;
esac

ASSET_URL=""
if command -v curl >/dev/null 2>&1 && command -v python3 >/dev/null 2>&1; then
  ASSET_URL="$(curl -fsSL -H "User-Agent: Verifiable.NET-Installer" "https://api.github.com/repos/Z3Prover/z3/releases/latest" \
    | python3 -c "import json,re,sys; d=json.load(sys.stdin); m=[a['browser_download_url'] for a in d.get('assets',[]) if re.search(r'$PATTERN', a['name'])]; print(m[0] if m else '')" || true)"
fi

if [ -z "$ASSET_URL" ]; then
  ASSET_URL="https://github.com/Z3Prover/z3/releases/download/${FALLBACK_VERSION}/${FALLBACK_ASSET}"
fi

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

echo "Downloading Z3 from $ASSET_URL ..."
curl -fsSL "$ASSET_URL" -o "$TMP_DIR/z3.zip"

if command -v unzip >/dev/null 2>&1; then
  unzip -q "$TMP_DIR/z3.zip" -d "$TMP_DIR/extract"
else
  python3 -m zipfile -e "$TMP_DIR/z3.zip" "$TMP_DIR/extract"
fi

BIN_DIR="$(dirname "$(find "$TMP_DIR/extract" -type f -name z3 | head -n 1)")"
if [ -z "$BIN_DIR" ] || [ ! -d "$BIN_DIR" ]; then
  echo "Failed to locate z3 binary in archive." >&2
  exit 1
fi

mkdir -p "$DEST"
cp -R "$BIN_DIR"/* "$DEST/"
chmod +x "$DEST/z3"

VER="$("$DEST/z3" --version)"
echo "Successfully installed $VER to $DEST/z3"
echo "VERIFIABLE_Z3_PATH=$DEST/z3"
