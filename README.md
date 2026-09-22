# Link

Link — компактный Windows desktop companion на C#/.NET и WPF. Текущая версия собирает текст, голос, screenshots и clipboard в единый локальный request, готовый для подключения AI provider на следующем этапе.

## Что уже работает

- compact auto-height frosted-glass always-on-top overlay: drag, constrained width, independent collapse/show-hide и optional click-through;
- сохранение позиции/размера на нескольких мониторах, opacity и остальных настроек;
- system tray, single-instance и корректное скрытие вместо завершения;
- переназначаемые глобальные hotkeys через `RegisterHotKey`; low-level hook используется только для push-to-talk по нажатию/отпусканию;
- захват текущего монитора, всего virtual desktop, каждого монитора, активного окна и выбранной области;
- preview последнего screenshot, attachment chips и ограниченная история, которая не удаляет активные attachments;
- локальная WAV-запись и Windows Speech-to-Text для RU/EN/Auto;
- видимый context stack для screenshots, voice и clipboard;
- проекты с `General` по умолчанию, active project и current task;
- сборка versioned `PromptRequest`, preview JSON, debug output и atomic-сохранение последнего request.

## Запуск

Требуется Windows и .NET 10 SDK/runtime:

```powershell
dotnet build Link.sln --configuration Release
dotnet run --project src/Link.Desktop/Link.Desktop.csproj
```

Закрытие overlay через `Alt+F4` скрывает Link в tray. Полностью завершить приложение можно через `Exit` в tray menu.

### Hotkeys по умолчанию

| Действие | Hotkey |
|---|---|
| Показать/скрыть | `Ctrl+Alt+Space` |
| Push-to-talk | `Ctrl+Alt+V` |
| Весь desktop | `Ctrl+Alt+D` |
| Активное окно | `Ctrl+Alt+W` |
| Выбор области | `Ctrl+Alt+R` |

Все сочетания меняются в Settings.

## Локальные данные

Данные находятся в `%LocalAppData%\Link`:

- `settings.json` — UI, hotkeys и voice settings;
- `projects.json` и `Projects\` — проекты;
- `Screenshots\` — ограниченная история PNG;
- `Recordings\` — последние WAV-записи;
- `last-request.json` — последний собранный request.

## Архитектура

```text
src/Link.Desktop/
├── Models/        settings, projects, context и PromptRequest
├── Services/      JSON storage, history, clipboard, STT, request builder
├── Windows/       Win32 hotkeys/styles, GDI capture, MCI audio, tray
├── MainWindow*    overlay shell, capture и voice orchestration
├── SettingsWindow*
└── RegionSelectionWindow*
```

Проект пока остаётся одним assembly. UI, состояние и Windows-specific код логически разделены без преждевременной многослойности.

## Зависимости и ограничения

- Единственная внешняя runtime-зависимость — официальный пакет `System.Speech` 10.0.0.
- Для локальной транскрипции нужен установленный Windows speech recognizer нужного языка. Без RU/EN speech pack WAV сохранится и Link покажет понятную ошибку, но транскрипции не будет.
- Захват защищённого DRM/UAC content может вернуть пустое изображение — это ограничение Windows/GDI.
- AI provider пока намеренно не подключён: кнопка Send показывает и сохраняет готовый request JSON.
