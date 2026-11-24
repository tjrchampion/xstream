#!/usr/bin/env bash
# Authenticode-signs Windows binaries (exe/dll) in place, if a signing method is configured; otherwise does nothing.
#   scripts/sign-files.sh file.dll [file.exe ...]
# Either a certificate file:   SIGN_PFX=cert.pfx SIGN_PFX_PASSWORD=...                    (needs osslsigncode)
# or Azure Artifact Signing:   AZURE_SIGNING_ENDPOINT=https://eus.codesigning.azure.net
#                              AZURE_SIGNING_ACCOUNT=... AZURE_SIGNING_PROFILE=...         (needs jsign + az login)
# (GitHub releases sign through SignPath instead; see .github/workflows/release.yml.)
# Smart App Control checks every binary Windows loads, so the DLLs Vexel unpacks need signing as well as the exe.
set -euo pipefail
TSA="http://timestamp.digicert.com"

if [ -n "${SIGN_PFX:-}" ]; then
  for f in "$@"; do
    osslsigncode sign -pkcs12 "$SIGN_PFX" -pass "${SIGN_PFX_PASSWORD:-}" -n "Vexel" -h sha256 -t "$TSA" \
      -in "$f" -out "$f.signed" >/dev/null
    mv "$f.signed" "$f"
    echo "  signed $(basename "$f")"
  done
elif [ -n "${AZURE_SIGNING_ENDPOINT:-}" ]; then
  TOKEN=$(az account get-access-token --resource https://codesigning.azure.net --query accessToken -o tsv)
  for f in "$@"; do
    jsign --storetype TRUSTEDSIGNING --keystore "$AZURE_SIGNING_ENDPOINT" --storepass "$TOKEN" \
      --alias "$AZURE_SIGNING_ACCOUNT/$AZURE_SIGNING_PROFILE" --tsaurl http://timestamp.acs.microsoft.com --tsmode RFC3161 \
      --name Vexel "$f" >/dev/null
    echo "  signed $(basename "$f")"
  done
else
  exit 0
fi
