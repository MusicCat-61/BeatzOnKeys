# Сторонние компоненты / Third-party notices

**[Русский](#русский)** · [English](#english)

По умолчанию открывается русская версия. / The Russian section comes first.

---

## Русский

BeatzOnKeys использует и адаптирует открытое программное обеспечение. Каждый компонент остаётся под своей лицензией.

### MusicCatRGB

Визуальный язык Frost, подход к темам и локализации во время выполнения, помощник Mica, структура собственного окна, жизненный цикл значка в трее и подход к низкоуровневому перехвату клавиатуры адаптированы из [MusicCatRGB](https://github.com/MusicCat-61/MusicCatRGB).

MusicCatRGB распространяется по лицензии MIT. Полный требуемый текст уведомления лежит в [`third-party/MusicCatRGB-LICENSE.txt`](third-party/MusicCatRGB-LICENSE.txt) и воспроизводится в исходниках и в релизных сборках.

Copyright (c) 2026 Project GutsV<br>
Copyright (c) 2026 MusicCat161 (модификация MusicCatRGB)

### NAudio

Декодирование звука и воспроизведение в Windows построены на [NAudio](https://github.com/naudio/NAudio): `AudioFileReader` из NAudio, поставщики сэмплов NAudio.Core, вывод звука NAudio.WinMM и NAudio.Wasapi для декодирования через Windows Media Foundation.

NAudio распространяется по лицензии MIT. Полный текст уведомления лежит в [`third-party/NAudio-LICENSE.txt`](third-party/NAudio-LICENSE.txt).

Copyright 2020 Mark Heath

### .NET

Автономная сборка включает компоненты [.NET](https://github.com/dotnet/runtime), распространяемые по лицензии MIT вместе с уведомлениями, которые поставляет Microsoft.

### yt-dlp (скачивается во время работы, не входит в сборку)

Раздел YouTube управляет [yt-dlp](https://github.com/yt-dlp/yt-dlp) как отдельным процессом. Исполняемый файл **не** входит ни в один архив BeatzOnKeys: приложение скачивает `yt-dlp.exe` из официального релиза на GitHub в `%LOCALAPPDATA%\BeatzOnKeys\bin` при первом поиске или скачивании пользователя и никогда при запуске.

yt-dlp передан в общественное достояние по [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE). Его релизные сборки содержат CPython и несколько библиотек под разрешительными лицензиями; их уведомления поставляются внутри самого дистрибутива yt-dlp.

BeatzOnKeys только передаёт аргументы командной строки и читает его вывод. Приложение не компонуется с yt-dlp, не изменяет его и не претендует на авторство.

### ffmpeg (необязательно, предоставляет пользователь)

BeatzOnKeys не содержит кода ffmpeg и не скачивает двоичные файлы ffmpeg.

Кеш YouTube хранит M4A (AAC), который Windows декодирует через Media Foundation. В редакциях Windows без этого компонента — варианты «N», поставляемые без мультимедийных средств — приложение предлагает конвертацию в WAV и использует `ffmpeg.exe`, который **пользователь кладёт рядом с `yt-dlp.exe`** в `%LOCALAPPDATA%\BeatzOnKeys\bin` (или в любую папку на `PATH`). Если его нет, скачивание завершается ошибкой с объяснением, а не ломается молча.

Почему он не скачивается автоматически: единственная компактная сборка, выполняющая это преобразование, скомпилирована с `--enable-nonfree`, что делает полученный двоичный файл нераспространяемым, а корректно лицензированные монолитные сборки занимают 86–167 МБ — больше, чем всё приложение.

FFmpeg распространяется по LGPL 2.1 или новее по умолчанию и по GPL, если собран с `--enable-gpl`. Если вы предоставляете ffmpeg, вы отвечаете за выбор сборки, чья лицензия подходит для вашего использования; сборку под GPL можно запускать как отдельный процесс, а сборки под LGPL — самый безопасный вариант по умолчанию. Полный текст лицензии FFmpeg поставляется с каждой сборкой FFmpeg (`LICENSE`, `COPYING.LGPLv2.1`, `COPYING.GPLv2`).

### Zapret2UI (внешняя рекомендация, не входит в сборку)

Раздел устранения неполадок во встроенном руководстве ссылается на [Zapret2UI](https://github.com/Asterlike/zapret2UI) — графическую оболочку для обхода DPI на сетевом уровне. BeatzOnKeys не включает, не скачивает, не устанавливает и не вызывает её: руководство просто открывает её страницу на GitHub в браузере пользователя. Использовать ли такой инструмент и законно ли это — решает сам пользователь.

### Примечание о распространении

Никакие аппаратные драйверы MusicCatRGB, вендорные DLL, шрифты, логотип или скриншоты не распространяются вместе с BeatzOnKeys. Указаны только адаптированный дизайн и описанные выше приёмы приложения. В релизный архив не входит ни один сторонний исполняемый файл; `BeatzOnKeys.exe` собирается целиком из этого репозитория.

---

## English

BeatzOnKeys uses and adapts open-source software. Each component remains under its own license.

### MusicCatRGB

The Frost visual language, runtime theme/localization pattern, Mica helper, custom window structure, tray lifecycle, and low-level keyboard-hook approach are adapted from [MusicCatRGB](https://github.com/MusicCat-61/MusicCatRGB).

MusicCatRGB is licensed under the MIT License. The complete required notice is included in [`third-party/MusicCatRGB-LICENSE.txt`](third-party/MusicCatRGB-LICENSE.txt) and is reproduced in source and release distributions.

Copyright (c) 2026 Project GutsV<br>
Copyright (c) 2026 MusicCat161 (MusicCatRGB modification)

### NAudio

Audio decoding and Windows playback use [NAudio](https://github.com/naudio/NAudio): AudioFileReader from NAudio, NAudio.Core sample providers, NAudio.WinMM audio output, and NAudio.Wasapi for Windows Media Foundation decoding.

NAudio is licensed under the MIT License. The complete notice is included in [`third-party/NAudio-LICENSE.txt`](third-party/NAudio-LICENSE.txt).

Copyright 2020 Mark Heath

### .NET

The self-contained release includes components from [.NET](https://github.com/dotnet/runtime), distributed under the MIT License and associated notices supplied by Microsoft.

### yt-dlp (downloaded at runtime, not bundled)

The YouTube section drives [yt-dlp](https://github.com/yt-dlp/yt-dlp) as a separate process. The executable is **not** included in any BeatzOnKeys archive: the app downloads `yt-dlp.exe` from the official GitHub release into `%LOCALAPPDATA%\BeatzOnKeys\bin` the first time the user searches or downloads, and never at startup.

yt-dlp is released into the public domain under the [Unlicense](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE). Its release binaries bundle CPython and several permissively licensed libraries; their notices ship inside the yt-dlp distribution itself.

BeatzOnKeys only passes command-line arguments and reads its output. It does not link against yt-dlp, does not modify it, and claims no authorship over it.

### ffmpeg (optional, supplied by the user)

BeatzOnKeys contains no ffmpeg code and downloads no ffmpeg binary.

The YouTube cache stores M4A (AAC), which Windows decodes through Media Foundation. On Windows editions without that component — the "N" variants, which ship without media features — the app offers a WAV conversion, and uses an `ffmpeg.exe` that **the user places next to `yt-dlp.exe`** in `%LOCALAPPDATA%\BeatzOnKeys\bin` (or anywhere on `PATH`). If none is present, the download is reported as failed with an explanation instead of silently breaking.

Why it is not fetched automatically: the only compact build that performs this conversion is compiled with `--enable-nonfree`, which makes the resulting binary non-redistributable, while the properly licensed monolithic builds are 86–167 MB — larger than the entire application.

FFmpeg is licensed under the LGPL 2.1 or later by default, and under the GPL when built with `--enable-gpl`. If you supply ffmpeg, you are responsible for choosing a build whose license fits your use; a GPL build is fine to run as a separate process, and LGPL builds are the safest default. The complete FFmpeg license text is distributed with every FFmpeg build (`LICENSE`, `COPYING.LGPLv2.1`, `COPYING.GPLv2`).

### Zapret2UI (external recommendation, not included)

The troubleshooting section of the in-app guide links to [Zapret2UI](https://github.com/Asterlike/zapret2UI), a front-end for network-level DPI circumvention. BeatzOnKeys does not bundle, download, install or invoke it: the guide simply opens its GitHub page in the user's browser. Whether to use such a tool, and whether doing so is lawful, is the user's own decision.

### Distribution note

No MusicCatRGB hardware drivers, vendor DLLs, fonts, logo, or screenshots are distributed with BeatzOnKeys. Only the adapted design and application patterns described above are credited. No third-party executables are included in the release archive; `BeatzOnKeys.exe` is built entirely from this repository.

---

**[Русский](#русский)** · [English](#english)
