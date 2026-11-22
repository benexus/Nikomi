using Nikomi.Core;
using Microsoft.UI.Dispatching;

namespace Nikomi;

/// <summary>
/// Measures on the UI thread: the process list on a slow timer while the window is closed and on a fast
/// one while it is open, a line of history about every half minute, and the total load every second
/// while the window is open, for the live graph. The monitor, the history and the live load are used
/// only on this thread (see <see cref="ProcessMonitor"/>).
/// </summary>
public sealed class SamplingService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<AppSettings> _settings;
    private readonly string _historyPath;
    private DispatcherQueueTimer? _timer;
    private DispatcherQueueTimer? _liveTimer;
    private bool _windowOpen;
    private long? _sampledAt;   // Environment.TickCount64

    public SamplingService(SystemSource source, DispatcherQueue dispatcher, Func<AppSettings> settings, string historyPath)
    {
        Source = source;
        _dispatcher = dispatcher;
        _settings = settings;
        _historyPath = historyPath;
        Monitor = new ProcessMonitor(source);
        History = new UsageHistory(source);
        LiveLoad = new LiveLoad(source);
    }

    /// <summary>What is measured: this PC, or made-up processes in a demo build.</summary>
    public SystemSource Source { get; }
    public ProcessMonitor Monitor { get; }
    public UsageHistory History { get; }
    /// <summary>The total load about every second while the window is open, for the live graph.</summary>
    public LiveLoad LiveLoad { get; }

    /// <summary>Raised after each sample, with the processes that just became runaway (usually none).</summary>
    public event Action<List<ProcInfo>>? Sampled;
    /// <summary>Raised about every second while the window is open, with a new <see cref="LiveLoad"/> reading.</summary>
    public event Action? LiveSampled;

    /// <summary>Reads back the saved history and takes the first sample. Called once, on the UI thread.</summary>
    public void Start()
    {
        // From here on the monitor and the history are used on this thread only: anything else that
        // touches them is a bug, and fails at once instead of reading half-updated figures.
        Monitor.BindToCurrentThread();
        History.BindToCurrentThread();
        History.Load(_historyPath);
        _timer = _dispatcher.CreateTimer();
        _timer.Tick += (_, _) => Sample();
        ApplyInterval();
        _timer.Start();
        Sample();
        _liveTimer = _dispatcher.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromSeconds(1);
        _liveTimer.Tick += (_, _) => SampleLive();
    }

    /// <summary>The window was shown, hidden, minimized or restored.</summary>
    public void SetWindowOpen(bool open)
    {
        bool opened = open && !_windowOpen;
        _windowOpen = open;
        ApplyInterval();
        if (opened) Sample();   // fresh numbers the moment the window appears
        // The live graph is measured only while someone can see it.
        if (open && _liveTimer is { IsRunning: false })
        {
            SampleLive();
            _liveTimer.Start();
        }
        else if (!open)
        {
            _liveTimer?.Stop();
        }
    }

    /// <summary>Takes up changed intervals from the settings.</summary>
    public void ApplyInterval()
    {
        if (_timer == null) return;
        var s = _settings();
        var interval = TimeSpan.FromSeconds(_windowOpen ? s.WindowSeconds : s.BackgroundSeconds);
        if (_timer.Interval != interval) _timer.Interval = interval;
    }

    public void Sample()
    {
        try
        {
            _sampledAt = Environment.TickCount64;
            var snapshot = Source.TakeSnapshot();
            var newlyRunaway = Monitor.Update(snapshot, _settings());
            if (History.Add(snapshot) is { } line) UsageHistory.Append(_historyPath, line);
            Sampled?.Invoke(newlyRunaway);
        }
        catch (Exception ex)
        {
            Log.Write("Sample failed: " + ex);
        }
    }

    /// <summary>Samples now unless the last sample is younger than two seconds.</summary>
    public void SampleIfStale()
    {
        if (_sampledAt is not { } at || Environment.TickCount64 - at > 2000) Sample();
    }

    private void SampleLive()
    {
        try
        {
            if (LiveLoad.Sample(Environment.TickCount64) && _windowOpen) LiveSampled?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Write("Live sample failed: " + ex);
        }
    }

    public void Stop()
    {
        _timer?.Stop();
        _liveTimer?.Stop();
    }
}
