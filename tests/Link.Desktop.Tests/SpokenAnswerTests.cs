using System.Collections.Generic;
using System.Linq;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

internal static class SpokenAnswerTests
{
    public static void Run()
    {
        Speaks("short answer", "Да, так можно. Это безопасно.", "Да, так можно. Это безопасно.");
        Speaks("three sentences at most", "Первое. Второе. Третье. Четвёртое.",
            "Первое. Второе. Третье. Подробности на экране.");
        Speaks("heading and markup are skipped", "## Ответ\n\nИспользуй **Nanite** и `Lumen`.",
            "Используй Nanite и Lumen.");
        Speaks("code after the opening", "Вот пример:\n\n```csharp\nvar x = 1;\n```",
            "Вот пример: Подробности на экране.");
        Speaks("code before the opening", "```\nnpm install\n```\n\nКоманда ставит зависимости.",
            "Команда ставит зависимости. Подробности на экране.");
        Speaks("unclosed code block", "Начало.\n\n```js\nconst a = 1;", "Начало. Подробности на экране.");
        Speaks("table", "Сравнение в таблице.\n\n| A | B |\n| --- | --- |\n| 1 | 2 |",
            "Сравнение в таблице. Подробности на экране.");
        Speaks("links keep their text, URLs go",
            "Смотри [README](https://github.com/k2-fsa/sherpa-onnx#readme): там есть пример. Ещё https://k2-fsa.github.io/sherpa/ поможет.",
            "Смотри README: там есть пример. Ещё поможет.");
        Speaks("list items become sentences", "- Открой настройки\n- Нажми **Reset**\n- Перезапусти редактор\n- Проверь лог",
            "Открой настройки. Нажми Reset. Перезапусти редактор. Подробности на экране.");
        Speaks("numbered list", "1) Открой\n2) Закрой", "Открой. Закрой.");
        Speaks("numbered list with stops", "1. Открой настройки.\n2. Включи Nanite.", "Открой настройки. Включи Nanite.");
        Speaks("typical answer",
            "Да — это нормально для Nanite.\n\n## Почему\n\n- Nanite кластеризует меши.\n- Lumen считает свет.\n\n"
            + "```ini\nr.Nanite=1\n```\n\nЕсли не поможет, пришли лог.",
            "Да — это нормально для Nanite. Подробности на экране.");
        Speaks("quote", "> **Важно:** сделай бэкап.\n\nПотом обнови.", "Важно: сделай бэкап. Подробности на экране.");
        Speaks("pictographs and arrows", "✅ Готово → можно запускать.", "Готово, можно запускать.");
        Speaks("abbreviations and decimals do not split",
            "Это файл, т. е. конфиг версии 1.5. Он лежит рядом.", "Это файл, т. е. конфиг версии 1.5. Он лежит рядом.");
        Speaks("English answer", "Sure. Restart the editor and try again.", "Sure. Restart the editor and try again.");
        Speaks("English answer with more", "Restart the editor.\n\n```\ndotnet build\n```",
            "Restart the editor. Details are on screen.");
        Speaks("only code", "```python\nprint(1)\n```", "The answer is on screen.");

        var words = string.Join(' ', Enumerable.Repeat("очень", 15));
        Speaks("long sentences are cut earlier",
            $"Первое {words} длинное. Второе {words} длинное. Третье {words} длинное.",
            $"Первое {words} длинное. Второе {words} длинное. Подробности на экране.");

        Program.Equal("Russian language", Whole("Открой Project Settings и включи Nanite.")?.Language, "ru");
        Program.Equal("English language", Whole("Open Project Settings.")?.Language, "en");

        var stream = new SpokenAnswerStream();
        Program.Equal("the sentence being written waits", stream.Next("Готово")?.Text, null);
        Program.Equal("a sentence is said once the next one begins", stream.Next("Готово. Д")?.Text, "Готово.");
        Program.Equal("nothing is said twice", stream.Next("Готово. Дальше идёт")?.Text, null);
        Program.Equal("the last sentence is said when the answer is final",
            stream.Finish("Готово. Дальше идёт текст.")?.Text, "Дальше идёт текст.");

        stream = new SpokenAnswerStream();
        Program.Equal("a paragraph is complete once the next block begins",
            stream.Next("Вот пример:\n\n```js\nconst a")?.Text, "Вот пример:");

        stream = new SpokenAnswerStream();
        stream.Next("Сначала заметка. Потом");
        Program.Equal("a swapped text is read from its start", stream.Next("Ответ готов. Всё")?.Text, "Ответ готов.");

        stream = new SpokenAnswerStream();
        stream.Next("Всё готово. Use");
        Program.Equal("an English sentence keeps a Russian answer's voice",
            stream.Next("Всё готово. Use Nanite. Дальше")?.Language, "ru");
    }

    private static SpokenAnswer? Whole(string markdown) => new SpokenAnswerStream().Finish(markdown);

    /// <summary>
    /// Checks what is said of the whole answer, and that streaming it character by character, as the bridge
    /// reports it (trimmed), says the same: complete sentences only, each once.
    /// </summary>
    private static void Speaks(string name, string markdown, string expected)
    {
        Program.Equal(name, Whole(markdown)?.Text, expected);

        var stream = new SpokenAnswerStream();
        var said = new List<string>();
        for (var length = 1; length <= markdown.Length; length++)
        {
            if (stream.Next(markdown[..length].Trim()) is { } part) said.Add(part.Text);
        }

        if (stream.Finish(markdown) is { } rest) said.Add(rest.Text);
        Program.Equal($"{name}, streamed", string.Join(' ', said), expected);
    }
}
