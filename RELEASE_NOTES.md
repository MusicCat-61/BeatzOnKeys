# BeatzOnKeys 1.0.3

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

# BeatzOnKeys 1.0.2 (historical / история)

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

---

The following 1.0.1 notes are historical; current playback behavior is documented above.
Записи 1.0.1 ниже сохранены как история; актуальное поведение описано выше.

# BeatzOnKeys 1.0.1 (historical / история)

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
