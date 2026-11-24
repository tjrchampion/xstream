#!/usr/bin/env bash
# Builds libopus for Windows x64 (opus.dll). Vexel decodes game audio with it: about 5x faster than the managed
# decoder and nothing for the garbage collector to clean up mid-stream. Without it the app falls back to the
# managed decoder (Concentus), so this is optional.
#
# BSD-licensed, from the official Xiph release (checksum verified). Needs a MinGW-w64 cross compiler:
#   macOS: brew install mingw-w64   Debian/Ubuntu: apt install mingw-w64
# Output: .cache/win-x64/opus/opus.dll (prepare-natives.sh picks it up).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=1.6.1
SHA256=6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1
SRC="$PWD/.cache/opus-src"
OUT="$PWD/.cache/win-x64/opus"
mkdir -p "$SRC"
TARBALL="$SRC/opus-$VERSION.tar.gz"
[ -s "$TARBALL" ] || curl -fsSL --retry 3 -o "$TARBALL" "https://downloads.xiph.org/releases/opus/opus-$VERSION.tar.gz"
echo "$SHA256  $TARBALL" | (shasum -a 256 -c - 2>/dev/null || sha256sum -c -) >/dev/null || { echo "opus checksum mismatch" >&2; exit 1; }
rm -rf "$SRC/opus-$VERSION" && tar -xzf "$TARBALL" -C "$SRC"
cd "$SRC/opus-$VERSION"

./configure --host=x86_64-w64-mingw32 --prefix="$SRC/install" \
  --enable-shared --disable-static --disable-doc --disable-extra-programs \
  LDFLAGS="-static-libgcc" > configure.log 2>&1 || { tail -30 configure.log; exit 1; }
make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 8)" > make.log 2>&1 || { tail -40 make.log; exit 1; }
make install > install.log 2>&1

rm -rf "$OUT" && mkdir -p "$OUT"
cp "$SRC/install/bin/libopus-0.dll" "$OUT/opus.dll"
x86_64-w64-mingw32-strip "$OUT/opus.dll"
echo "Built libopus $VERSION for Windows: $(du -k "$OUT/opus.dll" | cut -f1) KB"
# Must load on a plain Windows install: only system DLLs as dependencies.
x86_64-w64-mingw32-objdump -p "$OUT/opus.dll" | grep "DLL Name" | sort -u | sed 's/^/  needs /'
