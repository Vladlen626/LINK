# Харнесс: самообучение без порчи, инструменты Unreal и Blender

Команда для нового чата: «Выполни этап из docs/stages/harness-learning.md». Решения пользователя — от 2026-10-04,
вечер; начать утром 2026-10-05.

- **Зачем:** агенты в BigSteps тратят токены на разведку: противоречивые skills, нет готовых инструментов, знание
  теряется на оборванных прогонах. Харнесс должен заставлять их учиться и убирать за собой так, чтобы знание
  становилось точнее, а не длиннее и не хуже.
- **Прочитать:** `CLAUDE.md`, `docs/harness/agent-rules.md`, skill `rebuild-link`, `docs/map.md` и
  `.agents/skills/*` в `C:\Users\vladl\projects\BigSteps_GlobalFolder`.

## Что видно по прогонам 2026-10-04 (лазание Nori)

- Codex в трёх прогонах читал C++ моста (`UE_MCP_Bridge/.../BlueprintHandlers_Graph*`), чтобы узнать параметры
  `add_node` и `connect_pins_batch`; прогон оборвался на лимите, и это знание пропало.
- Claude написал 4 временные обёртки (`$TEMP/claude_lvl.mjs`, `claude_read.mjs`, `claude_compile.mjs`,
  `claude_save.mjs`) для обычных действий: открыть уровень, прочитать граф, собрать, сохранить.
- Править Blueprint учат три skills по-разному: `unreal-blueprint-edit` (DSL старого HTTP MCP), `ue-mcp-blueprint`
  и `ue-mcp-epic-routing` («бери DSL» с припиской, что в BigSteps он виснет). Рабочий путь (Node-скрипт с `Graph`
  из `tools/checks/wall_bridge.mjs`) описан только в карте.
- Агент заменил `Nori_Jump_Air` пользователя своим циклом под тем же именем; карта записала его версию как факт.
  Оригинал — только в `%TEMP%\blender_backup\nori_Export_ondisk_2026-09-30.blend`.
- MCP: в прогоны Claude цепляется коннектор claude.ai «Claude Docs»; у Codex Blender MCP и `node_repl` из
  глобального `~/.codex/config.toml` стартуют в любой папке. У Claude Blender MCP нет (он ходит через
  `tools/blender_mcp.py`).
- Тикет P4 истёк: `p4 info` отвечает и без входа, а упавшие `fstat`/`reconcile` Link читает как «изменений нет» —
  прогон показан «0 files changed», changelist 4 не получил файлы и английское описание.
- Метка петли и карточка Start over: в работе с редактором прогресс идёт через MCP и скрипты, а не правки файлов,
  поэтому «searching» и «repeated call» срабатывают на нормальной работе, и карточка с оранжевой кнопкой
  «Start over with a note» всплывает посреди рабочего прогона.

## Перед началом

- В Link не работает агент (нет `claude.exe` / `codex.exe` с родителем `Link.exe`). BigSteps не трогать, пока
  агент там работает; Link пересобирать только без агента (`rebuild-link`). `claude` / `codex` самому не запускать.
- Перечитать skills и карту BigSteps заново: последний прогон Claude (стоп 2026-10-04 23:58) дописал
  `unreal-blueprint-edit` и `unreal-verify`.
- P4: `p4 login -s` — тикет жив. Все правки BigSteps — в отдельный pending changelist с английским описанием
  (`Agent harness: …`), не в changelist задачи (4). Не сабмитить: это делает пользователь.
- Проверку после Do (`CheckCommand`) не добавлять: пользователь не хочет тратить на неё токены, тестирует сам;
  автотесты — может быть, позже.

## Шаги

- **H.1 Правила самообучения** (`docs/harness/agent-rules.md`, раздел «Get stronger with each task» и «How to work»):
  1. Записывать выученное сразу, как доказано, а не в конце: прогон могут оборвать.
  2. Помощник, который понадобится снова, — в tools проекта и в skill, не во временную папку.
  3. Верно и коротко: изменившееся переписывать на месте, без приписок, противоречащих остальному; skill — как
     сейчас, без истории; одно место для каждого факта; skill длиннее ~150 строк — сжать перед дополнением.
  4. Удалять только доказанно неверное или заменённое (слитый skill, старый помощник, временный актор проверки).
     Неиспользованный skill остаётся: он для другой работы.
  5. В ответе называть, что сохранено, переписано и удалено.
  6. Ассет, сделанный пользователем вручную (анимация, модель, текстура, уровень), не перезаписывать под тем же
     именем: свой — рядом, и переключиться на него.
- **H.2 Один источник MCP — `.mcp.json` проекта.** Claude: `--strict-mcp-config` и `--mcp-config <.mcp.json>`, если
  он есть (`--mcp-config` берёт несколько значений, `--allowedTools` остаётся последним). Codex: каждый сервер из
  `$CODEX_HOME/config.toml` (заголовки `[mcp_servers.<имя>]`), которого нет в `.mcp.json`, —
  `-c mcp_servers.<имя>.enabled=false` (поле `enabled` есть в codex-cli 0.154.0). Конфиг пользователя не менять.
  Тесты аргументов.
- **H.3 Тикет P4.** `P4StatusAsync`: stderr `fstat`/`reconcile` с «please login» или «P4PASSWD» — Perforce
  недоступен. До прогона это уже ошибка «Sign in with P4V»; после прогона `AgentCoordinator.RunAsync` не должен
  терять ответ агента: `IOException` из `CompareAsync` — событие Error («Perforce stopped answering…»), изменения
  пустые. Тесты.
- **H.4 Метка петли и карточка Start over.** `AgentLoopDetector`: «searches in a row» сбрасывается любым вызовом,
  который не поиск (скрипт, MCP, сборка), а не только правкой файла; не-поисковый вызов забывает другие вызовы (петля
  — тот же вызов трижды, когда между ними ничего не делалось). `RestartCard` не показывать посреди прогона — только
  в конце (провал или Stop пользователя при метке). Тесты детектора.
- **H.5 Настройки Bigsteps.** Убрать одноразовое правило Bash (длинная команда с кадрами `Nori_Jump_Land`) из
  `AgentDoTools` в `projects.json` — при закрытом Link, с копией и проверкой файла после.
- **H.6 Skills BigSteps.**
  - `unreal-blueprint` — лучшее из `unreal-blueprint-edit`, `ue-mcp-blueprint`, `ue-mcp-epic-routing`: рабочий
    путь (Node-скрипт на `tools/ue.mjs`, функция пересобирается с нуля, readback, compile, save), DSL на ue-mcp 1.3.9
    виснет (одна попытка + readback, без повторов, искать недостижимые дубликаты), SCS / CDO / интерфейсы, AnimGraph.
    Три старых удалить.
  - `unreal-mcp` вбирает `ue-mcp-workflow` (статус первым, жизнь редактора, «still initializing»); второй удалить.
  - `ue-mcp-native-cpp`, `ue-mcp-niagara`, `ue-mcp-animation` оставить (C++ и VFX будут позже), поправить в них
    ссылки на удалённые skills.
  - Новый `unreal-animbp` — общий для любых AnimBP: состояния и фазы, переходы и смешивание, sync groups,
    inertialization, приземление → бег без скольжения ступней, процедурные позы (влить `unreal-anim-procedural` и
    удалить его), как здесь собирается AnimGraph, проверка числом в PIE (скорость ступни на земле).
  - Новый `blender-anim` — общий для анимаций персонажей в Blender: подключение (`tools/blender_mcp.py`, Blender
    MCP), риг, экшены пользователя и правило H.1.6, скрипты `tools/blender/*` → `export_ue.py` →
    `import_jump_anims.py` → ABP, грабли (некеянные кости берут позу прошлого экшена при экспорте, обрезка клипа в ABP).
  - Язык — русский, как у проектных skills; каждый до ~150 строк.
- **H.7 `tools/ue.mjs`** (BigSteps, `Big_Steps/tools/`): CLI и модуль над `EditorBridge` ue-mcp — `status`,
  `call <method> [json|@file]`, `compile <asset>…`, `save`, `level <path>`, `pie start|stop|status`,
  `shot <png> [json]`, `log [N] [filter]`. Методы, проверенные скриптами: `read_blueprint_graph`, `add_node`,
  `set_node_property`, `connect_pins_batch`, `invoke_object_function(s)`, `set_object_property`,
  `get_object_properties`, `pie_control`, `capture_scene_png`; из ue-mcp: `compile_blueprint {path}`,
  `load_level {levelPath}`, `get_current_level`, `save_dirty`, `get_output_log`. `Graph` переезжает сюда с
  параметром ассета; `tools/checks/wall_bridge.mjs` реэкспортирует его для `BP_LenoraLovett`. Проверка: импорт обоих
  модулей и `node tools/ue.mjs status` (только чтение).
- **H.8 Blender MCP в `.mcp.json` BigSteps:** `uv --directory C:/Users/vladl/Documents/blender_mcp/mcp run
  blender-mcp` (`p4 edit .mcp.json`).
- **H.9 Оригиналы пользователя.** Копию `%TEMP%\blender_backup\nori_Export_ondisk_2026-09-30.blend` — в P4
  (`Nori_Char_Blender/originals/`, `p4 add`). В карте: какие экшены пользователя, какие агентские, где оригиналы.
- **H.10 Карта BigSteps:** ссылки на новые skills, факты последних прогонов, до ~150 строк.
- **H.11 Итог:** README и PROJECT_CONTEXT (принятые решения), `dotnet build Link.sln -c Release` 0/0, тесты,
  `rebuild-link`, один коммит; отчёт с SHA, номером changelist BigSteps и коротким чеклистом.

## Готово, когда

- В правилах агента есть H.1; Claude и Codex в прогонах Link видят только серверы `.mcp.json`.
- Истёкший тикет P4 останавливает прогон до запуска; посреди прогона ответ агента не теряется.
- Карточка Start over не всплывает посреди прогона; метка петли не срабатывает на чередовании поиска и скриптов.
- В BigSteps: одна инструкция по Blueprint, `unreal-mcp` без дубля, `unreal-animbp`, `blender-anim`, `tools/ue.mjs`,
  Blender MCP в `.mcp.json`, оригинал `.blend` в P4 — всё в отдельном pending changelist.
