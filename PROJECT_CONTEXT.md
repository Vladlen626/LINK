# Link — Project Context

## Что мы создаём

**Link** — персональный Windows desktop companion и системный слой между пользователем, компьютером, проектами и AI.

Главная продуктовая идея: пользователь работает в Unreal, Blender, IDE, игре, браузере или другом приложении и не должен уходить из него в большое окно ChatGPT ради каждого вопроса. Link висит маленьким overlay поверх рабочего процесса, понимает активный проект и текущую задачу, по запросу получает контекст экрана/clipboard/voice/current app, отправляет его в связанную AI-беседу и возвращает ответ обратно в Link.

**UI для пользователя — Link. Браузер, extension, WebView, API или другой транспорт — внутренняя реализация, которую пользователь в нормальном сценарии не должен замечать.**

Целевой UX:

```text
current app / screen / clipboard / voice
                 ↓
            Context Brain
                 ↓
                Link
                 ↓
        Chat Integration Layer
                 ↓
      existing AI conversation
                 ↓
             response
                 ↓
      Link overlay / TTS / action
```

Link должен ощущаться как маленький персональный «Jarvis» для ежедневной работы: быстро спросить, показать экран, уточнить ошибку, продолжить проектный разговор или позже выполнить локальное действие, не разрывая основной рабочий поток.

Название Link одновременно означает «связь» и является личной отсылкой к Link из Zelda. В продукте не нужно копировать визуальную айдентику Nintendo/Zelda.

## Product north star

Обычный сценарий должен выглядеть примерно так:

1. Пользователь работает в текущем приложении и не переключается из него.
2. Link уже знает активный проект и Current Task.
3. Пользователь пишет или говорит вопрос.
4. При необходимости Link добавляет screen / clipboard / current app / project context.
5. Link отправляет запрос в связанную AI-беседу.
6. Ответ возвращается в overlay.
7. Пользователь продолжает работу в исходном приложении.

Браузер не является частью пользовательского workflow. Если browser-based transport используется внутри интеграции, он может быть запущен или свёрнут в фоне, но Link не должен требовать от пользователя вручную переключаться в него для обычного обмена сообщениями.

В долгосрочной перспективе Link должен не только разговаривать с AI, но и связывать AI с локальными действиями на ПК, project memory, command palette и автоматизацией.

## Основные продуктовые принципы

- Windows-first.
- Быстрый старт и низкое потребление ресурсов.
- Link — маленький floating companion, а не полноразмерный desktop chat client.
- Пользователь должен оставаться в текущем рабочем приложении во время обычного общения с AI.
- Local-first там, где это разумно: настройки, память проектов, скриншоты, голосовая предобработка, команды.
- Не отправлять экран/микрофон постоянно без явного режима пользователя.
- Chat integration / transport должен быть заменяемым; приложение не должно быть жёстко связано с одной моделью, провайдером или способом доставки.
- По возможности использовать существующие пользовательские AI-чаты и подписку вместо обязательного отдельного API-бюджета.
- Browser automation не должна проникать в core-модель Link: browser bridge, API, local model и другие способы связи должны быть адаптерами вокруг общего request/context слоя.
- Контекст должен быть видимым и управляемым: пользователь должен понимать, что именно Link отправляет в запрос.
- Простота важнее overengineering. Фичи добавляются этапами.

## Ключевые функции

### 1. Overlay
- Полупрозрачное borderless окно.
- Always-on-top.
- Компактное состояние, которое не мешает работе.
- Перетаскивание.
- Настраиваемая прозрачность.
- Click-through в неактивном состоянии.
- Быстрое раскрытие/скрытие по глобальному хоткею.
- System tray.
- В дальнейшем — компактное отображение AI-ответа без необходимости открывать браузер.

### 2. Vision
Link должен уметь по команде пользователя получать:
- весь экран;
- все мониторы;
- активное окно;
- выделенную область;
- при необходимости последние локальные снимки для сравнения.

Экран не должен постоянно отправляться куда-либо без явного режима.

Долгосрочная цель — current screen/current app context должен делать AI значительно полезнее, чем обычный отдельный чат: пользователь может спросить «что здесь не так?» или «что мне делать дальше?» без ручного переноса информации между приложениями.

### 3. Voice
- Push-to-talk по глобальному хоткею.
- Все хоткеи переназначаемые.
- Русский, английский и auto language.
- Core speech stack должен работать локально и без поминутного лимита.
- GPT остаётся intelligence layer: reasoning, search, screenshots, project context и linked chat не переносятся в отдельный voice provider.
- Целевой input: live partial STT → локальный Whisper final correction.
- Целевой output: local TTS с interrupt/barge-in.
- Нужны режимы Text / Voice Reply / Conversation.
- В Conversation Mode финальная voice-реплика автоматически отправляется обычным `PromptRequest`, поэтому screenshots/current app/project context продолжают работать.
- Транскрипт успешной voice-сессии является user text и не должен повторно отправляться как отдельный voice context.
- Полный принятый план: [docs/VOICE_CONVERSATION_PLAN.md](docs/VOICE_CONVERSATION_PLAN.md).

### 4. Projects
Пользователь выбирает активный проект, например:
- Big Steps / Zelda-like;
- рабочий проект;
- английский;
- другие проекты.

У проекта могут быть:
- связанные AI-чаты;
- project memory;
- decisions;
- open questions;
- current task;
- chat summaries;
- локальные файлы/папки;
- настройки контекста.

Проект должен позволять связать рабочий контекст Link с конкретной долгоживущей AI-беседой, чтобы пользователь мог продолжать существующий разговор из overlay.

### 5. Context Brain
Контекст разделяется на уровни:

GLOBAL
→ PROJECT
→ DECISIONS
→ CURRENT TASK
→ RELEVANT CHAT SUMMARIES
→ LIVE SCREEN / CLIPBOARD / APP

Link не должен отправлять всё подряд. Context Router выбирает только релевантные части.

Пользователь должен видеть Context Stack и иметь возможность исключить отдельные элементы перед отправкой.

### 6. Persistent AI conversation bridge
Link должен уметь общаться с существующей AI-беседой, не заставляя пользователя открывать её UI поверх рабочего приложения.

Core flow:

```text
PromptRequest
→ IChatIntegration / equivalent boundary
→ transport adapter
→ existing conversation
→ assistant response
→ Link overlay
```

Возможные transport adapters:
- browser companion / extension;
- direct provider API;
- local model;
- другие поддерживаемые интеграции.

Первый transport может использовать браузер в фоне, но обычный пользовательский сценарий не должен требовать:
- ручного перехода в браузер;
- ручного copy/paste каждого запроса;
- ручного получения ответа из чата.

Сам browser UI не является интерфейсом Link.

### 7. Chat handoff / compaction
Link должен помогать переносить работу между длинными чатами.

Нужны режимы:
- Full;
- Compact;
- Tiny.

Handoff должен сохранять:
- цель;
- важные правила проекта;
- уже сделанное;
- решения;
- текущий статус;
- следующий шаг;
- открытые вопросы.

В будущем — объединение контекста нескольких чатов.

### 8. Decision Memory
Решения хранятся отдельно от обычных summary, чтобы важные проектные договорённости не терялись при очередном сжатии.

Пример:
- принятое решение;
- дата/источник при необходимости;
- статус active/deprecated.

Отдельно хранить Open Questions.

### 9. Clipboard / Current App context
Link может использовать по явному действию или настройке:
- clipboard;
- название активного процесса;
- заголовок окна;
- выделенный текст;
- файлы.

Current application context — важная часть будущего «Jarvis»-сценария: Link должен понимать, с чем пользователь работает прямо сейчас, не заставляя его объяснять это вручную в каждом сообщении.

### 10. Command Palette / Actions
По аналогии с сильными сторонами Raycast:
- единая command palette;
- запуск приложений;
- открытие файлов/папок/ссылок;
- пользовательские quick commands;
- Script Commands через PowerShell/Python/EXE;
- позже — безопасные локальные действия голосом.

Локальные простые действия не должны требовать AI.

## Предварительный стек

Сейчас:
- C#;
- .NET 10;
- WPF;
- Windows native APIs;
- `Whisper.net` с CPU runtime и локальной мультиязычной моделью Base для final STT.

Принятый voice roadmap:
- `sherpa-onnx` как единый local runtime для streaming ASR, Silero VAD, local TTS и при необходимости speech enhancement;
- `Whisper large-v3-turbo` как целевой final correction после benchmark на реальной машине;
- Supertonic 3 через sherpa-onnx как первый кандидат на локальный RU/EN TTS;
- Windows native audio playback с interrupt;
- cloud speech providers допускаются позже только как optional adapters, не как обязательная зависимость.

Позже, когда появится реальная потребность:
- SQLite для локальной памяти;
- Windows Graphics Capture для vision;
- browser companion/extension на TypeScript для поддерживаемых пользовательских интеграций.

Не добавлять эти зависимости заранее без реализуемой фичи.

## Архитектурные границы

Даже если пока существует один .NET project, логически разделять:
- Presentation/UI;
- Application state / use cases;
- Windows integrations;
- Capture;
- Voice;
- Projects/Context;
- Chat Integration adapters;
- Commands.

`PromptRequest` и Link context model принадлежат самому Link. Конкретные способы связи с ChatGPT/API/browser/local AI должны находиться за adapter boundary и не протекать в основной UI/context код.

Не создавать отдельные assemblies только ради «чистой архитектуры», пока размер проекта этого не требует.

## План разработки

### Milestone 0 — Foundation
Статус: выполнен.
- solution;
- минимальное WPF-приложение;
- AGENTS.md;
- .gitignore;
- README.

### Milestone 1 — Link Shell
Статус: выполнен.
- полупрозрачное borderless окно;
- always-on-top;
- drag;
- click-through inactive mode;
- global show/hide hotkey;
- tray icon;
- отдельное Settings окно;
- корректное сохранение/восстановление положения окна и opacity.

### Milestone 2 — Vision
Статус: выполнен.
- full-screen capture;
- multi-monitor capture;
- active-window capture;
- region selection;
- локальный preview.

### Milestone 3 — Voice
Статус: реализована запись и локальная транскрипция через Whisper; нужна ручная проверка после первой загрузки модели.
- microphone capture — waveIn 16 kHz mono с выбором устройства (Core Audio endpoint id хранится в settings), тест и индикатор уровня в Settings;
- push-to-talk;
- local transcription — записи без сигнала (< ~-40 dBFS) и короче 0.3 с в Whisper не передаются;
- RU/EN/auto.

Модель скачивается один раз в `%LocalAppData%\Link\Models`; Windows speech pack не нужен. Первый запуск требует интернет, последующие работают локально. Транскрипт попадает в поле запроса и не прикладывается повторно как audio.

### Milestone 4 — Persistent AI Conversation Bridge
Статус: текстовый end-to-end сценарий работает у пользователя. Загрузка изображения дала конкретную ошибку `ChatGPT image input was not found`; расширение обновлено для открытия upload-контрола и ожидания preview, но требуется повторная ручная проверка на реальной вкладке.

Цель milestone: пользователь общается с существующей AI-беседой из Link и получает ответ обратно в overlay, не переключаясь в браузер.

Этапы:
- provider-neutral / transport-neutral `IChatIntegration` boundary — реализован;
- связь проекта с конкретной AI-беседой — реализована;
- browser companion и loopback transport — реализованы;
- отправка user text и clipboard — проверена; путь загрузки image context обновлён, ожидает проверки на реальном ChatGPT;
- получение streamed assistant response обратно в Link — реализовано;
- компактное отображение ответа в overlay — реализовано;
- ручная проверка обновлённого image upload, minimized/background и ответа на запрос с изображением — ожидается;
- браузер, если используется как transport, работает в фоне и не становится пользовательским UI;
- уровень ответа Fast / Normal / Deep — реализован: provider-neutral подсказка в запросе + эвристическое переключение режима ChatGPT (Instant / Auto / Thinking) в companion; переключение на реальном залогиненном ChatGPT ожидает ручной проверки;
- ответ восстанавливается как Markdown из DOM (код, списки, таблицы) — проверено на новом UI ChatGPT;
- отмена (Stop/Esc) доходит до вкладки и останавливает генерацию; вкладка без ответа даёт ошибку через 20 с — реализовано и проверено изолированными тестами bridge.

Direct API integration остаётся возможным вторым adapter'ом, но не должна быть обязательным фундаментом продукта.

### Milestone 5 — Projects & Context Brain
Статус: project selector, active project, current task, `ContextItem`, метаданные предыдущего рабочего окна и опциональный Auto active window capture реализованы. Остаются:
- memory;
- decisions;
- summaries;
- handoff/compaction;
- context router;
- выбор контекста по релевантности и долговременная память.

### Milestone 6 — Commands
- command palette;
- quicklinks;
- script commands;
- локальные действия.

### Milestone 7 — Voice & Conversation
Принята архитектура local-first без поминутной оплаты. Полный план — [docs/VOICE_CONVERSATION_PLAN.md](docs/VOICE_CONVERSATION_PLAN.md).

Порядок:
- общий live PCM stream из существующего waveIn capture;
- local VAD + streaming partial STT через sherpa-onnx;
- финальная коррекция Whisper (целевой benchmark: large-v3-turbo);
- local TTS (первый кандидат: Supertonic 3);
- Voice Reply;
- Conversation Mode с auto-send, waiting cues и PTT interrupt;
- позже — lower-latency sentence streaming, enhancement, wake word и echo cancellation.

## Текущий статус репозитория

Реализована первая используемая локальная версия desktop companion:

- компактный auto-height frosted-glass overlay с завершённым collapsed state, tray и глобальными hotkeys;
- screen capture всех требуемых источников и локальный preview;
- push-to-talk, WAV и локальный RU/EN/Auto STT через Whisper;
- clipboard, видимый context stack, проекты и current task;
- canonical versioned `PromptRequest` для application и integration flow;
- browser ChatGPT integration через локальный loopback bridge;
- project-to-conversation linking, send/progress/response flow и response UI;
- `Enter` для отправки и `Shift+Enter` для новой строки; Stop/Esc отменяет запрос;
- уровень ответа Fast / Normal / Deep рядом со статусом чата;
- ширина overlay 320–1200 px (ресайз только по бокам, высота остаётся auto);
- выбор и тест микрофона в Settings;
- current application metadata и включаемый в Screen-меню Auto active window capture на каждый Send;
- обычные shortcut’ы через `RegisterHotKey`, low-level hook только для key-down/key-up push-to-talk;
- atomic JSON persistence и защищённый lifecycle активных screenshot attachments;
- `link.log` для неожиданных ошибок: после старта приложение предупреждает и продолжает работу, ошибка на старте показывает сообщение и завершает процесс.

Локальные данные хранятся в `%LocalAppData%\Link`. Код остаётся в одном WPF assembly и разделён на `Models`, `Services`, `Windows` и Presentation.

Следующая задача — проверить новую версию на реальном рабочем столе:
1. Завершить старый Link через tray → Exit, пересобрать и запустить; нажать **Reload** у unpacked browser extension (версия 0.3.0) и обновить связанную вкладку ChatGPT.
2. Enter в поле Link отправляет запрос, Shift+Enter оставляет новую строку; во время ответа Stop/Esc отменяет его и останавливает генерацию в ChatGPT.
3. Fast / Normal / Deep: после первого запроса в статусе Link видно «ChatGPT mode: Instant/Auto/Thinking» или «ChatGPT mode not switched (причина)». Причину прислать, если переключение не сработало.
4. Ответ с кодом/списком/таблицей отображается структурированно, Copy даёт Markdown.
5. Settings → Microphone: выбрать микрофон, нажать Test (полоска уровня и «Works — peak level …»), затем Voice/push-to-talk.
6. Растянуть overlay за левый/правый край — ширина до 1200 px, после отпускания высота продолжает подстраиваться под ответ.
7. Выделить регион и отправить запрос с изображением (проверка upload, как раньше).

Предел текущей реализации: browser companion привязан к DOM ChatGPT и требует ручного Reload после изменения extension. Новый UI ChatGPT («octane», замечен у незалогиненных пользователей) поддержан и проверен без логина; классический залогиненный UI проверяет пользователь. Переключение режима модели эвристическое. Claude-транспорта пока нет: effort-подсказка для него уже provider-neutral, а переключение extended thinking — задача будущего адаптера. Цель продукта остаётся прежней: Link — маленький overlay поверх текущего приложения; браузер служит скрытым транспортом, а долгосрочно нужны project memory, context router, handoff между чатами, команды и голосовой ответ.


## Следующая сессия — зафиксированные UX-задачи (2026-09-24)

Эти пункты замечены пользователем на реальной текущей версии и являются приоритетом следующего прохода:

1. **Region capture: не допускать несколько одинаково активных регионов.**
   - Сейчас каждый успешный `CaptureRegionAsync()` вызывает `AddScreenshot(result)`, поэтому несколько Region-capture накапливаются в `_context` и все уходят в следующий запрос.
   - Требуемое поведение: одновременно только один manual Region context. Новый успешно выбранный регион заменяет предыдущий Region screenshot.
   - Дополнительно нужен guard от повторного запуска `RegionSelectionWindow`, пока первый selector ещё открыт: повторный hotkey/click в этот момент просто игнорируется.

2. **Последний ответ должен быть project-scoped.**
   - Сейчас `_responseText` и ResponsePanel принадлежат `MainWindow`, а `LinkProject` не хранит последний assistant response. При `SwitchProject()` меняются task/connection, но остаётся ответ от прошлого проекта.
   - При переключении General / любого созданного проекта overlay должен показывать последний известный assistant response именно связанного чата этого проекта.
   - Минимальный первый вариант: сохранять последний ответ, полученный LINK, per-project и восстанавливать его при `LoadProject/SwitchProject`. Если bridge может надёжно прочитать последний assistant message из уже связанного чата, позже можно синхронизировать и сообщения, появившиеся вне LINK.

3. **Отдельный быстрый hotkey Send.**
   - `Enter` уже отправляет сообщение из PromptBox, `Shift+Enter` делает новую строку.
   - Нужен переназначаемый global shortcut `Send current prompt`, чтобы сценарий voice → release → send выполнялся без мыши и без необходимости переводить фокус в PromptBox.
   - Добавить action в `HotkeySettings`, Settings UI, `GlobalHotkeyService` и App dispatch. Во время активного request hotkey не должен случайно работать как Cancel/Stop.

4. **Live / partial voice transcription во время речи.**
   - Текущий Whisper flow транскрибирует только после `_audio.Stop()`: пока пользователь говорит, PromptBox не меняется.
   - Целевой UX: во время удержания PTT текст постепенно появляется в PromptBox; после отпускания выполняется финальная корректировка/финализация, без дублей.
   - Это не косметический UI fix: нужен streaming/partial STT слой поверх текущего waveIn PCM capture + Whisper, с throttling и безопасной заменой только текущего voice draft.
   - Финальный transcript остаётся обычным user text; raw audio не становится ContextItem.

Порядок следующего прохода: сначала Region dedupe + project-scoped response + Send hotkey как маленькие детерминированные UX-фиксы; затем отдельно сделать streaming voice, не ломая уже работающую запись/Whisper pipeline.

5. **Уведомление, когда AI закончил ответ.**
   - Если LINK скрыт/свёрнут или не активен, по завершении ответа нужен системный сигнал, чтобы пользователь мог отправить задачу, убрать LINK и продолжать работать.
   - Минимальный вариант без новой тяжёлой зависимости: tray notification / balloon через уже существующий tray service.
   - Более polished вариант позже: Windows App Notification (toast), клик по которому показывает LINK и нужный project response.
   - Не уведомлять, если LINK сейчас открыт и активен; ошибки/отмена могут иметь отдельный тип уведомления.

6. **Добавить вертикальный resize; горизонтальный уже работает хорошо.**
   - Пользователь подтвердил, что горизонтальное изменение ширины удобно и его не нужно переделывать.
   - Нужен ручной resize по нижнему краю: пользователь может увеличить/уменьшить высоту overlay.
   - После ручного vertical resize выбранная высота сохраняется и response area использует доступное место.
   - Collapsed mode остаётся фиксированным; horizontal resize 320–1200 px сохранить без изменений.

7. **Voice interaction / spoken assistant.**
   - Решение зафиксировано: core должен быть local-first и без лимита минут; GPT остаётся мозгом и продолжает получать screenshots/project/current-app context.
   - Live text: sherpa-onnx streaming ASR + Silero VAD; final correction: Whisper, целевой benchmark large-v3-turbo.
   - Voice output: `ITextToSpeech`, первый local candidate — Supertonic 3 через sherpa-onnx.
   - Нужны Text / Voice Reply / Conversation modes, auto-send after endpoint, честные waiting cues и PTT interrupt.
   - Во время долгого ожидания можно говорить «Секунду», «Думаю над ответом», «Получаю ответ», но не выдумывать web-search activity.
   - Полный implementation plan и state machine: [docs/VOICE_CONVERSATION_PLAN.md](docs/VOICE_CONVERSATION_PLAN.md).

## Для AI-агентов

Репозиторий: `Vladlen626/LINK`, рабочая ветка: `LeN`. Перед работой проверить фактический HEAD и чистоту дерева. Собирать `Link.sln` в Release; если запущенный Link блокирует `bin\Release\...\Link.exe`, указать отдельный `OutputPath`, не завершая пользовательский процесс без необходимости. Помнить, что пользователь запускает именно `bin\Release`: сборка только в отдельный `OutputPath` оставляет у него старую версию (так «не работал» уже исправленный Enter). После проверки пересобрать `bin\Release` с разрешения пользователя или явно попросить его выйти из Link и пересобрать. После изменения `browser-extension/content.js` обязательно перезагрузить unpacked extension и вкладку ChatGPT — обычная пересборка WPF не обновляет content script в уже открытой вкладке.

Перед каждой существенной задачей:
1. Прочитать `AGENTS.md`.
2. Прочитать этот файл.
3. Изучить существующий код до изменения архитектуры.
4. Реализовывать только текущий milestone/задачу.
5. Не добавлять «на будущее» большие абстракции и зависимости.
6. После работы выполнить сборку и устранить warnings/errors.
7. Кратко описать сделанное, архитектурные решения и что остаётся следующим шагом.

Для проверок, которые зависят от реального Windows desktop, browser session, фокуса окон, logged-in ChatGPT UI или другого окружения пользователя: ограничить самостоятельную проверку примерно 10 минутами. Если результат нельзя надёжно проверить, не гадать — дать пользователю точные шаги ручной проверки и попросить вернуть конкретный результат/скрин/лог.

Если продуктовые решения меняются, обновлять этот файл только когда изменение действительно стало принятым решением проекта.
