# Link

Link — компактный Windows desktop companion на C#/.NET и WPF.

Главная идея продукта: пользователь работает в Unreal, Blender, IDE, игре, браузере или другом приложении и не должен постоянно переключаться в большое окно ChatGPT. Link остаётся маленьким floating overlay, понимает активный проект и текущую задачу, собирает нужный screen / clipboard / voice / app context, отправляет его в связанную AI-беседу и возвращает ответ обратно в overlay.

**UI для пользователя — Link. Браузер, extension, WebView, API или другой transport — внутренняя реализация.**

Целевой рабочий поток:

```text
current app / screen / clipboard / voice
                 ↓
               Link
                 ↓
            Context Brain
                 ↓
       Chat Integration Layer
                 ↓
      existing AI conversation
                 ↓
        response back to Link
```

Текущая версия уже собирает text, voice, screenshots и clipboard в единый локальный `PromptRequest`. Следующая крупная задача — первый end-to-end AI conversation bridge: отправить запрос в связанную беседу и получить ответ обратно без необходимости переключаться в браузер.

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

## Куда идёт продукт

Link должен ощущаться как маленький персональный «Jarvis» для работы с AI:

- спросить что-то, не покидая текущее приложение;
- показать AI экран или активное окно;
- автоматически добавить релевантный project/current-task context;
- продолжать существующую долгую AI-беседу;
- получить ответ прямо в overlay;
- позже — услышать его через local TTS или выполнить безопасное локальное действие.

Обычный сценарий не должен требовать ручного перехода в browser UI, copy/paste каждого запроса или ручного переноса ответа обратно в Link.

Browser companion, direct provider API и local model должны быть заменяемыми transport adapters вокруг общего request/context слоя.

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

Следующий архитектурный seam — Chat Integration Layer. `PromptRequest` и context model принадлежат самому Link; browser bridge, OpenAI/API adapters, другие провайдеры и local AI должны подключаться поверх них, не протекая в основной UI.

## Следующий milestone

### Milestone 4 — Persistent AI Conversation Bridge

Цель:

```text
Link prompt/context
→ linked AI conversation
→ assistant response
→ Link overlay
```

Пользователь остаётся в текущем рабочем приложении.

Если первый transport использует browser companion/extension, браузер должен быть только фоновым transport и не становиться обязательным пользовательским UI.

Direct API integration остаётся возможным дополнительным adapter'ом, а не обязательной основой продукта.

## Зависимости и ограничения

- Единственная внешняя runtime-зависимость текущего WPF-приложения — официальный пакет `System.Speech` 10.0.0.
- Для локальной транскрипции нужен установленный Windows speech recognizer нужного языка. Без RU/EN speech pack WAV сохранится и Link покажет понятную ошибку, но транскрипции не будет.
- Захват защищённого DRM/UAC content может вернуть пустое изображение — это ограничение Windows/GDI.
- AI conversation bridge пока не подключён: кнопка Send показывает и сохраняет готовый request JSON.
