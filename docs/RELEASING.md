# Releasing XStream (Windows)

This is for maintainers. Players just download `XStream.exe` from the releases page.

## One-time setup

- **Release key:** the update manifest is signed with an ECDSA P-256 key kept outside the repository, in
  `~/.xstream-release/update-signing-private.pem` (create it with
  `openssl ecparam -name prime256v1 -genkey -noout -out update-signing-private.pem`). Its public half is
  built into the app in `src/XStream.Core/Update/UpdateManifest.cs`. **Back the private key up somewhere safe
  and never commit or share it.** Without it, existing installs can't be updated automatically.
- **Update feed:** set `Updater.DefaultFeed` in `src/XStream.App/Services/Updater.cs` to the URL of the
  published `latest.json`, for example `https://github.com/OWNER/xstream/releases/latest/download/latest.json`.
  (Users can override it with `"UpdateFeedUrl"` in settings.json.)
- **Smaller exe (recommended):** `./scripts/build-ffmpeg-windows.sh` cross-compiles a minimal LGPL FFmpeg
  (H.264 with D3D11VA/DXVA2, and Opus). It needs MinGW-w64 and nasm (`brew install mingw-w64 nasm`, or
  `apt install mingw-w64 nasm`). `publish-windows.sh` uses it when present: about 30 MB instead of about 90 MB.

## Each release

1. Bump `<Version>` in `src/XStream.App/XStream.App.csproj`.
2. Build, sign and write the update manifest:
   ```bash
   XSTREAM_UPDATE_BASE_URL=https://github.com/OWNER/xstream/releases/download/v1.2.0 \
   XSTREAM_RELEASE_NOTES="What changed" \
   ./scripts/publish-windows.sh
   ```
   Add code signing with one of:
   - a certificate file: `SIGN_PFX=cert.pfx SIGN_PFX_PASSWORD=...` (needs `osslsigncode`, or `signtool` on Windows)
   - Azure Artifact Signing: `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE` (needs `jsign` and `az login`)
3. Upload `dist/XStream.exe` and `dist/latest.json` to the release.

## How updates are verified

`latest.json` holds the version, download URL, SHA-256 and size of `XStream.exe`, and an ECDSA P-256 signature
over those fields. The app installs an update only if the signature verifies against the public key built into
it, the download is HTTPS, and the file matches the hash and size. Code signing (Authenticode) is separate: it's
what Windows SmartScreen checks; the manifest signature is what the updater checks.

## Testing the Windows build without Windows

`tests/wine/run.sh` builds `tests/XStream.WinSmoke` and runs it under Wine in Docker with each renderer, checking
that frames are shown and the colour matches BT.709. It can't test AMD drivers or hardware decoding.
