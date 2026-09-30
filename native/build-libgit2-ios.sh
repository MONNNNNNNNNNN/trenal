#!/usr/bin/env bash
# Builds libgit2 for iOS arm64 as git2-<hash>.framework, for LibGit2Sharp's `git` command.
# LibGit2Sharp only works with the exact libgit2 commit its NativeBinaries package was built
# from (it P/Invokes "git2-<first 7 of that sha>"), so the commit is pinned to that package:
#   LibGit2Sharp 0.32.0 -> LibGit2Sharp.NativeBinaries 2.0.324 -> libgit2 5853918 (v1.8.6)
# HTTPS via Apple SecureTransport; no SSH transport (use HTTPS remotes, or scp/ssh separately).
set -euo pipefail

SHA="${LIBGIT2_SHA:-5853918c4c6a7b12f8becf4bd11ff4362ebb9020}"
NAME="git2-${SHA:0:7}"
MIN_IOS="${MIN_IOS:-16.0}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
WORK="$ROOT/.build/libgit2"

rm -rf "$WORK"
mkdir -p "$WORK/src"
git -C "$WORK/src" init --quiet
git -C "$WORK/src" fetch --quiet --depth 1 https://github.com/libgit2/libgit2.git "$SHA"
git -C "$WORK/src" checkout --quiet FETCH_HEAD

cmake -S "$WORK/src" -B "$WORK/build" \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET="$MIN_IOS" \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_SHARED_LIBS=ON \
  -DBUILD_TESTS=OFF \
  -DBUILD_CLI=OFF \
  -DBUILD_EXAMPLES=OFF \
  -DBUILD_FUZZERS=OFF \
  -DUSE_SSH=OFF \
  -DUSE_HTTPS=SecureTransport \
  -DUSE_BUNDLED_ZLIB=ON \
  -DUSE_NTLMCLIENT=OFF \
  -DUSE_GSSAPI=OFF \
  -DREGEX_BACKEND=builtin \
  -DLIBGIT2_FILENAME="$NAME"
cmake --build "$WORK/build" --config Release -j "$(sysctl -n hw.ncpu)"

DYLIB="$(find "$WORK/build" -name "lib$NAME*.dylib" -type f -print -quit)"
[ -n "$DYLIB" ] || { echo "lib$NAME.dylib not found" >&2; find "$WORK/build" -name '*.dylib' >&2; exit 1; }
"$ROOT/make-framework.sh" "$DYLIB" "$NAME" "$MIN_IOS"
