#!/usr/bin/env bash
# Builds Vexel.app for Apple Silicon Macs and zips it to dist/Vexel-mac.zip.
#
#   scripts/publish-macos.sh            (needs: .NET 10 SDK and Homebrew opus: brew install opus; SDL2 is downloaded)
#
# The app is self-contained (.NET runtime inside) and carries SDL2 and libopus next to its executable, where
# Vexel looks first. Video is decoded by macOS itself (VideoToolbox), so FFmpeg isn't needed. The bundle is
# ad-hoc signed, not notarized: the first time, people right-click Vexel and choose Open (or run
# `xattr -dr com.apple.quarantine /Applications/Vexel.app`).
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${XS_VERSION:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/XStream.App/XStream.App.csproj | head -1)}"
OUT=dist/mac
APP="$OUT/Vexel.app"
BREW="$(brew --prefix 2>/dev/null || echo /opt/homebrew)"

echo "Publishing Vexel.app $VERSION (self-contained osx-arm64)..."
rm -rf "$OUT" && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
dotnet publish src/XStream.App/XStream.App.csproj -c Release -r osx-arm64 --self-contained true \
  -p:DebugType=none -p:AllowedReferenceRelatedFileExtensions=none -p:Version="$VERSION" \
  -o "$APP/Contents/MacOS" | grep -E "error|->" || true
[ -f "$APP/Contents/MacOS/XStream" ] || { echo "Publish failed: no XStream executable"; exit 1; }

# Native libraries, found next to the executable (NativeLibs searches AppContext.BaseDirectory first).
# SDL2: the official framework build (universal, needs only macOS). Homebrew's sdl2 is now sdl2-compat, which
# loads SDL3 from Homebrew's folder at run time and so breaks on Macs without it.
SDL_VERSION=2.32.10
SDL_CACHE=.cache/mac/SDL2-$SDL_VERSION
if [ ! -f "$SDL_CACHE/SDL2" ]; then
  mkdir -p "$SDL_CACHE"
  curl -fsSL -o "$SDL_CACHE/SDL2.dmg" "https://github.com/libsdl-org/SDL/releases/download/release-$SDL_VERSION/SDL2-$SDL_VERSION.dmg"
  MNT=$(mktemp -d)
  hdiutil attach -quiet -nobrowse -readonly -mountpoint "$MNT" "$SDL_CACHE/SDL2.dmg"
  cp "$MNT/SDL2.framework/Versions/A/SDL2" "$SDL_CACHE/SDL2"
  hdiutil detach -quiet "$MNT"
  rm -f "$SDL_CACHE/SDL2.dmg"
fi
# The framework is universal (Intel + Apple Silicon): keep the arm64 half.
lipo "$SDL_CACHE/SDL2" -thin arm64 -output "$APP/Contents/MacOS/libSDL2-2.0.0.dylib" 2>/dev/null || cp "$SDL_CACHE/SDL2" "$APP/Contents/MacOS/libSDL2-2.0.0.dylib"
# libopus (game audio; Vexel has a slower managed decoder if it's missing).
[ -f "$BREW/lib/libopus.0.dylib" ] || { echo "Missing $BREW/lib/libopus.0.dylib (brew install opus)"; exit 1; }
cp -L "$BREW/lib/libopus.0.dylib" "$APP/Contents/MacOS/"
chmod u+w "$APP"/Contents/MacOS/*.dylib
# The names Vexel asks for.
(cd "$APP/Contents/MacOS" && ln -sf libSDL2-2.0.0.dylib libSDL2.dylib && ln -sf libopus.0.dylib libopus.dylib)
# Each library must only need macOS itself (a Homebrew path would break on other Macs). The first line is its own id.
for lib in "$APP"/Contents/MacOS/libSDL2-2.0.0.dylib "$APP"/Contents/MacOS/libopus.0.dylib; do
  install_name_tool -id "@loader_path/$(basename "$lib")" "$lib" 2>/dev/null
  if otool -L "$lib" | tail -n +3 | grep -v -E "^\s+(/usr/lib/|/System/)" | grep -q .; then
    echo "$lib depends on non-system libraries:"; otool -L "$lib"; exit 1
  fi
done

# Icon: the 1024 px PNG as an .icns.
ICONSET="$OUT/Vexel.iconset"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s src/XStream.App/Assets/xstream-1024.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) src/XStream.App/Assets/xstream-1024.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/Vexel.icns" && rm -rf "$ICONSET"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Vexel</string>
  <key>CFBundleDisplayName</key><string>Vexel</string>
  <key>CFBundleIdentifier</key><string>app.vexel.player</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>XStream</string>
  <key>CFBundleIconFile</key><string>Vexel</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSMicrophoneUsageDescription</key><string>Vexel sends your microphone to your Xbox for party and game chat, when you turn it on.</string>
  <key>NSLocalNetworkUsageDescription</key><string>Vexel connects to your Xbox on your home network to stream it.</string>
</dict>
</plist>
PLIST

# Ad-hoc signature over the whole bundle (Apple Silicon refuses to run unsigned code).
codesign --force --deep --sign - "$APP" >/dev/null
codesign --verify --deep "$APP" && echo "Signed (ad-hoc)"

rm -f dist/Vexel-mac.zip
(cd "$OUT" && ditto -c -k --keepParent Vexel.app ../Vexel-mac.zip)
echo "Built dist/Vexel-mac.zip ($(du -h dist/Vexel-mac.zip | cut -f1))"
