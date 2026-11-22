using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nikomi.Core;

/// <summary>
/// What the console program and its MCP server put their questions to. It asks the running app; when no
/// app is listening it measures by itself, which is enough for reading but not for ending anything.
/// The user's switch in Settings is read on every question, so turning it off works at once.
/// </summary>
public sealed class AgentClient : IDisposable
{
    public static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public static readonly JsonSerializerOptions Pretty = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };

    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(20);
    // The app waits up to 45 s for the user before it answers "pending".
    private static readonly TimeSpan EndTimeout = TimeSpan.FromSeconds(75);

    private readonly string _dataDir;
    private readonly SemaphoreSlim _local = new(1, 1);
    private readonly SystemSource _source = new();
    private readonly ProcessMonitor _mon;
    private long? _sampledAt;

    public AgentClient(string dataDir)
    {
        _dataDir = dataDir;
        _mon = new ProcessMonitor(_source);
    }

    /// <param name="cancel">Gives up on the answer (the MCP client cancelled its request): throws <see cref="OperationCanceledException"/>.</param>
    public async Task<JsonObject> AskAsync(AgentRequest request, CancellationToken cancel = default)
    {
        var settings = AppSettings.Load(DataFolder.SettingsPath(_dataDir));
        if (!settings.AgentAccess) return AgentQueries.Error(AgentQueries.OffMessage);

        bool ending = request.Command is "end" or "end_result";
        if (await AgentPipe.TryAskAsync(AgentPipe.Name(_dataDir), request, ending ? EndTimeout : AskTimeout, cancel) is { } answer)
            return answer;
        if (ending)
            return AgentQueries.Error("Nikomi is not running. Ending a process needs its confirmation dialog, so the user has to start Nikomi first.");

        // The monitor is not thread-safe: it is used by one worker at a time, under this lock.
        await _local.WaitAsync(cancel);
        try
        {
            return await Task.Run(() => Local(request, settings));
        }
        finally
        {
            _local.Release();
        }
    }

    private JsonObject Local(AgentRequest request, AppSettings settings)
    {
        long now = Environment.TickCount64;
        if (_sampledAt is not { } at || now - at > 10_000)
        {
            // Cores are CPU time over a stretch: two samples a second apart give a fresh figure.
            _mon.Update(_source.TakeSnapshot(), settings);
            Thread.Sleep(1000);
            _mon.Update(_source.TakeSnapshot(), settings);
        }
        else if (now - at >= 1000) _mon.Update(_source.TakeSnapshot(), settings);
        _sampledAt = Environment.TickCount64;

        var history = new UsageHistory(_source);
        if (request.Command == "history") history.Load(DataFolder.HistoryPath(_dataDir));
        return new AgentQueries(_mon, () => history.Entries, fromApp: false).Answer(request, settings);
    }

    public void Dispose() => _source.Dispose();
}
