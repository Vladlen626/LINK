# Карта LINK

## Проект
- `src/Link.Desktop/` — WPF-приложение .NET 10; агенты и интеграции находятся в `Services/`.
- `tests/Link.Desktop.Tests/` — консольный runner всех C# и browser checks.
- `companion/` — встроенная интеграция ChatGPT; `docs/harness/` — правила задач агентов.
- Пользовательский Release: `src/Link.Desktop/bin/Release/net10.0-windows/Link.exe`.

## Изменения агента и VCS
- `Services/AgentChanges.cs` снимает baseline, показывает diff и выполняет безопасный undo.
- Git определяется через `git rev-parse`; Perforce — по `.p4config` и успешному `p4 -ztag info`.
- Для P4 статус объединяет `p4 fstat -Ro` (локальные пути; `opened` даёт синтаксис клиента) и сухой
  `p4 reconcile -n -e -a -d -m`; backup workspace не создаётся. Файлы задачи после прогона —
  `KeepInChangelistAsync` → её pending changelist; правила агента для P4 — `docs/harness/perforce.md`.
- Undo не трогает файлы, которые уже были открыты/изменены до задачи; чистые файлы возвращаются через
  `p4 revert` и `p4 sync -f <file>#have`. Истёкший ticket требует входа через P4V или `p4 login`.
- Без Git/Perforce действует ограниченная backup-копия из `%LocalAppData%/Link/Backups`.

## Проверка
- По skill `rebuild-link`: сначала scratch build/tests, затем только без активного агента закрыть LINK,
  `dotnet build Link.sln -c Release`, запустить tests и снова открыть LINK.
