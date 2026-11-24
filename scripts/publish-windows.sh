#!/usr/bin/env bash
# Builds dist/Vexel.exe: one self-contained file for Windows x64 (ROG Ally X, any Windows 10/11 PC).
# No .NET install needed; SDL2 and FFmpeg's LGPL shared DLLs are packed inside and unpacked on first launch
# to %LOCALAPPDATA%\XStream\native (with a progress window).
# Runs on macOS, Linux or Windows (Git Bash). Downloads are cached in .cache/.
set -euo pipefail
cd "$(dirname "$0")/.."

OUT="dist/.publish-win-x64"
CACHE=".cache/win-x64"
mkdir -p "$CACHE"

# ---- Native DLLs: gathered, then code-signed BEFORE they're packed (Smart App Control checks every DLL) ----------
# XS_NATIVES_DIR: an already-signed folder from prepare-natives.sh (the release workflow signs it with SignPath).
# Otherwise they're gathered here and signed with SIGN_PFX / AZURE_SIGNING_* if set (scripts/sign-files.sh).
if [ -n "${XS_NATIVES_DIR:-}" ]; then
  NATIVES_DIR="$(cd "$XS_NATIVES_DIR" && pwd)"
  echo "Using prepared native DLLs from $NATIVES_DIR"
else
  NATIVES_DIR="$PWD/$CACHE/natives"
  scripts/prepare-natives.sh "$NATIVES_DIR"
  scripts/sign-files.sh "$NATIVES_DIR"/*.dll
fi
echo "Packing native libraries..."
rm -rf "$CACHE/x" && mkdir -p "$CACHE/x"
NATIVES="$PWD/$CACHE/x/natives.zip"
# SDL2 + FFmpeg (+ libopus) ride along as natives.zip (unpacked on first launch); ANGLE goes into .NET's own bundle (below).
(cd "$NATIVES_DIR" && zip -q -9 "$NATIVES" SDL2.dll avcodec-*.dll avutil-*.dll swresample-*.dll $(ls opus.dll 2>/dev/null))

# Version: from XS_VERSION (the release workflow sets it from the git tag), else the csproj.
VERSION="${XS_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/XStream.App/XStream.App.csproj | head -1)}"
echo "Publishing single-file Vexel.exe $VERSION (self-contained win-x64)..."
rm -rf "$OUT" dist/XStream.exe dist/Vexel.exe
# Single file: runtime, app and UI libraries inside the exe; SDL2/FFmpeg ride along as natives.zip and are
# unpacked on first launch with a progress window (src/XStream.App/Services/NativeBundle.cs).
dotnet publish src/XStream.App/XStream.App.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -p:XsTrim=true -p:Version="$VERSION" -p:XsNativeBundle="$NATIVES" -p:XsAngleDll="$NATIVES_DIR/av_libglesv2.dll" -o "$OUT" | grep -E "error|->|Using ANGLE DLL" || true
[ -f "$OUT/XStream.exe" ] || { echo "Publish failed: no XStream.exe"; exit 1; }
EXTRA=$(ls "$OUT" | grep -v '^XStream.exe$' || true)
[ -z "$EXTRA" ] || echo "Note: extra files next to the exe: $EXTRA"
# The projects keep their XStream code names; the app is published as Vexel.exe.
cp "$OUT/XStream.exe" dist/Vexel.exe

# ---- Code signing of the exe (optional; SIGN_PFX or AZURE_SIGNING_*, see scripts/sign-files.sh) ---------------
if [ -n "${SIGN_PFX:-}${AZURE_SIGNING_ENDPOINT:-}" ]; then
  echo "Signing Vexel.exe..."
  scripts/sign-files.sh dist/Vexel.exe
  command -v osslsigncode >/dev/null && osslsigncode verify -in dist/Vexel.exe 2>&1 | grep -E "Signature verification|Subject:" | head -2 || true
else
  echo "Not code-signed (set SIGN_PFX or AZURE_SIGNING_* to sign; GitHub releases use SignPath)."
fi

# ---- Update manifest (local releases): dist/latest.json, signed with the release key ---------------------
# The GitHub release workflow skips this (the key never leaves your machine): scripts/finish-release.sh
# signs the manifest for the exe the workflow built. For a fully local release, set XSTREAM_UPDATE_BASE_URL.
if [ -n "${XSTREAM_UPDATE_BASE_URL:-}" ]; then
  scripts/make-manifest.sh dist/Vexel.exe "$VERSION" "${XSTREAM_UPDATE_BASE_URL%/}/Vexel.exe" dist/latest.json
fi

echo
echo "Done: dist/Vexel.exe  ($(du -h dist/Vexel.exe | cut -f1))"
echo "      Copy it anywhere and run it. First launch unpacks SDL2/FFmpeg (progress window), later launches start straight away."
