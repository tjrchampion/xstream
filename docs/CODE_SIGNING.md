# Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

Windows releases of XStream (`XStream.exe` on the [releases page](../../../releases)) are code-signed so that Windows can
verify they come from this project and haven't been changed.

## What gets signed

- **What:** only `XStream.exe` built by this repository's [release workflow](../.github/workflows/release.yml) from a
  version tag, on a GitHub-hosted runner, from the source code in this repository.
- **Third-party libraries** packed inside it (FFmpeg, SDL2) are built from their published sources by
  `scripts/build-ffmpeg-windows.sh` or downloaded from their official releases by `scripts/publish-windows.sh`. They are not
  signed by this project.

## Team roles

- **Committers and reviewers:** [members of this repository](../../../graphs/contributors)
- **Approvers:** [@tjrchampion](https://github.com/tjrchampion)

Every signing request is approved manually by an approver.

## Privacy policy

XStream does not send any information to other networked systems unless the user asks for it.

When you sign in and stream, XStream connects to:

- **Microsoft and Xbox services**, which provide sign-in, your console list, game streaming, your recently played games and what's playing now
- **the Microsoft Store image service**, for game art
- **public STUN servers (Google, Cloudflare)**, which see only your internet address. That lets XStream connect to your console when you're away from home.
- **this project's GitHub releases**, to check for updates (this can be turned off in Settings)
- **the Discord app on your own computer**, only if it's running and "Show on Discord" is on

Your sign-in, settings, session logs and cached game art are stored only on your device, in `~/.xstream`. XStream sends no
analytics or crash reports. The diagnostics zip (**Settings → Export diagnostics**) is saved on your device and only leaves
it if you send it yourself.
