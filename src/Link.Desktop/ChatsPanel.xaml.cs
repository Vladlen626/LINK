using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Link.Desktop;

/// <summary>
/// The Chat tab's sidebar, as ChatGPT's own list of chats: the project's chats, a new one, and how the chat stands.
/// Choosing a chat makes it the one Link talks in, and its thread shows beside. It only shows and asks; the window does
/// the work.
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

    public void ShowChats(IReadOnlyList<ChatRow> chats)
    {
        ChatList.ItemsSource = chats;
        ChatsEmptyText.Visibility = chats.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The keyboard on the shown chat's row, else on New chat: arrows and Tab go on from there.</summary>
    public void FocusList()
    {
        ChatList.UpdateLayout();
        var open = ChatList.Items.OfType<ChatRow>().FirstOrDefault(row => row.IsOpen);
        if (open is not null && ChatList.ItemContainerGenerator.ContainerFromItem(open) is ContentPresenter presenter &&
            VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is UIElement row)
        {
            row.Focus();
        }
        else NewChatButton.Focus();
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

    private void ChatRow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ChatRow row }) ChatSelected?.Invoke(row);
    }
}
