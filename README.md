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
- локальная WAV-запись и распознавание Whisper для RU/EN/Auto без Windows speech pack: large-v3-turbo на видеокарте (Vulkan), Base на CPU, если GPU нет или turbo ещё скачивается;
- live-черновик во время push-to-talk: слова появляются в поле запроса, пока вы говорите (локальный streaming-распознаватель sherpa-onnx, RU или EN; под Auto — язык прошлой диктовки, в первый раз — язык Windows), после отпускания Whisper заменяет черновик финальным текстом без дублей. Уже набранный текст остаётся перед черновиком; пока идёт диктовка и распознавание, поле только для чтения. Модели (~2 МБ VAD и 28 МБ RU / 43 МБ EN) скачиваются один раз при первом использовании; до этого статус пишет «live text model is downloading». Выключается в Settings → Voice;
- выбор микрофона в Settings → Microphone (полные имена устройств, Windows default, отключённое устройство не теряется) и тест с индикатором уровня; запись без сигнала или без речи (локальный Silero VAD) не отправляется в Whisper, чтобы он не «придумывал» текст на тишине и шуме;
- голосовой ответ: если запрос продиктован через push-to-talk, Link читает вслух начало ответа голосом Windows — первый абзац, не больше трёх предложений, без кода, таблиц, URL и разметки; если в ответе есть что-то ещё, добавляет «Подробности на экране.». Читает, пока ответ ещё приходит: каждое предложение — как только оно закончено (началось следующее или новый блок), ничего не повторяя; остаток и «Подробности на экране.» — когда ответ готов. Если ChatGPT молчит дольше ~6 с, Link один раз говорит короткую фразу («Секунду.», «Думаю над ответом.», «Ещё немного.») на языке вопроса. Push-to-talk во время речи сразу обрывает её и начинает запись (продиктованное во время ответа остаётся в поле как следующее сообщение); Esc, кнопка Stop и hotkey Send сначала только заглушают Link, а повторные Esc/Stop останавливают запрос. В Settings → Voice replies: включение, голос (Auto подбирает голос по языку ответа), скорость и проверка голоса;
- видимый context stack для screenshots, voice и clipboard;
- проекты с `General` по умолчанию, active project и current task;
- сборка versioned `PromptRequest`, debug output и atomic-сохранение последнего request;
- provider-neutral `IChatIntegration` и локальный browser bridge на `127.0.0.1:43127`;
- привязка проекта к обнаруженной ChatGPT-беседе;
- отправка текста и clipboard через browser companion; путь загрузки изображений обновлён после ошибки на реальном ChatGPT и ждёт повторной проверки;
- streamed response status и последний ответ прямо в overlay с копированием; companion восстанавливает Markdown из DOM ChatGPT (код, списки, таблицы, цитаты, ссылки), а не плоский `innerText`;
- уровень ответа **Fast / Normal / Deep** (кнопка рядом со статусом чата): в запрос добавляется подсказка о длине/глубине ответа (работает в любом чате), а в ChatGPT companion дополнительно переключает режим в пикере composer («Мгновенный» / «Средний» / «Высокий»; в старом UI — Instant / Auto / Thinking в шапке) и проверяет, что пикер действительно показывает выбранный режим;
- `Enter` отправляет запрос, `Shift+Enter` вставляет новую строку; во время ответа кнопка отправки становится Stop, `Esc` тоже отменяет запрос (генерация в ChatGPT останавливается);
- запрос не зависает в ожидании: состояние видно в шапке ответа (Sending, Waiting for response, Receiving, Reconnecting, Cancelled, Error); вкладка, которая 20 с не подтвердила запрос или 45 с молчит во время ответа, даёт понятную ошибку; если вкладка ненадолго пропала (перезагрузка страницы или расширения), Link сам передаёт ей запрос снова, и она подхватывает ответ без повторной отправки prompt;
- опциональный Auto active window в меню Screen делает свежий снимок рабочего окна для каждого запроса;
- рабочее приложение фиксируется в момент вызова Link (hotkey или возврат в Link после работы в приложении): просмотр ChatGPT-вкладки и собственные hide/show Link при захвате экрана его не подменяют;
- каждый проект помнит последний ответ своей связанной беседы: при переключении проекта overlay сразу показывает его ответ или прячет панель;
- глобальный hotkey Send отправляет текущий запрос из любого приложения; нажатый во время push-to-talk или распознавания, он отправит запрос сразу после появления транскрипта;
- если Link скрыт или неактивен, о готовом ответе (или ошибке) сообщает уведомление Windows; клик открывает Link на нужном проекте;
- «Check chat» в шапке ответа — запасной путь: забирает ответ прямо из связанной вкладки ChatGPT, если он всё же не дошёл до Link; если запрос ещё ждёт, он завершается этим ответом (только ответом на этот же prompt). Link и сам проверяет чат, когда ждущий запрос 20 с не получает новых данных. Что видела вкладка при необычном завершении ответа или ошибке, записывается в `link.log`;
- одновременно только один выделенный Region: новое выделение заменяет предыдущее, повторный hotkey при открытом выборе области игнорируется.

## Куда идёт продукт

Link должен ощущаться как маленький персональный «Jarvis» для работы с AI:

- спросить что-то, не покидая текущее приложение;
- показать AI экран или активное окно;
- автоматически добавить релевантный project/current-task context;
- продолжать существующую долгую AI-беседу;
- получить ответ прямо в overlay;
- услышать его через local TTS и вести голосовой Conversation Mode без отдельного AI-сеанса;
- позже — выполнять безопасные локальные действия.

Обычный сценарий не должен требовать ручного перехода в browser UI, copy/paste каждого запроса или ручного переноса ответа обратно в Link.

### Voice / Conversation roadmap

Принятое направление: speech-layer должен быть local-first и без поминутного лимита, а GPT остаётся intelligence layer для reasoning, search, screenshots, project context и linked conversation.

Целевой flow:

```text
microphone
→ local VAD + live partial STT
→ Whisper final correction
→ обычный PromptRequest + screenshots/context
→ GPT
→ full written response in LINK
→ local TTS
→ next voice turn
```

Стек: `sherpa-onnx` для streaming ASR и Silero VAD (сделано), `Whisper.net` для final correction — large-v3-turbo на GPU через Vulkan, Base на CPU (сделано), Supertonic 3 через sherpa-onnx как первый кандидат на локальный RU/EN voice output (следующий шаг).

Режимы: **Text / Voice Reply / Conversation**. В Conversation Mode voice endpoint автоматически отправляет обычный LINK request, поэтому vision и project context не теряются. PTT должен мгновенно прерывать TTS.

Полный поэтапный план: [docs/VOICE_CONVERSATION_PLAN.md](docs/VOICE_CONVERSATION_PLAN.md).

Browser companion, direct provider API и local model должны быть заменяемыми transport adapters вокруг общего request/context слоя.

## Запуск

Требуется Windows и .NET 10 SDK/runtime:

```powershell
dotnet build Link.sln --configuration Release
dotnet run --project src/Link.Desktop/Link.Desktop.csproj
```

Проверки логики без микрофона, звука и настоящего ChatGPT (что именно Link прочитает вслух, переходы голосовой сессии на фейках, browser bridge по реальному loopback HTTP) — консольный раннер без тестового фреймворка: печатает упавшие примеры, код выхода равен числу ошибок. Он же запускает проверки browser companion (`tests/browser-extension`): настоящие `content.js` и `background.js` против мок-страницы ChatGPT в headless Edge или Chrome с виртуальным временем; без этих браузеров они пропускаются с пометкой SKIP. CI запускает раннер после сборки.

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

С версии 0.7.2 расширение обновляется само: раз в 10 с оно сверяет версию своего `manifest.json` на диске и при новой перезагружается; новая версия скрипта запускается в уже открытых вкладках ChatGPT (обновлять вкладку не нужно), а запрос, который шёл во время перезагрузки, продолжается без повторной отправки. Нажать **Reload** на карточке LINK ChatGPT Companion в `chrome://extensions` или `edge://extensions` нужно только один раз — чтобы перейти на 0.7.2 с более старой версии.

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
tests/browser-extension/    проверки browser companion на мок-странице ChatGPT (запускает раннер выше)
```

`VoiceSession` — единственный владелец голосового состояния (Idle → Listening → Finalizing → Sending → Waiting → Speaking): микрофон, live-черновик, Whisper, прогресс запроса, waiting cue и чтение ответа вслух по мере streaming (`SpokenAnswerStream` выбирает законченные предложения, озвучка — через `ITextToSpeech`). UI только отображает его фазу и черновик.

Один waveIn-захват кормит и WAV для Whisper, и `IStreamingSpeechToText` (`IAudioRecorder.SamplesCaptured`). `SherpaStreamingSpeechToText` в одном рабочем потоке прогоняет блоки через Silero VAD (была ли речь) и streaming Zipformer (черновик); распознаватель грузится ~2 с, поэтому живёт между фразами и выгружается после 10 минут без голоса. `SpeechModelManager` скачивает модели один раз и принимает файл только при совпадении размера и SHA-256. `PromptDraft` держит черновик в конце поля и заменяет его, а не дописывает.

`browser-extension/` содержит первый ChatGPT transport. Desktop-приложение отдаёт ему pending requests через локальный HTTP bridge; extension взаимодействует с DOM уже открытой беседы и возвращает progress/response. Bridge предлагает запрос вкладке снова, пока та о нём молчит, и завершает его ошибкой, если вкладка пропала совсем; companion помнит в `chrome.storage.local`, дошёл ли prompt до чата, поэтому повторная доставка (после перезагрузки страницы или расширения) только подхватывает ответ. Browser-specific код не попадает в модели Link или `MainWindow`.

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

Пользователь остаётся в текущем рабочем приложении.

Ближайший UX pass:
- один Region context вместо накопления дубликатов;
- последний assistant response отдельно для каждого проекта;
- global Send hotkey;
- **vertical resize** overlay (horizontal resize уже работает);
- уведомление «ответ готов», когда LINK скрыт;
- корректно latched current work target.

После этого основной следующий слой — local Voice / Conversation по [зафиксированному плану](docs/VOICE_CONVERSATION_PLAN.md): live transcript (сделан), stronger final Whisper, local TTS, Voice Reply и Conversation Mode. Затем — project memory, context router и handoff.

## Зависимости и ограничения

- Для локального распознавания речи используются `Whisper.net` с CPU- и Vulkan-runtime. При первом использовании Voice загружается мультиязычная модель Base (148 МБ) в `%LocalAppData%\Link\Models` и работает на CPU. Если Whisper.net смог загрузить Vulkan-сборку, после первой диктовки в фоне скачивается large-v3-turbo q5_0 (574 МБ) и один раз прогревается на видеокарте (первая компиляция шейдеров ~10–15 с); дальше финальный текст даёт он — примерно за секунду вместе с загрузкой модели и заметно точнее, особенно на английских терминах в русской речи. Turbo включается только если whisper.cpp подтвердил работу на GPU; без видеокарты и при сбое GPU остаётся Base. Для runtime нужны Windows 11+, интернет при первой загрузке моделей и Microsoft Visual C++ Redistributable 2022 x64; для GPU — драйвер видеокарты с Vulkan.
- Live-черновик и VAD — `org.k2fsa.sherpa.onnx` (Apache-2.0, ONNX Runtime на CPU). Модели: Silero VAD v5 (MIT), small streaming Zipformer RU от Alpha Cephei / Vosk (Apache-2.0), 20M streaming Zipformer EN из icefall (Apache-2.0); скачиваются с GitHub/Hugging Face по закреплённым ревизиям. Распознаватель занимает ~75–100 МБ RAM, пока загружен. Сборка — только win-x64 (`RuntimeIdentifier`), чтобы не копировать нативные библиотеки других платформ.
- Голосовые ответы идут через `System.Speech` — официальную обёртку Microsoft над SAPI, которая есть в Windows, — и голоса, установленные в Windows (классические Desktop и OneCore, например Irina, Pavel, Zira). Ничего не скачивается. Если русского голоса нет, Settings подскажет, где его добавить. Это не нейросетевые голоса: звучат заметно синтетически.
- Browser transport требует запущенный Chrome/Edge, вручную загруженный unpacked companion и открытую вкладку связанной беседы.
- Companion зависит от DOM ChatGPT. Конец ответа он узнаёт по нескольким признакам (кнопки действий хода, видимая кнопка Stop, `data-message-complete`, ответ, который перестал меняться), поэтому смена одного селектора не теряет ответ; после крупных изменений UI селекторы всё же придётся обновить — для этого необычные завершения и ошибки пишут в `link.log`, что видела вкладка.
- Переключение режима ChatGPT для Fast/Deep эвристическое: companion читает текущий режим с кнопки в composer (Instant / Medium / High, по-русски «Мгновенный» / «Средний» / «Высокий»), переключает на Instant / High, если чат в другом режиме, и после выбора проверяет, что кнопка показывает новый режим; Normal возвращает режим, выбранный до LINK. Селектор модели в шапке («ChatGPT») используется, только если в composer пикера нет (старый UI с Instant / Auto / Thinking). Если переключить не удалось, статус Link пишет «ChatGPT mode not switched (…)» с причиной или списком предложенных пунктов, а подсказка о длине ответа всё равно уходит в запрос.
- Сейчас транспорт есть только для ChatGPT; Claude-чат подключается тем же `IChatIntegration` отдельным адаптером (ещё не реализован).
- Качество и скорость Voice зависят от микрофона и CPU; на первой попытке нужно дождаться загрузки модели.
- Захват защищённого DRM/UAC content может вернуть пустое изображение — это ограничение Windows/GDI.
