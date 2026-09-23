using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

public partial class MainWindow
{
    private static readonly Geometry StopIconGeometry = CreateFrozenGeometry("M8,8 L16,8 L16,16 L8,16 Z");
    private static readonly (ResponseEffort Effort, string Label, string Description)[] EffortOptions =
    [
        (ResponseEffort.Fast, "Fast", "Fast — short answer, no long thinking"),
        (ResponseEffort.Normal, "Normal", "Normal — the chat's usual answer"),
        (ResponseEffort.Deep, "Deep", "Deep — thinks longer, more thorough")
    ];
    private readonly IChatIntegration _chat;
    private readonly DispatcherTimer _connectionTimer;
    private CancellationTokenSource? _sendCancellation;
    private bool _sending;
    private string _responseText = string.Empty;
    private string? _chatNotice;

    private async void Send_OnClick(object sender, RoutedEventArgs e)
    {
        if (_sending)
        {
            CancelSend();
            return;
        }

        await SendAsync();
    }

    private void CancelSend()
    {
        if (_sendCancellation is not { IsCancellationRequested: false } cancellation) return;
        cancellation.Cancel();
        SetStatus("Cancelling…", "Cancelling");
    }

    private async Task SendAsync()
    {
        if (_sending) return;
        RemoveMissingContextFiles();
        if (string.IsNullOrWhiteSpace(PromptBox.Text) && _context.Count == 0)
        {
            SetError("Add text or context before sending.");
            return;
        }

        var conversation = _projects.ActiveProject.LinkedConversation;
        if (conversation is null)
        {
            SetError("Link this project to a ChatGPT conversation first.");
            return;
        }

        var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;
        _chatNotice = null;
        SetSending(true);
        ShowResponse(ChatProgressState.Sending, string.Empty);
        ContextItem? transientContext = null;

        try
        {
            _projects.ActiveProject.CurrentTask = CurrentTaskBox.Text.Trim();
            _projects.Save();
            var externalWindow = _currentApplication.IsExternalWindow(_lastExternalWindow)
                ? _lastExternalWindow
                : IntPtr.Zero;
            var currentApplication = _currentApplication.GetContext(externalWindow);
            if (_settings.Current.AutoCaptureActiveWindowOnSend && externalWindow != IntPtr.Zero)
            {
                try
                {
                    var screenshot = await CaptureTransientWindowAsync(externalWindow);
                    transientContext = new ContextItem
                    {
                        Type = ContextItemType.Screenshot,
                        Label = "Current application",
                        FilePath = screenshot.FilePath,
                        MimeType = "image/png"
                    };
                }
                catch (Exception exception)
                {
                    SetStatus($"Auto screen unavailable: {exception.Message}", "Sending");
                }
            }

            var requestContext = transientContext is null
                ? _context.ToArray()
                : _context.Append(transientContext).ToArray();
            var request = _requests.BuildAndLog(
                _projects.ActiveProject,
                PromptBox.Text,
                requestContext,
                currentApplication,
                _settings.Current.ResponseEffort);
            var progress = new Progress<ChatProgress>(OnChatProgress);
            var response = await _chat.SendAsync(request, conversation, progress, cancellation.Token);
            ShowResponse(ChatProgressState.Completed, response.Text);
            PromptBox.Clear();
            ClearContext(prune: true);
            SetStatus(_chatNotice is null ? "Response received" : $"Response received · {_chatNotice}", "Done");
            ShowPassive();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowResponse(ChatProgressState.Error, "Request cancelled.");
            SetStatus("Request cancelled. Your prompt and context were kept.", "Cancelled");
        }
        catch (Exception exception)
        {
            ShowResponse(ChatProgressState.Error, exception.Message);
            SetError($"{exception.Message} Prompt and context were kept.");
        }
        finally
        {
            DeleteTransientContext(transientContext);
            _sendCancellation = null;
            cancellation.Dispose();
            SetSending(false);
            UpdateConnectionState();
        }
    }

    private void Effort_OnClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = EffortButton, Placement = PlacementMode.Bottom };
        foreach (var option in EffortOptions)
        {
            var item = new MenuItem
            {
                Header = option.Description,
                IsCheckable = true,
                IsChecked = option.Effort == _settings.Current.ResponseEffort
            };
            item.Click += (_, _) => SetEffort(option.Effort);
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

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

    private static Geometry CreateFrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private void DeleteTransientContext(ContextItem? item)
    {
        if (item?.FilePath is null) return;
        try
        {
            File.Delete(item.FilePath);
        }
        catch (IOException)
        {
            // A failed transient cleanup can be handled by the normal screenshot pruning pass.
        }
        catch (UnauthorizedAccessException)
        {
            // Keep sending successful even if Windows briefly retains the capture file.
        }
        finally
        {
            _history.Prune(_context.Where(context => context.FilePath is not null).Select(context => context.FilePath!));
        }
    }

    private void LinkConversation()
    {
        if (_sending) return;
        var dialog = new LinkConversationWindow(_chat, _projects.ActiveProject.LinkedConversation) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.SelectedConversation is not { } conversation) return;

        _projects.ActiveProject.LinkedConversation = new ChatConversationLink
        {
            TransportId = _chat.TransportId,
            ConversationId = conversation.ConversationId,
            Title = conversation.Title,
            Url = conversation.Url
        };
        _projects.Save();
        UpdateConnectionState();
        SetStatus($"Linked to {conversation.Title}");
    }

    private void UnlinkConversation()
    {
        if (_sending) return;
        _projects.ActiveProject.LinkedConversation = null;
        _projects.Save();
        UpdateConnectionState();
        SetStatus("Conversation unlinked");
    }

    private void ChatConnection_OnClick(object sender, RoutedEventArgs e) => LinkConversation();

    private void UpdateConnectionState()
    {
        var conversation = _projects.ActiveProject.LinkedConversation;
        var state = _chat.GetConnectionState(conversation);
        ChatConnectionText.Text = state switch
        {
            ChatConnectionState.NoLinkedConversation => "No linked conversation",
            ChatConnectionState.BridgeUnavailable => $"ChatGPT unavailable · {conversation?.Title}",
            ChatConnectionState.ConversationAvailable => $"ChatGPT connected · {conversation?.Title}",
            _ => "ChatGPT unavailable"
        };
        ChatStatusDot.Fill = (Brush)FindResource(state switch
        {
            ChatConnectionState.ConversationAvailable => "SuccessOrbBrush",
            ChatConnectionState.BridgeUnavailable => "ErrorOrbBrush",
            _ => "IdleOrbBrush"
        });
    }

    private void SetSending(bool sending)
    {
        _sending = sending;
        // While a request runs, the send orb becomes Stop; Esc does the same.
        SendIcon.Data = sending ? StopIconGeometry : (Geometry)FindResource("SendIconGeometry");
        SendIcon.Fill = sending ? SendIcon.Stroke : Brushes.Transparent;
        SendButton.ToolTip = sending ? "Stop request (Esc)" : "Send to linked chat";
        PromptBox.IsReadOnly = sending;
        CurrentTaskBox.IsEnabled = !sending;
        ProjectMenuButton.IsEnabled = !sending;
        ChatConnectionButton.IsEnabled = !sending;
        ContextActionsPanel.IsEnabled = !sending;
        ContextItemsControl.IsEnabled = !sending;
        ScreenButton.IsEnabled = !sending;
        VoiceButton.IsEnabled = !sending;
        ClipboardButton.IsEnabled = !sending;
    }

    private void OnChatProgress(ChatProgress progress)
    {
        if (progress.Notice is not null)
        {
            // Kept for the final status line; the next progress state would hide it right away.
            _chatNotice = progress.Notice;
            SetStatus(progress.Notice, "Sending");
            return;
        }

        ShowResponse(progress.State, progress.Text.Length > 0 ? progress.Text : progress.Error ?? string.Empty);
        SetStatus(progress.State switch
        {
            ChatProgressState.Sending => "Sending to ChatGPT…",
            ChatProgressState.Waiting => "Waiting for ChatGPT…",
            ChatProgressState.Receiving => "Receiving response…",
            ChatProgressState.Completed => "Response received",
            ChatProgressState.Error => $"Error: {progress.Error}",
            _ => "Working…"
        }, progress.State switch
        {
            ChatProgressState.Sending => "Sending",
            ChatProgressState.Waiting => "Waiting",
            ChatProgressState.Receiving => "Receiving",
            ChatProgressState.Completed => "Done",
            _ => "Attention"
        });
    }

    private void ShowResponse(ChatProgressState state, string text)
    {
        ResponsePanel.Visibility = Visibility.Visible;
        ResponseStateText.Text = state switch
        {
            ChatProgressState.Sending => "Sending",
            ChatProgressState.Waiting => "Waiting for response",
            ChatProgressState.Receiving => "Receiving",
            ChatProgressState.Completed => "Assistant",
            ChatProgressState.Error => "Error",
            _ => "Assistant"
        };
        ResponseStateText.Foreground = (Brush)FindResource(state == ChatProgressState.Error ? "ErrorBrush" : "MutedBrush");
        ResponseStateDot.Fill = (Brush)FindResource(state switch
        {
            ChatProgressState.Completed => "SuccessOrbBrush",
            ChatProgressState.Error => "ErrorOrbBrush",
            _ => "BusyOrbBrush"
        });
        if (text.Length > 0 || state is ChatProgressState.Sending or ChatProgressState.Waiting)
        {
            _responseText = text;
            RenderResponse(text);
        }
    }

    private void CopyResponse_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_responseText)) return;
        try
        {
            Clipboard.SetText(_responseText);
            SetStatus("Response copied");
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }

    private void CloseResponse_OnClick(object sender, RoutedEventArgs e) =>
        ResponsePanel.Visibility = Visibility.Collapsed;

    private void RenderResponse(string text)
    {
        ResponseDocument.Blocks.Clear();
        if (string.IsNullOrWhiteSpace(text)) return;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index]))
            {
                index++;
                continue;
            }

            if (lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                index++;
                var codeLines = new List<string>();
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    codeLines.Add(lines[index++]);
                }
                if (index < lines.Length) index++;

                var code = new Paragraph(new Run(string.Join('\n', codeLines)))
                {
                    Margin = new Thickness(0, 3, 0, 10),
                    Padding = new Thickness(9, 7, 9, 7),
                    Background = (Brush)FindResource("ResponseCodeBrush"),
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12.5,
                    LineHeight = 18
                };
                ResponseDocument.Blocks.Add(code);
                continue;
            }

            if (TryGetHeading(lines[index], out var headingLevel, out var headingText))
            {
                var heading = new Paragraph
                {
                    Margin = new Thickness(0, index == 0 ? 0 : 5, 0, 5),
                    FontSize = headingLevel switch { 1 => 17, 2 => 15.5, _ => 14.5 },
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = 22
                };
                AddInlineContent(heading.Inlines, headingText);
                ResponseDocument.Blocks.Add(heading);
                index++;
                continue;
            }

            if (IsRuleLine(lines[index]))
            {
                ResponseDocument.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 2, 0, 11),
                    BorderBrush = (Brush)FindResource("HairlineBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1,
                    LineHeight = 1
                });
                index++;
                continue;
            }

            if (IsTableLine(lines[index]))
            {
                var tableLines = new List<string>();
                while (index < lines.Length && IsTableLine(lines[index])) tableLines.Add(lines[index++]);
                ResponseDocument.Blocks.Add(CreateTable(tableLines));
                continue;
            }

            if (IsQuoteLine(lines[index]))
            {
                var quoteLines = new List<string>();
                while (index < lines.Length && IsQuoteLine(lines[index]))
                {
                    quoteLines.Add(lines[index++].TrimStart()[1..].Trim());
                }

                var quote = new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, 9),
                    Padding = new Thickness(9, 1, 0, 1),
                    BorderBrush = (Brush)FindResource("AquaBrush"),
                    BorderThickness = new Thickness(2.5, 0, 0, 0),
                    Foreground = (Brush)FindResource("MutedBrush")
                };
                AddInlineContent(quote.Inlines, string.Join(' ', quoteLines.Where(line => line.Length > 0)));
                ResponseDocument.Blocks.Add(quote);
                continue;
            }

            if (TryGetListItem(lines[index], numbered: false, out _))
            {
                index = AddList(lines, index, numbered: false);
                continue;
            }

            if (TryGetListItem(lines[index], numbered: true, out _))
            {
                index = AddList(lines, index, numbered: true);
                continue;
            }

            var paragraphLines = new List<string>();
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index]))
            {
                paragraphLines.Add(lines[index].Trim());
                index++;
            }

            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 9) };
            AddInlineContent(paragraph.Inlines, string.Join(' ', paragraphLines));
            ResponseDocument.Blocks.Add(paragraph);
        }
    }

    private int AddList(string[] lines, int index, bool numbered)
    {
        var list = new System.Windows.Documents.List
        {
            MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
            Margin = new Thickness(18, 1, 0, 9),
            Padding = new Thickness(0)
        };

        while (index < lines.Length && TryGetListItem(lines[index], numbered, out var itemText))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 3) };
            AddInlineContent(paragraph.Inlines, itemText);
            list.ListItems.Add(new ListItem(paragraph));
            index++;
        }

        ResponseDocument.Blocks.Add(list);
        return index;
    }

    private static bool StartsBlock(string line) =>
        line.TrimStart().StartsWith("```", StringComparison.Ordinal) ||
        TryGetHeading(line, out _, out _) ||
        IsRuleLine(line) ||
        IsTableLine(line) ||
        IsQuoteLine(line) ||
        TryGetListItem(line, numbered: false, out _) ||
        TryGetListItem(line, numbered: true, out _);

    private static bool IsRuleLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 && (trimmed.All(ch => ch == '-') || trimmed.All(ch => ch == '*') || trimmed.All(ch => ch == '_'));
    }

    private static bool IsTableLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 1 && trimmed[0] == '|' && trimmed.IndexOf('|', 1) > 0;
    }

    private static bool IsQuoteLine(string line) => line.TrimStart().StartsWith('>');

    private Paragraph CreateTable(IReadOnlyList<string> lines)
    {
        // Monospace columns keep tables readable without a grid control in the compact overlay.
        var rows = lines
            .Select(SplitTableRow)
            .Where(cells => !cells.All(cell => cell.Length > 0 && cell.All(ch => ch is '-' or ':')))
            .ToList();
        var columns = rows.Max(row => row.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(column => rows.Max(row => column < row.Count ? row[column].Length : 0))
            .ToArray();
        var formatted = rows
            .Select(row => string.Join("  ", Enumerable.Range(0, columns)
                .Select(column => (column < row.Count ? row[column] : string.Empty).PadRight(widths[column]))).TrimEnd())
            .ToList();

        var table = new Paragraph
        {
            Margin = new Thickness(0, 3, 0, 10),
            Padding = new Thickness(9, 7, 9, 7),
            Background = (Brush)FindResource("ResponseCodeBrush"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12.5,
            LineHeight = 18
        };
        table.Inlines.Add(new Bold(new Run(formatted[0])));
        if (formatted.Count > 1) table.Inlines.Add(new Run("\n" + string.Join('\n', formatted.Skip(1))));
        return table;
    }

    private static List<string> SplitTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|')) trimmed = trimmed[1..];
        if (trimmed.EndsWith('|') && !trimmed.EndsWith("\\|", StringComparison.Ordinal)) trimmed = trimmed[..^1];
        return System.Text.RegularExpressions.Regex.Split(trimmed, @"(?<!\\)\|")
            .Select(cell => cell.Trim().Replace("\\|", "|", StringComparison.Ordinal)
                .Replace("**", string.Empty, StringComparison.Ordinal)
                .Replace("`", string.Empty, StringComparison.Ordinal))
            .ToList();
    }

    private static bool TryGetHeading(string line, out int level, out string text)
    {
        var trimmed = line.TrimStart();
        level = 0;
        while (level < trimmed.Length && level < 6 && trimmed[level] == '#') level++;
        if (level == 0 || level >= trimmed.Length || trimmed[level] != ' ')
        {
            text = string.Empty;
            return false;
        }

        text = trimmed[(level + 1)..].Trim();
        return text.Length > 0;
    }

    private static bool TryGetListItem(string line, bool numbered, out string text)
    {
        var trimmed = line.TrimStart();
        if (!numbered)
        {
            if (trimmed.Length >= 2 && trimmed[1] == ' ' && trimmed[0] is '-' or '*' or '+')
            {
                text = trimmed[2..].Trim();
                return true;
            }
        }
        else
        {
            var separator = trimmed.IndexOf(". ", StringComparison.Ordinal);
            if (separator > 0 && trimmed[..separator].All(char.IsDigit))
            {
                text = trimmed[(separator + 2)..].Trim();
                return true;
            }
        }

        text = string.Empty;
        return false;
    }

    private void AddInlineContent(InlineCollection inlines, string text)
    {
        var plainStart = 0;
        var index = 0;
        while (index < text.Length)
        {
            if (!TryReadInline(text, index, out var inline, out var next))
            {
                index++;
                continue;
            }

            if (index > plainStart) inlines.Add(new Run(text[plainStart..index]));
            inlines.Add(inline);
            index = plainStart = next;
        }

        if (plainStart < text.Length) inlines.Add(new Run(text[plainStart..]));
    }

    private bool TryReadInline(string text, int index, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Inline? inline, out int next)
    {
        inline = null;
        next = index;
        if (text[index] == '`')
        {
            var end = text.IndexOf('`', index + 1);
            if (end < 0) return false;
            inline = new Run(text[(index + 1)..end])
            {
                Background = (Brush)FindResource("ResponseCodeBrush"),
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 12.5
            };
            next = end + 1;
            return true;
        }

        if (text[index] == '*' && index + 1 < text.Length && text[index + 1] == '*')
        {
            var end = text.IndexOf("**", index + 2, StringComparison.Ordinal);
            if (end < 0) return false;
            var bold = new Bold();
            AddInlineContent(bold.Inlines, text[(index + 2)..end]);
            inline = bold;
            next = end + 2;
            return true;
        }

        if (text[index] == '[')
        {
            var labelEnd = text.IndexOf(']', index + 1);
            if (labelEnd < 0 || labelEnd + 1 >= text.Length || text[labelEnd + 1] != '(') return false;
            var targetEnd = text.IndexOf(')', labelEnd + 2);
            if (targetEnd < 0 ||
                !Uri.TryCreate(text[(labelEnd + 2)..targetEnd], UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https"))
            {
                return false;
            }

            var link = new Hyperlink
            {
                NavigateUri = uri,
                Foreground = (Brush)FindResource("LinkTextBrush"),
                ToolTip = uri.AbsoluteUri
            };
            AddInlineContent(link.Inlines, text[(index + 1)..labelEnd]);
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                OpenLink(e.Uri);
            };
            inline = link;
            next = targetEnd + 1;
            return true;
        }

        return false;
    }

    private void OpenLink(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetError(exception.Message);
        }
    }
}
