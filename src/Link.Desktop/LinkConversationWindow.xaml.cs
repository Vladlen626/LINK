using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

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
        StatusText.Text = conversations.Count == 0
            ? "No conversations detected. Reload the companion on the Extensions page, then reload an open ChatGPT conversation."
            : "Select the conversation this project should use.";
    }

    private void Refresh_OnClick(object sender, RoutedEventArgs e) => RefreshConversations();

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
