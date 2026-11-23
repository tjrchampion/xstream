#!/usr/bin/env bash
# Runs the Windows smoke test (tests/XStream.WinSmoke) under Wine in Docker, with each renderer.
# Prints PASS/FAIL per renderer and leaves screenshots in dist/wine-smoke/. Needs Docker; the image is
# built on first use (see Dockerfile). Usage: tests/wine/run.sh [extra smoke args, e.g. --software]
set -euo pipefail
cd "$(dirname "$0")/../.."
OUT=dist/wine-smoke
docker image inspect xstream-wine >/dev/null 2>&1 || docker build --platform linux/amd64 -t xstream-wine tests/wine
rm -rf "$OUT" && mkdir -p "$OUT"
dotnet publish tests/XStream.WinSmoke -c Release -r win-x64 --self-contained true -p:DebugType=none -o "$OUT/app" | grep -E "error" || true
unzip -q -o .cache/win-x64/sdl2.zip SDL2.dll -d "$OUT/app"
if ls .cache/win-x64/ffmpeg-min/bin/avcodec-*.dll >/dev/null 2>&1; then cp .cache/win-x64/ffmpeg-min/bin/*.dll "$OUT/app/"
else unzip -q -j -o .cache/win-x64/ffmpeg.zip '*/bin/avcodec-*.dll' '*/bin/avutil-*.dll' '*/bin/swresample-*.dll' -d "$OUT/app"; fi
status=0
run_one() { # renderer, extra args...  (retries: Rosetta's x86 emulation sometimes dies starting .NET under Wine)
  local r="$1"; shift
  for attempt in 1 2 3 4 5 6 7 8; do
    out=$(docker run --rm --platform linux/amd64 -v "$PWD/$OUT:/work" -e DISPLAY=:99 xstream-wine sh -c \
      "Xvfb :99 -screen 0 1600x900x24 -nolisten tcp >/dev/null 2>&1 & sleep 2; \
       timeout 240 wine /work/app/XStream.WinSmoke.exe --renderer $r --seconds 4 --min-frames 1 --out 'Z:\\work' $* 2>&1" 2>&1 || true)
    if ! grep -q "rosetta error" <<<"$out"; then break; fi
    echo "  (emulator crash starting .NET, retry $attempt)"
  done
  grep -E "Renderer|First picture|Presented|Colour|Decoder:|Screenshot|PASS|FAIL" <<<"$out" | sed 's/^/  /'
  grep -q "^PASS" <<<"$out"
}
# Under Wine, hardware decoding opens but can't create surfaces; with no flags this also tests the
# automatic fall back to software decoding. --software skips straight to it.
for r in ${RENDERERS:-sdl d3d11}; do
  echo "=== renderer: $r $*"
  run_one "$r" "$@" || status=1
done
ls -l "$OUT"/*.png 2>/dev/null || true
exit $status
