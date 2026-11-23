#!/usr/bin/env bash
# Builds dist/XStream.exe: one self-contained file for Windows x64 (ROG Ally X, any Windows 10/11 PC).
# No .NET install needed; SDL2 and FFmpeg's LGPL shared DLLs are packed inside and unpacked on first launch
# to %LOCALAPPDATA%\XStream\native (with a progress window).
# Runs on macOS, Linux or Windows (Git Bash). Downloads are cached in .cache/.
set -euo pipefail
cd "$(dirname "$0")/.."

SDL_URL="https://github.com/libsdl-org/SDL/releases/download/release-2.32.10/SDL2-2.32.10-win32-x64.zip"
FFMPEG_URL="https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip"
OUT="dist/.publish-win-x64"
CACHE=".cache/win-x64"

mkdir -p "$CACHE"
fetch() { # url file
  if [ ! -s "$CACHE/$2" ]; then
    echo "Downloading $2..."
    curl -fL --retry 3 -o "$CACHE/$2.part" "$1" && mv "$CACHE/$2.part" "$CACHE/$2"
  fi
}
fetch "$SDL_URL" sdl2.zip
fetch "$FFMPEG_URL" ffmpeg.zip

echo "Packing native libraries..."
rm -rf "$CACHE/x" && mkdir -p "$CACHE/x/natives"
unzip -q -o "$CACHE/sdl2.zip" SDL2.dll -d "$CACHE/x/natives"
# FFmpeg: the minimal build (H.264 + Opus only, ~3 MB) when scripts/build-ffmpeg-windows.sh has been run,
# otherwise the general-purpose LGPL build (~100 MB, every codec).
if ls "$CACHE"/ffmpeg-min/bin/avcodec-*.dll >/dev/null 2>&1; then
  echo "Using minimal FFmpeg from $CACHE/ffmpeg-min"
  cp "$CACHE"/ffmpeg-min/bin/{avcodec,avutil,swresample}-*.dll "$CACHE/x/natives/"
else
  if [ -n "${XS_REQUIRE_MIN_FFMPEG:-}" ]; then echo "Minimal FFmpeg missing: run scripts/build-ffmpeg-windows.sh" >&2; exit 1; fi
  echo "Using the full FFmpeg build (run scripts/build-ffmpeg-windows.sh for a much smaller exe)"
  unzip -q -j -o "$CACHE/ffmpeg.zip" '*/bin/avcodec-*.dll' '*/bin/avutil-*.dll' '*/bin/swresample-*.dll' -d "$CACHE/x/natives"
fi
NATIVES="$PWD/$CACHE/x/natives.zip"
(cd "$CACHE/x/natives" && zip -q -9 "$NATIVES" *.dll)

# Version: from XS_VERSION (the release workflow sets it from the git tag), else the csproj.
VERSION="${XS_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/XStream.App/XStream.App.csproj | head -1)}"
echo "Publishing single-file XStream.exe $VERSION (self-contained win-x64)..."
rm -rf "$OUT" dist/XStream.exe
# Single file: runtime, app and UI libraries inside the exe; SDL2/FFmpeg ride along as natives.zip and are
# unpacked on first launch with a progress window (src/XStream.App/Services/NativeBundle.cs).
dotnet publish src/XStream.App/XStream.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -p:XsTrim=true -p:Version="$VERSION" -p:XsNativeBundle="$NATIVES" -o "$OUT" | grep -E "error|->" || true
[ -f "$OUT/XStream.exe" ] || { echo "Publish failed: no XStream.exe"; exit 1; }
EXTRA=$(ls "$OUT" | grep -v '^XStream.exe$' || true)
[ -z "$EXTRA" ] || echo "Note: extra files next to the exe: $EXTRA"
cp "$OUT/XStream.exe" dist/XStream.exe

# ---- Code signing (optional; removes the "Windows protected your PC" SmartScreen warning) --------------------
# Either a certificate file:            SIGN_PFX=cert.pfx SIGN_PFX_PASSWORD=...         (needs osslsigncode, or signtool on Windows)
# or Azure Trusted Signing:             AZURE_SIGNING_ENDPOINT=https://eus.codesigning.azure.net
#                                       AZURE_SIGNING_ACCOUNT=... AZURE_SIGNING_PROFILE=...  (needs jsign + az login)
TSA="http://timestamp.digicert.com"
if [ -n "${SIGN_PFX:-}" ]; then
  echo "Signing with $SIGN_PFX..."
  osslsigncode sign -pkcs12 "$SIGN_PFX" -pass "${SIGN_PFX_PASSWORD:-}" -n "XStream" -h sha256 -t "$TSA" \
    -in dist/XStream.exe -out dist/XStream.signed.exe >/dev/null
  mv dist/XStream.signed.exe dist/XStream.exe
  osslsigncode verify -in dist/XStream.exe 2>&1 | grep -E "Signature verification|Subject:" | head -3 || true
elif [ -n "${AZURE_SIGNING_ENDPOINT:-}" ]; then
  echo "Signing with Azure Trusted Signing ($AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE)..."
  TOKEN=$(az account get-access-token --resource https://codesigning.azure.net --query accessToken -o tsv)
  jsign --storetype TRUSTEDSIGNING --keystore "$AZURE_SIGNING_ENDPOINT" --storepass "$TOKEN" \
    --alias "$AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE" --tsaurl http://timestamp.acs.microsoft.com --tsmode RFC3161 \
    --name XStream dist/XStream.exe
else
  echo "Not code-signed (set SIGN_PFX or AZURE_SIGNING_* to sign; see README)."
fi

# ---- Update manifest (local releases): dist/latest.json, signed with the release key ---------------------
# The GitHub release workflow skips this (the key never leaves your machine): scripts/finish-release.sh
# signs the manifest for the exe the workflow built. For a fully local release, set XSTREAM_UPDATE_BASE_URL.
if [ -n "${XSTREAM_UPDATE_BASE_URL:-}" ]; then
  scripts/make-manifest.sh dist/XStream.exe "$VERSION" "${XSTREAM_UPDATE_BASE_URL%/}/XStream.exe" dist/latest.json
fi

echo
echo "Done: dist/XStream.exe  ($(du -h dist/XStream.exe | cut -f1))"
echo "      Copy it anywhere and run it. First launch unpacks SDL2/FFmpeg (progress window), later launches start straight away."
