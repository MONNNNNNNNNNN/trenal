#!/usr/bin/env bash
# Builds PowerShell's small native helper (libpsl-native: stat, getpwuid, syslog...) for iOS arm64
# and wraps it as a framework. System.Management.Automation P/Invokes it during runspace startup,
# so without it PowerShell dies with DllNotFoundException. Runs on macOS with Xcode.
set -euo pipefail

REF="${PSL_NATIVE_REF:-master}"
MIN_IOS="${MIN_IOS:-16.0}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
WORK="$ROOT/.build"
OUT="$ROOT/out/libpsl-native.framework"

rm -rf "$WORK" "$OUT"
mkdir -p "$WORK" "$OUT"
git clone --quiet --depth 1 --branch "$REF" https://github.com/PowerShell/PowerShell-native.git "$WORK/src"
SRC="$WORK/src/src/libpsl-native"

# Upstream builds with -Werror; newer iOS SDKs deprecate a few POSIX calls it uses (fork, syscall).
sed -i '' 's/ -Werror//' "$SRC/CMakeLists.txt"

cmake -S "$SRC" -B "$WORK/build" \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_SYSTEM_PROCESSOR=arm64 \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_IOS" \
  -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-install_name,@rpath/libpsl-native.framework/libpsl-native"
cmake --build "$WORK/build" --config Release -j "$(sysctl -n hw.ncpu)"

DYLIB="$(find "$SRC/.." "$WORK/build" -name 'libpsl-native.dylib' -print -quit)"
[ -n "$DYLIB" ] || { echo "libpsl-native.dylib not found" >&2; exit 1; }
cp "$DYLIB" "$OUT/libpsl-native"
install_name_tool -id @rpath/libpsl-native.framework/libpsl-native "$OUT/libpsl-native"

cat > "$OUT/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>libpsl-native</string>
  <key>CFBundleIdentifier</key><string>com.microsoft.powershell.psl-native</string>
  <key>CFBundleName</key><string>libpsl-native</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleSupportedPlatforms</key><array><string>iPhoneOS</string></array>
  <key>MinimumOSVersion</key><string>$MIN_IOS</string>
</dict>
</plist>
PLIST

file "$OUT/libpsl-native"
echo "built $OUT"
