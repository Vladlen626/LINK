using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Link.Desktop.Windows;

namespace Link.Desktop;

/// <summary>
/// What the very first start shows: which agents Link finds, where Settings chooses their files, and a first project for
/// a folder. It asks for no key and signs in to nothing; closing it is the same as Continue.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly IAgentProvider[] _agents;
    private readonly AgentSettings _settings;
    private readonly Action _openSettings;
    private readonly Func<string, string> _addProject;
    private readonly CancellationTokenSource _checks = new();

    /// <param name="agents">The agents, Claude Code and Codex, each checked.</param>
    /// <param name="openSettings">Opens Settings and returns when it is closed.</param>
    /// <param name="addProject">Makes a project for a folder and returns its name.</param>
    public FirstRunWindow(IEnumerable<IAgentProvider> agents, AgentSettings settings, Action openSettings, Func<string, string> addProject)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.PaintFrame(this);
        _agents = agents.ToArray();
        _settings = settings;
        _openSettings = openSettings;
        _addProject = addProject;
        Closing += (_, _) => _checks.Cancel();
        Loaded += async (_, _) => await CheckAgentsAsync();
    }

    private (TextBlock Status, Button Install) RowOf(AgentProviderKind kind) =>
        kind == AgentProviderKind.Codex ? (CodexStatusText, InstallCodexButton) : (ClaudeStatusText, InstallClaudeButton);

    /// <summary>Each CLI is run for its version and sign-in, off the UI thread, both at once.</summary>
    private Task CheckAgentsAsync() => Task.WhenAll(_agents.Select(CheckAsync));

    private async Task CheckAsync(IAgentProvider agent)
    {
        var (text, install) = RowOf(agent.Kind);
        text.Text = "Checking…";
        install.Visibility = Visibility.Collapsed;
        try
        {
            var status = await Task.Run(() => agent.CheckAsync(_settings, _checks.Token));
            text.Text = FirstRun.Status(agent.Kind, status);
            install.Visibility = status.Installed ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // The window was closed while the CLI was asked.
        }
    }

    private void Install_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } || !Enum.TryParse<AgentProviderKind>(tag, out var kind)) return;
        try
        {
            Process.Start(new ProcessStartInfo(FirstRun.InstallAddress(kind)) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            // Without a default browser the page is simply not opened.
        }
    }

    private async void Settings_OnClick(object sender, RoutedEventArgs e)
    {
        // Settings may have chosen where an agent is.
        _openSettings();
        await CheckAgentsAsync();
    }

    private void AddFolder_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the folder your work lives in" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            ProjectText.Text = $"Project “{_addProject(dialog.FolderName)}” added.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ProjectText.Text = $"The project could not be added: {exception.Message}";
        }
    }

    private void Continue_OnClick(object sender, RoutedEventArgs e) => Close();
}
