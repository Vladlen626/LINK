using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Link.Desktop.Models;

namespace Link.Desktop;

// The labels under the prompt: where Send goes (ChatGPT or the agent) and with what response effort.
public partial class MainWindow
{
    /// <summary>The menu of a label under the prompt: one line per option, the current one checked.</summary>
    private static void ShowOptions<T>(Button button, (T Value, string Label, string Description)[] options, T current, Action<T> choose)
    {
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        foreach (var option in options)
        {
            var item = new MenuItem { Header = option.Description, IsCheckable = true, IsChecked = Equals(option.Value, current) };
            item.Click += (_, _) => choose(option.Value);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Where Send sends: ChatGPT in the chat named, or the project's agent. What goes along is the chips; other chats
    /// are in the panel's Chats (Ctrl+K).
    /// </summary>
    private void Route_OnClick(object sender, RoutedEventArgs e)
    {
        var open = TaskTakesWords ? _open!.Record : null;
        ShowOptions(RouteButton,
        [
            (RequestRoute.Chat, "ChatGPT", $"ChatGPT — the chat {ChatName(_projects.ActiveProject.LinkedConversation)}, with the chips you see"),
            (RequestRoute.Agent, "Agent", open is not null
                ? $"Agent — the task «{Shorten(open.Goal, 40)}» goes on with your words"
                : "Agent — a card for a new task first; nothing runs before Run")
        ], _settings.Current.Route, SetRoute);
    }

    private void SetRoute(RequestRoute route)
    {
        _settings.Current.Route = route;
        _settings.Save();
        UpdateRouteButton();
        // What an agent's task does not carry is not shown as going along.
        ShowApplicationContext();
        UpdateSees();
        SetStatus($"Send goes to {DestinationName} now");
    }

    /// <summary>
    /// Where Send goes, said where the user looks: ChatGPT's chat; on the Agent route the open task, as its next words
    /// (steered now while the agent works), or a card for a new task when none is open.
    /// </summary>
    private void UpdateRouteButton()
    {
        var agent = _settings.Current.Route == RequestRoute.Agent;
        var open = agent && TaskTakesWords ? _open!.Record : null;
        var running = _open?.Run is not null;
        var starting = StartProvider;
        var provider = _open?.Run?.Task.Provider ?? open?.LastRun?.Provider ?? starting;
        var chat = _projects.ActiveProject.LinkedConversation;
        RouteText.Text = !agent ? $"ChatGPT · {Shorten(ChatName(chat), 30)}"
            : open is not null ? $"{ShortName(provider)} · {Shorten(open.Goal, 30)}"
            : $"New {ShortName(starting)} task";
        RouteButton.ToolTip = (!agent
                ? $"Send asks ChatGPT in {(chat is { IsNew: false } ? "the chat " + chat.Title : "a new chat")}, with the chips you see."
                : open is not null
                    ? $"Send gives your words and chips to the task «{open.Goal}»: {(running ? "steer now, the agent stops its step and goes on with them" : "it goes on in the same session")}. Ctrl+Enter opens a card for a new task instead."
                    : $"Send opens a task card for {_agents.Provider(starting).Name}; nothing runs before Run.") +
            "\nClick: ChatGPT or the agent · Ctrl+K: the chats";
        PromptPlaceholder.Text = !agent
            ? chat is { IsNew: false } ? $"Ask in «{Shorten(chat.Title, 40)}»…" : "Ask in a new chat…"
            : open is not null
                ? running ? $"Steer {ShortName(provider)} now: it stops the step and goes on with your words…" : $"Your words for {ShortName(provider)}: the task goes on…"
                : $"A new task for {ShortName(starting)}: Enter shows its card first…";
        SendHintText.Text = !agent ? "sends to ChatGPT"
            : open is null ? "opens a task card"
            : running ? "steers the agent now"
            : "goes on with the task";
        UpdateSendButton();
    }

    /// <summary>A chat as the user knows it: its title, or what the next message does without one.</summary>
    private static string ChatName(ChatConversationLink? chat) => chat is { IsNew: false } ? chat.Title : "new chat";

    /// <summary>Who the next message goes to, in the words of the status line.</summary>
    private string DestinationName => _settings.Current.Route != RequestRoute.Agent
        ? _projects.ActiveProject.LinkedConversation is { IsNew: false } chat ? $"ChatGPT, the chat {chat.Title}" : "ChatGPT, a new chat"
        : TaskTakesWords && _open is { } open ? $"the task «{Shorten(open.Record.Goal, 40)}»"
        : $"a task card for {_agents.Provider(StartProvider).Name}";

    private void Effort_OnClick(object sender, RoutedEventArgs e) =>
        ShowOptions(EffortButton, EffortOptions, _settings.Current.ResponseEffort, SetEffort);

    private void SetEffort(ResponseEffort effort)
    {
        _settings.Current.ResponseEffort = effort;
        _settings.Save();
        UpdateEffortButton();
        SetStatus($"Response effort: {EffortText.Text} — applies to the next message");
    }

    private void UpdateEffortButton()
    {
        var option = EffortOptions.First(option => option.Effort == _settings.Current.ResponseEffort);
        EffortText.Text = option.Label;
        EffortButton.ToolTip = $"Response effort: {option.Description}";
    }
}
