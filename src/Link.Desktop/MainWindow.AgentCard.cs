using System.Windows.Media;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// The task card: which agent works with what model and effort, the limits it shows, and the setup of the next run.
public partial class MainWindow
{
    /// <summary>The task card for a snapshot: <paramref name="brief"/> is what the agent is told, the question by default.</summary>
    private void OpenAgentCard(InputSnapshot snapshot, string? brief = null)
    {
        _agentSnapshot = snapshot;
        var (_, images) = AgentCoordinator.Words(snapshot, newTask: true);
        var others = snapshot.Context.Append(snapshot.Screen).OfType<ContextItem>()
            .Where(item => item.FilePath is null || !images.Contains(item.FilePath))
            .Select(item => item.Label)
            .ToArray();
        var (starting, notice) = StartingAgent();
        // The task runs beside the others at once; only its edits wait for another task's in the same folder.
        if (_projects.ActiveProject.WorkspaceRoot is { Length: > 0 } folder && FolderBusy(null, folder) is { } busy)
        {
            var waits = $"{busy}: this task's edits wait for that run and start by itself, reading and planning start at once.";
            notice = notice is null ? waits : $"{waits} {notice}";
        }

        var card = CardModel(brief ?? snapshot.Text, others.Length == 0 ? string.Empty : "Goes along: " + string.Join(", ", others),
            images, starting) with { Notice = notice };
        _cardRun = RunAgentAsync;
        ShowAgentPanel();
        _agentPanel!.ShowCard(card);
        RefreshStudio();
        AgentStatus("Check the card and press Run: nothing runs before that");
    }

    /// <summary>
    /// The card's agents, each with its own models and efforts, Settings' choice first, and its usage. The card keeps the
    /// project it is made for (the active one unless <paramref name="forProject"/> says), whatever is active when it runs.
    /// </summary>
    private AgentCardModel CardModel(string goal, string attachments, IReadOnlyList<string> images, AgentProviderKind provider,
        LinkProject? forProject = null)
    {
        var project = forProject ?? _projects.ActiveProject;
        var settings = _settings.Current.Agent;
        var workspace = project.WorkspaceRoot is { } root && Directory.Exists(root) ? root : null;
        var roles = ProjectRoles.In(workspace);
        IReadOnlyList<ProjectSkill> skills = workspace is null ? [] : ProjectSkills.In(workspace);
        return new AgentCardModel(
            _agents.Providers.Select(agent =>
            {
                var (model, effort) = settings.Level(agent.Kind, AgentLevel.Standard);
                var own = agent.OwnSetup;
                return new AgentCardProvider(
                    agent.Kind,
                    agent.Name,
                    agent.Models,
                    agent.EffortsOf,
                    own.Model is { } ownModel ? $"Its own: {ModelName(agent, ownModel)}" : $"{agent.Name}'s own model",
                    own.Effort is { } ownEffort ? $"Its own: {EffortName(agent, ownEffort)}" : "Its own effort",
                    model,
                    effort,
                    LimitRows(agent.Kind));
            }).ToArray(),
            provider,
            project.Name,
            workspace,
            attachments,
            images,
            goal,
            project.CheckCommand,
            settings.DefaultAccess)
        {
            AutoEffort = settings.AutoEffort,
            TeamOffered = settings.Team.Offered,
            Roles = roles,
            RoleId = ProjectRoles.Suggest(goal, roles)?.Id,
            WorkspaceSkills = skills,
            HarnessLine = CardHarnessLine(workspace, skills.Count),
            ProjectId = project.Id,
            Renew = () => CardModel(goal, attachments, images, provider, project)
        };
    }

    private string CardHarnessLine(string? workspace, int skills) =>
        HarnessView.CardLine(_settings.Current.Agent.ContextBudget, skills, workspace is null ? [] : CliAgentProvider.McpServerNames(workspace));

    private static string ModelName(IAgentProvider agent, string model) =>
        agent.Models.FirstOrDefault(choice => choice.Value == model)?.Label ?? model;

    private static string EffortName(IAgentProvider agent, string effort) =>
        agent.Efforts.FirstOrDefault(choice => choice.Value == effort)?.Label.Split(" — ")[0] ?? effort;

    /// <summary>Whether a run is on the strongest model and effort Settings has for its agent.</summary>
    private bool IsStrongest(AgentTask task) => (task.Model, task.Effort) == _settings.Current.Agent.Level(task.Provider, AgentLevel.Deep);

    /// <summary>
    /// An agent's usage windows as bars: how much is used and when each resets. Each asks the agent anew when clicked;
    /// an agent that has told nothing yet gets one row that offers it.
    /// </summary>
    private IReadOnlyList<LimitRow> LimitRows(AgentProviderKind provider)
    {
        // No subscription, no windows: nothing to show or to ask.
        if (!_agents.Provider(provider).Capabilities.Subscription) return [];
        var asking = _askingLimits.Contains(provider);
        var again = $"\nClick to ask {AgentName(provider)} now: it reads the account's usage and spends no tokens";
        var known = _limits.For(provider);
        if (known.Count == 0)
        {
            return [new LimitRow($"{ShortName(provider)} · usage", 0, asking ? "asking…" : "click to ask", (Brush)FindResource("MutedBrush"),
                $"{AgentName(provider)} has told Link no usage yet.{again}", provider)];
        }

        return known.Select(limit =>
        {
            var percent = Math.Clamp(limit.UsedPercent ?? (limit.Reached ? 100 : 0), 0, 100);
            var brush = (Brush)FindResource(limit.Reached || percent >= AgentLimits.NearPercent ? "ErrorBrush" : percent >= 70 ? "AccentBrush" : "SuccessBrush");
            var resets = limit.ResetsAt is { } at ? $" · {AgentLimits.Time(at)}" : string.Empty;
            var used = limit.UsedPercent is not null ? $"{percent:0}%" : limit.Reached ? "reached" : "—";
            var hint = $"{AgentName(provider)}, the {limit.Window} window: " +
                       (limit.UsedPercent is not null ? $"{percent:0}% used" : limit.Reached ? "the limit is reached" : "no number yet") +
                       (limit.ResetsAt is { } reset ? $", resets {AgentLimits.Time(reset)}" : string.Empty) +
                       $" (as of {AgentLimits.Time(limit.SeenAt)}).{again}";
            return new LimitRow($"{ShortName(provider)} · {limit.Window}", percent, asking ? "asking…" : used + resets, brush, hint, provider);
        }).ToArray();
    }

    /// <summary>Asks an agent's CLI for its usage now, such as after its account was switched.</summary>
    public void RefreshLimits(AgentProviderKind provider) => _ = RefreshLimitsAsync(provider);

    /// <summary>
    /// A click on an agent's usage: its CLI is asked for the account's windows now, without a model run (Claude Code's
    /// /usage, Codex's app-server), and every bar shows the answer.
    /// </summary>
    private async Task RefreshLimitsAsync(AgentProviderKind provider)
    {
        if (!_askingLimits.Add(provider)) return;
        ShowLimitRows();
        var agent = _agents.Provider(provider);
        try
        {
            var limits = await Task.Run(() => agent.ReadLimitsAsync(CancellationToken.None));
            if (limits.Count > 0) _limits.Update(limits);
            AgentStatus(limits.Count > 0
                ? $"{agent.Name}: {_limits.Describe(provider)}"
                : $"{agent.Name} told no usage: it may be signed out, or work without a subscription");
        }
        catch (OperationCanceledException)
        {
            AgentStatus($"{agent.Name} did not tell its usage in time; try again later");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AgentStatus($"Could not ask {agent.Name} for its usage: {exception.Message}");
        }
        finally
        {
            _askingLimits.Remove(provider);
            ShowLimitRows();
            UpdateAgentPill();
        }
    }

    /// <summary>The bars wherever they are: the sidebar's foot and an open card.</summary>
    private void ShowLimitRows()
    {
        RefreshTaskList();
        if (_agentPanel is null) return;
        foreach (var agent in _agents.Providers) _agentPanel.ShowCardLimits(agent.Kind, LimitRows(agent.Kind));
    }

    private IReadOnlyList<LimitRow> AllLimitRows() => _agents.Providers.SelectMany(agent => LimitRows(agent.Kind)).ToArray();

    /// <summary>Cancel on the card: back to the task it was opened from, or to the middle with no task open.</summary>
    private void CancelCard()
    {
        _cardRun = null;
        _agentPanel?.HideCard();
        if (_ownShown) ShowOwnRun();
        else if (_shown is { } work) ShowTask(work);
        else ShowTaskList();
        RefreshStudio();
    }

    /// <summary>
    /// A run's model and effort as the user reads them, "Opus 5.5 · Extra high"; the CLI's own as its config names it.
    /// Where Link chose the effort, <paramref name="autoReason"/> says why: "Opus 5.5 · Medium · auto: a usual task". The
    /// model the CLI said it ran (<paramref name="ranModel"/>) follows a family: "Haiku · latest (claude-haiku-4-5)", and
    /// stands alone where neither Link nor the CLI's config named one; else <paramref name="unknown"/>.
    /// </summary>
    private string SetupWords(AgentProviderKind provider, string? model, string? effort, string? autoReason = null, string? ranModel = null,
        string unknown = "default model")
    {
        var agent = _agents.Provider(provider);
        var own = agent.OwnSetup;
        var modelName = (model ?? own.Model) is { } chosen
            ? ranModel is null ? ModelName(agent, chosen) : $"{ModelName(agent, chosen)} ({ranModel})"
            : ranModel ?? unknown;
        var words = (effort ?? own.Effort) is { } level ? $"{modelName} · {EffortName(agent, level)}" : modelName;
        return autoReason is null ? words : $"{words} · auto: {autoReason}";
    }

    /// <summary>
    /// What the user's next words in the task's session run on, chosen in the composer: the model, the effort (null with
    /// Auto, which the task record keeps) and the specialist. Nothing runs when it is chosen; the next run takes it.
    /// </summary>
    private sealed record NextSetup(AgentProviderKind Provider, string? Model, string? Effort, string? RoleId);

    /// <summary>The setup the task's next words run on: the one chosen, else the session's own; null when no session takes words.</summary>
    private static NextSetup? CurrentSetup(TaskWork work)
    {
        if (work.NextSetup is { } chosen) return chosen;
        var task = work.Run?.Task ?? ResumableTask(work)?.Task ?? work.LastRun?.Task;
        return task is null ? null : new NextSetup(task.Provider, task.Model, task.Effort, work.Record.RoleId);
    }

    /// <summary>
    /// The composer's choice of model, effort and specialist for the next words, like the model picker of the agents' own
    /// apps: the line says what they run on, the menu changes it, and the runs after keep it.
    /// </summary>
    private void ShowSetup(AgentPanel panel, TaskWork work)
    {
        if (CurrentSetup(work) is not { } setup)
        {
            panel.ShowSetup(string.Empty, []);
            return;
        }

        var record = work.Record;
        var agent = _agents.Provider(setup.Provider);
        var auto = record.AutoEffort && _settings.Current.Agent.AutoEffort;
        var efforts = agent.EffortsOf(setup.Model);
        var effortChoices = efforts.Select(effort => new PanelAction(effort.Label, "This effort from the next words on; Auto stops choosing it",
            () => ChooseSetup(work, setup with { Effort = effort.Value }, auto: false), Checked: !auto && setup.Effort == effort.Value)).ToList();
        if (efforts.Count > 0 && _settings.Current.Agent.AutoEffort)
        {
            effortChoices.Insert(0, new PanelAction("Auto effort", "Link chooses the effort of each run, and says why in its line",
                () => ChooseSetup(work, setup, auto: true), Checked: auto));
        }

        var models = agent.Models.Select(model => new PanelAction(model.Label,
            "The same session goes on with this model: it keeps what it knows, the model reads it anew once",
            () => ChooseSetup(work, setup with
            {
                Model = model.Value,
                Effort = agent.EffortsOf(model.Value).Any(effort => effort.Value == setup.Effort) ? setup.Effort : null
            }, auto), Checked: setup.Model == model.Value)).ToArray();
        var roles = ProjectRoles.In(record.Workspace).Select(role => new PanelAction(role.Name,
                $"{role.Description} The same session goes on as this specialist, with its skills: it does not read the project again",
                () => ChooseSetup(work, setup with { RoleId = role.Id }, auto),
                Checked: string.Equals(role.Id, setup.RoleId, StringComparison.OrdinalIgnoreCase)))
            .Prepend(new PanelAction("General", "The same session goes on without a specialist role",
                () => ChooseSetup(work, setup with { RoleId = null }, auto), Checked: setup.RoleId is null)).ToArray();
        var effortWords = auto ? "Auto" : setup.Effort is { } effortValue ? EffortName(agent, effortValue) : null;
        var label = string.Join(" · ", new[]
        {
            RoleName(setup.RoleId, record.Workspace) ?? "General",
            setup.Model is { } modelValue ? ModelName(agent, modelValue) : null,
            effortWords
        }.OfType<string>());
        panel.ShowSetup(label, [effortChoices, models, roles]);
    }

    private void ChooseSetup(TaskWork work, NextSetup setup, bool auto)
    {
        work.Record.AutoEffort = auto;
        SaveTask(work);
        work.NextSetup = setup;
        if (Shows(work)) ShowSetup(_agentPanel!, work);
    }

    /// <summary>
    /// The run with the setup chosen for it in the composer, when it is its agent's: the model and effort, and another
    /// specialist, told so before the user's words.
    /// </summary>
    private static AgentTask WithNextSetup(TaskWork work, AgentTask task)
    {
        if (work.NextSetup is not { } next) return task;
        work.NextSetup = null;
        if (next.Provider != task.Provider) return task;
        var record = work.Record;
        task = task with { Model = next.Model, Effort = next.Effort };
        if (string.Equals(next.RoleId, record.RoleId, StringComparison.OrdinalIgnoreCase)) return task;
        var role = ProjectRoles.Find(next.RoleId, record.Workspace);
        var skills = ProjectRoles.SkillsFor(BriefOf(record), role, ProjectSkills.In(record.Workspace));
        record.RoleId = role?.Id;
        record.Skills = [.. skills];
        record.Add(AgentEntryKind.HandOff, $"Specialist: {role?.Name ?? "General"}");
        if (work.Origin is { } origin) work.Origin = origin with { RoleId = role?.Id, Skills = skills };
        return task with { RoleId = role?.Id, Skills = skills, Prompt = $"{ProjectRoles.SwitchText(role, skills, record.Workspace)}\n\n{task.Prompt}" };
    }
}
