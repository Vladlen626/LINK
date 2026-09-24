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

Текущая версия собирает text, voice, screenshots, clipboard и данные текущего приложения в единый `PromptRequest`, отправляет его в связанную ChatGPT-беседу через browser companion и показывает ответ в overlay.

## Что уже работает

- compact frosted-glass always-on-top overlay: drag, ширина 320–1200 px (левый/правый край), высота следует содержимому; нижним краем задаётся, до какой высоты Link может вырасти: длинный ответ занимает её целиком и прокручивается, короткий остаётся компактным (двойной клик по нижнему краю возвращает компактный режим по умолчанию); independent collapse/show-hide и optional click-through;
- сохранение позиции/размера на нескольких мониторах, opacity и остальных настроек;
- system tray, single-instance и корректное скрытие вместо завершения;
- переназначаемые глобальные hotkeys через `RegisterHotKey`; low-level hook используется только для push-to-talk по нажатию/отпусканию;
- захват текущего монитора, всего virtual desktop, каждого монитора, активного окна и выбранной области;
- preview последнего screenshot, attachment chips и ограниченная история, которая не удаляет активные attachments;
- локальная WAV-запись и распознавание Whisper для RU/EN/Auto без Windows speech pack;
- выбор микрофона в Settings → Microphone (полные имена устройств, Windows default, отключённое устройство не теряется) и тест с индикатором уровня; запись без сигнала не отправляется в Whisper, чтобы он не «придумывал» текст на тишине;
- голосовой ответ: если запрос продиктован через push-to-talk, Link читает вслух начало ответа голосом Windows — первый абзац, не больше трёх предложений, без кода, таблиц, URL и разметки; если в ответе есть что-то ещё, добавляет «Подробности на экране.». Push-to-talk во время речи сразу обрывает её и начинает запись; Esc, кнопка Stop и hotkey Send просто останавливают. В Settings → Voice replies: включение, голос (Auto подбирает голос по языку ответа), скорость и проверка голоса;
- видимый context stack для screenshots, voice и clipboard;
- проекты с `General` по умолчанию, active project и current task;
- сборка versioned `PromptRequest`, debug output и atomic-сохранение последнего request;
- provider-neutral `IChatIntegration` и локальный browser bridge на `127.0.0.1:43127`;
- привязка проекта к обнаруженной ChatGPT-беседе;
- отправка текста и clipboard через browser companion; путь загрузки изображений обновлён после ошибки на реальном ChatGPT и ждёт повторной проверки;
- streamed response status и последний ответ прямо в overlay с копированием; companion восстанавливает Markdown из DOM ChatGPT (код, списки, таблицы, цитаты, ссылки), а не плоский `innerText`;
- уровень ответа **Fast / Normal / Deep** (кнопка рядом со статусом чата): в запрос добавляется подсказка о длине/глубине ответа (работает в любом чате), а в ChatGPT companion дополнительно переключает режим Instant / Auto / Thinking, если находит переключатель моделей;
- `Enter` отправляет запрос, `Shift+Enter` вставляет новую строку; во время ответа кнопка отправки становится Stop, `Esc` тоже отменяет запрос (генерация в ChatGPT останавливается);
- если вкладка ChatGPT не подтвердила запрос за 20 секунд, Link сообщает об этом сразу, а не ждёт 10 минут;
- опциональный Auto active window в меню Screen делает свежий снимок рабочего окна для каждого запроса;
- рабочее приложение фиксируется в момент вызова Link (hotkey или возврат в Link после работы в приложении): просмотр ChatGPT-вкладки и собственные hide/show Link при захвате экрана его не подменяют;
- каждый проект помнит последний ответ своей связанной беседы: при переключении проекта overlay сразу показывает его ответ или прячет панель;
- глобальный hotkey Send отправляет текущий запрос из любого приложения; нажатый во время push-to-talk или распознавания, он отправит запрос сразу после появления транскрипта;
- если Link скрыт или неактивен, о готовом ответе (или ошибке) сообщает уведомление Windows; клик открывает Link на нужном проекте;
- одновременно только один выделенный Region: новое выделение заменяет предыдущее, повторный hotkey при открытом выборе области игнорируется.

## Куда идёт продукт

Link должен ощущаться как маленький персональный «Jarvis» для работы с AI:

- спросить что-то, не покидая текущее приложение;
- показать AI экран или активное окно;
- автоматически добавить релевантный project/current-task context;
- продолжать существующую долгую AI-беседу;
- получить ответ прямо в overlay;
- услышать короткий ответ голосом, а дальше — разговаривать с Link без рук (Conversation Mode) или выполнить безопасное локальное действие.

Обычный сценарий не должен требовать ручного перехода в browser UI, copy/paste каждого запроса или ручного переноса ответа обратно в Link.

Browser companion, direct provider API и local model должны быть заменяемыми transport adapters вокруг общего request/context слоя.

## Запуск

Требуется Windows и .NET 10 SDK/runtime:

```powershell
dotnet build Link.sln --configuration Release
dotnet run --project src/Link.Desktop/Link.Desktop.csproj
```

Проверки логики без микрофона, звука и браузера (что именно Link прочитает вслух, переходы голосовой сессии на фейках) — консольный раннер без тестового фреймворка: печатает упавшие примеры, код выхода равен числу ошибок. CI запускает его после сборки.

```powershell
dotnet run --project tests/Link.Desktop.Tests
```

### Подключение ChatGPT

1. Запустить Link.
2. Открыть `chrome://extensions` или `edge://extensions`.
3. Включить Developer mode, нажать **Load unpacked** и выбрать папку `browser-extension`.
4. Открыть нужную беседу ChatGPT. У новой беседы сначала отправить одно сообщение, чтобы URL получил `/c/<id>`.
5. Нажать строку статуса ChatGPT в Link, выбрать найденную беседу и нажать **Link**.

После привязки вкладка и окно браузера могут оставаться в фоне. Подробная диагностика находится в [`browser-extension/README.md`](browser-extension/README.md).

После обновления кода расширения нажмите **Reload** на карточке LINK ChatGPT Companion в `chrome://extensions` или `edge://extensions`, затем обновите вкладку ChatGPT. Без этого браузер продолжит выполнять старую версию скрипта.

Закрытие overlay через `Alt+F4` скрывает Link в tray. Полностью завершить приложение можно через `Exit` в tray menu.

Перед пересборкой в `bin\Release` завершите Link через `Exit` в tray: запущенный `Link.exe` блокирует файл, а повторный запуск при работающем экземпляре лишь покажет «Link is already running» — и продолжит работать старая сборка. Номер сборки виден внизу Settings.

### Hotkeys по умолчанию

| Действие | Hotkey |
|---|---|
| Показать/скрыть | `Ctrl+Alt+Space` |
| Push-to-talk | `Ctrl+Alt+V` |
| Отправить запрос | `Ctrl+Alt+Enter` |
| Весь desktop | `Ctrl+Alt+D` |
| Активное окно | `Ctrl+Alt+W` |
| Выбор области | `Ctrl+Alt+Shift+R` |

Все сочетания меняются в Settings.

## Локальные данные

Данные находятся в `%LocalAppData%\Link`:

- `settings.json` — UI, hotkeys и voice settings;
- `projects.json` и `Projects\` — проекты вместе с последним ответом связанной беседы;
- `Screenshots\` — ограниченная история PNG;
- `Recordings\` — последние WAV-записи;
- `last-request.json` — последний собранный request;
- `link.log` — неожиданные ошибки (UTF-8, до 1 МБ); приложение показывает предупреждение и продолжает работу.

## Архитектура

```text
src/Link.Desktop/
├── Models/        settings, projects, context и PromptRequest
├── Services/      storage, context, request builder, chat integration, voice session, STT и TTS
├── Windows/       Win32 hotkeys/styles, GDI capture, waveIn audio + Core Audio device list, tray
├── MainWindow*    overlay shell; capture и voice UI
├── SettingsWindow*
└── RegionSelectionWindow*
tests/Link.Desktop.Tests/   проверки логики без тестового фреймворка
```

`VoiceSession` — единственный владелец голосового состояния (Idle → Listening → Finalizing → Sending → Waiting → Speaking): микрофон, Whisper, прогресс запроса и чтение ответа вслух. UI только отображает его фазу.

`browser-extension/` содержит первый ChatGPT transport. Desktop-приложение отдаёт ему pending requests через локальный HTTP bridge; extension взаимодействует с DOM уже открытой беседы и возвращает progress/response. Browser-specific код не попадает в модели Link или `MainWindow`.

Проект пока остаётся одним assembly. UI, состояние и Windows-specific код логически разделены без преждевременной многослойности.

`PromptRequest` и context model принадлежат самому Link. `IChatIntegration` отделяет их от browser bridge; будущие OpenAI/API adapters, другие провайдеры и local AI подключаются через ту же границу.

## Ближайшие задачи

Текстовый end-to-end сценарий работает:

```text
Link prompt/context
→ linked AI conversation
→ assistant response
→ Link overlay
```

Пользователь остаётся в текущем рабочем приложении. Сейчас нужно проверить обновлённую загрузку изображения после Reload расширения и первую локальную транскрипцию Whisper. Далее — укрепить browser transport по результатам проверки, затем развивать project memory, выбор релевантного контекста и handoff между чатами. Direct API integration остаётся возможным дополнительным adapter'ом.

## Зависимости и ограничения

- Для локального распознавания речи используются `Whisper.net` и его CPU runtime. При первом использовании Voice загружается мультиязычная модель Base в `%LocalAppData%\Link\Models`; дальше распознавание работает локально. Для этого runtime нужны Windows 11+, интернет при первой загрузке и Microsoft Visual C++ Redistributable 2022 x64.
- Голосовые ответы идут через `System.Speech` — официальную обёртку Microsoft над SAPI, которая есть в Windows, — и голоса, установленные в Windows (классические Desktop и OneCore, например Irina, Pavel, Zira). Ничего не скачивается. Если русского голоса нет, Settings подскажет, где его добавить. Это не нейросетевые голоса: звучат заметно синтетически.
- Browser transport требует запущенный Chrome/Edge, вручную загруженный unpacked companion и открытую вкладку связанной беседы.
- Companion зависит от DOM ChatGPT. Он поддерживает классический composer и новый интерфейс ChatGPT (`textarea[name="prompt"]`, `data-message-complete`), но после изменений UI могут потребоваться обновления селекторов.
- Переключение режима ChatGPT для Fast/Deep эвристическое (ищет переключатель моделей и пункты Instant/Auto/Thinking). Если переключить не удалось, статус Link пишет «ChatGPT mode not switched», а подсказка о длине ответа всё равно уходит в запрос.
- Сейчас транспорт есть только для ChatGPT; Claude-чат подключается тем же `IChatIntegration` отдельным адаптером (ещё не реализован).
- Качество и скорость Voice зависят от микрофона и CPU; на первой попытке нужно дождаться загрузки модели.
- Захват защищённого DRM/UAC content может вернуть пустое изображение — это ограничение Windows/GDI.
