using Nikomi.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Nikomi;

/// <summary>
/// Owns the tray icon, the window and the settings, and wires them to the two services that do the
/// work: <see cref="SamplingService"/> (the timers, the monitor, the history) and <see cref="AgentHost"/>
/// (AI agents' pipe and their requests). Never ends a process by itself.
/// </summary>
public sealed class AppController
{
    private const int CmdOpen = 1, CmdSettings = 2, CmdStartup = 3, CmdExit = 4, CmdMute = 5;
    private static readonly TimeSpan MuteTime = TimeSpan.FromHours(1);

    private readonly Options _options;
    private readonly SingleInstance _instance;
    private readonly DispatcherQueue _dispatcher;
    private readonly SamplingService _sampling;
    private readonly AgentHost _agents;
    private TrayIcon _tray = null!;
    private MainWindow? _window;
    private string? _lastNotifiedKey;
    private RegisteredWaitHandle? _showWait;
    private bool _exiting;
    private bool _alertShown;

    private long? _mutedUntil;   // Environment.TickCount64

    public AppSettings Settings { get; private set; }
    /// <summary>What is measured: this PC, or made-up processes in a demo build.</summary>
    public SystemSource Source => _sampling.Source;
    public ProcessMonitor Monitor => _sampling.Monitor;
    public UsageHistory History => _sampling.History;
    /// <summary>The total load about every second while the window is open, for the live graph.</summary>
    public LiveLoad LiveLoad => _sampling.LiveLoad;
    /// <summary>Notifications are off for a while (tray menu). Runaways are still marked and the icon still turns red.</summary>
    public bool IsMuted => _mutedUntil is { } until && Environment.TickCount64 < until;
    public StartupState StartupState => _startup.State;
    /// <summary>Reads Start with Windows again, which the user can change in Windows at any time.</summary>
    public Task LoadStartupAsync() => _startup.LoadAsync();
    /// <summary>The Run key unpackaged, a startup task in the Store package.</summary>
    private readonly StartWithWindows _startup;
    public string ExePath { get; } = Environment.ProcessPath!;
    public string DataDir => _options.DataDir;
    public bool IsExiting => _exiting;

    /// <summary>Raised on the UI thread after each sample while the window is open.</summary>
    public event Action? Sampled;
    /// <summary>Raised on the UI thread about every second while the window is open, with a new <see cref="LiveLoad"/> reading.</summary>
    public event Action? LiveSampled
    {
        add => _sampling.LiveSampled += value;
        remove => _sampling.LiveSampled -= value;
    }

    public AppController(Options options, SystemSource source, SingleInstance instance, DispatcherQueue dispatcher)
    {
        _options = options;
        _startup = StartWithWindows.For(ExePath, options.DataDir);
        _instance = instance;
        _dispatcher = dispatcher;
        Settings = AppSettings.Load(options.SettingsPath, out bool unreadable);
        // The next save would put defaults over the user's real settings: keep what is there first.
        if (unreadable) AppSettings.KeepUnreadable(options.SettingsPath);
        _sampling = new SamplingService(source, dispatcher, () => Settings, options.HistoryPath);
        _sampling.Sampled += OnSampled;
        _agents = new AgentHost(options.DataDir, dispatcher, () => Settings, _sampling, () => _exiting,
            ConfirmAgentEndAsync, () => _window?.Confirmation.Withdraw());
    }

    public void Start()
    {
        Log.Write($"Started (PID {Environment.ProcessId}), data folder {_options.DataDir}, {Settings}");
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        _tray = new TrayIcon(Path.Combine(assets, "Nikomi.ico"), Path.Combine(assets, "NikomiAlert.ico"));
        _tray.LeftClicked += () => ShowWindow();
        _tray.BalloonClicked += () => ShowWindow(_lastNotifiedKey);
        _tray.MenuProvider = () =>
        {
            var startup = StartupState;
            return new TrayIcon.MenuItem[]
            {
                new(CmdOpen, "Open Nikomi", IsDefault: true),
                new(CmdSettings, "Settings..."),
                new(CmdMute, "Mute notifications for 1 hour", Checked: IsMuted),
                new(CmdStartup, startup switch
                    {
                        StartupState.OffInWindows => "Start with Windows (turned off in Windows)...",
                        StartupState.OnByPolicy or StartupState.OffByPolicy => "Start with Windows (set by your organization)",
                        _ => "Start with Windows",
                    },
                    Checked: startup.IsOn()),
                new(0, "", Separator: true),
                new(CmdExit, "Exit"),
            };
        };
        _tray.MenuCommand += OnMenuCommand;
        _ = _startup.LoadAsync();
        _sampling.Start();

        // A second start of Nikomi signals this event: open the window.
        _showWait = ThreadPool.RegisterWaitForSingleObject(_instance.ShowRequested,
            (_, _) => _dispatcher.TryEnqueue(() => ShowWindow()), null, Timeout.Infinite, executeOnlyOnce: false);

        _agents.ApplySettings();
        if (!_options.Background) ShowWindow();
    }

    private void OnMenuCommand(int id)
    {
        switch (id)
        {
            case CmdOpen: ShowWindow(); break;
            case CmdSettings: ShowWindow(); _window?.OpenSettings(); break;
            case CmdStartup: ToggleStartWithWindows(); break;
            case CmdMute:
                _mutedUntil = IsMuted ? null : Environment.TickCount64 + (long)MuteTime.TotalMilliseconds;
                Log.Write(IsMuted ? "Notifications muted for 1 hour" : "Notifications on again");
                break;
            case CmdExit: Exit(); break;
        }
    }

    public void OpenStartupSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:startupapps") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Write("Could not open the Startup apps settings: " + ex.Message);
        }
    }

    /// <summary>
    /// Called when the close button hid the window. The first time, says that the app is still running:
    /// a window that closes while its program stays must not be a surprise.
    /// </summary>
    public void WindowHidden()
    {
        if (Settings.CloseHintShown || _exiting) return;
        _tray.ShowNotification("Nikomi is still running",
            "It keeps watching from the notification area. To quit, right-click its icon there and choose Exit.", warning: false);
        var changed = Settings.Copy();
        changed.CloseHintShown = true;
        SaveSettings(changed);
    }

    /// <summary>Adds a process name to, or takes it off, the list of names that are never flagged runaway.</summary>
    public void SetNeverFlag(string processName, bool never)
    {
        var changed = Settings.Copy();
        changed.IgnoreNames.RemoveAll(entry => AppSettings.NameMatches(entry, processName));
        if (never) changed.IgnoreNames.Add(processName);
        SaveSettings(changed);
        Sample();
    }

    public bool IsWindowOpen => _window is { IsOpen: true };

    public void ShowWindow(string? selectKey = null)
    {
        if (_exiting) return;
        _window ??= new MainWindow(this);
        _window.ShowAndActivate(selectKey);
    }

    /// <summary>Called by the window when it is shown, hidden, minimized or restored.</summary>
    public void WindowStateChanged() => _sampling.SetWindowOpen(IsWindowOpen);

    /// <summary>Brings the window forward and asks the user there; the confirmation closes unanswered after a while.</summary>
    private Task<EndOutcome> ConfirmAgentEndAsync(ProcInfo p, bool tree, string reason)
    {
        ShowWindow(p.Key);
        _lastNotifiedKey = p.Key;
        // The window may not be allowed to come to the front by itself; the notification is.
        _tray.ShowNotification("An AI agent asks to end a process",
            $"{p.Name} (PID {p.Pid}). Answer in the Nikomi window. Nothing is ended unless you confirm.", warning: false);
        return _window!.Confirmation.ConfirmAndEndAsync(p, tree, reason, AgentEndRequests.DialogTime);
    }

    // ---------------------------------------------------------------------------------------------

    public void Sample() => _sampling.Sample();

    private void OnSampled(List<ProcInfo> newlyRunaway)
    {
        if (newlyRunaway.Count > 0) Notify(newlyRunaway);
        UpdateTray();
        if (IsWindowOpen) Sampled?.Invoke();
    }

    private void Notify(List<ProcInfo> procs)
    {
        string names = string.Join(", ", procs.Select(p => $"{p.Name} (PID {p.Pid})"));
        string text = procs.Count == 1
            ? $"{procs[0].Name} (PID {procs[0].Pid}) has used {Settings.RunawayCores:0.00} cores or more for {procs[0].HotMinutes:0} minutes. Click to see what started it."
            : $"{procs.Count} processes have used {Settings.RunawayCores:0.00} cores or more for a long time: {names}.";
        _lastNotifiedKey = procs[0].Key;
#if DEMO
        // The made-up runaway is not worth a notification on the PC the demo runs on.
        Log.Write("RUNAWAY, no notification (demo build): " + names);
        return;
#else
        if (IsMuted)
        {
            Log.Write("RUNAWAY, no notification (muted): " + names);
            return;
        }
        _tray.ShowNotification("Runaway process", text);
        Log.Write("RUNAWAY, notification shown: " + names);
#endif
    }

    private void UpdateTray()
    {
        bool alert = Monitor.RunawayCount > 0;
        if (alert != _alertShown)
        {
            _alertShown = alert;
            _tray.SetAlert(alert);
            Log.Write("Tray icon: " + (alert ? "runaway" : "normal"));
        }
        string tip = Monitor.Load is { } l ? $"Nikomi: {l * 100:0}% CPU" : "Nikomi";
        if (alert)
            tip += " - runaway: " + string.Join(", ", Monitor.ByKey.Values.Where(p => p.Runaway).Select(p => $"{p.Name} {p.Pid}"));
        _tray.SetTooltip(tip);
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings.Clamped();
        try
        {
            Settings.Save(_options.SettingsPath);
            Log.Write("Settings saved: " + Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // They still apply until Nikomi exits; a file Nikomi cannot write must not take the app down.
            Log.Write($"Could not save settings ({ex.Message}); using them until Nikomi exits: {Settings}");
        }
        _sampling.ApplyInterval();
        _agents.ApplySettings();
    }

    /// <summary>Done in the background: the Store package's startup task answers asynchronously.</summary>
    /// <summary>
    /// The tray menu's Start with Windows. Windows' own off switch is the user's: it is not overridden, only
    /// shown where to change it. A policy's switch is left as it is.
    /// </summary>
    private async void ToggleStartWithWindows()
    {
        await LoadStartupAsync();   // the menu showed the last state read; act on the current one
        switch (StartupState)
        {
            case StartupState.OffInWindows: OpenStartupSettings(); break;
            case StartupState.On: SetStartWithWindows(false); break;
            case StartupState.Off: SetStartWithWindows(true); break;
            default: Log.Write($"Start with Windows is {StartupState}: left as it is"); break;
        }
    }

    public async void SetStartWithWindows(bool enable)
    {
        try
        {
            await _startup.SetAsync(enable);
            Log.Write($"Start with Windows: {(enable ? "on" : "off")} ({_startup.Where}), now {_startup.State}");
        }
        catch (Exception ex)
        {
            Log.Write("Could not change start with Windows: " + ex.Message);
        }
    }

    public void Exit()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Write("Stopped");
        _sampling.Stop();
        _showWait?.Unregister(null);
        _agents.Dispose();
        _tray.Dispose();
        _window?.Close();
        Source.Dispose();
        Application.Current.Exit();
    }
}
