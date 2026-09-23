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
- В дальнейшем — conversation mode.
- Speech-to-text предпочтительно локальный.
- В дальнейшем — локальный text-to-speech, чтобы Link мог отвечать голосом без обязательного использования отдельного Voice/Live режима AI-сервиса.
- Транскрипт успешной voice-сессии является user text и не должен повторно отправляться как отдельный voice context.

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
- `System.Speech` для локального STT через установленные Windows recognizers.

Позже, когда появится реальная потребность:
- SQLite для локальной памяти;
- Windows Graphics Capture для vision;
- локальный TTS;
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
Статус: выполнен в первой локальной версии через Windows Speech Recognition.
- microphone capture;
- push-to-talk;
- local transcription;
- RU/EN/auto.

### Milestone 4 — Persistent AI Conversation Bridge
Следующая крупная цель.

Цель milestone: пользователь общается с существующей AI-беседой из Link и получает ответ обратно в overlay, не переключаясь в браузер.

Этапы:
- provider-neutral / transport-neutral chat integration boundary;
- связь проекта с конкретной AI-беседой;
- первый работающий transport для существующего ChatGPT workflow;
- отправка user text + поддерживаемого live context;
- получение assistant response обратно в Link;
- компактное отображение ответа в overlay;
- браузер, если используется как transport, работает в фоне и не становится пользовательским UI.

Direct API integration остаётся возможным вторым adapter'ом, но не должна быть обязательным фундаментом продукта.

### Milestone 5 — Projects & Context Brain
Статус: базовый project selector, active project, current task и локальная модель `ContextItem` реализованы. Остаются:
- memory;
- decisions;
- summaries;
- handoff/compaction;
- context router;
- context stack;
- current application context.

### Milestone 6 — Commands
- command palette;
- quicklinks;
- script commands;
- локальные действия.

### Milestone 7 — Voice replies
- local TTS;
- conversation mode;
- interruption/cancel;
- выбор голоса.

## Текущий статус репозитория

Реализована первая используемая локальная версия desktop companion:

- компактный auto-height frosted-glass overlay с завершённым collapsed state, tray и глобальными hotkeys;
- screen capture всех требуемых источников и локальный preview;
- push-to-talk, WAV и локальный RU/EN STT через установленные Windows recognizers;
- clipboard, видимый context stack, проекты и current task;
- versioned `PromptRequest` без AI-интеграции;
- обычные shortcut’ы через `RegisterHotKey`, low-level hook только для key-down/key-up push-to-talk;
- atomic JSON persistence и защищённый lifecycle активных screenshot attachments.

Локальные данные хранятся в `%LocalAppData%\Link`. Код остаётся в одном WPF assembly и разделён на `Models`, `Services`, `Windows` и Presentation.

Следующая крупная задача — построить первый end-to-end AI conversation bridge: Link отправляет запрос в связанную беседу и получает ответ обратно, при этом пользователь остаётся в текущем рабочем приложении.

## Для AI-агентов

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
