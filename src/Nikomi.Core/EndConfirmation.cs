using System.Diagnostics;

namespace Nikomi.Core;

/// <summary>What became of a confirmation to end processes.</summary>
public enum EndAnswer
{
    /// <summary>Another dialog was open, or the window could not show one, so the confirmation was not shown.</summary>
    NotShown,
    Declined,
    /// <summary>Nobody answered in time.</summary>
    Expired,
    /// <summary>The user turned agent access off while the agent's request was waiting.</summary>
    Withdrawn,
    Confirmed,
}

/// <summary>What became of a confirmation, and of each process when it was confirmed.</summary>
public sealed record EndOutcome(EndAnswer Answer, IReadOnlyList<EndResult> Results)
{
    public static readonly EndOutcome NotShown = new(EndAnswer.NotShown, Array.Empty<EndResult>());
    public static readonly EndOutcome Declined = new(EndAnswer.Declined, Array.Empty<EndResult>());
    public static readonly EndOutcome Expired = new(EndAnswer.Expired, Array.Empty<EndResult>());
    public static readonly EndOutcome Withdrawn = new(EndAnswer.Withdrawn, Array.Empty<EndResult>());
}

/// <summary>One process in the confirmation's list: its cleaned name and PID, and its command line.</summary>
public sealed record EndTargetLine(string Label, string Detail, bool Critical);

/// <summary>
/// Everything a confirmation shows, worked out before it appears. <see cref="Targets"/> is fixed here:
/// what the user confirms is exactly what is ended, whatever starts or exits while the dialog is open.
/// </summary>
public sealed record EndPlan(
    ProcInfo Process,
    bool Tree,
    IReadOnlyList<ProcInfo> Targets,
    IReadOnlyList<EndTargetLine> Lines,
    string? More,
    string? AgentReason,
    string Title,
    string ListHeading,
    string Warning,
    string ConfirmText,
    string CancelText,
    TimeSpan? ArmDelay)
{
    public const string AgentIntro = "An AI agent connected to Nikomi asks for this. Nothing is ended unless you confirm.";
    public const string AgentReasonLabel = "Its reason, in its own words (not checked by Nikomi):";

    /// <summary>An AI agent asked for this; <see cref="AgentReason"/> is its reason.</summary>
    public bool FromAgent => AgentReason != null;
}

/// <summary>What the window says once the processes were ended.</summary>
public sealed record EndReport(bool AllEnded, string Title, string Summary);

/// <summary>
/// The window's part of a confirmation: showing it and saying what the user pressed. Everything about
/// what is ended, and when a confirmation counts as declined, expired or withdrawn, is decided by
/// <see cref="EndConfirmation"/>.
/// </summary>
public interface IEndDialog
{
    /// <summary>
    /// Waits until a dialog can be shown (a window created a moment ago has not been laid out yet), for at
    /// most <paramref name="limit"/> when there is one. False when it cannot be shown.
    /// </summary>
    Task<bool> WaitUntilReadyAsync(TimeSpan? limit);

    /// <summary>Another dialog of the window is open.</summary>
    bool IsBusy { get; }

    /// <summary>
    /// Shows the plan, with the cancel button as the default and the end button working only after
    /// <see cref="EndPlan.ArmDelay"/>. True only when the user pressed the end button. When
    /// <paramref name="close"/> is cancelled the dialog closes; what it then returns does not count.
    /// </summary>
    Task<bool> AskAsync(EndPlan plan, CancellationToken close);

    /// <summary>Shows what became of each process, after the user confirmed.</summary>
    void ShowEnded(EndReport report);
}

/// <summary>
/// The one way anything is ended: the user's own click and an AI agent's request both come through
/// here. Works out the targets and what the dialog says, asks through <see cref="IEndDialog"/>, and ends
/// the targets only if the user confirmed and the confirmation was neither withdrawn nor expired.
/// Used on the window's thread, like the <see cref="ProcessMonitor"/> it reads.
/// </summary>
public sealed class EndConfirmation
{
    /// <summary>The most processes listed by name; the rest are counted.</summary>
    public const int MaxListed = 60;
    /// <summary>How long an agent's confirmation is shown before its end button can be pressed.</summary>
    public static readonly TimeSpan AgentArmDelay = TimeSpan.FromSeconds(1);

    private readonly ProcessMonitor _monitor;
    private readonly IEndDialog _dialog;
    private readonly Func<bool> _agentAccess;
    private readonly Func<IReadOnlyList<ProcInfo>, IReadOnlyList<EndResult>> _end;
    private readonly Func<ProcInfo, bool> _isCritical;
    private CancellationTokenSource? _withdraw;

    /// <param name="agentAccess">Whether the user allows agents right now.</param>
    /// <param name="end">Ends the confirmed targets; <see cref="ProcessKiller.End"/> unless a check stands in for it.</param>
    /// <param name="isCritical">Whether Windows depends on a process; <see cref="ProcessKiller.IsCritical"/> unless a check stands in for it.</param>
    public EndConfirmation(ProcessMonitor monitor, IEndDialog dialog, Func<bool> agentAccess,
        Func<IReadOnlyList<ProcInfo>, IReadOnlyList<EndResult>>? end = null, Func<ProcInfo, bool>? isCritical = null)
    {
        _monitor = monitor;
        _dialog = dialog;
        _agentAccess = agentAccess;
        _end = end ?? (targets => ProcessKiller.End(targets));
        _isCritical = isCritical ?? ProcessKiller.IsCritical;
    }

    /// <summary>
    /// A confirmation that expires stays open at least this long, however little of its time limit is
    /// left once the window could show it, so it can be read.
    /// </summary>
    public TimeSpan MinimumOpen { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>What the confirmation would show for this process (and, with <paramref name="tree"/>, what it started).</summary>
    public EndPlan Plan(ProcInfo p, bool tree, string? agentReason)
    {
        bool agent = agentReason != null;
        var targets = new List<ProcInfo> { p };
        if (tree) targets.AddRange(ProcessTree.Descendants(p, _monitor.ByKey.Values));
        int n = targets.Count;

        var lines = new List<EndTargetLine>(Math.Min(n, MaxListed));
        foreach (var t in targets.Take(MaxListed))
        {
            _monitor.EnsureDetails(t);
            bool critical = _isCritical(t);
            // Names and command lines are the programs' own text: shown without characters that hide or reorder it.
            lines.Add(new EndTargetLine(
                $"{AgentText.Clean(t.Name, 260)}   (PID {t.Pid}){(critical ? "   ·   Windows depends on it, it will be skipped" : "")}",
                AgentText.Clean(t.CommandLine, 200) ?? "(command line not available)",
                critical));
        }

        return new EndPlan(
            p, tree, targets, lines,
            More: n > MaxListed ? $"... and {n - MaxListed} more" : null,
            AgentReason: agentReason,
            Title: agent ? (tree ? "An AI agent asks to end a process tree" : "An AI agent asks to end a process")
                : tree ? "End process tree?" : "End process?",
            ListHeading: n == 1
                ? (tree ? "It has no running child processes, so only this process will be ended:" : "This process will be ended:")
                : $"This process and the {(n == 2 ? "process" : $"{n - 1} processes")} it started will be ended:",
            Warning: $"Unsaved work in {(n == 1 ? "it" : "them")} is lost. This cannot be undone.",
            ConfirmText: n == 1 ? "End process" : $"End {n} processes",
            CancelText: agent ? "Do not end" : "Cancel",
            // An agent chooses when its dialog appears, possibly while the user is clicking somewhere in the
            // window. Its end button works only after the dialog has been in view for a moment.
            ArmDelay: agent ? AgentArmDelay : null);
    }

    /// <summary>Closes an open confirmation an AI agent asked for, as withdrawn. Nothing is ended.</summary>
    public void Withdraw() => _withdraw?.Cancel();

    /// <summary>
    /// Shows the confirmation and, only if the user confirms, ends the process (and what it started).
    /// For an agent (<paramref name="agentReason"/> set) the dialog says who is asking and why. With a
    /// <paramref name="timeout"/>, a confirmation nobody answers closes as expired; the time runs from
    /// this call, not from when the dialog appears.
    /// </summary>
    public async Task<EndOutcome> ConfirmAndEndAsync(ProcInfo p, bool tree, string? agentReason, TimeSpan? timeout)
    {
        bool agent = agentReason != null;
        var waiting = Stopwatch.StartNew();
        // A window that never finishes loading must not keep the request (and every later one) waiting for good.
        if (!await _dialog.WaitUntilReadyAsync(timeout)) return EndOutcome.NotShown;
        // Agent access may have been turned off while the window was loading.
        if (agent && !_agentAccess()) return EndOutcome.Withdrawn;
        if (_dialog.IsBusy) return EndOutcome.NotShown;

        var plan = Plan(p, tree, agentReason);

        using var expire = new CancellationTokenSource();
        using var withdraw = new CancellationTokenSource();
        using var close = CancellationTokenSource.CreateLinkedTokenSource(expire.Token, withdraw.Token);
        if (timeout is { } limit)
        {
            var left = limit - waiting.Elapsed;
            expire.CancelAfter(left > MinimumOpen ? left : MinimumOpen);
        }
        // Turning agent access off withdraws the agent's request: the dialog closes and nothing is ended.
        if (agent) _withdraw = withdraw;
        bool pressed;
        try { pressed = await _dialog.AskAsync(plan, close.Token); }
        finally { if (agent) _withdraw = null; }

        // A confirmation that was withdrawn or ran out of time ends nothing, even if the end button was
        // pressed in the same moment.
        if (withdraw.IsCancellationRequested) return EndOutcome.Withdrawn;
        if (expire.IsCancellationRequested) return EndOutcome.Expired;
        if (!pressed) return EndOutcome.Declined;

        var results = await Task.Run(() => _end(plan.Targets));
        string summary = string.Join("\n", results.Select(r => $"{r.Target.Name} (PID {r.Target.Pid}): {r.Message}"));
        Log.Write((agent ? "Ended on an AI agent's request, confirmed by user: " : "Ended by user: ") + summary.Replace("\n", "; "));
        bool allEnded = results.All(r => r.Ended);
        _dialog.ShowEnded(new EndReport(allEnded,
            allEnded ? (results.Count == 1 ? "Process ended" : $"{results.Count} processes ended") : "Not everything could be ended",
            summary));
        return new EndOutcome(EndAnswer.Confirmed, results);
    }
}
