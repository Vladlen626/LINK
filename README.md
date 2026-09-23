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

Текущая версия собирает text, voice, screenshots и clipboard в единый `PromptRequest`, отправляет его в связанную ChatGPT-беседу через browser companion и показывает ответ в overlay.

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
- сборка versioned `PromptRequest`, debug output и atomic-сохранение последнего request;
- provider-neutral `IChatIntegration` и локальный browser bridge на `127.0.0.1:43127`;
- привязка проекта к обнаруженной ChatGPT-беседе;
- отправка текста, clipboard и изображений через browser companion;
- streamed response status и последний ответ прямо в overlay с копированием.

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

### Подключение ChatGPT

1. Запустить Link.
2. Открыть `chrome://extensions` или `edge://extensions`.
3. Включить Developer mode, нажать **Load unpacked** и выбрать папку `browser-extension`.
4. Открыть нужную беседу ChatGPT. У новой беседы сначала отправить одно сообщение, чтобы URL получил `/c/<id>`.
5. Нажать строку статуса ChatGPT в Link, выбрать найденную беседу и нажать **Link**.

После привязки вкладка и окно браузера могут оставаться в фоне. Подробная диагностика находится в [`browser-extension/README.md`](browser-extension/README.md).

Закрытие overlay через `Alt+F4` скрывает Link в tray. Полностью завершить приложение можно через `Exit` в tray menu.

### Hotkeys по умолчанию

| Действие | Hotkey |
|---|---|
| Показать/скрыть | `Ctrl+Alt+Space` |
| Push-to-talk | `Ctrl+Alt+V` |
| Весь desktop | `Ctrl+Alt+D` |
| Активное окно | `Ctrl+Alt+W` |
| Выбор области | `Ctrl+Alt+Shift+R` |

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
├── Services/      storage, context, request builder и chat integration
├── Windows/       Win32 hotkeys/styles, GDI capture, MCI audio, tray
├── MainWindow*    overlay shell, capture и voice orchestration
├── SettingsWindow*
└── RegionSelectionWindow*
```

`browser-extension/` содержит первый ChatGPT transport. Desktop-приложение отдаёт ему pending requests через локальный HTTP bridge; extension взаимодействует с DOM уже открытой беседы и возвращает progress/response. Browser-specific код не попадает в модели Link или `MainWindow`.

Проект пока остаётся одним assembly. UI, состояние и Windows-specific код логически разделены без преждевременной многослойности.

`PromptRequest` и context model принадлежат самому Link. `IChatIntegration` отделяет их от browser bridge; будущие OpenAI/API adapters, другие провайдеры и local AI подключаются через ту же границу.

## Следующий milestone

### Milestone 4 — Persistent AI Conversation Bridge

Первая end-to-end реализация готова:

```text
Link prompt/context
→ linked AI conversation
→ assistant response
→ Link overlay
```

Пользователь остаётся в текущем рабочем приложении.

Следующий шаг — проверить extension на реальном logged-in ChatGPT DOM и укрепить селекторы/фоновые сценарии по результатам этой проверки. Direct API integration остаётся возможным дополнительным adapter'ом.

## Зависимости и ограничения

- Единственная внешняя runtime-зависимость текущего WPF-приложения — официальный пакет `System.Speech` 10.0.0.
- Browser transport требует запущенный Chrome/Edge, вручную загруженный unpacked companion и открытую вкладку связанной беседы.
- Companion зависит от DOM ChatGPT. После изменений интерфейса ChatGPT могут потребоваться обновления селекторов.
- Для локальной транскрипции нужен установленный Windows speech recognizer нужного языка. Без RU/EN speech pack WAV сохранится и Link покажет понятную ошибку, но транскрипции не будет.
- Захват защищённого DRM/UAC content может вернуть пустое изображение — это ограничение Windows/GDI.
