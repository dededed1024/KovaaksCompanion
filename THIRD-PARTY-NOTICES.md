# Third-party notices

KovaaksCompanion is an unofficial companion tool. It is not affiliated with, endorsed by, or sponsored by KovaaK Games, Inc.
"KovaaK's" and related names and branding belong to KovaaK Games.

## FFmpeg

The app embeds an unmodified FFmpeg build and runs it as a separate process to record video.

- Build: BtbN FFmpeg-Builds, FFmpeg n9.0.2 (`n9.0.2-22-g46d8f462ee`), win64 **LGPL** variant (built without GPL and nonfree libraries; no libx264).
- License: GNU Lesser General Public License v3 (the build is configured with `--enable-version3`). License text: https://www.gnu.org/licenses/lgpl-3.0.html
- This software uses code of FFmpeg licensed under the LGPLv3. FFmpeg is a trademark of Fabrice Bellard, originator of the FFmpeg project.
- Binary used: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-07-13-07 (file `ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-9.0.zip`, SHA-256 `3aa4a8161a29866fba8f8c87f07dc0a57c4c9bab42c0db84510b046ca80d41cd`).
- Corresponding source: https://github.com/FFmpeg/FFmpeg at tag `n9.0.2`; build scripts and configuration: https://github.com/BtbN/FFmpeg-Builds
- You can replace the embedded `ffmpeg.exe` (extracted to `%LOCALAPPDATA%\KovaaksCompanion\ffmpeg\`) with another build; the app prefers its own extracted copy, so remove that folder to make it fall back to an `ffmpeg.exe` found on your system.
- Hardware H.264 encoders are used (NVIDIA NVENC, AMD AMF, Intel QuickSync). There is no software encoding.

## Playlists

Benchmark playlist definitions (names, groups, colors, share codes) come from https://evxl.app by EviL.

## KovaaK's data

The app reads the game's own local files (`* Stats.csv`, `performances/*.perf`) and requests public leaderboard and benchmark
progress data for your Steam account from kovaaks.com.
