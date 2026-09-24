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

        Program.Equal("Russian language", SpokenAnswer.From("Открой Project Settings и включи Nanite.").Language, "ru");
        Program.Equal("English language", SpokenAnswer.From("Open Project Settings.").Language, "en");
    }

    private static void Speaks(string name, string markdown, string expected) =>
        Program.Equal(name, SpokenAnswer.From(markdown).Text, expected);
}
