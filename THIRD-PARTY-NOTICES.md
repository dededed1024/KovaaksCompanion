# Third-party notices

KovaaksCompanion is an unofficial companion tool. It is not affiliated with, endorsed by, or sponsored by KovaaK Games, Inc.
"KovaaK's" and related names and branding belong to KovaaK Games.

## OBS Studio (libobs)

The app embeds parts of an unmodified OBS Studio build and loads libobs in its own process to capture the game window, record its audio, encode video and mix in hand cams.

- Build: OBS Studio 32.2.2 for Windows x64, `OBS-Studio-32.2.2-Windows-x64.zip`, SHA-256 `4d6e40e3ab155f56b30de517380566a206d74b63cdf5ad49aa596924768f97e1`, https://github.com/obsproject/obs-studio/releases/tag/32.2.2
- Parts used: `obs.dll`, the Direct3D 11 graphics module, the window-capture, WASAPI, DirectShow, NVENC, Intel QSV and AMD AMF/AAC (obs-ffmpeg) plugins, their libraries and effect files. Nothing else from the release is shipped; `scripts/fetch-libobs.ps1` selects the files and checks the hash.
- License: GNU General Public License v2 or later. License text: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html. KovaaksCompanion is therefore licensed under the GPL as well (see LICENSE).
- Corresponding source: https://github.com/obsproject/obs-studio at tag `32.2.2`; this app's own source is in this repository.
- OBS Studio is a trademark of its authors; this app is not affiliated with or endorsed by the OBS Project.
- The release bundles FFmpeg libraries (LGPL v2.1 or later, `avcodec`, `avformat`, `avutil`, `swscale`, `swresample`, `avfilter`, `avdevice`) and x264 (GPL v2 or later): https://ffmpeg.org, https://www.videolan.org/developers/x264.html. Their sources are linked from the OBS build dependencies: https://github.com/obsproject/obs-deps
- The runtime is unpacked to `%LOCALAPPDATA%\KovaaksCompanion\libobs\<key>\` where you can inspect it.
- Hardware H.264 encoders are used (NVIDIA NVENC, AMD AMF, Intel QuickSync). There is no software encoding.

## Playlists

Benchmark playlist definitions (names, groups, colors, share codes) come from https://evxl.app by EviL.

## KovaaK's data

The app reads the game's own local files (`* Stats.csv`, `performances/*.perf`) and requests public leaderboard and benchmark
progress data for your Steam account from kovaaks.com.
