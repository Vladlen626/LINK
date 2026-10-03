# ChatGPT в браузере: общие правила и как писать промпты

Основа — рекомендации OpenAI для GPT-5.x: описывать результат и критерии готовности, не расписывать шаги без нужды,
убирать противоречия (модель тратит рассуждения на их согласование). Плюс наш принцип: чистая цель и контекст без
лишней обвязки.

## Где что лежит

- **Общие правила** (ниже) — Settings → Personalization → Custom instructions: действуют во всех обычных чатах.
- **Проект, с которым связан LINK** — свои инструкции: [chat-instructions.md](chat-instructions.md). По описаниям,
  внутри проекта действуют инструкции проекта, а глобальные могут не применяться, поэтому текст для проекта
  самодостаточный (проверить на своём аккаунте).
- **Память** подмешивает скрытый контекст. Для рабочих проектов надёжнее «только память проекта» или выключенная; то,
  что важно, лежит в состоянии проекта LINK и в инструкциях, где его видно и можно править.
- **Глубина** — режимом (Fast / Normal / Deep в LINK), а не словами «думай тщательно» или «шаг за шагом».

## Общие правила (вставить в Custom instructions, ~1 000 знаков)

```text
Get to the point: the conclusion or answer first, then the reasons. No preamble, compliments or retelling of the question.

- You do not know or are not sure: say so plainly and keep facts apart from guesses. Do not invent links, quotes, names of functions or settings.
- The answer depends on fresh data or versions (prices, APIs, releases): check the web and give links.
- The task is unclear: ask one short question, only if without it the result would be wrong; otherwise pick a reasonable assumption and name it.
- Keep to the goal of the question: do not widen the task or offer extras. A problem in how it is put: say so in one line.
- Length fits the task: simple is 2–4 sentences, complex is a structure with headings. Code in blocks, only what is needed.
- Before "done" on an important task, check the answer yourself: numbers, logic, version compatibility.
- Write in Russian; terms and names as in the original.
```

## Как писать промпты

Минимум, который работает почти всегда:

```text
Цель: что должно получиться и зачем.
Контекст: что приложено, что уже пробовал, чего модель знать не может.
Готово, когда: как пойму, что ответ годится (формат, длина, критерий).
```

1. **Результат, а не шаги.** Путь расписывай, только если важен именно он. Модель обычно выберет лучше.
2. **Приложи, а не пересказывай:** скриншот, текст ошибки, файл, кусок кода. Модели нужно то, чего она не знает, а не
   то, что она умеет.
3. **Одна задача — один промпт.** Большую начни с «задай вопросы, если не хватает» или «предложи план в 3 пункта»,
   потом делай.
4. **Формат — одной фразой:** список, таблица, 5 строк, готовый код, «голосом — коротко».
5. **Ограничения — только настоящие и без противоречий.** «Коротко» и «со всеми подробностями» в одном промпте
   ломают ответ. Капс, «никогда», «всегда» и длинные списки запретов не улучшают, а мешают; вместо «не делай X» —
   «сделай Y».
6. **Проверка для важного:** «как ты это проверил и что может быть неверно?»; для свежих фактов — «проверь в
   интернете, дай ссылки».
7. **Итерируй, а не наращивай.** Ответ плох — скажи, что именно не так и чего хочешь. Не дописывай промпт абзацами. Чат
   разросся и тупит — новый чат с короткой выжимкой (в LINK: New chat).
8. **Примеры — один-два,** и только когда нужен точный формат.
9. **Агенту (Claude, Codex):** цель, контекст и «как проверим»; метод оставь ему. План — его первым ответом, если
   задача большая.
