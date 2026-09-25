# Link

Компактный Windows desktop companion на C#/.NET 10 и WPF. Вы работаете в Unreal, Blender, IDE или браузере, а Link
висит маленьким overlay поверх: собирает нужный контекст — текст, голос, скриншоты, clipboard, текущее приложение —
отправляет его в связанную беседу ChatGPT и показывает ответ, не заставляя переключаться в окно чата.

**UI для пользователя — Link. Браузер, расширение или API — внутренняя реализация.**

## Возможности

**Overlay**
- frosted-glass always-on-top: перетаскивание, ширина 320–1200 px, высота по содержимому; нижним краем задаётся
  предел роста (двойной клик по нему возвращает компактный режим); collapse, click-through, прозрачность;
- tray, один экземпляр, глобальные hotkeys (все меняются в Settings); позиция и размер сохраняются на нескольких
  мониторах;
- `Enter` отправляет, `Shift+Enter` — новая строка; во время ответа кнопка отправки и `Esc` останавливают запрос и
  генерацию в ChatGPT;
- если Link скрыт или неактивен, о готовом ответе сообщает уведомление Windows; клик открывает нужный проект.

**Контекст**
- скриншоты: текущий монитор, весь рабочий стол, каждый монитор, активное окно, выбранная область (одна за раз);
  Auto active window в меню Screen делает свежий снимок рабочего окна для каждого запроса;
- clipboard и данные текущего приложения в видимом context stack;
- проекты (по умолчанию General) с текущей задачей, связанной беседой и последним ответом;
- рабочее приложение фиксируется в момент вызова Link: просмотр вкладки ChatGPT и скрытие Link при захвате экрана его
  не подменяют.

**Голос**
- push-to-talk: слова появляются в поле, пока вы говорите (локальный распознаватель, RU или EN), после отпускания
  Whisper заменяет черновик финальным текстом — large-v3-turbo на видеокарте, Base на CPU; запись без речи в Whisper
  не идёт;
- выбор и тест микрофона — Settings → Microphone;
- голосовой ответ на продиктованный вопрос: Link читает начало ответа голосом Windows по мере его прихода, при долгом
  ожидании говорит короткую фразу («Секунду.»). Push-to-talk во время речи перебивает; Esc и Stop сначала заглушают,
  повторно — останавливают запрос. Настройки — Settings → Voice replies.

**ChatGPT**
- уровень ответа **Fast / Normal / Deep**: подсказка в запросе плюс переключение режима ChatGPT; Fast ставит самый
  лёгкий режим, Normal возвращает ваш;
- ответ приходит потоково и восстанавливается как Markdown: код, списки, таблицы, ссылки;
- запрос не зависает: статус в шапке ответа (Sending, Waiting for response, Receiving, Reconnecting, Cancelled,
  Error) и понятные ошибки; после перезагрузки вкладки или расширения ответ подхватывается без повторной отправки;
- **Check chat** забирает ответ прямо из вкладки, если он не дошёл; Link и сам проверяет чат после 20 с тишины.

## Запуск

Нужны Windows 11 и .NET 10 SDK:

```powershell
dotnet build Link.sln --configuration Release
dotnet run --project src/Link.Desktop/Link.Desktop.csproj
```

Проверки логики без микрофона и настоящего ChatGPT — консольный раннер, код выхода равен числу ошибок. Он же
прогоняет browser companion на мок-страницах ChatGPT в headless Edge или Chrome (без них эти проверки пропускаются).
CI запускает раннер после сборки.

```powershell
dotnet run --project tests/Link.Desktop.Tests
```

`Alt+F4` прячет Link в tray, завершить его — `Exit` в меню tray. Перед пересборкой в `bin\Release` завершите Link:
запущенный `Link.exe` блокирует файл, а второй запуск только покажет «Link is already running». Номер сборки — внизу
Settings.

### Подключение ChatGPT

1. Запустить Link.
2. В `chrome://extensions` (в Edge и Opera — так же) включить Developer mode, нажать **Load unpacked** и выбрать папку
   `browser-extension`.
3. Открыть нужную беседу ChatGPT. У новой беседы сначала отправить одно сообщение, чтобы в URL появился `/c/<id>`.
4. В Link нажать строку статуса ChatGPT, выбрать беседу и нажать **Link**.

Вкладка и браузер могут оставаться в фоне, но в фоновой вкладке ChatGPT печатает ответ медленно (см. ограничения).
Расширение обновляется само; **Reload** на его карточке нужен только один раз — при переходе с версии старше 0.7.2.
Диагностика — [`browser-extension/README.md`](browser-extension/README.md).

### Hotkeys по умолчанию

| Действие | Hotkey |
|---|---|
| Показать/скрыть | `Ctrl+Alt+Space` |
| Push-to-talk | `Ctrl+Alt+V` |
| Отправить запрос | `Ctrl+Alt+Enter` |
| Весь desktop | `Ctrl+Alt+D` |
| Активное окно | `Ctrl+Alt+W` |
| Выбор области | `Ctrl+Alt+Shift+R` |

## Локальные данные

`%LocalAppData%\Link`:

- `settings.json` — UI, hotkeys и голос;
- `projects.json` и `Projects\` — проекты с последним ответом связанной беседы;
- `Screenshots\` — ограниченная история PNG; `Recordings\` — последние WAV;
- `Models\` — модели распознавания речи;
- `last-request.json` — последний собранный запрос;
- `link.log` — неожиданные ошибки и диагностика ChatGPT (UTF-8, до 1 МБ).

## Архитектура

```text
src/Link.Desktop/
├── Models/        settings, projects, context и PromptRequest
├── Services/      storage, запрос, chat integration, голосовая сессия, STT и TTS
├── Windows/       Win32: hotkeys, стили окон, GDI capture, waveIn + список устройств, tray
├── MainWindow*    overlay; части по темам: Chat, Capture, Voice, Sizing
├── SettingsWindow*, RegionSelectionWindow*, ...
tests/Link.Desktop.Tests/   проверки логики без тестового фреймворка
tests/browser-extension/    проверки browser companion на мок-страницах ChatGPT
```

- `PromptRequest` и контекст принадлежат Link; `IChatIntegration` отделяет их от транспорта. Сейчас это
  `BrowserChatIntegration`: локальный HTTP-мост отдаёт запросы расширению, а оно работает с DOM открытой беседы.
- `VoiceSession` — единственный владелец голосового состояния (Idle → Listening → Finalizing → Sending → Waiting →
  Speaking); UI только отображает фазу и черновик. Один захват waveIn кормит и WAV для Whisper, и live-распознаватель.

## Ограничения

- **ChatGPT.** Нужен браузер с companion и открытой вкладкой беседы. В фоновой вкладке браузер тормозит страницу, и
  ChatGPT печатает ответ медленно — встроенный ChatGPT в Link прорабатывается. Первое слово приходит через 15–20 с —
  это ожидание сервера ChatGPT.
- **DOM ChatGPT.** Конец ответа companion узнаёт по нескольким признакам, а при смене разметки пишет в `link.log`, что
  видела вкладка. Переключение режима эвристическое: если не вышло, статус пишет «ChatGPT mode not switched (…)», а
  подсказка всё равно уходит в запрос.
- **Голос.** При первом использовании скачиваются модели (Whisper Base 148 МБ; turbo 574 МБ, если есть видеокарта с
  Vulkan; live-модели 30–45 МБ; всё проверяется по SHA-256). Нужен Microsoft Visual C++ Redistributable 2022 x64.
  Голоса ответа — установленные в Windows и звучат синтетически; если русского нет, Settings подскажет, где его
  добавить.
- Транспорт пока только для ChatGPT; Claude подключится отдельным адаптером `IChatIntegration`.
- Защищённый (DRM/UAC) контент может захватиться пустым — ограничение Windows/GDI.

## Дальше

- встроенный ChatGPT: без открытого браузера и медленной фоновой печати, с кнопкой «Открыть чат»;
- локальный TTS и Conversation Mode — [docs/VOICE_CONVERSATION_PLAN.md](docs/VOICE_CONVERSATION_PLAN.md);
- память проектов, context router, handoff между чатами, команды.
