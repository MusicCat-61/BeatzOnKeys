# BeatzOnKeys

[![Release](https://img.shields.io/github/v/release/MusicCat-61/BeatzOnKeys?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Download](https://img.shields.io/badge/download-portable%20win--x64-2ea44f?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases/latest)
[![CI](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/MusicCat-61/BeatzOnKeys/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](<LICENSE>)
[![Version](https://img.shields.io/badge/version-1.5.0.1-4CC2FF?style=flat-square)](https://github.com/MusicCat-61/BeatzOnKeys/releases)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?style=flat-square)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-10-512BD4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)

![BeatzOnKeys start screen](<assets/home-light-en.png>)

**he start screen; [the My files section](<assets/preview-en.png>).T

![BeatzOnKeys English interface](<assets/preview-en.png>)

TEnglish interface · the Russian one is shown on the [Russian page](<README.md>).T

**English** · [Русский](<README.md>)

**BeatzOnKeys is a focus player that follows your rhythm.** Listen to your own music. Work. While you type, the track comes alive. When you stop, it fades. You never switch tracks or reach for pause. The music adapts to your rhythm, not the other way round.

This is not a player with a gimmick; it is a tool for working. It removes the decisions that stand between you and starting. One screen to choose where the music comes from. One button for sound. After that, you just type.

## Who this is for

- **People whom music helps to concentrate, but loud and continuous music gets in the way of thinking.** A quiet bed that rises only while you are working.
- **People with ADHD** who need an outside cue to start and to hold focus: the sound becomes feedback that says "I am here, I am working" — with no numbers, no scores and no guilt.
- **Anyone who listens to YouTube or TikTok while working** and wants the music not to get in the way of thinking.

**BeatzOnKeys does not sell music and needs no subscription.** It works with your files and your links: MP3 and WAV from your computer, tracks from YouTube and sound from TikTok. No account, no catalogue, no payment.

BeatzOnKeys is a tool, not medical care: it is not treatment or therapy, it is a way to remove unnecessary decisions from the path to work.

## Privacy in two sentences

**BeatzOnKeys does not read text, does not log key presses and does not send data anywhere.** The keyboard hook exists only to know that you are working: the app learns that a key was pressed and nothing else — no character, no window title, no application name. What is stored and where lives in the [Privacy](#privacy) section.

## What is inside

Value first, mechanics second.

| Value | What stands behind it |
|---|---|
| 🎯 **Focus mode** | One tile removes lists, caches, counters, advice plates and settings. What is left is what is playing, the transport and an exit button under the card. The queue never runs dry there: a track ends and the next one starts |
| 🚪 **Start screen** | One question instead of four tabs: where does the music come from today. The greeting follows the time of day |
| 🎹 **Music that knows when you work** | Volume follows your typing: it rises on a key press and falls when you stop. Shift, Ctrl and Alt alone do not count |
| ⏸ **Idle pause** | You looked away and the track froze. The first key brings it back at the same position |
| 🔁 **Never silent** | Next track, repeat one, or loop the list: silence never arrives by surprise |
| 🎧 **Continuous playback** | MP3 and PCM WAV play normally; typing controls the volume, not the position |
| 🌗 **Frost themes** | Dark and light MusicCatRGB-inspired palettes switch without a restart |
| 🌐 **Two languages** | English and Russian switch instantly; first launch follows the system language |
| 🪟 **Mica window** | Native Windows 11 backdrop, custom frameless title bar, and a solid Windows 10 fallback |
| 💾 **Saved preferences** | Playlist and selection in `library.json`, volume levels, idle options, language, theme, auto-advance and loop |
| 🥁 **Music-reactive extras** | Punch attack on transients, bass boost, stereo pulse, and a quiet level that follows the track |
| 🎛 **Playlist controls** | Move a track up or down, remove one, or clear the whole list without touching the files |
| 🔕 **Tray mode** | The window hides only on an explicit press; a dialog now explains where it went |
| 🗒 **Logs and updates** | Daily UTF-8 diagnostics, quiet update checks, and a verified in-place updater |
| ▶️ **YouTube as a music source** | Its own section: search by name, direct links, a cache in `%LOCALAPPDATA%\BeatzOnKeys\Music` and a disk-usage figure |
| 🎵 **TikTok as a music source** | A link to a clip and the audio lands in `Music\TikTok`, with its own cache, list and cancel button |
| 📖 **Built-in guide** | One window explains every button, slider, switch, tray action and path — open it with F1 |

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

No installer, administrator rights, or separate .NET runtime are required. **Upgrading from 1.0.x:** close the old app, download 1.5.0.1 manually, and replace the old folder rather than using the old in-app installer. Existing settings migrate automatically as described [above](#settings).

### Requirements

- Windows 10 x64 or Windows 11 x64.
- A working Windows audio output device.
- At least one readable MP3 file; PCM WAV is optional.

## Usage

1. On the start screen, choose a source: **My files**, **YouTube** or **TikTok**. The greeting at the top follows the time of day, and the counters on the tiles say how much music is already waiting.
2. In **My files**, drag MP3 or PCM WAV files onto the drop area, or use **Add files**; select a track. The arrows in a row reorder the playlist and **Clear all** empties it.
3. Set the quiet and maximum volume levels, idle timeout, and **Pause on idle**. Choose **Auto-advance** and **Loop** as needed.
4. Click **Start**. The track starts immediately from the beginning and the window stays open.
5. Type in any application to raise the volume; stop typing to lower it. If idle pausing is enabled, the first key after a pause resumes the track at the same position.
6. Press the **Focus** tile on the start screen when everything except the music should leave the screen. The way out is **Leave focus mode**, a button directly under the track card; it returns to the start screen. The **Home** button is present in every section except focus mode itself, which has its own exit.
7. Use the title-bar close button to hide the window to the tray. Double-click the tray icon to reopen it, or use **Stop playback** / **Exit** in its menu.

**Reset** removes imported entries and saved settings. It never deletes the original audio files.

**Focus mode is a view, not a setting.** It writes nothing to `settings.json`: auto-advance and loop are switched on in the controller's memory only, and leaving the mode hands back exactly the switches you chose yourself. The queue never runs dry there: a track ends and the next one from that section starts. Leaving focus mode goes back to the **start screen**, not into a section: the mode begins with choosing a source and ends there too.

**The queue belongs to the source.** A track started on YouTube advances to the next YouTube track, not into the local playlist; **Loop** wraps inside that section, and opening another section never stops what is playing. A preview never joins the queue.

**The window remembers its size**: it reopens at the size you dragged it to. The size only ever grows — a temporarily smaller window does not become permanent, and a maximised window is not recorded as a size.

## YouTube

**Your YouTube as a music source for focus.** The YouTube tile on the start screen opens a section of its own: tracks from YouTube live there and **never mix with the local playlist**. This is not "one more feature" — it is a second way to get music, next to files from your computer and TikTok.

- **Search.** Type a name and press **Search** for up to ten results; the **Download** button on a row saves that track.
- **Filter in the downloaded list.** The field above the list searches the tracks you already saved, and its cross clears the field. The filter only hides rows: the counter, clearing and the queue keep working on the full list.
- **Direct link.** Paste `https://youtube.com/watch?v=…` and press **Download** — the app recognises the link and fetches it without searching.
- **Where the files live.** `%LOCALAPPDATA%\BeatzOnKeys\Music`, which survives a reinstall. **Open the music folder** launches Explorer there.
- **How much is used.** The YouTube cache block shows the size in KB, MB or GB.
- **Clearing.** **Clear the YouTube cache** deletes the files after a confirmation and never touches the local playlist.
- **Caching.** The **Cache tracks** checkbox turns accumulation off.
- **yt-dlp.** Downloads go through `yt-dlp.exe`, which is **not** fetched at startup: 22.5 MB arrive on the first action with a progress bar, and **Download/Update yt-dlp** repeats the download.

Limits: one link at a time, playlists and live streams are refused with an explanation, at most two downloads run at once, and each is capped at ten minutes.

The format is M4A (AAC), decoded by Media Foundation. On Windows without that component (the "N" editions) the app converts to WAV through an `ffmpeg.exe` placed in `%LOCALAPPDATA%\BeatzOnKeys\bin`. ffmpeg is never downloaded automatically: the compact builds cannot be redistributed, and the properly licensed ones are larger than the application itself.
## TikTok

**TikTok is another music source.** The TikTok tile on the start screen opens a section with one field and one button. Paste a link to a clip and press **Download** — the audio track lands in `Music\TikTok` and joins the queue of that source.

- **Short links.** `vm.tiktok.com/…` and `vt.tiktok.com/…` work: the program follows the short link to the real address of the clip first and only then fetches the audio. Pasting the same link again does not add a second copy.
- **Cancel.** While a download runs, **Cancel** appears next to the field and stops exactly that download.
- **Its own cache.** TikTok files sit apart from the YouTube cache, the counter counts only them, and **Clear TikTok cache** removes only them.
- **Its own list.** Downloaded from TikTok is a separate collection, not the Local playlist: counters and clearing are never shared.
- **Filter and order.** The field above the list filters the downloaded tracks and its cross clears it. A row can be dragged to change the order, and the pencil in a row renames the track: `Enter` confirms, `Esc` cancels. Only the name in the list changes — the file on disk keeps its own name.
- **When TikTok refuses the clip.** The last attempt is the relay `tikwm.com`: the clip link leaves for that third-party service, which returns a direct address of the track, and the file is downloaded from TikTok's own servers. The service allows one request per second, so the program waits and retries once.
- **No ffmpeg.** When TikTok serves a video container, the audio is unwrapped with the Media Foundation decoder and stored as WAV.

There is no search by words here: TikTok does not hand out a list for a query without an account. No cookies, tokens or sign-in are used — only what TikTok serves openly. Private, deleted and sign-in-only clips are refused with an explanation.

## Languages and themes

The title-bar `EN | RU` switch changes the language immediately; the sun/moon button changes the light/dark palette. Both choices are saved.

![BeatzOnKeys light theme](<assets/preview-light-en.png>)

[About dialog](<assets/about-ru.png>) · [Light About dialog](<assets/about-light-ru.png>) · [Guide, English](<assets/guide-en.png>) · [Guide, Russian](<assets/guide-ru.png>)

Focus mode: [just the player](<assets/focus-en.png>)

YouTube source: [search with listening](<assets/youtube-search-ru.png>) · [downloaded tracks](<assets/youtube-downloaded-ru.png>) · [search in progress](<assets/youtube-searching-ru.png>)

Settings sections: [Volume](<assets/section-volume.png>) · [Music reaction](<assets/section-reaction.png>) · [Behaviour](<assets/section-behavior.png>) · [all collapsed](<assets/section-collapsed.png>)

TikTok source: [empty section](<assets/tiktok-empty-ru.png>) · [a downloaded clip](<assets/tiktok-track-ru.png>) · [a download with cancel](<assets/tiktok-cancel-ru.png>)

Matching documentation is available in [English](<README.en.md>) and [Russian](<README.md>).

## Privacy

The keyboard hook follows the privacy-preserving approach used by MusicCatRGB:

- it does **not** convert key codes into text;
- it does **not** inspect the focused window, clipboard, or text fields;
- it does **not** log keys or application names;
- injected events and bare Shift/Ctrl/Alt presses are ignored; the existing hook also ignores CapsLock and Windows keys;
- the callback only signals keyboard activity and returns immediately; audio work stays on the playback side.

The hook is active only after **Start** and is removed on **Stop** or exit. It runs in a **separate headless watcher process** (`BeatzOnKeys.exe --watcher`, the same approach as MusicCatRGB), so hiding, minimising, or a busy window cannot delay or silence typing. The watcher reports readiness and real key deliveries to the app, and a dropped connection is logged instead of failing silently. Keep the application folder together: the watcher is the same executable.

**Network.** Nothing leaves the machine except what your own action requires: searches and downloads go to YouTube, TikTok and GitHub (updates). When TikTok refuses a clip, the last attempt uses the third-party service `tikwm.com`, and the clip link is sent there; the file itself is downloaded from TikTok's servers. There is no telemetry and no reporting.

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

Create the **1.5.0.1** portable self-contained folder in Windows **cmd**:

```bat
set BOK_VERSION=1.5.0.1
publish.bat
```

The publisher now defaults to `BOK_VERSION=1.5.0.1`; setting it explicitly makes the intended release version clear.

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

## What changed in 1.5

- **The app is presented as a focus player.** The headline, the opening paragraph, the "Who this is for" section and the source descriptions are rewritten around the job rather than the mechanism: value first, mechanics second.
- **A start screen instead of tabs.** One question — where does the music come from today — plus source tiles with counters. The greeting follows the time of day. Every section carries a **Home** button.
- **Focus mode.** One button removes lists, caches, counters, advice plates and settings; the track and the transport stay. The queue never runs dry there, and no setting is touched: leaving the mode hands your switches back.
- **Notifications became dialogs.** Hiding to the tray and the low-system-volume warning are shown as windows instead of Windows balloons: a balloon is easy to miss and can be switched off entirely.
- **Larger type.** The smallest text is raised from 10.5–12 to 12.5 px, body text to 15–15.5, headings to 16–17.
- **The README is rewritten** for the new positioning: "Who this is for", privacy near the top, and music sources described as sources rather than features.

BeatzOnKeys is a tool, not medical care: it is not treatment or therapy.

## What changed in 1.5.0.1

- **Fields at the top, lists stretched.** The search field and the link field sit at the top of their section, the found list takes the free height, and the kept tracks are a compact card under it. The right-hand block is gone from TikTok: the list uses the full width.
- **Continue remembers the source.** The card on the start screen used to offer a local file even when you had stopped a TikTok track: the selection only exists for the local playlist. The last track that actually **played** is remembered together with its section, and the card names it.
- **Leaving focus mode goes home**, not into a section.
- **Every line follows the language.** The Russian interface showed `My files` under the Continue card, and the English one kept a Russian sentence under the cache checkbox: strings built from localisation keys were not rebuilt on a language switch. All of them are now.
- **The English README is repaired.** It carried `TT` 277 times where `**` belonged, so bold text was broken across the whole file.
