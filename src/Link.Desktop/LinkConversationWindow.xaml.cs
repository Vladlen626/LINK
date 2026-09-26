using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

public partial class LinkConversationWindow : Window
{
    private readonly IChatIntegration _chat;
    private readonly string? _currentConversationId;
    private readonly DispatcherTimer _refreshTimer;

    public LinkConversationWindow(IChatIntegration chat, ChatConversationLink? current)
    {
        _chat = chat;
        _currentConversationId = current?.ConversationId;
        InitializeComponent();
        SourceInitialized += (_, _) => WindowStyleService.EnableBackdrop(this, ThemeService.IsDark);

        // New chats are started in the built-in ChatGPT window; a summary needs a chat to write it.
        var newChats = chat.TransportId != ChatTransports.Browser;
        NewChatButton.Visibility = newChats ? Visibility.Visible : Visibility.Collapsed;
        SummaryBox.Visibility = newChats ? Visibility.Visible : Visibility.Collapsed;
        SummaryBox.IsEnabled = current is { IsNew: false };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshConversations();
        Loaded += (_, _) =>
        {
            RefreshConversations();
            _refreshTimer.Start();
        };
        Closed += (_, _) => _refreshTimer.Stop();
    }

    public AvailableChatConversation? SelectedConversation { get; private set; }

    /// <summary>The user asked to see ChatGPT; the dialog closed so that the chat can take input.</summary>
    public bool OpenChatRequested { get; private set; }

    /// <summary>The next message should start a new chat, with a summary of the current one when <see cref="WithSummary"/>.</summary>
    public bool NewChatRequested { get; private set; }

    public bool WithSummary => SummaryBox.IsEnabled && SummaryBox.IsChecked == true;

    private void RefreshConversations()
    {
        var selectedId = (ConversationList.SelectedItem as AvailableChatConversation)?.ConversationId ??
                         _currentConversationId;
        var conversations = _chat.GetAvailableConversations();
        ConversationList.ItemsSource = conversations;
        ConversationList.SelectedItem = conversations.FirstOrDefault(item => item.ConversationId == selectedId);
        if (ConversationList.SelectedItem is null && conversations.Count == 1)
        {
            ConversationList.SelectedIndex = 0;
        }

        LinkButton.IsEnabled = ConversationList.SelectedItem is not null;
        StatusText.Text = _chat.TransportId == ChatTransports.Browser
            ? conversations.Count > 0
                ? "Select the conversation this project should use."
                : "No conversations detected. Reload the companion on the Extensions page, then reload an open ChatGPT conversation."
            : conversations.Count > 0
                ? "Select the conversation this project should use, or start a new chat."
                : "Start a new chat, or open ChatGPT, go to the chat for this project and come back here.";
    }

    private void OpenChat_OnClick(object sender, RoutedEventArgs e)
    {
        OpenChatRequested = true;
        DialogResult = false;
    }

    private void NewChat_OnClick(object sender, RoutedEventArgs e)
    {
        NewChatRequested = true;
        DialogResult = true;
    }

    private void ConversationList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        LinkButton.IsEnabled = ConversationList.SelectedItem is not null;

    private void Link_OnClick(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not AvailableChatConversation conversation) return;
        SelectedConversation = conversation;
        DialogResult = true;
    }

    private void ConversationList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Link_OnClick(sender, e);
}
