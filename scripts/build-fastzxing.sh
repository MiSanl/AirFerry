#!/usr/bin/env bash
# Build the self-compiled ZXing-C++ → WASM fast decoder (M3 fast path) and copy
# it into apps/sender/src/fastzxing/ so the web/ext builds bundle it.
#
# Prereqs:
#   - Emscripten installed & on PATH (e.g. `source ~/emsdk/emsdk_env.sh`)
#   - Network for the first FetchContent clone of zxing-cpp (pinned commit)
#
# Usage:
#   ./scripts/build-fastzxing.sh
#   # reuse the Android build cache's zxing-src (no re-download):
#   ./scripts/build-fastzxing.sh --use-cache
set -euo pipefail
cd "$(dirname "$0")/.."

HERE="$(pwd)"
SRC="$HERE/core/zxing-decoder"
OUT="$HERE/apps/sender/src/fastzxing"
BUILD="$SRC/build-wasm"

if ! command -v emcc >/dev/null 2>&1; then
  echo "error: emcc not found. Activate Emscripten first (source ~/emsdk/emsdk_env.sh)." >&2
  exit 1
fi

EXTRA_CMAKE=()
if [[ "${1:-}" == "--use-cache" ]]; then
  CACHE_SRC="$HERE/apps/scanner/app/.cxx/Debug/3m4r2j6m/arm64-v8a/zxing-src"
  if [[ -d "$CACHE_SRC" ]]; then
    EXTRA_CMAKE=("-DZXING_SRC_DIR=$CACHE_SRC")
    echo "using cached zxing-cpp source: $CACHE_SRC"
  else
    echo "cache not found; will FetchContent download zxing-cpp" >&2
  fi
fi

echo "== configure (emcmake) =="
emcmake cmake -S "$SRC" -B "$BUILD" -DCMAKE_BUILD_TYPE=Release "${EXTRA_CMAKE[@]}"

echo "== build =="
emmake cmake --build "$BUILD" -j"$(sysctl -n hw.ncpu 2>/dev/null || echo 4)"

echo "== link =="
mkdir -p "$OUT"
"$SRC/link-wasm.sh" "$BUILD" "$OUT"

echo "== done =="
ls -la "$OUT"
