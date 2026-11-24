#!/usr/bin/env bash
# Gathers the third-party native DLLs Vexel ships that aren't already signed, into one folder:
#   SDL2.dll, avcodec-NN.dll, avutil-NN.dll, swresample-N.dll   (packed into natives.zip inside Vexel.exe)
#   opus.dll (libopus, if scripts/build-opus-windows.sh has been run; otherwise audio uses the managed decoder)
#   av_libglesv2.dll                                             (Avalonia's ANGLE renderer, bundled by .NET)
# so they can all be code-signed in one go before publish-windows.sh packs them (Smart App Control refuses to
# load unsigned DLLs). SkiaSharp/HarfBuzz natives are already signed by Microsoft and are left alone.
#   scripts/prepare-natives.sh <output dir>
set -euo pipefail
cd "$(dirname "$0")/.."
OUTDIR="${1:?usage: scripts/prepare-natives.sh <output dir>}"

SDL_URL="https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip"
FFMPEG_URL="https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip"
CACHE=".cache/win-x64"
mkdir -p "$CACHE"
fetch() { # url file
  if [ ! -s "$CACHE/$2" ]; then
    echo "Downloading $2..."
    curl -fL --retry 3 -o "$CACHE/$2.part" "$1" && mv "$CACHE/$2.part" "$CACHE/$2"
  fi
}

rm -rf "$OUTDIR" && mkdir -p "$OUTDIR"
fetch "$SDL_URL" sdl2.zip
unzip -q -o "$CACHE/sdl2.zip" SDL2.dll -d "$OUTDIR"

# FFmpeg: the minimal build (H.264 + Opus only, ~3 MB) when scripts/build-ffmpeg-windows.sh has been run,
# otherwise the general-purpose LGPL build (~100 MB, every codec).
if ls "$CACHE"/ffmpeg-min/bin/avcodec-*.dll >/dev/null 2>&1; then
  echo "Using minimal FFmpeg from $CACHE/ffmpeg-min"
  cp "$CACHE"/ffmpeg-min/bin/{avcodec,avutil,swresample}-*.dll "$OUTDIR/"
else
  if [ -n "${XS_REQUIRE_MIN_FFMPEG:-}" ]; then echo "Minimal FFmpeg missing: run scripts/build-ffmpeg-windows.sh" >&2; exit 1; fi
  echo "Using the full FFmpeg build (run scripts/build-ffmpeg-windows.sh for a much smaller exe)"
  fetch "$FFMPEG_URL" ffmpeg.zip
  unzip -q -j -o "$CACHE/ffmpeg.zip" '*/bin/avcodec-*.dll' '*/bin/avutil-*.dll' '*/bin/swresample-*.dll' -d "$OUTDIR"
fi

# libopus: built from source by scripts/build-opus-windows.sh.
if [ -s "$CACHE/opus/opus.dll" ]; then
  cp "$CACHE/opus/opus.dll" "$OUTDIR/"
elif [ -n "${XS_REQUIRE_OPUS:-}" ]; then
  echo "opus.dll missing: run scripts/build-opus-windows.sh" >&2; exit 1
else
  echo "No opus.dll (run scripts/build-opus-windows.sh): audio will use the slower managed decoder"
fi

# Avalonia's ANGLE DLL: the exact version the app restores (from the NuGet cache).
dotnet restore src/XStream.App/XStream.App.csproj -r win-x64 -p:XsTrim=true >/dev/null
ANGLE_VER=$(grep -o '"Avalonia.Angle.Windows.Natives/[^"]*"' src/XStream.App/obj/project.assets.json | head -1 | sed 's:.*/\(.*\)":\1:')
NUGET="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
cp "$NUGET/avalonia.angle.windows.natives/$ANGLE_VER/runtimes/win-x64/native/av_libglesv2.dll" "$OUTDIR/"

echo "Native DLLs to sign ($OUTDIR):"
ls -1 "$OUTDIR" | sed 's/^/  /'
