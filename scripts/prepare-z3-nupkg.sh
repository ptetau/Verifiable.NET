#!/usr/bin/env bash
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${Z3_VERSION:-z3-5.3.3}"
DEST="${1:-$REPO_ROOT/src/Verifiable.Z3/tools}"
RIDS="${Z3_RIDS:-win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64}"

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT

RELEASE_JSON=""
if command -v curl >/dev/null 2>&1 && python3 --version >/dev/null 2>&1; then
  RELEASE_JSON="$(curl -fsSL -H "User-Agent: Verifiable.NET-Packager" "https://api.github.com/repos/Z3Prover/z3/releases/tags/${VERSION}" || true)"
fi

resolve_url() {
  local pattern="$1"
  local fallback="$2"
  local url=""
  if [ -n "$RELEASE_JSON" ] && python3 --version >/dev/null 2>&1; then
    url="$(printf '%s' "$RELEASE_JSON" | python3 -c "import json,re,sys; d=json.load(sys.stdin); m=[a['browser_download_url'] for a in d.get('assets',[]) if re.search(r'$pattern', a['name'])]; print(m[0] if m else '')" || true)"
  fi
  if [ -z "$url" ]; then
    url="https://github.com/Z3Prover/z3/releases/download/${VERSION}/${fallback}"
  fi
  printf '%s' "$url"
}

for rid in $RIDS; do
  case "$rid" in
    win-x64)     PATTERN='x64-win\.zip$';       EXE='z3.exe'; FALLBACK="${VERSION}-x64-win.zip" ;;
    win-arm64)   PATTERN='arm64-win\.zip$';     EXE='z3.exe'; FALLBACK="${VERSION}-arm64-win.zip" ;;
    linux-x64)   PATTERN='x64-glibc.*\.zip$';   EXE='z3';     FALLBACK="${VERSION}-x64-glibc-2.39.zip" ;;
    linux-arm64) PATTERN='arm64-glibc.*\.zip$'; EXE='z3';     FALLBACK="${VERSION}-arm64-glibc-2.38.zip" ;;
    osx-x64)     PATTERN='x64-osx.*\.zip$';     EXE='z3';     FALLBACK="${VERSION}-x64-osx-13.3.zip" ;;
    osx-arm64)   PATTERN='arm64-osx.*\.zip$';   EXE='z3';     FALLBACK="${VERSION}-arm64-osx-13.3.zip" ;;
    *) echo "Unsupported RID: $rid" >&2; exit 1 ;;
  esac

  TARGET_DIR="$DEST/$rid"
  TARGET_EXE="$TARGET_DIR/$EXE"
  if [ -f "$TARGET_EXE" ]; then
    echo "[$rid] Already staged at $TARGET_EXE"
    continue
  fi

  URL="$(resolve_url "$PATTERN" "$FALLBACK")"
  ZIP_PATH="$TMP_DIR/${rid}.zip"
  EXTRACT_DIR="$TMP_DIR/extract-${rid}"

  echo "[$rid] Downloading $URL ..."
  curl -fsSL "$URL" -o "$ZIP_PATH"
  mkdir -p "$EXTRACT_DIR" "$TARGET_DIR"

  if python3 --version >/dev/null 2>&1; then
    python3 - "$ZIP_PATH" "$EXE" "$TARGET_EXE" <<'PY'
import sys, zipfile
zip_path, exe_name, out_path = sys.argv[1], sys.argv[2], sys.argv[3]
with zipfile.ZipFile(zip_path) as zf:
    matches = [n for n in zf.namelist() if n.endswith(f"/bin/{exe_name}")]
    if not matches:
        raise RuntimeError(f"Missing bin/{exe_name} in {zip_path}")
    with zf.open(matches[0]) as src, open(out_path, "wb") as dst:
        dst.write(src.read())
PY
  else
    unzip -q "$ZIP_PATH" "*/bin/${EXE}" -d "$EXTRACT_DIR"
    FOUND="$(find "$EXTRACT_DIR" -type f -name "$EXE" | head -n 1)"
    cp "$FOUND" "$TARGET_EXE"
  fi

  if [ "$EXE" = "z3" ]; then
    chmod +x "$TARGET_EXE"
  fi
  echo "[$rid] Staged $TARGET_EXE"
done
