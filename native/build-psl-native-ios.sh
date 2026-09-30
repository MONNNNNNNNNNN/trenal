#!/usr/bin/env bash
# Builds PowerShell's small native helper (libpsl-native: stat, getpwuid, syslog...) for iOS arm64
# and wraps it as a framework. System.Management.Automation P/Invokes it during runspace startup,
# so without it PowerShell dies with DllNotFoundException. Runs on macOS with Xcode.
set -euo pipefail

# Pinned so a change upstream can't silently alter the build; bump deliberately.
REF="${PSL_NATIVE_REF:-0e619cee3591727a7beb1554b8e300fb790395d2}"
MIN_IOS="${MIN_IOS:-16.0}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
WORK="$ROOT/.build/psl-native"

rm -rf "$WORK"
mkdir -p "$WORK/src"
git -C "$WORK/src" init --quiet
git -C "$WORK/src" fetch --quiet --depth 1 https://github.com/PowerShell/PowerShell-native.git "$REF"
git -C "$WORK/src" checkout --quiet FETCH_HEAD
SRC="$WORK/src/src/libpsl-native"

# patch <file> <sed-expr> <text that must be present afterwards>
patch_src() {
  sed -i '' "$2" "$1"
  grep -qF -- "$3" "$1" || { echo "patch did not apply to $1: $2" >&2; exit 1; }
}
# Upstream builds with -Werror; newer iOS SDKs deprecate a few POSIX calls it uses (fork, syscall).
patch_src "$SRC/CMakeLists.txt" 's/ -Werror//' '-fstack-protector-strong'
if grep -q -- '-Werror' "$SRC/CMakeLists.txt"; then echo "-Werror still set" >&2; exit 1; fi
# The iOS SDK has no <sys/user.h>. On Apple, getppid.cpp only needs kinfo_proc, which is in <sys/sysctl.h>.
patch_src "$SRC/src/getppid.cpp" 's|#include <sys/user.h>|#include <sys/sysctl.h>|' '#include <sys/sysctl.h>'

cmake -S "$SRC" -B "$WORK/build" \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_SYSTEM_PROCESSOR=arm64 \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_IOS" \
  -DCMAKE_BUILD_TYPE=Release
cmake --build "$WORK/build" --config Release -j "$(sysctl -n hw.ncpu)"

DYLIB="$(find "$WORK" -name 'libpsl-native.dylib' -type f -print -quit)"
[ -n "$DYLIB" ] || { echo "libpsl-native.dylib not found" >&2; exit 1; }
"$ROOT/make-framework.sh" "$DYLIB" libpsl-native "$MIN_IOS"
