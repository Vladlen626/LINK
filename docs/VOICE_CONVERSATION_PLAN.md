# LINK Voice & Conversation Architecture

Статус: принятая архитектура / план реализации  
Дата: 2026-09-24

## Цель

Сделать LINK полноценным голосовым companion без обязательной поминутной оплаты:

```text
голос пользователя
→ локальное распознавание в реальном времени
→ финальная коррекция текста
→ обычный PromptRequest LINK
→ GPT + project context + screenshot + web/search/thinking
→ письменный ответ в overlay
→ локальная озвучка
→ следующий голосовой ход
```

Главное решение: **GPT остаётся мозгом LINK**. Speech-слой отвечает только за слух, детекцию речи и голос. Мы не переносим reasoning, поиск, vision, project memory или историю разговора в отдельный voice provider.

Core voice path должен работать локально и без лимита минут. Облачные TTS/STT можно позже добавить только как optional adapters.

## Что уже есть

На момент принятия плана в LINK уже работают:

- C# / .NET 10 / WPF;
- `waveIn` microphone capture, 16 kHz mono;
- выбор микрофона и тест уровня в Settings;
- PTT кнопкой и глобальным hotkey;
- `Whisper.net` + CPU runtime;
- текущая мультиязычная Whisper Base модель;
- RU / EN / Auto;
- transcript после отпускания PTT попадает в `PromptBox`;
- `PromptRequest` уже умеет text + screenshot + clipboard + current app + project;
- ChatGPT browser bridge, thinking/search внутри связанной GPT-беседы и возврат ответа в LINK;
- streamed progress / final response UI;
- Auto active-window screenshot на Send.

То есть голосовую систему нужно **наращивать вокруг существующего request/chat pipeline**, а не строить второй AI-клиент.

## Основные продуктовые режимы

### Text

Обычная работа LINK.

- ввод с клавиатуры;
- обычный GPT response;
- TTS выключен;
- подробные Markdown/code ответы остаются как сейчас.

### Voice Reply

Пользователь говорит через PTT, но отправка остаётся контролируемой.

```text
PTT
→ live transcript
→ final transcript
→ Send hotkey / Enter
→ GPT
→ written response
→ LINK произносит короткую полезную часть ответа
```

Полный ответ всегда остаётся в overlay. Голос не обязан читать большой Markdown, код и длинные списки.

### Conversation

Hands-free / Jarvis-like режим.

```text
Listening
→ partial transcript
→ endpoint / silence
→ final correction
→ auto Send
→ Waiting
→ GPT response
→ Speaking
→ Listening
```

В этом режиме LINK автоматически отправляет завершённую реплику и возвращается к слушанию после ответа.

## Архитектурное решение по speech stack

### Единый local speech runtime: sherpa-onnx

Основной кандидат — `sherpa-onnx`.

Причины:

- официальный .NET/C# API;
- streaming ASR;
- microphone examples;
- Silero VAD;
- offline TTS;
- speech enhancement;
- Windows support;
- Apache-2.0 для самого runtime;
- один runtime закрывает несколько speech-задач вместо набора случайных библиотек.

Важно: лицензия runtime и лицензии конкретных model weights — разные вещи. Перед распространением LINK лицензия выбранной модели проверяется отдельно.

### Live / partial STT

Добавить отдельный boundary:

```csharp
IStreamingSpeechToText
```

Он принимает живые PCM chunks и отдаёт partial/final segment events.

Цель UI:

```text
"почему..."
"почему у меня..."
"почему у меня материал..."
```

Текущая фраза должна обновляться как **voice draft**, а не дописываться новыми копиями.

Для explicit Russian / English можно использовать отдельные лёгкие streaming-модели sherpa-onnx.

Для `Auto` есть два этапа:

1. MVP: live model выбирается из текущей user language настройки; если Auto нельзя сделать качественно и быстро, partial может быть ограничен, а финал остаётся Whisper.
2. После benchmark — проверить multilingual streaming модель (например современный multilingual Nemotron streaming в sherpa-onnx) и включить её для Auto только если CPU/RAM/latency приемлемы.

Не блокировать весь voice roadmap из-за идеального Auto на первом шаге.

### VAD / endpointing

Использовать local Silero VAD через sherpa-onnx.

VAD нужен для:

- понимать, когда пользователь действительно начал говорить;
- автоматически заканчивать реплику после паузы в Conversation Mode;
- не отправлять тишину в финальный recognizer;
- later: hands-free без удержания PTT.

Стартовая логика endpoint:

- speech detected → Listening;
- около 0.6–1.0 s устойчивой тишины после речи → finalize utterance;
- значения вынести в voice settings после первой рабочей версии.

PTT остаётся самым надёжным manual override.

### Final STT

Существующий `ISpeechToText` и Whisper остаются.

Но Base — временный компромисс.

Следующая целевая модель:

`Whisper large-v3-turbo` через текущий `Whisper.net`.

Flow:

```text
streaming ASR = быстрый live draft
Whisper Turbo = окончательная версия после конца фразы
```

Это позволяет live recognizer оптимизировать под latency, а Whisper — под качество.

Нужен простой `SpeechModelManager`, который:

- знает model files;
- скачивает модель один раз;
- проверяет наличие/целостность;
- не блокирует UI thread;
- показывает Downloading / Ready / Error;
- позволяет позже иметь Balanced / Quality profile.

Если large-v3-turbo на пользовательском CPU окажется слишком медленным, архитектура не меняется: выбирается более лёгкая final model. Сначала benchmark на реальной машине, потом фиксируем default.

### Speech enhancement

Не добавлять отдельный RNNoise dependency сразу.

Сначала проверить качество:

```text
waveIn PCM
→ VAD / streaming ASR
→ Whisper final
```

Если фон/микрофон реально мешают, sherpa-onnx уже имеет streaming speech enhancement (GTCRN / DPDFNet). Его можно вставить перед VAD/ASR:

```text
microphone
→ optional enhancement
→ VAD
→ live ASR
→ final Whisper
```

Enhancement должен быть optional, потому что лишняя обработка тоже может портить хороший микрофон.

## Изменение AudioRecorder

Сейчас audio capture в первую очередь выдаёт WAV после `Stop()`.

Нужно эволюционировать его в источник живых samples, сохранив совместимость с финальным WAV.

Целевой shape:

```text
waveIn
├─ PCM chunks → streaming STT
├─ PCM chunks → VAD
├─ optional level meter
└─ buffer → WAV → Whisper final
```

Не создавать второй захват микрофона для live recognition.

Один microphone capture session должен кормить все consumers.

Можно ввести:

```csharp
IAudioCaptureSession
IStreamingSpeechToText
ISpeechActivityDetector
ISpeechToText          // final Whisper
```

Текущий `IAudioRecorder` можно расширить/заменить постепенно, без большого rewrite MainWindow за один коммит.

## Работа с PromptBox

Voice draft должен иметь отдельную семантику.

Пример:

```text
existing typed text | current voice draft
```

Во время речи partial recognizer может много раз менять только voice draft.

После final Whisper:

- draft заменяется финальным текстом;
- финальный текст становится обычным user text;
- никакого `Voice ContextItem`;
- raw audio не отправляется GPT;
- дублей partial/final быть не должно.

## Локальная озвучка ответа

Добавить provider-neutral boundary:

```csharp
ITextToSpeech
```

Основной бесплатный local candidate:

`sherpa-onnx + Supertonic 3 int8`.

Почему:

- offline;
- русский и английский;
- несколько speakers;
- нормальная скорость;
- доступен через sherpa OfflineTts;
- API поддерживает callback во время генерации, поэтому можно постепенно подавать audio в playback и уметь остановить генерацию.

Для первой версии:

```text
assistant text
→ SpeechTextSelector
→ ITextToSpeech
→ PCM
→ AudioPlaybackService
```

### Playback

Не добавлять тяжёлую multimedia dependency только ради звука.

У LINK уже есть WinMM integration для микрофона. Для первого local TTS player можно использовать Windows `waveOut`:

- очередь PCM buffers;
- Play;
- Stop;
- мгновенный interrupt;
- Dispose;
- volume.

Если позже понадобится более сложный mixer/echo cancellation, можно перейти на WASAPI.

## Что именно читать голосом

Полный письменный ответ остаётся source of truth.

Не читать вслух автоматически:

- огромный код;
- таблицы;
- URL;
- длинные bullet lists;
- технический Markdown слово-в-слово.

Добавить `SpeechTextSelector`.

Базовая политика:

- короткий ответ → прочитать полностью;
- длинный ответ → первые 1–3 естественные фразы / первый короткий абзац;
- код и Markdown остаются глазами в overlay;
- кнопка/команда `Read full answer` может быть добавлена позже.

## GPT response style

Voice не должен заменять GPT Voice Mode.

LINK продолжает отправлять обычный `PromptRequest` в связанную GPT-беседу, поэтому сохраняются:

- screenshots;
- current app;
- project/current task;
- history linked chat;
- GPT reasoning;
- web search;
- browser bridge.

В request добавить provider-neutral interaction hint:

```text
Interaction mode: Text | VoiceReply | Conversation
```

Для `Conversation`:

- начать ответ естественно и разговорно;
- сначала дать прямой короткий ответ;
- не начинать с длинного списка;
- детали оставить после короткой spoken-friendly части;
- код/точные шаги можно оставить в письменной части.

Это не означает, что все ответы становятся короткими. GPT всё ещё может дать полный технический ответ в overlay.

## Screenshots в Conversation Mode

Никакого отдельного voice chat.

Conversation Mode использует тот же `PromptRequest`.

```text
final transcript
+ active project
+ current task
+ latched work target
+ optional current app screenshot
+ explicit Region/Clipboard context
→ GPT
```

Поэтому голосовой вопрос вида «посмотри экран, что тут не так?» работает с тем же vision pipeline.

На первом этапе Conversation Mode просто уважает существующий `Auto active window on send`.

Позже можно добавить intent-triggered screen capture для фраз типа:

- «посмотри экран»;
- «что здесь не так»;
- «вот тут».

Но explicit user setting всегда важнее эвристики.

## Conversation Controller

Добавить orchestration class, а не разрастать `MainWindow.Voice.cs`.

Например:

```csharp
VoiceConversationController
```

Состояния:

```text
Idle
Listening
Finalizing
ReadyToSend
Sending
Waiting
Receiving
Speaking
Error
```

MainWindow отображает state и события, но не владеет всей voice state machine.

### Conversation flow

```text
Conversation ON
↓
Listening
↓
partial text
↓
VAD endpoint
↓
Whisper final correction
↓
build normal PromptRequest
↓
auto-send
↓
Waiting
↓
response
↓
TTS
↓
Speaking
↓
Listening
```

После нескольких секунд без следующей речи можно оставаться в passive listening или уйти в Idle — это отдельная UX-настройка.

## Waiting cues — ощущение живого ассистента

Во время долгого GPT response LINK может иногда говорить короткие локальные фразы.

Примеры:

- «Секунду.»
- «Думаю над ответом.»
- «Ещё немного.»
- «Получаю ответ.»

Правила:

- не говорить сразу после Send;
- первая реплика только если ожидание реально затянулось;
- не спамить чаще примерно одного cue за 8–15 секунд;
- не говорить «ищу в интернете», если transport реально не сообщил search state;
- cues должны быть короткими и не маскировать ошибки.

Эти фразы лучше один раз синтезировать выбранным local voice и держать в маленьком cache.

## Barge-in / interrupt

Ключевая Jarvis-функция.

Если LINK говорит и пользователь нажимает PTT:

```text
Speaking
→ Stop TTS immediately
→ Listening
```

Это первая версия interruption и она не требует acoustic echo cancellation.

Позже можно добавить настоящую hands-free barge-in по VAD, но тогда понадобится echo cancellation / suppression, чтобы LINK не распознавал собственный голос из динамиков.

До этого момента:

- PTT всегда имеет приоритет;
- TTS отключает автоматический open-mic;
- manual interrupt работает мгновенно.

## Notifications

Voice mode дополняет, но не заменяет Windows notification.

Если LINK скрыт и обычный text request завершился:

- system/tray notification «Ответ готов»;
- click → открыть LINK и нужный project response.

Если Conversation Mode включён и TTS активен, spoken response уже является notification, поэтому Windows toast можно не показывать.

## Настройки

Не перегружать Settings сразу.

Минимально:

### Voice input
- Language: Auto / Russian / English;
- Microphone;
- Live transcription: On/Off;
- Final recognition quality: Balanced / Quality (после benchmark).

### Voice output
- Speak responses: Off / Voice requests / Always;
- Voice / speaker;
- Speed;
- Test voice.

### Conversation
- Conversation Mode On/Off;
- Auto-send after speech;
- Waiting cues On/Off.

Advanced VAD thresholds не показывать, пока реально не понадобятся.

## Порядок реализации

### Phase 0 — текущие UX fixes

Сначала закрыть уже найденные небольшие проблемы:

- Region context dedupe + selector re-entry guard;
- last assistant response per project;
- global Send hotkey;
- **vertical resize** overlay; horizontal resize уже работает и его не ломать;
- notification «response ready»;
- latched work target для current app / screenshot.

### Phase 1 — audio stream foundation

Статус: сделано 2026-09-24 — `IAudioRecorder.SamplesCaptured` отдаёт каждый 50 ms блок waveIn (16 kHz mono, float) тому же сеансу, что пишет WAV.

Без изменения пользовательского поведения:

- существующий waveIn capture начинает публиковать PCM chunks;
- один capture session используется и для WAV, и для будущих realtime consumers;
- level meter продолжает работать.

### Phase 2 — final STT quality

Статус: сделано 2026-09-25. Замер на машине пользователя (Ryzen 5 5600, RTX 4070 SUPER), фраза ~7 с, «почему у меня материал в анриле не светится, хотя эмиссив включён»:

| модель | где | время | результат |
|---|---|---|---|
| Base | CPU, 8 потоков | 0,85 с | «в анрилине светится, хотя и миссия» |
| small q5_1 | CPU | 3,0 с | «в Анриле не светится, хотя амисси» |
| GigaAM v3 (sherpa, RU) | CPU | 0,3 с | «в Ангеле не светится, хотя включён» — теряет английские термины |
| large-v3-turbo q5_0 | CPU | 13,7 с | лучший текст, но слишком медленно; с укороченным `audio_ctx` 2–4 с и зацикливается |
| large-v3-turbo q5_0 | GPU, Vulkan | 0,25 с + ~0,8 с загрузка | «в анреле не светится, хотя эмиссив включён» |

Решение (согласовано с пользователем): `Whisper.net.Runtime.Vulkan` + turbo на GPU. После первой диктовки, если Whisper.net загрузил Vulkan-сборку, turbo (574 МБ, один раз) скачивается в фоне и прогревается (первый запуск компилирует шейдеры ~10–15 с, дальше драйвер держит кэш). Turbo включается, только если whisper.cpp в логе подтвердил GPU-бэкенд; до этого, без GPU и при сбое GPU посреди сессии — Base на CPU (`UseGpu = false`, без компиляции шейдеров). Скачивание Base тоже перенесено в `SpeechModelManager` (прогресс в %, SHA-256). Отдельной настройки качества нет: выбор автоматический.

- вынести model download/lifecycle в SpeechModelManager;
- попробовать `Whisper large-v3-turbo`;
- измерить latency на реальном PC;
- выбрать default quality profile;
- оставить текущий Base только как fallback до завершения migration.

### Phase 3 — local VAD + live transcript

Статус: сделано 2026-09-24 для PTT. `org.k2fsa.sherpa.onnx` 1.13.8; модели — Silero VAD v5 (v4 пропускал русские фразы), Vosk small streaming Zipformer RU int8 (28 МБ), icefall 20M streaming Zipformer EN int8 (43 МБ). Auto: черновик на языке прошлого транскрипта, в первый раз — на языке Windows. VAD пока только решает, запускать ли Whisper (сомнительное — речь); обрезать запись по VAD не стали: Silero местами недосчитывает речь, а `WithOffset/WithDuration` Whisper.net на деле не обрезают. Замер на 12-поточном CPU: загрузка RU-распознавателя ~2 с и ~75–100 МБ (кэш с выгрузкой через 10 мин простоя), декодирование ~30× быстрее речи на одном ядре.

- добавить `org.k2fsa.sherpa.onnx`;
- Silero VAD;
- streaming ASR;
- partial voice draft в PromptBox;
- final Whisper correction без дублей;
- PTT flow остаётся рабочим.

Это первый большой пользовательский скачок.

### Phase 4 — local TTS

- `ITextToSpeech`;
- Supertonic 3;
- `AudioPlaybackService`;
- Stop/interrupt;
- voice/speed settings;
- `SpeechTextSelector`.

### Phase 5 — Voice Reply

- голосовой input;
- обычный Send;
- GPT получает `InteractionMode.VoiceReply`;
- после ответа LINK озвучивает spoken-friendly часть;
- full response остаётся в UI.

### Phase 6 — Conversation Mode v1

- VAD endpoint;
- auto finalization;
- auto Send;
- waiting state;
- local waiting cues;
- TTS response;
- после речи снова Listening;
- PTT barge-in.

### Phase 7 — lower latency conversation

После стабильной v1:

- если browser bridge отдаёт достаточно надёжные partial assistant text updates, добавить `SentenceChunker`;
- законченные предложения поступают в TTS queue до полного завершения GPT response;
- не озвучивать незавершённые token fragments.

Если DOM streaming нестабилен — оставить TTS after-complete. Надёжность важнее нескольких секунд latency.

### Phase 8 — optional polish

Только после реального использования:

- streaming speech enhancement;
- wake word;
- hands-free barge-in;
- echo cancellation;
- smart screenshot intent;
- local cached filler phrases;
- optional cloud TTS adapter для premium voices.

## Что НЕ делаем

- не делаем ChatGPT Voice Mode фундаментом LINK;
- не переносим conversation в отдельный AI session без project/screen context;
- не делаем платный speech API обязательной зависимостью;
- не отправляем raw microphone audio в GPT;
- не запускаем несколько микрофонных capture pipelines одновременно;
- не запускаем full Whisper заново каждые 200 ms ради fake streaming;
- не заставляем TTS читать код и длинный Markdown целиком;
- не придумываем статусы «ищу в интернете», если transport этого не знает.

## Зависимости после реализации

Core:

- `Whisper.net` — final STT;
- `Whisper.net.Runtime` — current local runtime;
- `org.k2fsa.sherpa.onnx` — streaming ASR, VAD, local TTS, optional enhancement;
- WinMM / Windows native audio — capture/playback.

Модели хранятся отдельно в:

`%LocalAppData%\Link\Models`

и скачиваются по необходимости, а не кладутся огромными файлами в Git.

## Критерий готового Jarvis-like v1

Пользователь работает в Unreal/Blender/IDE и не трогает браузер:

```text
говорит вопрос
→ видит live transcript
→ LINK понимает конец фразы
→ прикладывает нужный screenshot/context
→ GPT думает/ищет как раньше
→ LINK сообщает, что всё ещё работает, если ожидание долгое
→ показывает полный ответ
→ произносит короткий нормальный ответ
→ пользователь может перебить его PTT
→ LINK снова слушает
```

При этом speech-layer не имеет поминутного лимита и не заменяет GPT как intelligence layer.
