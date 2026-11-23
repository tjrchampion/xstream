#!/usr/bin/env bash
# Finishes a release the GitHub workflow drafted: downloads the XStream.exe it built (signed by SignPath when
# that's set up), signs latest.json for it with your release key, uploads that, and publishes the release.
# Only then does the auto-updater offer it.
#   scripts/finish-release.sh v0.3.0
# Needs the GitHub CLI, signed in once (gh auth login), and the release key in ~/.xstream-release.
set -euo pipefail
cd "$(dirname "$0")/.."
TAG="${1:?usage: scripts/finish-release.sh v0.3.0}"
VERSION="${TAG#v}"
REPO="${XSTREAM_REPO:-tjrchampion/xstream}"
command -v gh >/dev/null || { echo "Install the GitHub CLI: brew install gh, then gh auth login" >&2; exit 1; }

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
echo "Downloading XStream.exe from the $TAG draft..."
gh release download "$TAG" --repo "$REPO" --pattern XStream.exe --dir "$WORK"

# Check it's the version we think it is before signing a manifest for it (Windows exe version resource).
if command -v osslsigncode >/dev/null; then
  osslsigncode verify -in "$WORK/XStream.exe" 2>&1 | grep -qE "Signature verification: ok" \
    && echo "Code signature: ok" || echo "Note: XStream.exe is not code-signed (SignPath not set up yet?)"
fi

URL="https://github.com/$REPO/releases/download/$TAG/XStream.exe"
XSTREAM_RELEASE_NOTES="${XSTREAM_RELEASE_NOTES:-}" scripts/make-manifest.sh "$WORK/XStream.exe" "$VERSION" "$URL" "$WORK/latest.json"

echo "Uploading latest.json and publishing $TAG..."
gh release upload "$TAG" "$WORK/latest.json" --repo "$REPO" --clobber
gh release edit "$TAG" --repo "$REPO" --draft=false --latest
echo "Published: https://github.com/$REPO/releases/tag/$TAG"
echo "The auto-updater picks it up from https://github.com/$REPO/releases/latest/download/latest.json"
