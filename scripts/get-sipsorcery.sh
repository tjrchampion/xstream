#!/usr/bin/env bash
# Fetches SIPSorcery at the pinned commit into third_party/sipsorcery and applies Vexel's patch:
#  - unordered, never-retransmitted data channels (PR-SCTP), and input sent immediately rather than queued
#  - hooks for Vexel's own media receive path (external receive loop, SRTP keys, receiver-report bookkeeping)
# XStream.WebRtc then builds against it automatically instead of the NuGet package.
set -euo pipefail
cd "$(dirname "$0")/.."
COMMIT=6f379fdba939398a51b567064613e1772538a1c8
DIR=third_party/sipsorcery
if [ ! -d "$DIR/.git" ]; then
  git clone --filter=blob:none --no-checkout https://github.com/sipsorcery-org/sipsorcery.git "$DIR"
fi
git -C "$DIR" fetch --depth 1 origin "$COMMIT"
git -C "$DIR" checkout -q --force "$COMMIT"
git -C "$DIR" clean -fdq -- src/SIPSorcery
git -C "$DIR" apply "$PWD/patches/sipsorcery-vexel.patch"
echo "SIPSorcery $COMMIT patched in $DIR. Rebuild XStream to use it."
