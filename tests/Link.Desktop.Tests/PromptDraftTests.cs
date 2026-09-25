using Link.Desktop.Services;

namespace Link.Desktop.Tests;

/// <summary>The dictated words at the end of the prompt are replaced, never added a second time.</summary>
internal static class PromptDraftTests
{
    public static void Run()
    {
        var draft = new PromptDraft();
        var prompt = draft.Show(string.Empty, "почему");
        Program.Equal("a draft fills an empty prompt", prompt, "почему");
        prompt = draft.Show(prompt, "почему у меня");
        Program.Equal("a newer draft replaces the older one", prompt, "почему у меня");
        prompt = draft.Commit(prompt, "Почему у меня?");
        Program.Equal("the transcript replaces the draft", prompt, "Почему у меня?");
        prompt = draft.Show(prompt, "и ещё");
        Program.Equal("the next dictation follows the text", prompt, "Почему у меня? и ещё");

        draft = new PromptDraft();
        prompt = draft.Show("Look at this:\n", "материал");
        Program.Equal("typed text stays before the draft", prompt, "Look at this: материал");
        prompt = draft.Show(prompt, "материал не светится");
        Program.Equal("typed text is kept while the draft changes", prompt, "Look at this: материал не светится");
        prompt = draft.Commit(prompt, string.Empty);
        Program.Equal("no speech takes the draft out again", prompt, "Look at this:");

        draft = new PromptDraft();
        Program.Equal("a blank prompt gives way to the draft", draft.Show("  ", "да"), "да");

        draft = new PromptDraft();
        prompt = draft.Show("Отправлено", "дальше");
        // The answer arrived meanwhile: the sent prompt leaves the box, the words dictated since stay.
        prompt = prompt["Отправлено".Length..].Trim();
        prompt = draft.Show(prompt, "дальше вопрос");
        Program.Equal("a sent prompt leaving does not repeat the draft", prompt, "дальше вопрос");

        draft = new PromptDraft();
        prompt = draft.Show("x", "раз два");
        draft.Keep();
        prompt = draft.Commit(prompt, "Три.");
        Program.Equal("a kept draft is ordinary text", prompt, "x раз два Три.");
    }
}
