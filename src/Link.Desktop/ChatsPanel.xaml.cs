using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Link.Desktop.Models;

namespace Link.Desktop;

/// <summary>
/// The Chats tab beside Link, as ChatGPT's own list of chats: the project's chats, and one chat as a thread of what
/// was asked through Link and what came back. Opening a chat makes it the one Link talks in. It only shows and asks;
/// the window does the work.
/// </summary>
public partial class ChatsPanel : UserControl
{
    private bool _canSummarize;

    public ChatsPanel() => InitializeComponent();

    /// <summary>A chat of the list was clicked.</summary>
    public event Action<ChatRow>? ChatSelected;

    /// <summary>A new chat was asked for; true: with a summary of the current one.</summary>
    public event Action<bool>? NewChatRequested;

    /// <summary>Link's ChatGPT page is to be shown, at the current chat.</summary>
    public event Action? OpenChatRequested;

    /// <summary>An answer of a thread is to be shown whole in Link's card.</summary>
    public event Action<ProjectExchange>? AnswerShown;

    public void ShowChats(IReadOnlyList<ChatRow> chats)
    {
        ChatList.ItemsSource = chats;
        ChatsEmptyText.Visibility = chats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowListView()
    {
        ListView.Visibility = Visibility.Visible;
        ThreadView.Visibility = Visibility.Collapsed;
    }

    /// <summary>One chat's thread, oldest first; <paramref name="hint"/> says where the next message goes.</summary>
    public void ShowThread(string title, IReadOnlyList<object> items, string hint, bool canOpen)
    {
        ThreadTitle.Text = title;
        ThreadTitle.ToolTip = title;
        ThreadList.ItemsSource = items;
        ThreadEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ThreadHintText.Text = hint;
        OpenThreadButton.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
        ListView.Visibility = Visibility.Collapsed;
        ThreadView.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(ThreadScroll.ScrollToEnd, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <param name="canSummarize">The current chat exists, so it can write a summary for a new one.</param>
    public void ShowConnection(string text, Brush dot, bool canSummarize)
    {
        ConnectionText.Text = text;
        ConnectionDot.Fill = dot;
        _canSummarize = canSummarize;
    }

    /// <summary>While a request runs the chat stays as it is: another one is not chosen meanwhile.</summary>
    public void SetBusy(bool busy)
    {
        NewChatButton.IsEnabled = !busy;
        ChatList.IsEnabled = !busy;
    }

    private void NewChat_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = MenuPlacement.Under(NewChatButton);
        var plain = new MenuItem { Header = "New chat" };
        plain.Click += (_, _) => NewChatRequested?.Invoke(false);
        var summarized = new MenuItem
        {
            Header = "New chat with a summary of this one",
            IsEnabled = _canSummarize,
            ToolTip = "The current chat writes a short summary first; it goes with your next message as context"
        };
        summarized.Click += (_, _) => NewChatRequested?.Invoke(true);
        menu.Items.Add(plain);
        menu.Items.Add(summarized);
        menu.IsOpen = true;
    }

    private void OpenChat_OnClick(object sender, RoutedEventArgs e) => OpenChatRequested?.Invoke();
    private void OpenThread_OnClick(object sender, RoutedEventArgs e) => OpenChatRequested?.Invoke();
    private void Back_OnClick(object sender, RoutedEventArgs e) => ShowListView();

    private void UserWords_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadUser words }) words.Open = !words.Open;
    }

    private void ChatRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatRow row }) ChatSelected?.Invoke(row);
    }

    private void ShowAnswer_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ThreadAnswer answer }) AnswerShown?.Invoke(answer.Exchange);
    }
}
