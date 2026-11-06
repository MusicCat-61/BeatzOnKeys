# Third-party notices

BeatzOnKeys uses and adapts open-source software. Each component remains under its own license.

## MusicCatRGB

The Frost visual language, runtime theme/localization pattern, Mica helper, custom window structure, tray lifecycle, and low-level keyboard-hook approach are adapted from [MusicCatRGB](https://github.com/MusicCat-61/MusicCatRGB).

MusicCatRGB is licensed under the MIT License. The complete required notice is included in [`third-party/MusicCatRGB-LICENSE.txt`](third-party/MusicCatRGB-LICENSE.txt) and is reproduced in source and release distributions.

Copyright (c) 2026 Project GutsV<br>
Copyright (c) 2026 MusicCat161 (MusicCatRGB modification)

## NAudio

Audio decoding and Windows playback use [NAudio](https://github.com/naudio/NAudio): AudioFileReader from NAudio, NAudio.Core sample providers, NAudio.WinMM audio output, and NAudio.Wasapi for Windows Media Foundation decoding.

NAudio is licensed under the MIT License. The complete notice is included in [`third-party/NAudio-LICENSE.txt`](third-party/NAudio-LICENSE.txt).

Copyright 2020 Mark Heath

## .NET

The self-contained release includes components from [.NET](https://github.com/dotnet/runtime), distributed under the MIT License and associated notices supplied by Microsoft.

## yt-dlp (downloaded at runtime, not bundled)

The YouTube section drives [yt-dlp](https://github.com/yt-dlp/yt-dlp) as a separate process. The executable is **not** included in any BeatzOnKeys archive: the app downloads `yt-dlp.exe` from the official GitHub release into `%LOCALAPPDATA%\BeatzOnKeys\bin` the first time the user searches or downloads, and never at startup.

yt-dlp is released into the public domain under the [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE). Its release binaries bundle CPython and several permissively licensed libraries; their notices ship inside the yt-dlp distribution itself.

BeatzOnKeys only passes command-line arguments and reads its output. It does not link against yt-dlp, does not modify it, and claims no authorship over it.

## ffmpeg (optional, supplied by the user)

BeatzOnKeys contains no ffmpeg code and downloads no ffmpeg binary.

The YouTube cache stores M4A (AAC), which Windows decodes through Media Foundation. On Windows editions without that component — the "N" variants, which ship without media features — the app offers a WAV conversion, and uses an `ffmpeg.exe` that **the user places next to `yt-dlp.exe`** in `%LOCALAPPDATA%\BeatzOnKeys\bin` (or anywhere on `PATH`). If none is present, the download is reported as failed with an explanation instead of silently breaking.

Why it is not fetched automatically: the only compact build that performs this conversion is compiled with `--enable-nonfree`, which makes the resulting binary non-redistributable, while the properly licensed monolithic builds are 86–167 MB — larger than the entire application.

FFmpeg is licensed under the LGPL 2.1 or later by default, and under the GPL when built with `--enable-gpl`. If you supply ffmpeg, you are responsible for choosing a build whose license fits your use; a GPL build is fine to run as a separate process, and LGPL builds are the safest default. The complete FFmpeg license text is distributed with every FFmpeg build (`LICENSE`, `COPYING.LGPLv2.1`, `COPYING.GPLv2`).

## Zapret2UI (external recommendation, not included)

The troubleshooting section of the in-app guide links to [Zapret2UI](https://github.com/Asterlike/zapret2UI), a front-end for network-level DPI circumvention. BeatzOnKeys does not bundle, download, install or invoke it: the guide simply opens its GitHub page in the user's browser. Whether to use such a tool, and whether doing so is lawful, is the user's own decision.

## Distribution note

No MusicCatRGB hardware drivers, vendor DLLs, fonts, logo, or screenshots are distributed with BeatzOnKeys. Only the adapted design and application patterns described above are credited. No third-party executables are included in the release archive; `BeatzOnKeys.exe` is built entirely from this repository.