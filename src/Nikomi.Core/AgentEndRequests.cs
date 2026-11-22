using System.Text.Json.Nodes;

namespace Nikomi.Core;

/// <summary>
/// An AI agent's requests to end a process, as the running app takes them. One request at a time; it
/// needs a process id (PID and start time, never a bare PID), a reason, and a process that is neither
/// Nikomi nor one Windows depends on. After a request the user did not confirm, the next waits out
/// <see cref="AgentEndCooldown"/>. The request itself is only ever a question to the user: the
/// confirmation is shown by whoever hands in <c>confirm</c>, and nothing is ended without it.
/// Used on the window's thread, like the <see cref="ProcessMonitor"/> it reads.
/// </summary>
public sealed class AgentEndRequests
{
    /// <summary>How long a request waits for the user before the agent is told "pending".</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(45);
    /// <summary>How long the confirmation stays open before it counts as not answered.</summary>
    public static readonly TimeSpan DialogTime = TimeSpan.FromMinutes(5);

    private readonly ProcessMonitor _monitor;
    private readonly Action _refresh;
    private readonly Func<ProcInfo, bool, string, Task<EndOutcome>> _confirm;
    private readonly Func<long> _clock;
    private readonly Func<ProcInfo, bool> _isCritical;
    private readonly int _selfPid;
    private readonly AgentEndCooldown _cooldown = new();
    private Task<JsonObject>? _pending;

    /// <param name="refresh">Brings the monitor up to date if its last sample is old.</param>
    /// <param name="confirm">
    /// Shows the user the confirmation for a process, whether to end what it started too, and the agent's
    /// cleaned reason, and ends it only if the user confirms; the confirmation closes unanswered after
    /// <see cref="DialogTime"/>.
    /// </param>
    /// <param name="clock">Milliseconds that only run forward; <see cref="Environment.TickCount64"/> unless a check stands in for it.</param>
    /// <param name="answerWait">How long to wait for the user before answering "pending"; <see cref="AnswerWait"/> unless a check stands in for it.</param>
    public AgentEndRequests(ProcessMonitor monitor, Action refresh, Func<ProcInfo, bool, string, Task<EndOutcome>> confirm,
        Func<long>? clock = null, Func<ProcInfo, bool>? isCritical = null, int? selfPid = null, TimeSpan? answerWait = null)
    {
        _monitor = monitor;
        _refresh = refresh;
        _confirm = confirm;
        _clock = clock ?? (() => Environment.TickCount64);
        _isCritical = isCritical ?? ProcessKiller.IsCritical;
        _selfPid = selfPid ?? Environment.ProcessId;
        Wait = answerWait ?? AnswerWait;
    }

    private TimeSpan Wait { get; }

    /// <summary>The agent's "end" command: checks the request and, if it is sound, asks the user.</summary>
    public async Task<JsonObject> RequestAsync(AgentRequest request)
    {
        if (_pending is { IsCompleted: false })
            return AgentQueries.Error("An earlier request is still waiting for the user's answer. Ask for end_result to get its outcome.");
        if (_cooldown.Refusal(_clock()) is { } wait) return AgentQueries.Error(wait);
        if (request.Id is not { Length: > 0 } id)
            return AgentQueries.Error("Ending needs the id of the process from a listing. A PID alone is not accepted, because Windows reuses PIDs.");

        _refresh();
        if (!_monitor.ByKey.TryGetValue(id, out var p))
            return AgentQueries.Error("No running process has this id. A process that restarted has a new id: list the processes again.");
        if (p.Pid == _selfPid) return AgentQueries.Error("That is Nikomi itself.");
        if (_isCritical(p))
            return AgentQueries.Error("Windows depends on this process: ending it would crash the system. Nikomi never ends it.");
        // The agent wrote this: hidden and direction-changing characters go, and it is cut at 400 characters.
        string reason = AgentText.Reason(request.Reason);
        if (reason.Length == 0) return AgentQueries.Error("Give a reason. The user sees it in the confirmation.");

        Log.Write($"Agent asks to end {p.Name} (PID {p.Pid}){(request.Tree ? " and what it started" : "")}. Its reason: {reason}");
        _pending = ConfirmAsync(p, request.Tree, reason);
        return await ResultAsync();
    }

    /// <summary>The agent's "end_result" command: the outcome of the latest request, or "pending".</summary>
    public async Task<JsonObject> ResultAsync()
    {
        if (_pending is not { } pending) return AgentQueries.Error("There is no end request to report on.");
        if (await Task.WhenAny(pending, Task.Delay(Wait)) != pending)
            return new JsonObject
            {
                ["pending"] = true,
                ["message"] = "The user has not answered yet. The confirmation stays open in Nikomi for up to 5 minutes. Ask for end_result to get the outcome.",
            };
        // A fresh copy each time: the outcome can be asked for more than once.
        return (JsonObject)(await pending).DeepClone();
    }

    private async Task<JsonObject> ConfirmAsync(ProcInfo p, bool tree, string reason)
    {
        string who = $"{p.Name} (PID {p.Pid})";
        try
        {
            var outcome = await _confirm(p, tree, reason);
            _cooldown.Record(outcome.Answer, _clock());
            if (outcome.Answer != EndAnswer.Confirmed)
            {
                string why = outcome.Answer switch
                {
                    EndAnswer.Declined => "The user declined. Do not ask again unless the user tells you to.",
                    EndAnswer.Withdrawn => "The user turned agent access off. Do not ask again unless the user tells you to.",
                    EndAnswer.Expired => "The user did not answer within 5 minutes.",
                    _ => "Nikomi could not show the confirmation: another of its dialogs is open, or its window did not open.",
                };
                Log.Write($"Agent's request to end {who}: not confirmed ({outcome.Answer})");
                return new JsonObject { ["confirmed"] = false, ["message"] = why + " Nothing was ended." };
            }
            var results = new JsonArray();
            foreach (var r in outcome.Results)
                results.Add(new JsonObject { ["pid"] = r.Target.Pid, ["name"] = r.Target.Name, ["ended"] = r.Ended, ["message"] = r.Message });
            return new JsonObject { ["confirmed"] = true, ["all_ended"] = outcome.Results.All(r => r.Ended), ["results"] = results };
        }
        catch (Exception ex)
        {
            Log.Write($"Agent's request to end {who} failed: {ex}");
            _cooldown.Record(EndAnswer.NotShown, _clock());
            return AgentQueries.Error("Nikomi could not show the confirmation: " + ex.Message);
        }
    }
}
