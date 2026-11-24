# Releasing Vexel (Windows)

This is for maintainers. Players just download `Vexel.exe` from the [releases page](../../../releases/latest).

## How a release works

1. **GitHub builds it.** Pushing a version tag runs [.github/workflows/release.yml](../.github/workflows/release.yml) on a free
   GitHub-hosted runner:
   - it builds the minimal FFmpeg and the patched WebRTC library, runs the tests and builds `Vexel.exe`
   - it code-signs the exe through SignPath (once set up)
   - it attaches the exe to a **draft** release
2. **You finish it.** `scripts/finish-release.sh <tag>` downloads that exact exe, signs `latest.json` for it with your
   release key, uploads it and publishes the release. The release key never goes to GitHub.

Draft releases are invisible to the auto-updater (it reads `releases/latest/download/latest.json`), so users only get
releases you've finished.

```bash
# 1. bump <Version> in src/XStream.App/XStream.App.csproj (keeps local builds in step), commit, then:
git tag v0.3.0 && git push origin v0.3.0
# 2. wait for the "Release" workflow to finish (Actions tab), then:
XSTREAM_RELEASE_NOTES="What changed" scripts/finish-release.sh v0.3.0
```

## One-time setup

- **GitHub CLI:** `brew install gh`, then `gh auth login`.
- **Release key:** an ECDSA P-256 key in `~/.xstream-release/update-signing-private.pem`, which signs every update manifest.
  Create it once with:
  ```bash
  openssl ecparam -name prime256v1 -genkey -noout -out update-signing-private.pem
  ```
  Its public half is built into the app (`src/XStream.Core/Update/UpdateManifest.cs`). **Back up the private key
  somewhere safe and never commit or share it.** Without it, existing installs can't be updated automatically.
- **Code signing (SignPath, free for open source):**
  1. Apply at [signpath.org](https://signpath.org). The project's policy is in [CODE_SIGNING.md](CODE_SIGNING.md).
  2. Once approved, create the SignPath project `xstream` with signing policy `release-signing`, linked to this
     repository as a trusted build system.
  3. Add two artifact configurations to the SignPath project:
     - `natives`: a zip of DLLs; sign every `*.dll` inside it.
     - the default one, for `Vexel.exe`.
  4. In the GitHub repository settings, add the secret `SIGNPATH_API_TOKEN` and the variable
     `SIGNPATH_ORGANIZATION_ID`. The workflow's signing steps switch on by themselves.

  **Why the DLLs are signed too:** Windows 11's Smart App Control checks every binary Windows loads, not just the
  exe, and blocks unsigned ones with no "Run anyway" option. So the build signs the third-party DLLs Vexel ships
  before packing them into the exe: SDL2, the three FFmpeg DLLs, and Avalonia's `av_libglesv2.dll`
  (`scripts/prepare-natives.sh` gathers them). SkiaSharp and HarfBuzz are already signed by Microsoft.

## How updates are verified

- **What `latest.json` holds:** the version, download URL, SHA-256 and size of `Vexel.exe`, plus an ECDSA P-256 signature over those fields.
- **What the app checks:** it installs an update only if all of these hold:
  - the signature verifies against the public key built into it
  - the download is over HTTPS
  - the file matches the hash and size
- **Code signing is a separate check:** the SignPath (Authenticode) signature is what Windows SmartScreen checks. The manifest signature is what the updater checks.

## Building a release locally (no GitHub)

`./scripts/publish-windows.sh` builds `dist/Vexel.exe` on macOS, Linux or Git Bash. Optional environment variables:

- `XSTREAM_UPDATE_BASE_URL=https://…`: also writes a signed `dist/latest.json` for that download location.
- `SIGN_PFX=cert.pfx SIGN_PFX_PASSWORD=…`: Authenticode-signs the native DLLs and the exe with a certificate file (needs `osslsigncode`). For example, a Certum "Open Source Code Signing" certificate.
- `AZURE_SIGNING_ENDPOINT`, `AZURE_SIGNING_ACCOUNT`, `AZURE_SIGNING_PROFILE`: signs with Azure Artifact Signing instead (needs `jsign` and `az login`).

Run `./scripts/build-ffmpeg-windows.sh` first for the minimal FFmpeg. It needs MinGW-w64 and nasm: `brew install mingw-w64 nasm`, or `apt install mingw-w64 nasm`.

## Testing the Windows build without Windows

`tests/wine/run.sh` builds `tests/XStream.WinSmoke` and runs it under Wine in Docker with each renderer, checking that frames
are shown and the colour matches BT.709. It can't test AMD drivers or hardware decoding.
