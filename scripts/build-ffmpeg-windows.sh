#!/usr/bin/env bash
# Builds a minimal FFmpeg for Windows x64 with only what XStream uses: the H.264 decoder (with Direct3D 11 /
# DXVA2 hardware decoding) and the Opus decoder. The general-purpose build has every codec (avcodec ~94 MB);
# this one is a few MB, which takes the single-file XStream.exe from ~68 MB to ~30 MB.
#
# LGPL (no --enable-gpl / nonfree), shared DLLs: avcodec-NN.dll, avutil-NN.dll, swresample-N.dll.
# Needs a MinGW-w64 cross compiler and nasm:  macOS: brew install mingw-w64 nasm   Debian/Ubuntu: apt install mingw-w64 nasm
# Output: .cache/win-x64/ffmpeg-min/bin/*.dll (publish-windows.sh uses it when present).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${FFMPEG_VERSION:-9.0}"
SRC=".cache/ffmpeg-src"
OUT="$PWD/.cache/win-x64/ffmpeg-min"
mkdir -p "$SRC"
[ -s "$SRC/ffmpeg-$VERSION.tar.xz" ] || curl -fL --retry 3 -o "$SRC/ffmpeg-$VERSION.tar.xz" "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz"
rm -rf "$SRC/ffmpeg-$VERSION" && tar -xf "$SRC/ffmpeg-$VERSION.tar.xz" -C "$SRC"
cd "$SRC/ffmpeg-$VERSION"

./configure \
  --prefix="$OUT" --target-os=mingw32 --arch=x86_64 --cross-prefix=x86_64-w64-mingw32- --enable-cross-compile \
  --enable-shared --disable-static --disable-debug --disable-doc --disable-programs \
  --disable-avdevice --disable-avformat --disable-avfilter --disable-swscale --disable-network \
  --disable-everything \
  --enable-decoder=h264,opus --enable-parser=h264,opus \
  --enable-d3d11va --enable-dxva2 --enable-hwaccel=h264_d3d11va,h264_d3d11va2,h264_dxva2 \
  --enable-swresample --enable-w32threads \
  --extra-ldflags="-static-libgcc -static" \
  > configure.log 2>&1 || { tail -30 configure.log; exit 1; }

make -j"$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 8)" > make.log 2>&1 || { tail -40 make.log; exit 1; }
rm -rf "$OUT" && make install > install.log 2>&1
x86_64-w64-mingw32-strip "$OUT"/bin/*.dll

echo "Built minimal FFmpeg $VERSION for Windows:"
ls -l "$OUT"/bin/*.dll | awk '{printf "  %6.1f MB  %s\n", $5/1048576, $NF}'
# Every DLL must load on a plain Windows install: only system DLLs as dependencies.
x86_64-w64-mingw32-objdump -p "$OUT"/bin/*.dll | grep "DLL Name" | sort -u | sed 's/^/  needs /'
