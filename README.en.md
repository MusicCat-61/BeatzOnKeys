# BeatzOnKeys

[![Release](https://img.shields.io/github/v/release/MusicCat-61/BeatzOnKeys?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Download](https://img.shields.io/badge/download-portable%20win--x64-2ea44f?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases/latest)
[![CI](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](<LICENSE>)
[![Version](https://img.shields.io/badge/version-1.4.0-4CC2FF?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)

![BeatzOnKeys English interface](<assets/preview-en.png>)

*English interface · the Russian one is shown on the [Russian page](<README.md>).*

**English** · [Русский](<README.md>)

**An audio player that plays continuously, gets louder while you type, and becomes quieter when you stop. After a few seconds of inactivity, the track pauses.**

Add an MP3 track (PCM WAV is also supported) and press **Start**: playback begins immediately at `0:00`. Typing controls the volume, not the playback position. Idle pausing is enabled by default and can be switched off for continuous quiet playback.

BeatzOnKeys is written in C# with .NET 10 and WPF, with NAudio for decoding and Windows audio output. The portable release is self-contained: no installer, administrator rights, or separately installed .NET runtime are required.

## What is inside

| | |
|---|---|
| 🎹 **Typing-reactive volume** | Smooth volume changes between quiet and typing levels; Shift, Ctrl, and Alt alone are ignored |
| 🎧 **Continuous playback** | The selected MP3 or PCM WAV plays normally from its beginning |
| ⏸ **Optional idle pause** | Pause after a configurable timeout; the first key resumes at the same position |
| 🔁 **Advance and loop** | Continue to the next track, repeat one track, or loop the complete list |
| 🪟 **Mica window** | Native Windows 11 backdrop, custom frameless title bar, and a solid Windows 10 fallback |
| 🌗 **Frost themes** | Dark and light MusicCatRGB-inspired palettes switch without a restart |
| 🌐 **Two languages** | English and Russian switch instantly; first launch follows the system language |
| 🔕 **Tray mode** | The window stays open after Start and hides only when you explicitly close it to the tray |
| 💾 **Saved preferences** | Playlist, selection, volume levels, idle options, language, theme, auto-advance, and loop |
| 🥁 **Music-reactive extras** | Punch attack on transients, bass boost, stereo pulse, and a quiet level that follows the track |
| 🎛 **Playlist controls** | Move a track up or down, remove one, or clear the whole list without touching the files |
| 🗒 **Logs and updates** | Daily UTF-8 diagnostics, quiet update checks, and a verified in-place updater that replaces the executable itself |
| ▶️ **YouTube section** | A separate tab with search by name, direct links, a cache in `%LOCALAPPDATA%\BeatzOnKeys\Music` and a disk-usage figure |
| 🎵 **TikTok section** | A third tab: a link to a clip and the audio lands in `Music\TikTok`, with its own cache, its own list and a cancel button |
| 📖 **Built-in guide** | One window explains every button, slider, switch, tray action, status, and path — open it with F1 |

## How it works

- **Start** immediately plays the selected track from `0:00` at the quiet level and enables the keyboard hook.
- A key press other than **Shift, Ctrl, or Alt** raises volume smoothly from the quiet level (`MinVolume`) to `MaxVolume` over **50 ms**. After **150 ms** without a qualifying key, volume falls to the quiet level over **200 ms**.
- With **Pause on idle** enabled, inactivity longer than `IdleTimeout` pauses the track. The first qualifying key resumes playback **at the same position and instantly at `MaxVolume`**.
- With idle pausing disabled, the track keeps playing at the quiet level while you are not typing.
- The background cache waits at most a minute per row. A slow one is marked «Deferred» and retried once at the end, so one track cannot stall the list. A download you start yourself gets the full ten minutes.
- **Only a search preview ignores the idle pause and plays in full.** You asked to hear that track, not to start a background session. A downloaded track from the list and local playlist tracks keep the pause as usual.
- **Stop → Start always restarts from the beginning.** An idle pause/resume preserves the position during the current run; playback position is not saved across application restarts.

## Settings

| Setting | Control / range | Default |
|---|---|---|
| `MinVolume` | Quiet-volume slider, **0–50%** | **20%** |
| `MaxVolume` | **Volume** slider / typing-volume ceiling, **0–100%** | **100%** |
| `IdleTimeout` | Idle timeout, **1–10 seconds** | **3 seconds** |
| `PauseOnIdle` | **Pause on idle** checkbox | **On (`true`)** |

The effective quiet volume is `min(MinVolume, MaxVolume)`: it never exceeds the maximum volume, even when the maximum slider is set below the quiet-volume slider.

Settings are stored in `<Root>\settings.json`. **Schema 6** contains `MinVolume`, `MaxVolume`, `IdleTimeout`, `PauseOnIdle`, `AutoAdvance`, `Loop`, `SelectedTrackId`, `Language` and `Theme`; the track collection lives separately, in `<Root>\library.json`. In JSON, volumes are fractions in `0..1` (`0.2` means 20%; `MinVolume` is limited to `0.5`), and `IdleTimeout` is in seconds.

The old `Volume` value automatically migrates to `MaxVolume`. Other new values use their defaults: `MinVolume = 0.2`, `IdleTimeout = 3`, `PauseOnIdle = true`. Your existing playlist and other saved preferences are retained.

### Where the data lives

Everything lives under one root, `<Root>`, and the root depends on where the program was started:

| Mode | When | `<Root>` |
|---|---|---|
| **Portable** | the exe sits in an ordinary writable folder | the folder beside the exe |
| **ChosenDirectory** | the exe sits on the desktop, in the profile, `C:\`, `C:\Windows`, Program Files or `%TEMP%` | the picked folder plus `\BeatzOnKeys\` |
| **LocalAppData** | fallback when no folder was picked | `%LOCALAPPDATA%\BeatzOnKeys\` |

```text
<Root>\
├─ settings.json          # preferences
├─ library.json           # the track collection: playlist, YouTube, TikTok
├─ Logs\                  # daily UTF-8 diagnostics
├─ Music\                 # the YouTube cache
│  └─ TikTok\             # downloaded from TikTok
├─ bin\                   # yt-dlp and ffmpeg
└─ downloads\             # download scratch space
```

On the first start from the desktop the program asks for a data folder and creates `BeatzOnKeys\` inside it. The answer is remembered in `%LOCALAPPDATA%\BeatzOnKeys\storage.json`; cancelling does not block startup — the program runs on `%LOCALAPPDATA%` and asks again next time. If the folder disappears, the question comes back and the recorded choice stays on disk.

On the first start in **Portable** or **ChosenDirectory** mode, older data from `%LOCALAPPDATA%\BeatzOnKeys\` is copied into the new root. The originals stay where they are, and the copy never runs twice.
## End-of-track behavior

These switches act at the track's **natural end**, not during an idle pause.

| Auto-advance | Loop | Result at the end |
|---|---|---|
| Off | Off | Stay at the end; choose a track or use Stop → Start to restart |
| Off | On | Restart and repeat the same track |
| On | Off | Play the next track; stop after the final track |
| On | On | Play the next track and wrap the complete list back to the first |

Choosing another track starts that track from its beginning.

## Installation

### Portable release

1. Download the latest **BeatzOnKeys win-x64** archive from [Releases](https://github.com/MusicCat-61/BeatzOnKeys/releases/latest).
2. Extract the archive's `BeatzOnKeys.exe` to a writable location, for example `C:\BeatzOnKeys`.
3. Run `BeatzOnKeys.exe`. It is the whole application; the documentation files are optional.

Only one copy can run at a time: launching it again restores the window of the running copy.

No installer, administrator rights, or separate .NET runtime are required. **Upgrading from 1.0.x:** close the old app, download 1.4.0 manually, and replace the old folder rather than using the old in-app installer. Existing settings migrate automatically as described [above](#settings).

### Requirements

- Windows 10 x64 or Windows 11 x64.
- A working Windows audio output device.
- At least one readable MP3 file; PCM WAV is optional.

## Usage

1. Drag MP3 or PCM WAV files onto the drop area, or use **Add files**; select a track. The arrows in a row reorder the playlist and **Clear all** empties it.
2. Set the quiet and maximum volume levels, idle timeout, and **Pause on idle**. Choose **Auto-advance** and **Loop** as needed.
3. Click **Start**. The track starts immediately from the beginning and the window stays open.
4. Type in any application to raise the volume; stop typing to lower it. If idle pausing is enabled, the first key after a pause resumes the track at the same position.
5. Use the title-bar close button to hide the window to the tray. Double-click the tray icon to reopen it, or use **Stop playback** / **Exit** in its menu.

**Reset** removes imported entries and saved settings. It never deletes the original audio files.

## YouTube

The **YouTube** tab in the title bar opens a section of its own: downloaded tracks live there and **never enter the local playlist**.

- **Search.** Type a name and press **Search** for up to ten results; the **Download** button on a row saves that track.
- **Direct link.** Paste `https://youtube.com/watch?v=…` and press **Download** — the app recognises the link and fetches it without searching.
- **Where the files live.** `%LOCALAPPDATA%\BeatzOnKeys\Music`, which survives a reinstall. **Open the music folder** launches Explorer there.
- **How much is used.** The YouTube cache block shows the size in KB, MB or GB.
- **Clearing.** **Clear the YouTube cache** deletes the files after a confirmation and never touches the local playlist.
- **Caching.** The **Cache tracks** checkbox turns accumulation off.
- **yt-dlp.** Downloads go through `yt-dlp.exe`, which is **not** fetched at startup: 22.5 MB arrive on the first action with a progress bar, and **Download/Update yt-dlp** repeats the download.

Limits: one link at a time, playlists and live streams are refused with an explanation, at most two downloads run at once, and each is capped at ten minutes.

The format is M4A (AAC), decoded by Media Foundation. On Windows without that component (the "N" editions) the app converts to WAV through an `ffmpeg.exe` placed in `%LOCALAPPDATA%\BeatzOnKeys\bin`. ffmpeg is never downloaded automatically: the compact builds cannot be redistributed, and the properly licensed ones are larger than the application itself.
## TikTok

The **TikTok** tab opens a third section: one field, one button. Paste a link to a clip and press **Download** — the audio track lands in `Music\TikTok`.

- **Short links.** `vm.tiktok.com/…` and `vt.tiktok.com/…` work: the program follows the short link to the real address of the clip first and only then fetches the audio. Pasting the same link again does not add a second copy.
- **Cancel.** While a download runs, **Cancel** appears next to the field and stops exactly that download.
- **Its own cache.** TikTok files sit apart from the YouTube cache, the counter counts only them, and **Clear TikTok cache** removes only them.
- **Its own list.** Downloaded from TikTok is a separate collection, not the Local playlist: counters and clearing are never shared.
- **No ffmpeg.** When TikTok serves a video container, the audio is unwrapped with the Media Foundation decoder and stored as WAV.

There is no search by words here: TikTok does not hand out a list for a query without an account. No cookies, tokens or sign-in are used — only what TikTok serves openly. Private, deleted and sign-in-only clips are refused with an explanation.

## Languages and themes

The title-bar `EN | RU` switch changes the language immediately; the sun/moon button changes the light/dark palette. Both choices are saved.

![BeatzOnKeys light theme](<assets/preview-light-en.png>)

[About dialog](<assets/about-ru.png>) · [Light About dialog](<assets/about-light-ru.png>) · [Guide, English](<assets/guide-en.png>) · [Guide, Russian](<assets/guide-ru.png>)

YouTube tab: [search with listening](<assets/youtube-search-ru.png>) · [downloaded tracks](<assets/youtube-downloaded-ru.png>) · [search in progress](<assets/youtube-searching-ru.png>)

Settings sections: [Volume](<assets/section-volume.png>) · [Music reaction](<assets/section-reaction.png>) · [Behaviour](<assets/section-behavior.png>) · [all collapsed](<assets/section-collapsed.png>)

Matching documentation is available in [English](<README.en.md>) and [Russian](<README.md>).

## Privacy

The keyboard hook follows the privacy-preserving approach used by MusicCatRGB:

- it does **not** convert key codes into text;
- it does **not** inspect the focused window, clipboard, or text fields;
- it does **not** log keys or application names;
- injected events and bare Shift/Ctrl/Alt presses are ignored; the existing hook also ignores CapsLock and Windows keys;
- the callback only signals keyboard activity and returns immediately; audio work stays on the playback side.

The hook is active only after **Start** and is removed on **Stop** or exit. It runs in a **separate headless watcher process** (`BeatzOnKeys.exe --watcher`, the same approach as MusicCatRGB), so hiding, minimising, or a busy window cannot delay or silence typing. The watcher reports readiness and real key deliveries to the app, and a dropped connection is logged instead of failing silently. Keep the application folder together: the watcher is the same executable.

## About and updates

**About** is available from the footer and tray menu. It shows the installed version, developer, project/license/support links, and update controls. MusicCatRGB attribution remains in [Credits & License](#credits--license) and the third-party notices.

A quiet background check runs after startup; only a genuinely newer release produces a tray notice. Opening **About** can refresh a stale result, and **Check for updates** is available manually.

The updater downloads `BeatzOnKeys.exe` beside the running application and checks it four ways before offering it: SHA-256, the size the release advertises, a real `MZ` executable header, and a file version that is not older than the running one. **Install and restart** then hands the swap to a detached script that waits for the app to exit, retries the rename, and falls back to a copy — because Windows keeps a running executable locked. It records every step in `update-apply.log`, so a failed update is reported on the next start instead of passing silently. Per-user settings and logs are never touched. The zip archive stays available for manual installs.

## Logs

Logs are written to `<Root>\Logs\beatzonkeys-YYYYMMDD.log`. Use **Logs** in the footer to open the folder.

| Category | What it records |
|---|---|
| `[audio]` | Decoder and output-device failures |
| `[playback]` | Playback lifecycle and worker failures — never typed keys |
| `[settings]` | Settings load, normalization, and save failures |
| `[update]` | Checks, verification, staging, and apply results |
| `[ui]` | UI lifecycle and unhandled errors |

## Building from source

Requires Windows and the .NET 10 SDK. Build and run the non-interactive test harness with:

```powershell
dotnet build BeatzOnKeys.sln -c Release
dotnet run --project BeatzOnKeys.Tests -c Release
```

Create the **1.4.0** portable self-contained folder in Windows **cmd**:

```bat
set BOK_VERSION=1.4.0
publish.bat
```

The publisher now defaults to `BOK_VERSION=1.4.0`; setting it explicitly makes the intended release version clear.

The publisher validates its `Build_Output` cleanup target, publishes `BeatzOnKeys.UI` self-contained for `win-x64`, and copies both READMEs, release notes, screenshots, the license, and third-party notices. It stops on any required publish/copy failure. The resulting `Build_Output` directory is the portable product.

### Tray and native keyboard regression

On an interactive Windows desktop with an audio output device, run:

```powershell
dotnet run --project tools/UiSmoke -c Release -- --root . --session tray-regression --production-tray
```

This runs the actual `App.Run` / startup path with a generated quiet WAV and isolated settings/logs under `artifacts`. It checks Start, explicit hide, idle resume, tray Open/Stop/Exit, and native callback delivery while the UI thread is intentionally stalled. Tagged injected F24 probes go only to the test's own window and remain ignored by the production filter; a separate synthetic non-injected callback checks the playback signal path. It does not claim a physical-keyboard end-to-end test or record typed text.

### Repository layout

```text
BeatzOnKeys.Core/       playback, settings, keyboard hook, updates
BeatzOnKeys.UI/         WPF shell, Frost themes, EN/RU resources, tray lifecycle
BeatzOnKeys.Tests/      non-interactive console test harness
assets/                English and Russian screenshots
release-notes/         one file per release; anything older than 1.3.0 is folded into ARCHIVE-through-1.2.6.md
third-party/           licenses for redistributed/adapted components
publish.bat            self-contained publisher
```

## Troubleshooting

- **No sound while typing** — select a readable track, check the Windows output device, its volume, and its mute state, and raise the maximum **Volume**. BeatzOnKeys multiplies its own gain by the system volume, so a 6% system level silences even 100% typing volume.
- **Too quiet when idle** — raise `MinVolume`; its effective level cannot exceed `MaxVolume`.
- **Unexpected idle pauses** — increase `IdleTimeout` or disable **Pause on idle** for continuous quiet playback.
- **A track will not open** — confirm that the file still exists and try a normal MP3 or PCM WAV file.
- **The track stays at its end** — use Stop → Start, enable Loop, or enable Auto-advance when more tracks exist.
- **An update will not install** — use a writable app folder and inspect `%LOCALAPPDATA%\BeatzOnKeys\logs\update-apply.log`.
- **YouTube will not download** — the built-in search uses yt-dlp. If your provider restricts YouTube (throttling, DPI), downloading may fail. BeatzOnKeys does not bundle any circumvention — that needs administrator rights. We recommend [Zapret2UI](https://github.com/Asterlike/zapret2UI). ⚠️ Circumvention tools are meant for research and educational use. Use them in accordance with the laws of your country.
- **The window disappeared** — Start keeps it open; if you used the title-bar close button, open the notification-area overflow and double-click the BeatzOnKeys icon.
- **A restored source is gone** — add the file again or remove the unavailable entry.

## Credits & License

- **Program channel:** [t.me/beatzonkeys](https://t.me/beatzonkeys) — news and releases.
- **Author:** [@music_cat1](https://t.me/music_cat1).


- **BeatzOnKeys:** [project](https://github.com/MusicCat-61/BeatzOnKeys).
- Design, themes, localization structure, Mica shell, tray lifecycle, and keyboard-hook approach are based on [**MusicCatRGB**](https://github.com/MusicCat-61/MusicCatRGB) by MusicCat161 / MusicCat-61.
- Audio decoding and Windows output use [**NAudio**](https://github.com/naudio/NAudio).
- The YouTube section drives [**yt-dlp**](https://github.com/yt-dlp/yt-dlp) (public domain, Unlicense) as an external process: the binary is downloaded on first use and is not part of any release archive.
- The troubleshooting notes recommend [**Zapret2UI**](https://github.com/Asterlike/zapret2UI); the app does not bundle, download or run it.

BeatzOnKeys is distributed under the [MIT License](<LICENSE>). Required MusicCatRGB and NAudio notices are retained in [THIRD-PARTY-NOTICES.md](<THIRD-PARTY-NOTICES.md>).
