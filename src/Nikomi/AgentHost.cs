using System.Text.Json.Nodes;
using Nikomi.Core;
using Microsoft.UI.Dispatching;

namespace Nikomi;

/// <summary>
/// AI agents' way in. They reach the app through the console program and a pipe that exists only while
/// the user has agent access turned on. Reading is answered directly; ending is only ever a request,
/// which the user confirms or declines in the window like any other end (<see cref="AgentEndRequests"/>).
/// Every answer is worked out on the UI thread, where the monitor lives.
/// </summary>
public sealed class AgentHost : IDisposable
{
    private readonly string _pipeName;
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<AppSettings> _settings;
    private readonly SamplingService _sampling;
    private readonly Func<bool> _closing;
    private readonly Action _withdraw;
    private readonly AgentEndRequests _ends;
    private readonly AgentRateLimit _reads = new();
    private AgentPipeServer? _server;
    private AgentQueries? _queries;
    private long? _loggedAt;   // Environment.TickCount64
    private int _questions;

    /// <param name="closing">True once Nikomi is exiting.</param>
    /// <param name="confirm">Asks the user to confirm an agent's end request (<see cref="AgentEndRequests"/>).</param>
    /// <param name="withdraw">Closes an open confirmation an agent asked for, as withdrawn.</param>
    public AgentHost(string dataDir, DispatcherQueue dispatcher, Func<AppSettings> settings, SamplingService sampling, Func<bool> closing,
        Func<ProcInfo, bool, string, Task<EndOutcome>> confirm, Action withdraw)
    {
        _pipeName = AgentPipe.Name(dataDir);
        _dispatcher = dispatcher;
        _settings = settings;
        _sampling = sampling;
        _closing = closing;
        _withdraw = withdraw;
        _ends = new AgentEndRequests(sampling.Monitor, sampling.SampleIfStale, confirm);
    }

    /// <summary>Opens or closes the pipe as the settings say. Called at start and after every save of the settings.</summary>
    public void ApplySettings()
    {
        bool on = _settings().AgentAccess;
        if (on == (_server != null)) return;
        if (on)
        {
            _server = new AgentPipeServer(_pipeName, OnRequest);
            _server.Start();
        }
        else
        {
            _server!.Dispose();
            _server = null;
            // A confirmation an agent asked for must not stay open, and work, once the user has said no to agents.
            _withdraw();
        }
        Log.Write("Agent access: " + (on ? "on" : "off"));
        if (on && IsElevated())
            Log.Write("Nikomi runs as administrator: its agent pipe belongs to the Administrators group, so only programs " +
                      "that also run as administrator can reach it. Agents' console programs that are not elevated measure by themselves.");
    }

    private static bool IsElevated()
    {
        using var me = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(me).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Called on a pipe thread; the answer is worked out on the UI thread, where the monitor lives.</summary>
    private Task<JsonObject> OnRequest(AgentRequest request)
    {
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool queued = _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                answer.SetResult(await AnswerAsync(request));
            }
            catch (Exception ex)
            {
                Log.Write("Agent request failed: " + ex);
                answer.SetResult(AgentQueries.Error("Nikomi could not answer: " + ex.Message));
            }
        });
        if (!queued) answer.SetResult(AgentQueries.Error("Nikomi is closing."));
        return answer.Task;
    }

    private async Task<JsonObject> AnswerAsync(AgentRequest request)
    {
        if (_closing()) return AgentQueries.Error("Nikomi is closing.");
        var settings = _settings();
        if (!settings.AgentAccess) return AgentQueries.Error(AgentQueries.OffMessage);

        // The log says that agents are reading, at most once a minute; every end request is logged in full.
        _questions++;
        long now = Environment.TickCount64;
        if (_loggedAt is not { } logged || now - logged >= 60_000)
        {
            Log.Write($"Agent access: {_questions} {(_questions == 1 ? "question" : "questions")} answered (latest: {request.Command})");
            _loggedAt = now;
            _questions = 0;
        }

        switch (request.Command)
        {
            case "end": return await _ends.RequestAsync(request);
            case "end_result": return await _ends.ResultAsync();
            default:
                // Reads are answered on this (the window's) thread: an agent in a loop must not make it stutter.
                if (!_reads.TryTake(Environment.TickCount64)) return AgentQueries.Error(AgentRateLimit.Refusal);
                _sampling.SampleIfStale();
                _queries ??= new AgentQueries(_sampling.Monitor, () => _sampling.History.Entries, fromApp: true);
                return _queries.Answer(request, settings);
        }
    }

    /// <summary>Stops listening: the pipe's name is gone, and agents' console programs measure by themselves.</summary>
    public void Dispose()
    {
        _server?.Dispose();
        _server = null;
    }
}
