# Release notes archive: 1.0.1 — 1.2.6.1

Earlier per-release notes, folded into one file. Each version also published its own GitHub release;
this archive exists so the folder keeps only the recent history without losing the old text.

---

## v1.0.1

## English

BeatzOnKeys 1.0.1 introduces direct audio scrubbing built around the original source track.

### Highlights

- Fixed unreadable headings in the dark About dialog, icon/text alignment, and narrow-window layout.
- Removed the design-foundation button from About; required attribution remains in Credits and license notices.
- Update checks now use GitHub release metadata instead of fragile HTML redirects.

- Add MP3 tracks directly; PCM WAV is available as an additional format.
- Every non-modifier key advances the cursor by the configured step and plays a short audio slice.
- Step is configurable from `0.05` to `0.50` seconds and defaults to `0.15` seconds.
- Fast typing sounds almost continuous, while a pause keeps the in-session cursor ready for the next press.
- Auto-advance and Loop now have explicit combined behavior:
  - both off: remain at the end; change tracks, enable Loop, or use Stop → Start to restart;
  - Loop only: repeat the current track;
  - Auto-advance only: continue through the list and stop after the final track;
  - both on: loop the complete track list.
- Volume is normalized to `0.0`–`1.0`.
- Tracks, selected track, volume, step, Auto-advance, Loop, language, and theme are stored under `%LOCALAPPDATA%\BeatzOnKeys`.
- The current fragment position remains runtime state and is not promised to survive an application restart.
- The portable Windows x64 package is self-contained on .NET 10 and no longer carries unrelated processing runtimes.
- Logs use the focused `[audio]`, `[playback]`, `[settings]`, `[update]`, and `[ui]` categories.

### Install or update

Download `BeatzOnKeys-win-x64.zip`, extract the complete archive to a writable folder, and run `BeatzOnKeys.exe`. The matching `.sha256` file can be used to verify the archive.

For 1.0.0 users, close the old app and extract 1.0.1 into a new folder using a manual download. Existing per-user preferences and playlist are migrated automatically; the old in-app installer is not compatible with this redesigned package.

## Русский

BeatzOnKeys 1.0.1 переходит на прямой аудиоскраббинг исходного трека.

### Главное

- Исправлены нечитаемые заголовки тёмного окна «О программе», выравнивание иконок/текста и компоновка узкого окна.
- Кнопка основы дизайна удалена из «О программе»; обязательная атрибуция сохранена в Credits и лицензиях.
- Проверка обновлений читает данные релиза GitHub вместо ненадёжных HTML-перенаправлений.

- MP3 добавляется напрямую; PCM WAV доступен как дополнительный формат.
- Каждое нажатие не-модификатора сдвигает курсор на выбранный шаг и проигрывает короткий фрагмент.
- Шаг настраивается от `0,05` до `0,50` секунды, значение по умолчанию — `0,15` секунды.
- Быстрый набор звучит почти непрерывно, а пауза сохраняет курсор текущего запуска до следующего нажатия.
- Автопереход и повтор получили однозначное совместное поведение:
  - оба выключены: оставаться в конце до повторного выбора трека;
  - только повтор: повторять текущий трек;
  - только автопереход: идти по списку и остановиться после последнего трека;
  - оба включены: зациклить весь список треков.
- Громкость нормализуется в диапазон `0,0`–`1,0`.
- Треки, выбранный трек, громкость, шаг, автопереход, повтор, язык и тема хранятся в `%LOCALAPPDATA%\BeatzOnKeys`.
- Позиция текущего фрагмента остаётся состоянием в памяти и не обещается к восстановлению после перезапуска приложения.
- Портативный пакет Windows x64 самодостаточен на .NET 10 и больше не содержит посторонних сред обработки.
- Для логов используются категории `[audio]`, `[playback]`, `[settings]`, `[update]` и `[ui]`.

### Установка или обновление

Скачайте `BeatzOnKeys-win-x64.zip`, распакуйте весь архив в доступную для записи папку и запустите `BeatzOnKeys.exe`. Архив можно проверить по соседнему файлу `.sha256`.

Если у вас 1.0.0, закройте её и распакуйте 1.0.1 в новую папку после ручного скачивания. Настройки и список треков мигрируют автоматически; встроенный установщик старой версии несовместим с новым составом пакета.

---

## v1.0.2

## English

BeatzOnKeys is now a continuous audio player: the track gets louder while you type and quieter when you stop. Idle pausing is enabled by default.

### Playback and settings

- **Start** begins the selected MP3 or PCM WAV immediately at `0:00`.
- Any key other than Shift, Ctrl, or Alt raises volume from `MinVolume` to `MaxVolume` over **50 ms**. After **150 ms** without such keys, volume falls to the quiet level over **200 ms**.
- `MinVolume`: **0–50%**, default **20%**. `MaxVolume` (Volume): **0–100%**, default **100%**. The effective quiet level never exceeds `MaxVolume`.
- `IdleTimeout`: **1–10 seconds**, default **3 seconds**. `PauseOnIdle`: **true** by default.
- With **Pause on idle** enabled, inactivity longer than the timeout pauses playback; the first qualifying key resumes at **the same position and instantly at `MaxVolume`**. With it disabled, the track keeps playing at the quiet level during inactivity.
- **Stop → Start always restarts from the beginning**; an idle pause/resume preserves the current position. Playback position is not persisted across application restarts.
- **Auto-advance** and **Loop** retain their behavior at the natural end: both off stays at the end; Loop alone repeats the track; Auto-advance alone plays the list once; both on loop the list.
- Settings use **schema 3** in `%LOCALAPPDATA%\BeatzOnKeys\settings.json`: `MinVolume`, `MaxVolume`, `IdleTimeout`, `PauseOnIdle`, `AutoAdvance`, `Loop`, `SelectedTrackId`, `Language`, `Theme`, `Tracks`.
- JSON volumes are fractions in `0..1` (`MinVolume` is limited to `0.5`); `IdleTimeout` is in seconds. Legacy `Volume` automatically migrates to `MaxVolume`; the other new fields use their defaults.

### Install or update

Download `BeatzOnKeys-win-x64.zip`, extract the whole archive to a writable folder, and run `BeatzOnKeys.exe`. The portable Windows x64 package is self-contained on .NET 10. Existing preferences and playlist migrate automatically. From 1.0.0, close the old app and manually extract 1.0.2 into a new folder instead of using the old in-app installer.

## Русский

**Программа работает как аудиоплеер. Трек играет непрерывно, но становится громче при печати и затихает, когда ты останавливаешься. Через несколько секунд простоя трек ставится на паузу**.

### Воспроизведение и настройки

- **«Старт»** сразу начинает выбранный MP3 или PCM WAV с `0:00`.
- Любая клавиша, кроме Shift, Ctrl и Alt, повышает громкость от `MinVolume` до `MaxVolume` за **50 мс**. После **150 мс** без таких нажатий громкость снижается до тихого уровня за **200 мс**.
- `MinVolume`: **0–50%**, по умолчанию **20%**. `MaxVolume` («Громкость»): **0–100%**, по умолчанию **100%**. Фактическая тихая громкость никогда не превышает `MaxVolume`.
- `IdleTimeout`: **1–10 секунд**, по умолчанию **3 секунды**. `PauseOnIdle`: **true** по умолчанию.
- С включённым чекбоксом **«Ставить на паузу при простое»** отсутствие нажатий дольше таймаута ставит трек на паузу; первая подходящая клавиша продолжает **с той же позиции и мгновенно на `MaxVolume`**. С выключенным чекбоксом трек продолжает играть на тихом уровне в простое.
- **«Стоп» → «Старт» всегда начинает сначала**, а пауза/возобновление при простое сохраняет текущую позицию. После перезапуска приложения позиция не восстанавливается.
- **Автоперелистывание треков** и **Зацикливание** срабатывают в естественном конце: оба выключены — остаться в конце; только повтор — повторять трек; только автопереход — один проход по списку; оба включены — зациклить список.
- Настройки используют **схему 3** в `%LOCALAPPDATA%\BeatzOnKeys\settings.json`: `MinVolume`, `MaxVolume`, `IdleTimeout`, `PauseOnIdle`, `AutoAdvance`, `Loop`, `SelectedTrackId`, `Language`, `Theme`, `Tracks`.
- В JSON громкости — доли `0..1` (`MinVolume` ограничен `0.5`), `IdleTimeout` — секунды. Старое `Volume` автоматически переносится в `MaxVolume`; остальные новые поля получают значения по умолчанию.

### Установка или обновление

Скачайте `BeatzOnKeys-win-x64.zip`, распакуйте весь архив в доступную для записи папку и запустите `BeatzOnKeys.exe`. Портативный Windows x64 пакет самодостаточен на .NET 10. Настройки и список треков переносятся автоматически. При переходе с 1.0.0 закройте старую программу и вручную распакуйте 1.0.2 в новую папку, не используя прежний встроенный установщик.

The following 1.0.1 notes are historical; current playback behavior is documented above.
Записи 1.0.1 ниже сохранены как история; актуальное поведение описано выше.

---

## v1.0.3

## English

BeatzOnKeys 1.0.3 fixes the confusing impression that the application has crashed after playback starts.

### Window behavior

- **Start now keeps the main window visible** while playback and global typing detection run.
- The title-bar close button still explicitly hides the window to the notification area; double-click the tray icon to restore it.
- The global keyboard hook now owns a dedicated message-loop thread, so UI stalls and audio-device initialization cannot block its input dispatch. Hidden-window playback uses the same worker as visible playback.
- Startup, hide/restore, and exit are logged with the process ID to distinguish a tray session from a terminated process.
- Idle pausing is unchanged: with the default setting, three seconds without typing pauses the track, and the next qualifying key resumes it at the same position.
- Existing settings and playlists are preserved.

### Install or update

Download `BeatzOnKeys-win-x64.zip`, extract the whole archive to a writable folder, and run `BeatzOnKeys.exe`. The portable Windows x64 package is self-contained on .NET 10.

## Русский

BeatzOnKeys 1.0.3 устраняет поведение, из-за которого после запуска музыки казалось, что приложение аварийно закрылось.

### Поведение окна

- После **«Старт» главное окно остаётся открытым**, пока работают воспроизведение и глобальное отслеживание набора.
- Кнопка закрытия в заголовке по-прежнему явно скрывает окно в область уведомлений; двойной щелчок по значку возвращает его.
- Глобальный перехватчик клавиатуры теперь работает на отдельном потоке обработки сообщений: задержки интерфейса и инициализация аудиоустройства не блокируют доставку клавиш. При скрытом окне используется тот же фоновый обработчик воспроизведения.
- Запуск, скрытие/восстановление окна и выход записываются с идентификатором процесса, чтобы отличить работу в трее от завершения приложения.
- Пауза при простое не изменилась: по умолчанию три секунды без набора ставят трек на паузу, а следующее подходящее нажатие продолжает с той же позиции.
- Существующие настройки и список треков сохраняются.

### Установка или обновление

Скачайте `BeatzOnKeys-win-x64.zip`, распакуйте весь архив в доступную для записи папку и запустите `BeatzOnKeys.exe`. Портативный Windows x64 пакет самодостаточен на .NET 10.

---

## v1.0.4

## English

1.0.4 fixes typing not reaching playback while the window is hidden or minimised.

### Keyboard and tray

- The global keyboard hook now runs in its **own headless watcher process** (BeatzOnKeys.exe --watcher), the same design the MusicCatRGB project uses. Hiding, minimising, or a busy UI can no longer delay or silence typing.
- The watcher reports to the app when the native hook is live and when a key actually arrives, so a dead hook is visible in the log instead of failing silently.
- Closing the window with the title-bar X keeps music playing and typing working; the notification icon shows a hint and its menu restores the window or exits.
- Minimising with the dash no longer affects playback or keyboard volume control.
- Logging failures are no longer swallowed: the app falls back to %TEMP%\BeatzOnKeys and reports the reason.

### Install or update

Download BeatzOnKeys-win-x64.zip, extract the whole archive to a writable folder, and run BeatzOnKeys.exe. Keep the folder together: the watcher is the same executable.

## Русский

1.0.4 исправляет главное: набор текста снова управляет музыкой, когда окно скрыто или свёрнуто.

### Клавиатура и трей

- Глобальный перехват клавиатуры теперь работает в **отдельном безоконном процессе-наблюдателе** (BeatzOnKeys.exe --watcher) — по той же схеме, что и в MusicCatRGB. Скрытие, сворачивание и занятый интерфейс больше не задерживают и не отключают набор.
- Наблюдатель сообщает приложению, что перехват установлен и что клавиша действительно пришла; мёртвый перехват виден в журнале, а не теряется молча.
- Закрытие крестиком оставляет музыку и набор работающими; значок в области уведомлений показывает подсказку, а его меню возвращает окно или завершает приложение.
- Сворачивание кнопкой-прочерком больше не влияет на воспроизведение и управление громкостью.
- Ошибки записи журнала больше не скрываются: приложение пишет в %TEMP%\BeatzOnKeys и сообщает причину.

### Установка или обновление

Скачайте BeatzOnKeys-win-x64.zip, распакуйте весь архив в доступную для записи папку и запустите BeatzOnKeys.exe. Папку нужно сохранять целиком: наблюдатель — этот же исполняемый файл.

---

## v1.1.0

## English

1.1.0 is a single executable and fixes typing that stopped reaching playback.

### One file, one instance

- The release is now a **single BeatzOnKeys.exe** (~72 MB) instead of 492 files. Nothing else has to sit next to it.
- **Only one copy can run.** Launching it again restores the window of the running copy instead of starting a second hook and a second audio player.

### Typing and the tray

- The native hook callback no longer waits for the pipe to the app. A slow or busy app used to stall that callback, and Windows then removes the hook for the whole desktop — typing worked while the window was focused and died as soon as focus moved. The hook now hands off through a bounded queue and returns immediately.
- Closing with the X hides to the notification area and keeps music and typing alive, matching MusicCatRGB; the window is restored from the tray icon or by launching the app again.
- The tray icon now comes from the executable itself, so it is correct even in the single-file build.

### Install or update

Download BeatzOnKeys.exe, put it anywhere writable, and run it.

## Русский

1.1.0 — это один исполняемый файл, и она исправляет главное: набор перестал доходить до музыки.

### Один файл, один экземпляр

- Релиз теперь **один BeatzOnKeys.exe** (~72 МБ) вместо 492 файлов. Рядом ничего не нужно.
- **Запустить можно только одну копию.** Повторный запуск поднимает окно уже работающей копии, а не создаёт второй перехват и второй плеер.

### Набор и трей

- Колбэк перехвата больше не ждёт записи в канал приложения. Раньше занятое приложение могло задержать этот колбэк, и Windows снимала перехват для всего рабочего стола — поэтому набор работал при активном окне и умирал при смене фокуса. Теперь передача идёт через ограниченную очередь, а колбэк возвращается сразу.
- Крестик скрывает окно в область уведомлений и оставляет музыку и набор работающими — как в MusicCatRGB; окно возвращается значком в трее или повторным запуском.
- Значок трея берётся из самого исполняемого файла, поэтому корректен и в single-file сборке.

### Установка или обновление

Скачайте BeatzOnKeys.exe, положите в доступную для записи папку и запустите.

---

## v1.1.1

## English

1.1.1 makes closing to the tray behave like MusicCatRGB and removes the stock Windows dialogs.

- The close button now hides the window and keeps playback and typing alive, and the tray menu restores it. Shutdown no longer closes the window first, so a later "show" can never fail with "cannot set Visibility after a Window has been closed".
- **Reset** and other in-app messages use a dialog drawn in the application's own theme instead of the stock Windows message box.
- Hiding to the tray shows a short balloon explaining where the window went.
- Launching the app again restores the running copy instead of starting a second one.

## Русский

1.1.1 приводит закрытие в трей к поведению MusicCatRGB и убирает стандартные окна Windows.

- Крестик скрывает окно, музыка и набор продолжают работать, а меню значка возвращает окно. Выход больше не закрывает окно заранее, поэтому повторный показ не может упасть с ошибкой «нельзя задать Visibility после закрытия окна».
- **Сброс** и другие сообщения показываются диалогом в оформлении приложения, а не стандартным окном Windows.
- При скрытии в трей появляется короткая подсказка, куда ушло окно.
- Повторный запуск поднимает уже работающую копию, а не открывает вторую.

---

## v1.1.2

## English

1.1.2 adds an in-app guide and makes the two failures that look like a broken player visible; Start also stops freezing the window.

- **A built-in guide.** The new Guide window explains every button, slider, switch, tray action, status, and data path in the current language. Open it with the help button in the title bar, with **F1**, or from the tray menu.
- **An unwritable settings file is reported.** When the per-user settings cannot be saved, the app now says so once instead of silently losing the playlist and volume levels at the next launch.
- **Inaudible output is explained.** If the Windows output is muted or below 20%, startup says so: at that level even the maximum typing volume is practically silent.
- **Start no longer blocks the window.** Installing the keyboard watcher waits up to five seconds for it to report readiness; that handshake now runs in the background instead of on the UI thread.

## Русский

1.1.2 добавляет встроенный гайд и показывает две ошибки, из-за которых приложение кажется сломанным; «Старт» больше не подвешивает окно.

- **Встроенный гайд.** Новое окно «Гайд» описывает каждую кнопку, ползунок, переключатель, действие в трее, статус и путь к данным на текущем языке. Открывается кнопкой со знаком вопроса в заголовке, клавишей **F1** или из меню значка в области уведомлений.
- **Если настройки не записываются, это видно.** Когда файл настроек сохранить не удаётся, приложение сообщает об этом один раз, а не теряет плейлист и уровни громкости молча.
- **Неслышимый вывод объясняется.** Если вывод Windows выключен или его громкость ниже 20%, при запуске появляется подсказка: на таком уровне не слышно даже максимальную громкость набора.
- **«Старт» больше не блокирует окно.** Установка перехватчика клавиатуры ждёт его готовности до пяти секунд; теперь это ожидание идёт в фоне, а не в потоке интерфейса.

---

## v1.2.0

## English

1.2.0 adds playlist management and makes **Reset** gentler.

### Playlist
- **Reorder with the arrows** in each row: the first row cannot move up and the last cannot move down. Auto-advance and looping follow the new order, and a reorder never restarts the track that is playing.
- **Clear all** in the playlist header empties the list after confirmation, stops playback, and saves the empty playlist.
- **Removing the playing track** stops playback and selects the neighbour that takes its place; removing any other track leaves playback running.

### Reset
- **Reset no longer changes your language or your theme.** It still returns the playlist and the playback settings — volumes, idle timeout, pause on idle, auto-advance, and loop — to their defaults, and it never touches the audio files.

## Русский

1.2.0 добавляет управление плейлистом и делает **«Сброс»** мягче.

### Плейлист
- **Перестановка стрелками** в строке трека: первая строка не двигается вверх, последняя — вниз. Автоперелистывание и зацикливание идут по новому порядку, а играющий трек при перестановке не перезапускается.
- **«Очистить всё»** в шапке списка очищает плейлист после подтверждения, останавливает воспроизведение и сохраняет пустой список.
- **Удаление играющего трека** останавливает воспроизведение и выбирает соседний, который встал на его место; удаление любого другого трека не прерывает музыку.

### Сброс
- **«Сброс» больше не меняет язык и тему.** Он по-прежнему возвращает плейлист и настройки воспроизведения — громкости, таймаут простоя, паузу при простое, автоперелистывание и повтор — к значениям по умолчанию и никогда не трогает аудиофайлы.

---

## v1.2.1

## English

1.2.1 makes the typing music react to the track itself: punch, bass, stereo and the track's own loudness.

### Music reaction
- **Punch response.** The player measures every buffer; when a drum hit just landed, the next key reaches full volume in 15 ms instead of 50 ms, so the flashes follow the rhythm. On by default.
- **Bass boost.** A low-pass estimate of 40–120 Hz adds up to 30% gain while bass and kick play, smoothed so a kick cannot click and capped by the maximum volume.
- **Stereo pulse.** Each key press shifts the balance 10% to one side for 50 ms, alternating sides. Off by default.
- **Adaptive threshold.** The quiet level follows the track: below RMS 0.1 it becomes 10%, above 0.5 it becomes 30%, and an average track keeps your own setting. The slider itself keeps its value.

Settings schema is now 4; a schema 3 file loads with the new defaults. All four controls sit in the **Music reaction** block of the settings card, and the in-app guide documents them.

## Русский

1.2.1 заставляет музыку реагировать на сам трек: удар, бас, стерео и собственную громкость записи.

### Реакция на музыку
- **Ударный отклик.** Плеер измеряет каждый буфер: если только что прозвучал удар, следующее нажатие выходит на полную громкость за 15 мс вместо 50 мс, поэтому вспышки совпадают с ритмом. Включён по умолчанию.
- **Усиление на бас.** Низкочастотная оценка 40–120 Гц добавляет до 30% громкости, пока звучит бас и бочка; сглажено, чтобы удар не щёлкал, и ограничено максимальной громкостью.
- **Стерео-пульс.** Каждое нажатие на 50 мс сдвигает баланс на 10% в одну сторону, чередуя стороны. По умолчанию выключен.
- **Адаптивный порог.** Тихий уровень следует за треком: ниже RMS 0,1 он становится 10%, выше 0,5 — 30%, средний трек оставляет ваше значение. Ползунок при этом сохраняет своё значение.

Схема настроек теперь 4; файл схемы 3 загружается с новыми значениями по умолчанию. Все четыре элемента управления находятся в блоке **«Реакция на музыку»** карточки настроек и описаны во встроенном гайде.

---

## v1.2.2

## English

1.2.2 reorganises the settings panel and makes update downloads survive a blocked folder.

### Settings panel
- **Section chips.** Three chips above the settings — Volume, Music reaction, Behaviour — open one section and close the others, so the panel always fits without scrolling.
- **Collapsible sections.** Click a header to fold it into a single line; ⌄ means expanded, › means collapsed. Several headers can still be open at once.
- **Numbered circle.** A collapsed section shows how many settings it holds — 4, 4, and 2 — so it is obvious that nothing is missing.
- **Live summary.** A collapsed section keeps its current values in the header, for example `100% · 20% · 3 s` or `bass 15% · stereo off`. It updates as soon as a slider moves.
- Hints in the settings panel were shortened in both languages, and the font of message dialogs grew by two pixels.

### Updates
- **Writable-folder fallback.** Before downloading, the app now probes both the per-user update folder and a temporary one. If the profile folder refuses writes — a restricted token, a broken profile ACL — the update is staged in the temporary folder and the About window says so.
- **Clear message instead of a raw error.** When no folder accepts the download, the About window explains the permission problem and lists the paths it tried, instead of showing `Access is denied`.

The in-app guide gained a **Settings panel** section describing the chips, collapsing, the counter, and the summary, plus a troubleshooting entry for a download that will not start.

## Русский

1.2.2 переделывает панель настроек и делает скачивание обновлений устойчивым к недоступной папке.

### Панель настроек
- **Чипы разделов.** Три чипа над настройками — «Громкость», «Реакция на музыку», «Поведение» — открывают один раздел и закрывают остальные, поэтому панель всегда помещается без прокрутки.
- **Сворачиваемые разделы.** Нажатие на заголовок сворачивает его в одну строку; ⌄ значит «развёрнут», › — «свёрнут». Несколько заголовков можно держать открытыми одновременно.
- **Кружок с цифрой.** У свёрнутого раздела видно, сколько в нём настроек — 4, 4 и 2 — так что понятно, что ничего не пропало.
- **Живая сводка.** Свёрнутый раздел держит текущие значения в заголовке, например `100% · 20% · 3 с` или `бас 15% · стерео выкл.`. Обновляется сразу при движении ползунка.
- Подсказки в панели настроек сокращены на обоих языках, а шрифт диалоговых окон увеличен на два пикселя.

### Обновления
- **Запасная папка.** Перед скачиванием приложение проверяет и папку обновлений в профиле, и временную. Если папка профиля не принимает запись — ограниченный токен, сломанные права — обновление сохраняется во временную папку, и окно «О программе» об этом сообщает.
- **Понятное сообщение вместо сырой ошибки.** Если запись не удаётся никуда, окно «О программе» объясняет проблему с правами и перечисляет проверенные пути, вместо `Access is denied`.

Во встроенном гайде появился раздел **«Панель настроек»** — про чипы, сворачивание, счётчик и сводку, — а также пункт про случай, когда обновление не скачивается.

---

## v1.2.3

## English

1.2.3 rebuilds the updater the way MusicCatRGB does it: the update is the executable itself, replaced in place after the app exits.

### How updating works now
- **The update is the executable.** The updater downloads `BeatzOnKeys.exe` — the same portable single-file build — and parks it next to the running application as `BeatzOnKeys.update.exe`. There is no archive to unpack and no payload folder any more.
- **Four checks before the swap.** The download must match its SHA-256, match the size the release advertises, start with a real `MZ` executable header (so a proxy error page saved as `.exe` is rejected), and carry a file version that is not older than the running one. Anything that fails is deleted immediately.
- **The swap waits for the app to exit.** Windows keeps a running executable locked, so a detached script does the work: it retries `move /Y` for 30 seconds, ends the process if it is still holding the file, retries 15 more times, then falls back to `copy /Y /B` — and finally verifies the size of the file it placed.
- **The app comes back on its own** after a successful or a failed swap.
- **Nothing is silent.** The script writes every step, and `RESULT=OK` or `RESULT=FAILED`, into `update-apply.log`. The next start reads that report and shows a notice when an update failed, then deletes the script and the log.
- **No downgrades.** A staged build that is not newer than the running one is deleted instead of being offered forever.
- **Paths are safe.** The script is pure ASCII and receives every path through environment variables, because `cmd.exe` reads a `.cmd` file in the OEM code page — a profile folder like `C:\Users\Маksim` would otherwise be mangled.

The release still ships the zip for manual installs; the in-app updater just no longer needs it.

### Release notes
Every version now has its own notes file with a table of the files attached to that release.

## Русский

1.2.3 переделывает апдейтер так же, как это сделано в MusicCatRGB: обновление — это сам исполняемый файл, который подменяется на месте после выхода приложения.

### Как теперь работает обновление
- **Обновление — это исполняемый файл.** Апдейтер скачивает `BeatzOnKeys.exe` — ту же портативную сборку одним файлом — и кладёт рядом с работающим приложением под именем `BeatzOnKeys.update.exe`. Никакого архива для распаковки и никакой папки payload больше нет.
- **Четыре проверки до подмены.** Скачанный файл должен совпасть по SHA-256, совпасть по размеру с заявленным в релизе, начинаться с настоящего заголовка `MZ` (так отсекается страница ошибки прокси, сохранённая как `.exe`) и иметь версию не старее работающей. Всё, что не прошло, сразу удаляется.
- **Подмена ждёт выхода приложения.** Windows держит запущенный exe заблокированным, поэтому работой занимается отдельный скрипт: он повторяет `move /Y` 30 секунд, при необходимости завершает процесс, держащий файл, повторяет ещё 15 раз, затем пробует `copy /Y /B` — и в конце сверяет размер положенного файла.
- **Приложение возвращается само** и после успешной подмены, и после неудачной.
- **Ничего не происходит молча.** Скрипт пишет каждый шаг и строку `RESULT=OK` или `RESULT=FAILED` в `update-apply.log`. Следующий запуск читает этот отчёт, показывает уведомление, если обновление не удалось, и удаляет скрипт вместе с логом.
- **Без откатов.** Подготовленная сборка, которая не новее работающей, удаляется, а не предлагается бесконечно.
- **Пути в безопасности.** Скрипт состоит только из ASCII и получает все пути через переменные окружения, потому что `cmd.exe` читает `.cmd` в OEM-кодировке — иначе путь вида `C:\Users\Маksim` превратился бы в мусор.

В релизе по-прежнему есть zip для ручной установки; встроенному апдейтеру он больше не нужен.

### Примечания к релизу
У каждой версии теперь свой файл заметок с таблицей приложенных к релизу файлов.

## Assets

Every file for this version is attached to the release:

| File | What it is |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys.exe) | Portable single-file build — **this is what the in-app updater downloads** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys.exe.sha256) | SHA-256 of the executable |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys-win-x64.zip) | The same build as a folder archive, for a manual install |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 of the archive |

## Ассеты

Все файлы этой версии прикреплены к релизу:

| Файл | Что это |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys.exe) | Портативная сборка одним файлом — **именно её скачивает встроенный апдейтер** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys.exe.sha256) | SHA-256 исполняемого файла |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys-win-x64.zip) | Та же сборка папкой, для ручной установки |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.3/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 архива |

---

## v1.2.4

## English

1.2.4 adds a pause that actually keeps your place.

### Pause and Resume
- **The track does not jump back to 0:00.** Pause silences the output without touching the decoder, so Resume continues from the exact sample it stopped at. Only **Start** begins a track from the beginning, as before.
- **Typing does not lift it.** An explicit pause is your decision, so a key press no longer quietly resumes playback — that behaviour belongs to the idle pause, which still works exactly as it did.
- **It is a real pause, not a stop.** The keyboard hook, the worker and the decoder all stay alive, so the position, the playback order and the selected track are untouched. The idle timer is suspended while you are paused and re-armed on resume.
- **The button appears only while playing**, in the transport row between Reset and Stop, and its label switches between **Pause** and **Resume**. The status line says the track is standing still.

### Also
- The in-app guide documents the new button and how it differs from the idle pause.

## Русский

1.2.4 добавляет паузу, которая действительно запоминает место.

### Пауза и «Продолжить»
- **Трек не откатывается на 0:00.** «Пауза» глушит звук, не трогая декодер, поэтому «Продолжить» возвращается ровно к тому сэмплу, на котором остановились. С начала трек запускает только **«Старт»**, как и раньше.
- **Набор клавиш её не снимает.** Явная пауза — ваше решение, поэтому нажатие клавиши больше не возобновляет воспроизведение тихо: так ведёт себя пауза при простое, и она работает как прежде.
- **Это пауза, а не остановка.** Перехват клавиатуры, рабочий поток и декодер остаются живыми, поэтому позиция, порядок воспроизведения и выбранный трек не меняются. Таймер простоя приостанавливается на время паузы и включается снова при продолжении.
- **Кнопка видна только во время игры** — в строке управления между «Сбросом» и «Стопом», её подпись переключается между **«Пауза»** и **«Продолжить»**. Строка статуса сообщает, что трек стоит на месте.

### Также
- Во встроенном гайде описана новая кнопка и её отличие от паузы при простое.

## Assets

Every file for this version is attached to the release:

| File | What it is |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys.exe) | Portable single-file build — **this is what the in-app updater downloads** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys.exe.sha256) | SHA-256 of the executable |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys-win-x64.zip) | The same build as a folder archive, for a manual install |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 of the archive |

## Ассеты

Все файлы этой версии прикреплены к релизу:

| Файл | Что это |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys.exe) | Портативная сборка одним файлом — **именно её скачивает встроенный апдейтер** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys.exe.sha256) | SHA-256 исполняемого файла |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys-win-x64.zip) | Та же сборка папкой, для ручной установки |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.4/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 архива |

---

## v1.2.5

## English

1.2.5 makes the playlist comfortable to read and to reorder.

### Drag a track to move it
- **Grab a row and drag it.** A thin accent line shows where the row will land: the upper half of a target inserts before it, the lower half after it, and dropping on the empty space below the list moves the track to the end.
- **Playback is not interrupted.** The move goes through the same ordered collection as the arrows, so the playing track keeps running from its position and the playback order follows the new layout.
- **It waits for a settled window.** Dragging is disabled while a start, stop, import or reset is in flight, exactly like the up and down arrows.
- The arrows remain for precise single-step moves and stay disabled at the edges.

### Larger, better-fitting layout
- **The right column is no longer small print.** Every label, value, chip, section title, summary and checkbox in the settings card grew by 1.5–2 points, and the hints by one.
- **The window sizes itself to its content on start.** It measures the settings column at the current font metrics and grows until nothing hides behind a scrollbar, clamped to the screen work area and never below the minimum size. A larger system font therefore no longer arrives half-cut. Resizing by hand still works.
- **The track count is a number, not a sentence.** A rounded badge beside the **Tracks** heading shows just the count, with the full wording kept in its tooltip.

## Русский

1.2.5 делает список треков удобнее для чтения и перестановки.

### Перетаскивание трека
- **Возьмите строку и потяните.** Тонкая акцентная линия показывает, куда встанет трек: верхняя половина строки вставляет перед ней, нижняя — после, а сброс на пустом месте под списком отправляет трек в конец.
- **Воспроизведение не прерывается.** Перестановка идёт через ту же упорядоченную коллекцию, что и стрелки, поэтому играющий трек продолжает звучать со своей позиции, а порядок воспроизведения следует новой раскладке.
- **Работает только в спокойный момент.** Перетаскивание отключено, пока идёт запуск, остановка, импорт или сброс — как и стрелки вверх-вниз.
- Стрелки остаются для точного шага на одну позицию и по-прежнему отключаются на краях.

### Крупнее и по размеру содержимого
- **Правая колонка больше не мелкий шрифт.** Все подписи, значения, чипы, заголовки разделов, сводки и флажки в карточке настроек выросли на 1,5–2 пункта, а подсказки — на один.
- **Окно подстраивает высоту под содержимое при запуске.** Оно измеряет колонку настроек при текущих метриках шрифта и растёт, пока ничего не прячется за полосой прокрутки; размер ограничен рабочей областью экрана и не опускается ниже минимального. Поэтому крупный системный шрифт больше не обрезается. Ручное изменение размера работает как прежде.
- **Количество треков — цифрой, а не фразой.** Круглый бейдж рядом с заголовком **«Треки»** показывает только число, а полная формулировка осталась в подсказке.

## Assets

Every file for this version is attached to the release:

| File | What it is |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys.exe) | Portable single-file build — **this is what the in-app updater downloads** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys.exe.sha256) | SHA-256 of the executable |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys-win-x64.zip) | The same build as a folder archive, for a manual install |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 of the archive |

## Ассеты

Все файлы этой версии прикреплены к релизу:

| Файл | Что это |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys.exe) | Портативная сборка одним файлом — **именно её скачивает встроенный апдейтер** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys.exe.sha256) | SHA-256 исполняемого файла |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys-win-x64.zip) | Та же сборка папкой, для ручной установки |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.5/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 архива |

---

## v1.2.6

## English

1.2.6 fixes a defect that made the new Pause button permanently unusable.

### The Pause button was greyed out

- **What you saw.** The button appeared in the transport row as soon as playback started, but it was disabled and could not be clicked, so pause felt missing even though it was implemented.
- **Why.** The view model refreshes every command's availability from one place, and the pause command was simply not in that list. A WPF button stays disabled until the command raises its change notification, so the button kept the state it had at startup — "cannot execute" — for the entire session.
- **The fix.** The pause command now refreshes together with Start, Stop and the playlist commands, so it enables the moment playback is running and stays enabled while paused, when it turns into **Resume**.

### Why the tests missed it

The smoke suite drove the command directly, which bypasses the button's enabled state — so the audio behaviour passed while the button stayed dead. It now inspects the real button in the visual tree: that it is visible, enabled, and that its command agrees. A regression of this kind now fails the build instead of reaching a release.

The pause implementation itself was already correct and is unchanged: it silences the output without touching the decoder, so the position survives and Resume continues from the same sample, and typing does not lift an explicit pause.

## Русский

1.2.6 исправляет дефект, из-за которого новая кнопка «Пауза» была постоянно недоступна.

### Кнопка «Пауза» была серой

- **Что было видно.** Кнопка появлялась в строке управления, как только начиналось воспроизведение, но была недоступна и не нажималась, поэтому казалось, что паузы нет вовсе.
- **Почему.** Доступность всех команд обновляется в одном месте модели представления, и команды паузы в этом списке просто не было. Кнопка WPF остаётся неактивной, пока команда не сообщит об изменении состояния, — она сохраняла стартовое «выполнить нельзя» на весь сеанс.
- **Исправление.** Теперь команда паузы обновляется вместе со «Старт», «Стоп» и командами списка, включается сразу как только идёт воспроизведение и остаётся активной на паузе, превращаясь в **«Продолжить»**.

### Почему тесты это пропустили

Смоук вызывал команду напрямую, минуя состояние кнопки, поэтому звуковое поведение проходило, а сама кнопка оставалась мёртвой. Теперь проверяется настоящая кнопка в визуальном дереве: что она видима, активна и что её команда это подтверждает. Подобная регрессия теперь валит сборку, а не доходит до релиза.

Сама реализация паузы была корректной и не менялась: звук глушится без обращения к декодеру, поэтому позиция сохраняется и «Продолжить» возвращается к тому же сэмплу, а набор клавиш явную паузу не снимает.

## Assets

Every file for this version is attached to the release:

| File | What it is |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys.exe) | Portable single-file build — **this is what the in-app updater downloads** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys.exe.sha256) | SHA-256 of the executable |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys-win-x64.zip) | The same build as a folder archive, for a manual install |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 of the archive |

## Ассеты

Все файлы этой версии прикреплены к релизу:

| Файл | Что это |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys.exe) | Портативная сборка одним файлом — **именно её скачивает встроенный апдейтер** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys.exe.sha256) | SHA-256 исполняемого файла |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys-win-x64.zip) | Та же сборка папкой, для ручной установки |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 архива |

---

## v1.2.6.1

## English

1.2.6.1 fixes the Pause button for the case where the track stopped on its own after the idle timeout.

### A track paused by the idle timer now offers Resume

- **What went wrong.** After the idle timeout the audio is already silent, but the transport button still read **Pause**, because it only looked at an explicit pause. Clicking it then "paused" something that was already paused: nothing was audible either way, and the only visible effect was that typing no longer woke the track up.
- **What it does now.** The button reads **Resume** in both silent states, and clicking it continues from the same second without recreating the stream.
- **Quietly, on purpose.** Resuming with the mouse is not typing, so the volume stays at its quiet baseline and the first keystroke raises it — which is the whole point of the app. A key press during the idle pause still resumes instantly at full level, exactly as before.
- The status line keeps the distinction: a self-pause says it was the idle timer, a manual one says the track is standing still.

### Version numbers now carry a fourth component

Small fixes from now on get a revision number, like 1.2.6.1, instead of being folded into the next minor release. The updater already compares four components, so it orders these correctly, and the About window, the assembly metadata and the release tag all agree.

## Русский

1.2.6.1 исправляет кнопку «Пауза» для случая, когда трек встал сам после таймаута простоя.

### Для паузы от простоя кнопка теперь предлагает «Продолжить»

- **Что было не так.** После таймаута простоя звук уже заглушён, но кнопка управления по-прежнему называлась **«Пауза»**, потому что смотрела только на явную паузу. Нажатие «ставило на паузу» то, что уже стояло: на слух ничего не менялось, а единственным видимым следствием было то, что набор перестал будить трек.
- **Как теперь.** В обоих состояниях тишины кнопка называется **«Продолжить»**, и нажатие продолжает трек с той же секунды, не пересоздавая поток.
- **Тихо, и это намеренно.** Продолжение мышью — не набор текста, поэтому громкость остаётся на тихом минимуме, а поднимает её первое нажатие клавиши: в этом и смысл программы. Нажатие клавиши во время паузы от простоя по-прежнему мгновенно возвращает полную громкость, как раньше.
- Строка статуса сохраняет различие: о паузе от простоя она так и говорит, а о ручной — что трек стоит на месте.

### У версии появился четвёртый разряд

Мелкие исправления теперь получают номер ревизии, например 1.2.6.1, вместо того чтобы растворяться в следующем минорном выпуске. Апдейтер и раньше сравнивал четыре компонента, поэтому порядок версий соблюдается, а окно «О программе», метаданные сборки и тег релиза совпадают.

## Assets

Every file for this version is attached to the release:

| File | What it is |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys.exe) | Portable single-file build — **this is what the in-app updater downloads** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys.exe.sha256) | SHA-256 of the executable |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys-win-x64.zip) | The same build as a folder archive, for a manual install |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 of the archive |

## Ассеты

Все файлы этой версии прикреплены к релизу:

| Файл | Что это |
|---|---|
| [`BeatzOnKeys.exe`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys.exe) | Портативная сборка одним файлом — **именно её скачивает встроенный апдейтер** |
| [`BeatzOnKeys.exe.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys.exe.sha256) | SHA-256 исполняемого файла |
| [`BeatzOnKeys-win-x64.zip`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys-win-x64.zip) | Та же сборка папкой, для ручной установки |
| [`BeatzOnKeys-win-x64.zip.sha256`](https://github.com/MusicCat-61/BeatzOnKeys/releases/download/v1.2.6.1/BeatzOnKeys-win-x64.zip.sha256) | SHA-256 архива |

