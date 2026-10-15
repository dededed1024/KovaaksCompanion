# Third-party notices

KovaaksCompanion is an unofficial companion tool. It is not affiliated with, endorsed by, or sponsored by KovaaK Games, Inc.
"KovaaK's" and related names and branding belong to KovaaK Games.

## OBS Studio (libobs)

The app embeds parts of an unmodified OBS Studio build and loads libobs in its own process for recording.

- Build: OBS Studio 32.2.2 for Windows x64, SHA-256 `4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1`, https://github.com/obsproject/obs-studio/releases/tag/32.2.2
- License: GNU General Public License v2 or later (https://www.gnu.org/licenses/old-licenses/gpl-2.0.html). KovaaksCompanion is therefore licensed under the GPL as well (see LICENSE).
- Corresponding source: https://github.com/obsproject/obs-studio at tag `32.2.2`; this app's own source is in this repository.
- The release bundles FFmpeg libraries (LGPL v2.1 or later) and x264 (GPL v2 or later): https://ffmpeg.org, https://www.videolan.org/developers/x264.html. Sources are linked from the OBS build dependencies: https://github.com/obsproject/obs-deps
- OBS Studio is a trademark of its authors; this app is not affiliated with or endorsed by the OBS Project.

## libwebp

The profile card is saved as an animated WebP with `img2webp.exe` from the unmodified libwebp 1.5.0 Windows x64 release, embedded in the app.

- Build: https://storage.googleapis.com/downloads.webmproject.org/releases/webp/libwebp-1.5.0-windows-x64.zip, SHA-256 `e8fe3bc7eb09774e69261a42bf9fa8a37ab5f3eecaab199f6420e6f9e822090c`
- License: BSD 3-Clause (https://chromium.googlesource.com/webm/libwebp/+/refs/heads/main/COPYING). Source: https://chromium.googlesource.com/webm/libwebp

## Playlists

Benchmark playlist definitions come from https://evxl.app by EviL.

## KovaaK's data

Leaderboard and benchmark data come from kovaaks.com.
