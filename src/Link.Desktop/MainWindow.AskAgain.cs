using System.Windows;
using System.Windows.Controls;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Ask again and Edit: the question on screen goes again, deeper, with a fresh screen, only its text, or corrected.
public partial class MainWindow
{
    /// <summary>
    /// Ask again ▾: the question on screen again, deeper, with a fresh screen of its window or with only its text, or
    /// back in the prompt to be corrected. Only what would change something is offered.
    /// </summary>
    private void AskAgain_OnClick(object sender, RoutedEventArgs e)
    {
        if (_attemptOnScreen is not { } shown) return;
        var (snapshot, route, effort) = shown;
        var menu = MenuPlacement.Under(AskAgainButton);
        if (effort != ResponseEffort.Deep) AddAgain(menu, "Deeper", "The same question, thought over longer", Redirect.Deeper);
        if (snapshot.WorkWindow != IntPtr.Zero)
        {
            AddAgain(menu, $"With a fresh screen of {snapshot.WorkTarget?.ProcessName ?? "its window"}",
                "The same question with a new screenshot of the window it was asked about", Redirect.FreshScreen);
        }

        if (route != RequestRoute.Direct) AddAgain(menu, "Only my text", "The same question with only its text and what you attached", Redirect.OnlyText);
        menu.Items.Add(new Separator());
        var edit = new MenuItem
        {
            Header = "Edit the question",
            ToolTip = "Back into the prompt to correct, e.g. a misheard word; it is asked again with the same screen and context"
        };
        edit.Click += (_, _) => EditQuestion();
        menu.Items.Add(edit);
        menu.IsOpen = true;
    }

    private void AddAgain(ContextMenu menu, string header, string tooltip, Redirect redirect)
    {
        var item = new MenuItem { Header = header, ToolTip = tooltip };
        item.Click += async (_, _) => await AskAgainAsync(redirect);
        menu.Items.Add(item);
    }

    private enum Redirect
    {
        Deeper,
        FreshScreen,
        OnlyText
    }

    /// <summary>
    /// The snapshot on screen goes again to the same chat, deeper, with only its text or with a fresh screenshot of its
    /// work window. The prompt and the clipboard are not read again.
    /// </summary>
    private async Task AskAgainAsync(Redirect redirect)
    {
        StopHandsFree();
        // While the user dictates, the words wait for the next message.
        if (_sending || _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        if (_attemptOnScreen is not { } shown) return;
        if (_projects.ActiveProject.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var (snapshot, route, effort) = shown;
        var fresh = redirect == Redirect.FreshScreen;
        if (redirect == Redirect.Deeper) effort = ResponseEffort.Deep;
        else if (redirect == Redirect.OnlyText) route = RequestRoute.Direct;
        // "Only my text" sends no screenshot of its own, so a fresh one goes with Link's chips.
        else route = RequestRoute.Chat;

        // After a failure or Stop the prompt still holds this input: once answered, it goes like a sent one.
        var prompt = PromptBox.Text;
        var kept = prompt.Trim() == snapshot.Text &&
                   _context.Select(item => item.Id).SequenceEqual(snapshot.Context.Select(item => item.Id));
        var answered = await AttemptAsync(
            snapshot.Text,
            route,
            effort,
            fresh ? () => WithFreshScreenAsync(snapshot) : () => Task.FromResult(snapshot));
        if (answered && kept) ClearSentInput(prompt);
    }

    private async Task<InputSnapshot> WithFreshScreenAsync(InputSnapshot snapshot) =>
        snapshot with { Id = Guid.NewGuid(), Screen = await CaptureWorkWindowAsync(snapshot.WorkWindow) };

    /// <summary>
    /// Edit the question: it goes back into the prompt to be corrected, e.g. a word the voice misheard. Send then asks
    /// it again from the same snapshot: its screen, context and window go as they were, and nothing is dictated or
    /// captured again.
    /// </summary>
    private void EditQuestion()
    {
        StopHandsFree();
        if (_sending || _voice.Phase is VoicePhase.Listening or VoicePhase.Finalizing) return;
        if (_attemptOnScreen is not { } shown) return;
        _editing = shown;
        SetPromptText(shown.Snapshot.Text);
        PromptBox.Focus();
        SetStatus("Correct the question — Enter asks again with the same screen and context, Esc cancels");
    }

    /// <summary>Esc while correcting: the question leaves the prompt, and the answer's ✎ Edit brings it back.</summary>
    private void CancelEdit()
    {
        _editing = null;
        SetPromptText(string.Empty);
        SetStatus("Edit cancelled");
        ScheduleListening();
    }

    private async Task SendCorrectedAsync((InputSnapshot Snapshot, RequestRoute Route, ResponseEffort Effort) edited)
    {
        if (_projects.ActiveProject.LinkedConversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var (snapshot, route, effort) = edited;
        var prompt = PromptBox.Text;
        var answered = await AttemptAsync(
            prompt,
            route,
            effort,
            () => Task.FromResult(snapshot with { Id = Guid.NewGuid(), Text = prompt.Trim() }));
        // The context stack was not part of this attempt, so it stays for the next message.
        if (answered) ClearSentPrompt(prompt);
    }

    /// <summary>Under an attempt's answer, error or Stop: Ask again ▾ and → Agent task, both from its snapshot.</summary>
    private void UpdateRedirectButtons() =>
        RedirectPanel.Visibility = _attemptOnScreen is null ? Visibility.Collapsed : Visibility.Visible;
}
