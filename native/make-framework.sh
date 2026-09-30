#!/usr/bin/env bash
# make-framework.sh <dylib> <name> <min-ios>
# Wraps an iOS arm64 dylib as native/out/<name>.framework (iOS rejects loose dylibs in an app).
# The app resolves P/Invoke library "<name>" to Frameworks/<name>.framework/<name>.
set -euo pipefail
DYLIB="$1"
NAME="$2"
MIN_IOS="$3"
OUT="$(cd "$(dirname "$0")" && pwd)/out/$NAME.framework"

rm -rf "$OUT"
mkdir -p "$OUT"
cp "$DYLIB" "$OUT/$NAME"
install_name_tool -id "@rpath/$NAME.framework/$NAME" "$OUT/$NAME"

# Bundle identifiers allow only letters, digits, '-' and '.'.
ID="dev.trenal.native.$(echo "$NAME" | tr -c 'A-Za-z0-9.\n-' '-')"
cat > "$OUT/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleExecutable</key><string>$NAME</string>
  <key>CFBundleIdentifier</key><string>$ID</string>
  <key>CFBundleName</key><string>$NAME</string>
  <key>CFBundlePackageType</key><string>FMWK</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>CFBundleSupportedPlatforms</key><array><string>iPhoneOS</string></array>
  <key>MinimumOSVersion</key><string>$MIN_IOS</string>
</dict>
</plist>
PLIST

file "$OUT/$NAME"
echo "built $OUT"
