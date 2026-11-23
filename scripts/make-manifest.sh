#!/usr/bin/env bash
# Writes a signed update manifest (latest.json) for an XStream.exe.
#   scripts/make-manifest.sh <XStream.exe> <version> <download url> <output.json>
# Signs with the release key (ECDSA P-256), by default ~/.xstream-release/update-signing-private.pem
# (override with XSTREAM_RELEASE_KEY). The app verifies this signature with the public key built into it
# (src/XStream.Core/Update/UpdateManifest.cs) before installing anything. Never commit or upload the key.
set -euo pipefail
EXE="$1"; VERSION="$2"; URL="$3"; OUT="$4"
KEY="${XSTREAM_RELEASE_KEY:-$HOME/.xstream-release/update-signing-private.pem}"
[ -f "$KEY" ] || { echo "Release key not found: $KEY" >&2; exit 1; }
case "$URL" in https://*) ;; *) echo "Note: the app only downloads updates over https (got $URL)" >&2 ;; esac

if command -v sha256sum >/dev/null; then SHA=$(sha256sum "$EXE" | cut -d' ' -f1); else SHA=$(shasum -a 256 "$EXE" | cut -d' ' -f1); fi
SIZE=$(wc -c < "$EXE" | tr -d ' ')
# Exactly what UpdateManifest.SignedPayload builds: one field per line, no trailing newline.
SIG=$(printf 'xstream-update-v1\n%s\n%s\n%s\n%s' "$VERSION" "$URL" "$SHA" "$SIZE" | openssl dgst -sha256 -sign "$KEY" | base64 | tr -d '\n')
NOTES=$(printf '%s' "${XSTREAM_RELEASE_NOTES:-}" | sed 's/\\/\\\\/g; s/"/\\"/g')
printf '{\n  "version": "%s",\n  "url": "%s",\n  "sha256": "%s",\n  "size": %s,\n  "notes": "%s",\n  "signature": "%s"\n}\n' \
  "$VERSION" "$URL" "$SHA" "$SIZE" "$NOTES" "$SIG" > "$OUT"
echo "Signed update manifest: $OUT (version $VERSION, $SIZE bytes)"
