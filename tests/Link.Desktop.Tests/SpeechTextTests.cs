using System.Collections.Generic;
using System.Linq;
using Link.Desktop.Services;

namespace Link.Desktop.Tests;

internal static class SpeechTextTests
{
    public static void Run()
    {
        Speaks("short answer", "Да, так можно. Это безопасно.", "Да, так можно. Это безопасно.");
        Speaks("every sentence is read", "Первое. Второе. Третье. Четвёртое.", "Первое. Второе. Третье. Четвёртое.");
        Speaks("heading and markup are skipped", "## Ответ\n\nИспользуй **Nanite** и `Lumen`.",
            "Используй Nanite и Lumen.");
        Speaks("code after the opening", "Вот пример:\n\n```csharp\nvar x = 1;\n```",
            "Вот пример: Код — на экране.");
        Speaks("code before the opening", "```\nnpm install\n```\n\nКоманда ставит зависимости.",
            "Код — на экране. Команда ставит зависимости.");
        Speaks("unclosed code block", "Начало.\n\n```js\nconst a = 1;", "Начало. Код — на экране.");
        Speaks("code is pointed to once", "Раз.\n\n```\na\n```\n\nДва.\n\n```\nb\n```", "Раз. Код — на экране. Два.");
        Speaks("table", "Сравнение в таблице.\n\n| A | B |\n| --- | --- |\n| 1 | 2 |",
            "Сравнение в таблице. Таблица — на экране.");
        Speaks("links keep their text, URLs go",
            "Смотри [README](https://github.com/k2-fsa/sherpa-onnx#readme): там есть пример. Ещё https://k2-fsa.github.io/sherpa/ поможет.",
            "Смотри README: там есть пример. Ещё поможет.");
        Speaks("list items become sentences", "- Открой настройки\n- Нажми **Reset**\n- Перезапусти редактор\n- Проверь лог",
            "Открой настройки. Нажми Reset. Перезапусти редактор. Проверь лог.");
        Speaks("numbered list", "1) Открой\n2) Закрой", "Открой. Закрой.");
        Speaks("numbered list with stops", "1. Открой настройки.\n2. Включи Nanite.", "Открой настройки. Включи Nanite.");
        Speaks("a long list is read up to its limit",
            string.Join('\n', Enumerable.Range(1, 11).Select(item => $"- Пункт номер {item}")),
            string.Join(' ', Enumerable.Range(1, SpeechTextSanitizer.MaxListItems).Select(item => $"Пункт номер {item}."))
            + " Остальные пункты — на экране.");
        Speaks("typical answer",
            "Да — это нормально для Nanite.\n\n## Почему\n\n- Nanite кластеризует меши.\n- Lumen считает свет.\n\n"
            + "```ini\nr.Nanite=1\n```\n\nЕсли не поможет, пришли лог.",
            "Да — это нормально для Nanite. Nanite кластеризует меши. Lumen считает свет. Код — на экране. Если не поможет, пришли лог.");
        Speaks("quote", "> **Важно:** сделай бэкап.\n\nПотом обнови.", "Важно: сделай бэкап. Потом обнови.");
        Speaks("pictographs and arrows", "✅ Готово → можно запускать.", "Готово, можно запускать.");
        Speaks("abbreviations and decimals do not split",
            "Это файл, т. е. конфиг версии 1.5. Он лежит рядом.", "Это файл, т. е. конфиг версии 1.5. Он лежит рядом.");
        Speaks("identifiers in code read as words, citations go",
            "Используй `gemini-3.8-flash-lite-tts` — он поддерживает **streaming** 【4†source】[1].",
            "Используй gemini 3.8 flash lite tts — он поддерживает streaming.");
        Speaks("ChatGPT source markers go", "Это работает.\ue200cite\ue202turn0search1\ue201 Проверено.", "Это работает. Проверено.");
        Speaks("a long path is said by its file name",
            @"Открой C:\Users\me\projects\LINK\src\App.xaml.cs и проверь.", "Открой App.xaml.cs и проверь.");
        Speaks("stack frames are skipped",
            "Падает при старте.\n   at Link.Desktop.App.OnStartup(StartupEventArgs e)\n   at System.Windows.Application.Run()",
            "Падает при старте.");
        Speaks("angle brackets and pipes are not Gemini tags", "Используй List<string> и |pipes| тут.",
            "Используй List и pipes тут.");
        Speaks("English answer", "Sure. Restart the editor and try again.", "Sure. Restart the editor and try again.", "en");
        Speaks("English answer with code", "Restart the editor.\n\n```\ndotnet build\n```",
            "Restart the editor. The code is on screen.", "en");
        Speaks("only code", "```python\nprint(1)\n```", "The code is on screen.", "en");
        Speaks("nothing to say", "---", "Ответ на экране.");

        var sentence = "Это довольно длинное предложение о настройке освещения в сцене.";
        var many = string.Join(' ', Enumerable.Repeat(sentence, 30));
        var fits = (VoiceTextChunker.MaxSpokenLength + 1) / (sentence.Length + 1);
        Speaks("a long answer stops with a pointer to the screen", many,
            string.Join(' ', Enumerable.Repeat(sentence, fits)) + " Остальное — на экране.");

        var words = string.Join(", ", Enumerable.Repeat("и ещё одна деталь настройки", 12));
        var pieces = SpeechTextSanitizer.SplitSentences($"Сначала {words}.").ToList();
        Program.Equal("a very long sentence is split at commas", pieces.Count > 1 && pieces.All(piece => piece.Length <= 220), true);
        Program.Equal("the split keeps every word", string.Join(' ', pieces), $"Сначала {words}.");

        Program.Equal("Russian language", Whole("Открой Project Settings и включи Nanite.")?.Language, "ru");
        Program.Equal("English language", Whole("Open Project Settings.", "en")?.Language, "en");
        // As the live test saw it: a Russian sentence full of English terms went to an English voice.
        Program.Equal("a technical Russian sentence is still Russian",
            Whole("Для интерьера в Unreal Engine 5 сначала включи Lumen в Project Settings → Rendering: Dynamic Global " +
                  "Illumination Method = Lumen, Reflection Method = Lumen.")?.Language, "ru");

        var stream = new VoiceTextChunker("ru");
        Program.Equal("the sentence being written waits", stream.Next("Готово, всё настроено")?.Text, null);
        Program.Equal("a sentence is said once the next one begins",
            stream.Next("Готово, всё настроено как надо. Д")?.Text, "Готово, всё настроено как надо.");
        Program.Equal("nothing is said twice", stream.Next("Готово, всё настроено как надо. Дальше идёт")?.Text, null);
        Program.Equal("the last sentence is said when the answer is final",
            stream.Finish("Готово, всё настроено как надо. Дальше идёт текст.")?.Text, "Дальше идёт текст.");

        stream = new VoiceTextChunker("ru");
        Program.Equal("a short opening waits for the next sentence", stream.Next("Да. Тут")?.Text, null);
        Program.Equal("and goes out with it", stream.Next("Да. Тут проблема в материале. Про")?.Text,
            "Да. Тут проблема в материале.");

        stream = new VoiceTextChunker("ru");
        Program.Equal("a paragraph is complete once the next block begins",
            stream.Next("Вот пример:\n\n```js\nconst a")?.Text, "Вот пример: Код — на экране.");

        stream = new VoiceTextChunker("ru");
        stream.Next("Сначала идёт короткая заметка. Потом");
        Program.Equal("a swapped text is read from its start",
            stream.Next("Ответ уже полностью готов. Всё")?.Text, "Ответ уже полностью готов.");

        stream = new VoiceTextChunker("ru");
        stream.Next("Всё готово, можно проверять. Use");
        Program.Equal("an English sentence keeps a Russian answer's voice",
            stream.Next("Всё готово, можно проверять. Use Nanite for this. Дальше")?.Language, "ru");
    }

    private static SpokenAnswer? Whole(string markdown, string language = "ru") => new VoiceTextChunker(language).Finish(markdown);

    /// <summary>
    /// Checks what is said of the whole answer, and that streaming it character by character, as the chat page
    /// reports it (trimmed), says the same: complete sentences only, each once.
    /// </summary>
    private static void Speaks(string name, string markdown, string expected, string language = "ru")
    {
        Program.Equal(name, Whole(markdown, language)?.Text, expected);

        var stream = new VoiceTextChunker(language);
        var said = new List<string>();
        for (var length = 1; length <= markdown.Length; length++)
        {
            if (stream.Next(markdown[..length].Trim()) is { } part) said.Add(part.Text);
        }

        if (stream.Finish(markdown) is { } rest) said.Add(rest.Text);
        Program.Equal($"{name}, streamed", string.Join(' ', said), expected);
    }
}
