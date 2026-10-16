using System.Windows.Threading;
using Link.Desktop.Models;
using Link.Desktop.Services;

namespace Link.Desktop;

// No-stop: a task that takes at each stop the choice its agent recommends, after a countdown the user can cancel, and
// waits for the user only when a stop rule says so (see NoStop).
public partial class MainWindow
{
    /// <summary>Once what happens now is over (a run's end, a queued reply starting), the task's choice is armed.</summary>
    private void NoStopSoon(TaskWork work)
    {
        // A run's end may offer a choice and end too: one arming for both.
        if (!work.Record.NoStop || work.NoStopArming) return;
        work.NoStopArming = true;
        Dispatcher.BeginInvoke(() =>
        {
            work.NoStopArming = false;
            ArmNoStop(work);
        });
    }

    /// <summary>
    /// The choice the task stands at, taken after the countdown on No-stop: the first option of its agent's question, the
    /// decision's button, the next step the agent named, or a fix of a red check. A stop rule makes it wait for the user
    /// instead; a task at work, or one with nothing to choose, has nothing armed.
    /// </summary>
    private void ArmNoStop(TaskWork work)
    {
        CancelNoStop(work);
        var record = work.Record;
        if (!record.NoStop || work.Busy || work.Waiting is not null || !_works.Contains(work)) return;
        if (NoStopChoice(work) is not var (key, label, take)) return;
        if (NoStop.WhyWait(record, _settings.Current.Agent.NoStopTurns, key, work.NoStopLast) is { } why)
        {
            record.Add(AgentEntryKind.HandOff, $"No-stop waits for you: {why}");
            SaveTask(work);
            ShowTask(work);
            AgentStatus(work, $"No-stop waits for you: {why}");
            RemoteAlert?.Invoke(record.Id, $"No-stop waits · {Shorten(record.Goal, 60)}", why);
            return;
        }

        // The choice is taken only while it still stands: an answer, a run or another card meanwhile is the user's.
        var (question, decision, run) = (work.Question, work.Decision, work.LastRun);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(NoStop.CountdownSeconds) };
        timer.Tick += (_, _) =>
        {
            CancelNoStop(work);
            if (!record.NoStop || work.Busy || work.Waiting is not null || !_works.Contains(work) ||
                work.Question != question || work.Decision != decision || work.LastRun != run)
            {
                return;
            }

            work.NoStopLast = key;
            record.NoStopTaken++;
            record.Add(AgentEntryKind.HandOff, $"No-stop took: {label}");
            take();
        };
        work.NoStopTimer = timer;
        work.NoStopPending = label;
        timer.Start();
        ShowTask(work);
        AgentStatus(work, $"No-stop takes «{Shorten(label, 60)}» in {NoStop.CountdownSeconds} s: untick No-stop to choose yourself");
    }

    /// <summary>What No-stop would take now: a key to tell a repeat by, its words for the user, and the action; null for none.</summary>
    private (string Key, string Label, Action Take)? NoStopChoice(TaskWork work)
    {
        if (work.Question is { } question && NoStop.Pick(question) is { } pick)
        {
            return ($"{question.Question} → {pick}", pick, () => ContinueAgent(work, pick));
        }

        if (work.Decision is { } decision)
        {
            return decision.NoStop ? (decision.Text, decision.Button, () => TakeDecision(work, decision, always: false)) : null;
        }

        // A team or a training goes on by its own moves; a solo run that ended well names its next step.
        var record = work.Record;
        if (record.Mode != TaskMode.Solo || work.LastRun is not { } run) return null;
        if (record.Status == AgentTaskStatus.Done && AgentNext.Split(run.Result.Summary).Next is { } next)
        {
            var words = NoStop.GoOn(next);
            return (words, next.Step, () => ContinueAgent(work, words));
        }

        return record.Status == AgentTaskStatus.CheckFailed
            ? (NoStop.FixCheck, "fix the check", () => ContinueAgent(work, NoStop.FixCheck))
            : null;
    }

    /// <summary>The countdown is off: the choice waits for the user, until No-stop arms it again.</summary>
    private static void CancelNoStop(TaskWork work)
    {
        work.NoStopTimer?.Stop();
        work.NoStopTimer = null;
        work.NoStopPending = null;
    }

    /// <summary>The user spoke to the task: No-stop counts its choices in a row from nought again.</summary>
    private static void UserSpoke(TaskWork work)
    {
        CancelNoStop(work);
        work.Record.NoStopTaken = 0;
        work.NoStopLast = null;
    }

    /// <summary>The box at the foot of a task: No-stop on or off, with the choice it is about to take.</summary>
    private PanelToggle NoStopToggle(TaskWork work) => new(
        work.NoStopPending is { } pending ? $"No-stop · «{Shorten(pending, 40)}» in {NoStop.CountdownSeconds} s" : "No-stop",
        "The task takes the choice its agent recommends at each stop and asks for nothing but the deny list of Settings; " +
        $"it waits for you after {_settings.Current.Agent.NoStopTurns} choices in a row, a repeated choice, or {NoStop.IdleRuns} runs without a changed file",
        work.Record.NoStop,
        on => SetNoStop(work, on));

    /// <summary>No-stop turned on or off for a task: on, the choice it stands at is armed; its next run asks for nothing.</summary>
    private void SetNoStop(TaskWork work, bool on)
    {
        var record = work.Record;
        if (record.NoStop == on) return;
        record.NoStop = on;
        UserSpoke(work);
        // Its box shows it: a line in the thread would read as words to the agent.
        record.UpdatedAt = DateTimeOffset.Now;
        SaveTask(work);
        if (on) ArmNoStop(work);
        ShowTask(work);
    }
}
