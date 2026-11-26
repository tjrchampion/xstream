#!/usr/bin/env bash
# Finishes a release the GitHub workflow drafted: downloads the Vexel.exe it built (signed by SignPath when
# that's set up), signs latest.json for it with your release key (and latest-mac.json for Vexel-mac.zip),
# uploads them, and publishes the release.
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
echo "Downloading Vexel.exe from the $TAG draft..."
gh release download "$TAG" --repo "$REPO" --pattern Vexel.exe --dir "$WORK"

# Check it's the version we think it is before signing a manifest for it (Windows exe version resource).
if command -v osslsigncode >/dev/null; then
  osslsigncode verify -in "$WORK/Vexel.exe" 2>&1 | grep -qE "Signature verification: ok" \
    && echo "Code signature: ok" || echo "Note: Vexel.exe is not code-signed (SignPath not set up yet?)"
fi

URL="https://github.com/$REPO/releases/download/$TAG/Vexel.exe"
XSTREAM_RELEASE_NOTES="${XSTREAM_RELEASE_NOTES:-}" scripts/make-manifest.sh "$WORK/Vexel.exe" "$VERSION" "$URL" "$WORK/latest.json"

# The Mac build (Vexel-mac.zip, from the workflow's mac job) gets its own signed manifest: latest-mac.json.
MANIFESTS=("$WORK/latest.json")
if gh release download "$TAG" --repo "$REPO" --pattern Vexel-mac.zip --dir "$WORK" 2>/dev/null; then
  MAC_URL="https://github.com/$REPO/releases/download/$TAG/Vexel-mac.zip"
  XSTREAM_RELEASE_NOTES="${XSTREAM_RELEASE_NOTES:-}" scripts/make-manifest.sh "$WORK/Vexel-mac.zip" "$VERSION" "$MAC_URL" "$WORK/latest-mac.json"
  MANIFESTS+=("$WORK/latest-mac.json")
else
  echo "Note: no Vexel-mac.zip on the $TAG draft (the mac job failed?): Macs won't be offered this version."
fi

echo "Uploading the update manifests and publishing $TAG..."
gh release upload "$TAG" "${MANIFESTS[@]}" --repo "$REPO" --clobber
gh release edit "$TAG" --repo "$REPO" --draft=false --latest
echo "Published: https://github.com/$REPO/releases/tag/$TAG"
echo "The auto-updater picks it up from https://github.com/$REPO/releases/latest/download/latest.json"
