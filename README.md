# BeatzOnKeys

[![Release](https://img.shields.io/github/v/release/MusicCat-61/BeatzOnKeys?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Download](https://img.shields.io/badge/download-portable%20win--x64-2ea44f?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases/latest)
[![CI](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)
[![Version](https://img.shields.io/badge/version-1.0.0-4CC2FF?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)

![BeatzOnKeys English interface](assets/preview-en.png)

*English interface · the Russian one is shown on the [Russian page](README.ru.md).*

**English** · [Русский](README.ru.md)

BeatzOnKeys turns ordinary typing into a melody. Import one or more MP3/WAV tracks, transcribe them to MIDI with Spotify's Basic Pitch, press **Start**, and every non-modifier key press plays the next detected note. The window gets out of the way into the system tray while you type in any application.

The app is written in C# with .NET 10 and WPF. NAudio supplies a built-in polyphonic synthesizer; Melanchall.DryWetMidi reads transcription results. Python, Basic Pitch, its ONNX model, and all Python dependencies are shipped inside the release folder — the end user installs nothing and needs no administrator rights.

> [!NOTE]
> Basic Pitch is instrument-agnostic and polyphonic, but it works best on a clear single instrument. Dense mastered mixes may produce fewer or less accurate notes.

## What is inside

| | |
|---|---|
| 🎹 **Type to play** | Every key-down except Shift, Ctrl, Alt and other bare modifiers triggers the next note |
| 🎼 **Audio-to-MIDI** | MP3/WAV transcription through bundled Basic Pitch and ONNX Runtime |
| 📚 **Batch queue** | Drop several files, process all sequentially, and watch per-track status updates |
| 🔁 **Auto-advance** | The selected track advances to the next ready track after its final note |
| 🔊 **Built-in synth** | NAudio sine/harmonic synthesizer; no MIDI device or soundfont is required |
| 🪟 **Mica window** | Native Windows 11 backdrop, custom frameless title bar, solid Windows 10 fallback |
| 🌗 **Frost themes** | Dark and light MusicCatRGB-inspired palettes, switched without restart |
| 🌐 **Two languages** | English and Russian switch instantly; the first launch follows the system language |
| 🔕 **Quiet tray mode** | Start hides the main window; no keyboard visualizer, RGB lighting, or pop-up noise |
| 💾 **Session restore** | Tracks, completed MIDI cache, selection, volume, speed, language, theme and auto-advance persist |
| 🗒 **Plain-text logs** | Daily UTF-8 diagnostics in `%LOCALAPPDATA%\BeatzOnKeys\logs` |
| ⬆️ **Verified updates** | Silent GitHub checks, About-window download, SHA-256 verification, and portable-folder restart |
| ℹ️ **About window** | Version, credits, project links, license notices, support links, and update controls |
| ♻️ **Portable** | Self-contained `.NET 10` app plus a neighboring `python` folder; no installer or admin access |

## How it works

1. **Import** — drag several `.mp3` or `.wav` files onto the drop area, or use **Add files**.
2. **Transcribe** — **Process all** starts the bundled `python\python.exe` once per queued track. `Tools\transcribe.py` loads Basic Pitch's ONNX model and writes MIDI into the per-user cache.
3. **Parse** — DryWetMidi sorts the MIDI notes and converts their timing and velocity into an in-memory sequence.
4. **Start** — BeatzOnKeys installs a low-level Windows keyboard hook and hides its window in the tray.
5. **Play** — the hook sends only a “non-modifier key went down” signal to a worker. The next note is synthesized through NAudio. Volume and note duration follow the UI sliders.
6. **Advance** — with auto-advance enabled, playing the last note selects the next ready track and resets its cursor to the beginning.

Simultaneous MIDI notes are deliberately flattened into a deterministic sequence: one physical key press always means one next note.

## Privacy

The keyboard hook is adapted from the privacy-preserving approach used by MusicCatRGB:

- it does **not** convert key codes into characters;
- it does **not** inspect the focused window, clipboard, or text fields;
- it does **not** log keys or application names;
- injected key events and bare modifier presses are ignored;
- the callback only queues a tiny signal, so audio work never blocks Windows input.

The hook is active only after **Start** and is removed on **Stop** or exit.

## Installation

### Portable release

1. Download the latest **BeatzOnKeys win-x64** archive from [Releases](https://github.com/MusicCat-61/BeatzOnKeys/releases/latest).
2. Extract the **whole folder** to a writable location, for example `C:\BeatzOnKeys`.
3. Keep `BeatzOnKeys.exe`, `Tools`, and `python` together. Do not move only the executable.
4. Run `BeatzOnKeys.exe`. No installer, separate .NET runtime, Python installation, or administrator rights are needed.

The first transcription loads the neural model and can take longer than later operations. Processing is CPU-intensive; the UI remains responsive and every track keeps its own status.

### Requirements

- Windows 10 x64 or Windows 11 x64.
- A working default audio output device.
- Enough free space for the portable Python environment and transcription cache.

## Usage

1. Add one or more MP3/WAV files. Three files can be dropped in one operation.
2. Click **Process all**. Status moves through **Waiting → Processing… → Ready** (or **Error** with a localized tooltip).
3. Select any ready track.
4. Adjust **Volume** and **Note speed**.
5. Optionally enable **Auto-advance tracks**.
6. Click **Start**. The window hides into the tray.
7. Type in any application. Each non-modifier press plays the next note.
8. Double-click the tray icon to reopen the window, or use **Stop playback** / **Exit** in its menu.

**Reset** removes imported entries, cached MIDI files and saved settings. It never deletes the original MP3/WAV files.

## Languages and themes

Both switches are in the title bar:

- `EN | RU` changes every dynamic interface string and error message immediately;
- the sun/moon button swaps the complete light/dark palette immediately;
- both choices are written to `%LOCALAPPDATA%\BeatzOnKeys\settings.json` and restored at startup.

This repository contains matching documentation in [English](README.md) and [Russian](README.ru.md).

## Persistence and cache

The app stores only user state and generated files under `%LOCALAPPDATA%\BeatzOnKeys`:

```text
BeatzOnKeys/
├─ settings.json          # volume, speed, selected track, theme, language, queue
├─ midi/                  # completed Basic Pitch MIDI results
└─ logs/                  # daily UTF-8 log files, retained for 14 days
```

A completed track is restored from its cached MIDI without running Basic Pitch again. If the cache or source file is missing, the track is marked with a clear localized error instead of silently retranscribing it.

## About and updates

**About** is available in both the footer and tray menu. It shows the installed version, project and MusicCatRGB credits, license/support links, and the complete update workflow.

Update checks follow the quiet MusicCatRGB model:

- one silent check runs a few seconds after startup;
- the tray timer wakes every 15 minutes, but a successful answer is reused for 30 minutes;
- a failed network check retries after 5 minutes and never opens an error dialog at startup;
- only a genuinely newer release produces a tray notice; clicking it opens **About**;
- opening **About** also refreshes a stale result, and **Check for updates** is always available manually.

A release is not trusted merely because it downloaded. BeatzOnKeys downloads `BeatzOnKeys-win-x64.zip` together with its `.sha256`, verifies SHA-256, extracts with path-traversal checks, and confirms that the EXE, `python\python.exe`, and transcription script exist. **Install and restart** launches a detached PowerShell helper, closes the app, copies the verified portable payload over the current folder, logs the outcome, and starts BeatzOnKeys again. Settings and MIDI cache stay in `%LOCALAPPDATA%` and are not replaced.

The application folder must be writable; no administrator privileges are requested. A failed apply keeps the existing executable and is reported on the next launch.

## Logs

Logs are written to `%LOCALAPPDATA%\BeatzOnKeys\logs\beatzonkeys-YYYYMMDD.log`. Use **Logs** in the footer to open the folder.

| Category | What it records |
|---|---|
| `[settings]` | Settings read/write failures and invalid restored entries |
| `[transcription]` | Python process result, output MIDI path, and Basic Pitch failures |
| `[midi]` | Invalid or unreadable cached MIDI files |
| `[playback]` | Global playback start/stop and worker failures — never typed keys |
| `[cache]` | Failure to remove an app-owned cached MIDI file |
| `[update]` | Automatic/manual checks, verified staging, detached apply, and previous apply result |
| `[startup]`, `[shutdown]`, `[ui]` | Lifecycle and unhandled UI errors |

Example:

```text
2026-11-04 12:30:15.207 +03:00 [INF] [transcription] Created MIDI for 'piano.wav' at '...\midi\....mid'.
2026-11-04 12:30:18.044 +03:00 [INF] [playback] Global keyboard playback started.
```

## Building from source

Requires Windows and the .NET 10 SDK. A normal developer build does not need Python:

```powershell
dotnet build BeatzOnKeys.sln -c Release
```

Run the required publisher to create a complete end-user folder:

```bat
publish.bat
```

`publish.bat`:

1. publishes `BeatzOnKeys.UI` self-contained for `win-x64`;
2. downloads the official CPython 3.10.11 embeddable archive and verifies its SHA-256;
3. installs pinned Basic Pitch 0.4.0 + ONNX dependencies into `Build_Output\python`;
4. validates imports and the packaged model;
5. copies both READMEs and license notices.

The resulting `Build_Output` folder is the portable product. The publisher needs internet access; the finished app does not need internet for transcription.

### Repository layout

```text
BeatzOnKeys.Core/       models, settings, Basic Pitch process, MIDI, hook, synth
BeatzOnKeys.UI/         WPF shell, Frost themes, EN/RU resources, tray lifecycle
build/                  reproducible portable-Python builder and pinned requirements
assets/                 English and Russian screenshots
publish.bat             one-command self-contained publisher
```

## Troubleshooting

- **Bundled Python is missing** — restore the complete release folder; `python\python.exe` must sit beside the app.
- **Basic Pitch fails on MP3** — try a PCM WAV to distinguish an audio-codec problem; details are in the track tooltip and log.
- **No notes detected** — use a clearer instrumental passage or a louder source with less percussion/noise.
- **No sound while typing** — select a ready track, confirm Windows has a default output device, and raise volume.
- **An update will not install** — keep the complete app in a writable folder and inspect `%LOCALAPPDATA%\BeatzOnKeys\logs\update-apply.log` after a failed restart.
- **The window disappeared** — this is expected after Start; double-click the BeatzOnKeys tray icon.
- **A restored source is gone** — completed MIDI remains playable; unprocessed missing sources are marked as errors.

## Credits & License

- **BeatzOnKeys:** [https://github.com/MusicCat-61/BeatzOnKeys](https://github.com/MusicCat-61/BeatzOnKeys)
- Design, themes, localization structure, Mica shell, tray lifecycle and keyboard-hook approach are based on [**MusicCatRGB**](https://github.com/MusicCat-61/MusicCatRGB) by MusicCat161 / MusicCat-61.
- Audio-to-MIDI transcription is powered by [**Basic Pitch**](https://github.com/spotify/basic-pitch) by Spotify.
- Audio output uses [**NAudio**](https://github.com/naudio/NAudio); MIDI parsing uses [**DryWetMidi**](https://github.com/melanchall/drywetmidi).

BeatzOnKeys is distributed under the [MIT License](LICENSE). MusicCatRGB's required MIT notices and all major dependency attributions are retained in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
