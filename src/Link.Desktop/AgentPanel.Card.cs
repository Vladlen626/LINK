using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;
using Microsoft.Win32;

namespace Link.Desktop;

/// <summary>
/// An agent as the card offers it: its own models and, for each, the efforts it takes; the default the card starts
/// from (Settings' choice, null: the CLI's own, named by <see cref="OwnModel"/> and <see cref="OwnEffort"/>), and its
/// usage windows.
/// </summary>
public sealed record AgentCardProvider(
    AgentProviderKind Kind,
    string Name,
    IReadOnlyList<AgentChoice> Models,
    Func<string?, IReadOnlyList<AgentChoice>> EffortsOf,
    string OwnModel,
    string OwnEffort,
    string? Model,
    string? Effort,
    IReadOnlyList<LimitRow> Limits);

/// <summary>
/// What the task card starts from. A hand-over shows its brief on the same card with <see cref="FixedProvider"/> and
/// its own <see cref="Title"/> and <see cref="RunLabel"/>. <see cref="Images"/> are the pictures that go along.
/// </summary>
public sealed record AgentCardModel(
    IReadOnlyList<AgentCardProvider> Providers,
    AgentProviderKind Provider,
    string ProjectName,
    string? Workspace,
    string Attachments,
    IReadOnlyList<string> Images,
    string Goal,
    string? CheckCommand,
    AgentAccessChoice Access)
{
    public string Title { get; init; } = "New task";
    public string? RunLabel { get; init; }
    public bool FixedProvider { get; init; }

    /// <summary>The effort list starts with Auto, Link's pick for each run, and the card starts on it.</summary>
    public bool AutoEffort { get; init; }

    /// <summary>The specialists the card offers (none: no row) and the one it starts on, Link's pick; null: General.</summary>
    public IReadOnlyList<AgentRole> Roles { get; init; } = [];

    public string? RoleId { get; init; }

    /// <summary>The workspace's skills, which the card picks from and offers to add (none: no row).</summary>
    public IReadOnlyList<ProjectSkill> WorkspaceSkills { get; init; } = [];

    /// <summary>The skills the card starts with, such as a task's own on a hand-over; null: picked from the goal.</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>"Context budget 400k · 5 skills · MCP: unreal, blender": what else goes with the run, shown muted under the setup.</summary>
    public string HarnessLine { get; init; } = string.Empty;

    /// <summary>The project the card was opened for: Run starts the task in it, whichever project the overlay shows by then.</summary>
    public Guid ProjectId { get; init; }

    /// <summary>Why the card is not on Settings' default agent (its limit is reached now); null when it is.</summary>
    public string? Notice { get; init; }

    /// <summary>Makes the card anew from Settings as they stand, for when they are saved while it is open; see <see cref="AgentPanel.RefreshSetup"/>.</summary>
    public Func<AgentCardModel>? Renew { get; init; }

    /// <summary>The card offers a team (Settings allow it); the box is ticked while the goal reads as team work (<see cref="TeamRunner.Suggests"/>).</summary>
    public bool TeamOffered { get; init; }
}

// The card before a run: the agent, model, effort and access, the goal, its images, and Run.
public partial class AgentPanel
{
    public void ShowCard(AgentCardModel card)
    {
        _card = card;
        _fillingCard = true;
        CardTitleText.Text = card.Title;
        ProviderCombo.ItemsSource = card.Providers.Select(provider => provider.Name).ToArray();
        ProviderCombo.SelectedIndex = Math.Max(0, card.Providers.ToList().FindIndex(provider => provider.Kind == card.Provider));
        ProviderCombo.IsEnabled = !card.FixedProvider;
        AccessCombo.SelectedIndex = card.Access == AgentAccessChoice.Auto ? 1 : 0;
        PlanFirstBox.IsChecked = false;
        PlanFirstBox.Visibility = card.FixedProvider ? Visibility.Collapsed : Visibility.Visible;
        NoStopBox.IsChecked = false;
        NoStopBox.Visibility = PlanFirstBox.Visibility;
        _teamChosen = false;
        TeamBox.Visibility = card.TeamOffered && !card.FixedProvider ? Visibility.Visible : Visibility.Collapsed;
        TeamBox.IsChecked = TeamBox.Visibility == Visibility.Visible && TeamRunner.Suggests(card.Goal, card.Roles);
        GoalBox.Text = card.Goal;
        ProjectText.Text = card.ProjectName;
        ShowWorkspace(card);
        HarnessLineText.Text = card.HarnessLine;
        CardNoticeText.Text = card.Notice ?? string.Empty;
        CardNoticeText.Visibility = card.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentsText.Text = card.Attachments;
        AttachmentsText.Visibility = card.Attachments.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ShowImages(card.Images);
        CardWarningText.Visibility = Visibility.Collapsed;
        ShowAgentChoices();
        ShowRoles(card);
        _fillingCard = false;
        UpdateCard();
        ShowView(CardView);
        Window.GetWindow(this)?.Activate();
        GoalBox.Focus();
        GoalBox.CaretIndex = GoalBox.Text.Length;
    }

    /// <summary>
    /// Settings or the project's workspace were saved while the card is open: its agents (their models and efforts, the
    /// defaults, Auto), their usage and the line of what goes with the run are made anew. The goal, the specialist and
    /// the skills stay, and so do the agent, model and effort the user picked here while they still exist; a model or
    /// effort the user left alone follows the new default. Another workspace brings its own specialists and skills,
    /// picked from the goal as it stands.
    /// </summary>
    public void RefreshSetup()
    {
        if (_card is not { Renew: { } renew } old || SelectedAgent is not { } was) return;
        var (model, effort, auto) = (SelectedModel, SelectedEffort, SelectedAutoEffort);
        var fresh = renew();
        var card = _card = old with { Providers = fresh.Providers, AutoEffort = fresh.AutoEffort, HarnessLine = fresh.HarnessLine };
        _fillingCard = true;
        if (fresh.Workspace != old.Workspace)
        {
            card = _card = card with
            {
                Workspace = fresh.Workspace,
                Roles = fresh.Roles,
                RoleId = ProjectRoles.Suggest(GoalBox.Text, fresh.Roles)?.Id,
                WorkspaceSkills = fresh.WorkspaceSkills
            };
            ShowWorkspace(card);
            ShowRoles(card);
        }

        ProviderCombo.ItemsSource = card.Providers.Select(provider => provider.Name).ToArray();
        var kinds = card.Providers.Select(provider => provider.Kind).ToList();
        ProviderCombo.SelectedIndex = Math.Max(0, kinds.IndexOf(kinds.Contains(was.Kind) ? was.Kind : card.Provider));
        HarnessLineText.Text = card.HarnessLine;
        ShowAgentChoices();
        if (SelectedAgent is { Kind: var kind } agent && kind == was.Kind)
        {
            var models = agent.Models.ToList();
            if (model != was.Model && (model is null || models.Any(choice => choice.Value == model)))
            {
                ModelCombo.SelectedIndex = model is null ? 0 : models.FindIndex(choice => choice.Value == model) + 1;
            }

            // Auto was the default when the card offered it; otherwise the agent's own default effort.
            var effortChanged = old.AutoEffort ? !auto : effort != was.Effort;
            if (effortChanged && (effort is null || agent.EffortsOf(SelectedModel).Any(choice => choice.Value == effort)))
            {
                ShowEfforts(agent, effort, auto: false);
            }
            else
            {
                ShowEfforts(agent, agent.Effort, auto: true);
            }
        }

        _fillingCard = false;
        UpdateCard();
    }

    /// <summary>The folder the run works in, or the way to choose one: Run waits for it.</summary>
    private void ShowWorkspace(AgentCardModel card)
    {
        WorkspaceText.Text = card.Workspace ?? "Not chosen yet: the agent needs the folder it works in";
        WorkspaceText.Foreground = (Brush)FindResource(card.Workspace is null ? "ErrorBrush" : "MutedBrush");
        ChooseWorkspaceButton.Visibility = card.Workspace is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseWorkspace_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card is not { } card) return;
        var dialog = new OpenFolderDialog { Title = $"Choose the folder {card.ProjectName}'s work lives in" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) WorkspaceChosen?.Invoke(card.ProjectId, dialog.FolderName);
    }

    /// <summary>The card is gone: back to the open task, or to the empty middle.</summary>
    public void HideCard()
    {
        _card = null;
        ShowView(_hasTask ? TaskView : EmptyView);
    }

    public void ShowCardWarning(string message)
    {
        CardWarningText.Text = message;
        CardWarningText.Visibility = Visibility.Visible;
    }

    /// <summary>An agent's windows anew on an open card, as they are once asked again.</summary>
    public void ShowCardLimits(AgentProviderKind provider, IReadOnlyList<LimitRow> limits)
    {
        if (_card is not { } card) return;
        _card = card with { Providers = card.Providers.Select(agent => agent.Kind == provider ? agent with { Limits = limits } : agent).ToArray() };
        if (SelectedAgent?.Kind == provider) CardLimitsList.ItemsSource = limits;
    }

    /// <summary>Small pictures of the images that go along; a click shows the file.</summary>
    private void ShowImages(IReadOnlyList<string> images)
    {
        ImagesPanel.Children.Clear();
        foreach (var path in images.Where(File.Exists))
        {
            // A picture that does not read still goes; only its preview is missing.
            if (Thumbnail(path) is { } picture)
            {
                var thumbnail = new Border
                {
                    Margin = new Thickness(0, 0, 6, 6),
                    CornerRadius = new CornerRadius(4),
                    BorderBrush = (Brush)FindResource("GlassBorderSoftBrush"),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Cursor = Cursors.Hand,
                    ToolTip = $"Goes to the agent as an image: {path}",
                    Child = new Image { Source = picture, Height = ThumbnailHeight, Stretch = Stretch.Uniform }
                };
                thumbnail.MouseLeftButtonUp += (_, _) => OpenFileRequested?.Invoke(path);
                ImagesPanel.Children.Add(thumbnail);
            }
        }

        ImagesPanel.Visibility = ImagesPanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private AgentCardProvider? SelectedAgent =>
        _card is { } card && ProviderCombo.SelectedIndex >= 0 ? card.Providers[ProviderCombo.SelectedIndex] : null;

    /// <summary>The selected agent's models and efforts, its default first chosen, and its usage windows.</summary>
    private void ShowAgentChoices()
    {
        if (SelectedAgent is not { } agent) return;
        var filling = _fillingCard;
        _fillingCard = true;
        ModelCombo.ItemsSource = agent.Models.Prepend(new AgentChoice(string.Empty, agent.OwnModel)).ToArray();
        ModelCombo.SelectedIndex = Math.Max(0, agent.Models.ToList().FindIndex(model => model.Value == agent.Model) + 1);
        ShowEfforts(agent, agent.Effort, auto: true);
        CardLimitsList.ItemsSource = agent.Limits;
        _fillingCard = filling;
    }

    /// <summary>
    /// The efforts the chosen model takes, keeping <paramref name="effort"/> when it is one of them. A card on Auto
    /// offers Auto first, and <paramref name="auto"/> chooses it.
    /// </summary>
    private void ShowEfforts(AgentCardProvider agent, string? effort, bool auto)
    {
        var efforts = agent.EffortsOf(SelectedModel);
        // A model with no effort levels (a Codex model without them) has nothing to choose, and Auto nothing to pick.
        EffortCombo.IsEnabled = efforts.Count > 0;
        EffortCombo.ToolTip = efforts.Count > 0 ? _effortTip : "This model takes no effort setting";
        if (efforts.Count == 0)
        {
            EffortCombo.ItemsSource = new[] { new AgentChoice(string.Empty, "None") };
            EffortCombo.SelectedIndex = 0;
            return;
        }

        var offered = _card?.AutoEffort == true;
        var choices = efforts.Prepend(new AgentChoice(string.Empty, agent.OwnEffort));
        EffortCombo.ItemsSource = (offered ? choices.Prepend(AutoChoice) : choices).ToArray();
        EffortCombo.SelectedIndex = offered && auto ? 0 : (offered ? 1 : 0) + Math.Max(0, efforts.ToList().FindIndex(choice => choice.Value == effort) + 1);
    }

    // The first item of each list is the CLI's own choice, sent as none; Auto, when offered, is before it.
    private string? SelectedModel => ModelCombo.SelectedItem is AgentChoice { Value.Length: > 0 } model ? model.Value : null;
    private bool SelectedAutoEffort => ReferenceEquals(EffortCombo.SelectedItem, AutoChoice);
    private string? SelectedEffort => EffortCombo.SelectedItem is AgentChoice { Value.Length: > 0 } effort && !SelectedAutoEffort ? effort.Value : null;

    // The items follow AgentAccessChoice: edit, auto.
    private bool SelectedAuto => AccessCombo.SelectedIndex == 1;

    private void Provider_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCard) return;
        ShowAgentChoices();
        UpdateCard();
    }

    private void Model_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingCard || SelectedAgent is not { } agent) return;
        ShowEfforts(agent, SelectedEffort, SelectedAutoEffort);
    }

    /// <summary>
    /// The goal as the user left it: the specialist is picked from the new words while it is still Link's pick, and the
    /// skills are, whoever chose the specialist. A hand-over keeps the task's.
    /// </summary>
    private void Goal_OnLostFocus(object sender, RoutedEventArgs e)
    {
        // A press on Run takes the focus before its click: that click is not to run a pick the card has not shown yet.
        _pickChangedByRun = PickFromGoal() && RunButton.IsMouseOver;
    }

    /// <summary>Picks the specialist and the skills from the goal as it stands; whether the pick changed.</summary>
    private bool PickFromGoal()
    {
        if (_card is not { FixedProvider: false } card) return false;
        var role = SelectedRoleId;
        if (!_roleChosen && card.Roles.Count > 0) SelectRole(ProjectRoles.Suggest(GoalBox.Text, card.Roles)?.Id);
        // Team follows the goal until the user ticks or clears it.
        if (!_teamChosen && TeamBox.Visibility == Visibility.Visible) TeamBox.IsChecked = TeamRunner.Suggests(GoalBox.Text, card.Roles);
        // The chips stay as they are unless the pick changed: a click on one lost the focus of the goal, and a chip
        // drawn anew under the mouse would take that click.
        var skills = PickedSkills();
        var changed = role != SelectedRoleId || !skills.SequenceEqual(_skills);
        if (changed) ShowSkills(skills);
        return changed;
    }

    /// <summary>Ctrl+Enter in the goal runs the task as the Run button does, with the same guard against a pick not yet seen.</summary>
    private void Goal_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        _pickChangedByRun = PickFromGoal();
        Run_OnClick(RunButton, e);
    }

    private void Card_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_fillingCard) UpdateCard();
    }

    private void UpdateCard()
    {
        if (_card is not { } card) return;
        CheckText.Text = $"After the edits Link runs the check itself, and the agent is told so: {card.CheckCommand}. " +
                         $"While it fails, the agent gets its output, {AgentCoordinator.CheckRetries} times at most.";
        CheckText.Visibility = card.CheckCommand is not null ? Visibility.Visible : Visibility.Collapsed;
        PlanFirstBox.ToolTip = "A plan, then you run it: the agent proposes a plan before the work, on Settings' Strong model; you run it, on the model chosen here, in the same session";
        // A team always plans first, on the lead.
        PlanFirstBox.IsEnabled = !Team;
        RunButton.IsEnabled = card.Workspace is not null;
        RunButton.ToolTip = card.Workspace is null ? "Choose the workspace folder first: Choose folder… below" : null;
        RunButton.Content = card.RunLabel ?? (Team ? "Plan the team" : PlanFirstBox.IsChecked == true ? "Make a plan" : SelectedAuto ? "Run on auto" : "Run with edits");
    }

    private bool Team => TeamBox.Visibility == Visibility.Visible && TeamBox.IsChecked == true;

    private void TeamBox_OnClick(object sender, RoutedEventArgs e) => _teamChosen = true;

    private void Run_OnClick(object sender, RoutedEventArgs e)
    {
        if (_card is not { Workspace: { } workspace } card || SelectedAgent is not { } agent) return;
        if (string.IsNullOrWhiteSpace(GoalBox.Text))
        {
            ShowCardWarning("Write what the agent should do.");
            return;
        }

        if (_pickChangedByRun)
        {
            _pickChangedByRun = false;
            ShowCardWarning("The specialist or skills changed with the goal: check them, then press Run again.");
            return;
        }

        var plan = new AgentPlan(
            agent.Kind,
            workspace,
            Team || (PlanFirstBox.Visibility == Visibility.Visible && PlanFirstBox.IsChecked == true),
            GoalBox.Text.Trim())
        {
            Team = Team,
            Model = SelectedModel,
            Effort = SelectedEffort,
            // A model with no effort levels leaves Auto as the card had it, for the agents the task may reach.
            AutoEffort = SelectedAutoEffort || (!EffortCombo.IsEnabled && card.AutoEffort),
            AutoApprove = SelectedAuto,
            RoleId = SelectedRoleId,
            Skills = _skills,
            ProjectId = card.ProjectId,
            NoStop = NoStopBox.Visibility == Visibility.Visible && NoStopBox.IsChecked == true,
            Attached = [.. _cardAttached]
        };
        _card = null;
        _cardAttached.Clear();
        ShowAttached();
        RunRequested?.Invoke(plan);
    }

    private void CardHarness_OnClick(object sender, RoutedEventArgs e) => HarnessRequested?.Invoke();

    private void CancelCard_OnClick(object sender, RoutedEventArgs e)
    {
        _card = null;
        _cardAttached.Clear();
        ShowAttached();
        CardCancelled?.Invoke();
    }
}
