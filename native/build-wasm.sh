#!/usr/bin/env bash
# build-wasm.sh ios|host
# Builds trenal-wasm (WAMR interpreter + WASI + shim.c):
#   ios  -> native/out/trenal-wasm.framework (macOS + Xcode)
#   host -> native/out/host/libtrenal-wasm.{so,dylib} for desktop tests
set -euo pipefail

WAMR_TAG="${WAMR_TAG:-WAMR-2.4.5}"
WAMR_SHA="25bd7eb63e828e4bd242cc9b38d260b4b31c6605" # WAMR-2.4.5; checked below
MIN_IOS="${MIN_IOS:-16.0}"
TARGET="${1:-host}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
WORK="$ROOT/.build/wasm-$TARGET"

if [ ! -d "$ROOT/.build/wamr" ]; then
  mkdir -p "$ROOT/.build"
  git clone --quiet --depth 1 --branch "$WAMR_TAG" https://github.com/bytecodealliance/wasm-micro-runtime.git "$ROOT/.build/wamr"
fi
[ "$(git -C "$ROOT/.build/wamr" rev-parse HEAD)" = "$WAMR_SHA" ] || { echo "WAMR checkout is not $WAMR_SHA" >&2; exit 1; }

rm -rf "$WORK"
if [ "$TARGET" = ios ]; then
  cmake -S "$ROOT/trenal-wasm" -B "$WORK" \
    -DWAMR_ROOT_DIR="$ROOT/.build/wamr" \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_IOS" \
    -DCMAKE_BUILD_TYPE=Release
  cmake --build "$WORK" --config Release -j "$(sysctl -n hw.ncpu)"
  DYLIB="$(find "$WORK" -name 'libtrenal-wasm.dylib' -type f -print -quit)"
  [ -n "$DYLIB" ] || { echo "libtrenal-wasm.dylib not found" >&2; find "$WORK" -name '*.dylib' >&2; exit 1; }
  "$ROOT/make-framework.sh" "$DYLIB" trenal-wasm "$MIN_IOS"
else
  cmake -S "$ROOT/trenal-wasm" -B "$WORK" -DWAMR_ROOT_DIR="$ROOT/.build/wamr" -DCMAKE_BUILD_TYPE=Release
  cmake --build "$WORK" --config Release -j "$(nproc 2>/dev/null || sysctl -n hw.ncpu)"
  mkdir -p "$ROOT/out/host"
  find "$WORK" -maxdepth 2 \( -name 'libtrenal-wasm.so' -o -name 'libtrenal-wasm.dylib' \) -exec cp {} "$ROOT/out/host/" \;
  ls -la "$ROOT/out/host"
fi
