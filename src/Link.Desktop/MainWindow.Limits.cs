using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// Subscription limits: warned before a run, reached during one, and the task going on after the reset.
public partial class MainWindow
{
    /// <summary>The agent is near its usage limit while it works: the ways on are offered, nothing is done by itself.</summary>
    private void OfferLimitAhead(TaskWork work, LiveRun live)
    {
        live.LimitOffered = true;
        var provider = live.Task.Provider;
        var other = Other(provider);
        SetLimitCard(work, $"{AgentName(provider)} is near its usage limit: {_limits.Describe(provider)}.",
        [
            new PanelAction($"Stop and hand off to {AgentName(other)}", "The agent stops; a card shows the brief for the other agent",
                () =>
                {
                    // A run that is over has nothing to stop: the card for the other agent comes at once.
                    if (work.Run != live)
                    {
                        _ = ShowHandOffCardAsync(work, other);
                        return;
                    }

                    work.HandOffWhenStopped = other;
                    live.Stop.Cancel();
                }),
            new PanelAction("Go on", "The agent works on; when the limit stops it, the same choices come again", () => { })
        ]);
        work.Record.Add(AgentEntryKind.Limit, $"{AgentName(provider)} near its limit: {_limits.Describe(provider)}");
        AgentStatus(work, $"{AgentName(provider)} is near its usage limit · the Studio offers a way on");
    }

    /// <summary>
    /// The run ended on its usage limit: hand the task to the other agent, or let the same session go on by itself a
    /// minute after the limit resets. Going on is a box on the card, ticked as the user last left it (see
    /// <see cref="AgentSettings.ContinueAfterLimit"/>). Without a known reset only the hand-off is offered.
    /// </summary>
    private void OfferLimitReached(TaskWork work, AgentProviderKind provider)
    {
        var record = work.Record;
        var until = _limits.ReachedUntil(provider);
        // No-stop waits for the reset whatever the box was left at.
        var goOn = until is not null && (_settings.Current.Agent.ContinueAfterLimit || record.NoStop);
        record.Add(AgentEntryKind.Limit, until is { } reset
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(reset)}" +
              (goOn ? $", and the task goes on by itself at {AgentLimits.Time(reset.AddMinutes(1))}" : string.Empty)
            : $"{AgentName(provider)} hit its usage limit");
        if (goOn)
        {
            record.ContinueAt = until!.Value.AddMinutes(1);
            ArmContinue(work);
        }

        ShowLimitReached(work, provider, until);
        SaveTask(work);
        ShowTask(work);
    }

    /// <summary>
    /// The card of a limit reached: the hand-off, and for a known reset the box that lets the task go on by itself after
    /// it.
    /// </summary>
    private void ShowLimitReached(TaskWork work, AgentProviderKind provider, DateTimeOffset? reset)
    {
        var other = Other(provider);
        PanelAction[] actions =
        [
            new($"Hand off to {AgentName(other)}", "A new session of the other agent with a brief of the task; you see it first",
                () => _ = ShowHandOffCardAsync(work, other))
        ];
        var toggle = reset is { } at
            ? new PanelToggle($"Continue at {AgentLimits.Time(at.AddMinutes(1))}",
                "The task goes on by itself a minute after the limit resets, also if Link is restarted before then; a big session starts fresh from the task's state. Untick it, and the task waits for your words",
                work.Record.ContinueAt is not null, on => GoOnAfterReset(work, at, on))
            : null;
        SetLimitCard(work, reset is { } time
            ? $"{AgentName(provider)} hit its usage limit; it resets {AgentLimits.Time(time)}."
            : $"{AgentName(provider)} hit its usage limit.", actions, toggle);
    }

    /// <summary>
    /// The box on the limit's card: ticked, the task goes on by itself a minute after the limit resets, the time kept
    /// with the task so that a restart of Link does not lose it; unticked, it waits for the user's words. The next limit
    /// finds the box as it was left. Finish or a new run of the task cancels the wait.
    /// </summary>
    private void GoOnAfterReset(TaskWork work, DateTimeOffset reset, bool on)
    {
        if (_settings.Current.Agent.ContinueAfterLimit != on)
        {
            _settings.Current.Agent.ContinueAfterLimit = on;
            _settings.Save();
        }

        var record = work.Record;
        var at = reset.AddMinutes(1);
        if (on)
        {
            record.ContinueAt = at;
            ArmContinue(work);
        }
        else
        {
            CancelContinue(work);
        }

        // The card keeps the box as it was left.
        if (work.Limit is { Toggle: { } toggle } card) work.Limit = card with { Toggle = toggle with { On = on } };
        record.Add(AgentEntryKind.Limit, on ? $"The task goes on by itself at {AgentLimits.Time(at)}" : "The task waits for your words after the reset");
        SaveTask(work);
        ShowTask(work);
        AgentStatus(work, on
            ? $"The task goes on by itself at {AgentLimits.Time(at)}, also if Link is restarted before then"
            : "The task waits for your words: tick Continue again to let it go on by itself");
    }

    /// <summary>
    /// Starts the timer that sends the task on at its <see cref="AgentTaskRecord.ContinueAt"/>; a time already past is a
    /// second away. Each task waits for its own reset, and its words go only to it.
    /// </summary>
    private void ArmContinue(TaskWork work)
    {
        work.ContinueTimer?.Stop();
        if (work.Record.ContinueAt is not { } at) return;
        var wait = at - DateTimeOffset.Now;
        var timer = new DispatcherTimer { Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_works.Contains(work)) return;
            // Its run waits for its folder: a minute later.
            if (work.Busy || work.Waiting is not null)
            {
                timer.Interval = TimeSpan.FromMinutes(1);
                timer.Start();
                return;
            }

            work.ContinueTimer = null;
            work.Record.ContinueAt = null;
            work.Record.Add(AgentEntryKind.Limit, "The limit reset: the session goes on");
            SaveTask(work);
            ContinueAgent(work, "Your usage limit has reset. Go on with the task from where you stopped.", afterLimit: true);
        };
        work.ContinueTimer = timer;
        timer.Start();
        KeepAwakeAtNight();
    }

    /// <summary>The wait for a limit's reset is over without firing: the timer stops and the task no longer remembers it.</summary>
    private void CancelContinue(TaskWork work)
    {
        work.ContinueTimer?.Stop();
        work.ContinueTimer = null;
        KeepAwakeAtNight();
        if (work.Record.ContinueAt is null) return;
        work.Record.ContinueAt = null;
        SaveTask(work);
    }

    /// <summary>
    /// Link started, or another project was loaded in the overlay or the Studio: their tasks that wait for a limit's reset
    /// (their time was kept with them) are taken up again, without opening the Studio, and their timers run; the first is
    /// the open task while none is.
    /// </summary>
    private void RearmContinue()
    {
        foreach (var project in new[] { _projects.ActiveProject, StudioProject }.Distinct())
        {
            foreach (var waiting in _projects.ReadAgentTasks(project).Where(task =>
                         task.ContinueAt is not null && task.Status != AgentTaskStatus.Finished && WorkOf(task.Id) is null && Directory.Exists(task.Workspace)))
            {
                var work = TakeUpTask(waiting, project);
                _open ??= work;
            }
        }

        UpdateAgentPill();
        UpdateRouteButton();
    }
}
