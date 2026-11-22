namespace Nikomi.Core;

/// <summary>
/// Turns successive samples into per-process "cores in use" over the interval, tracks how long each process
/// has stayed at or above the runaway threshold, and finds orphans. Never ends anything.
/// <para>
/// Not thread-safe, and neither are the <see cref="ProcInfo"/> objects it hands out: <see cref="Update"/>
/// and <see cref="EnsureDetails"/> change them in place. Use a monitor, and its processes' figures, on one
/// thread. The app binds its monitor to the UI thread (<see cref="BindToCurrentThread"/>), so any use from
/// another thread fails at once instead of reading half-updated figures; pipe requests are handed to that
/// thread first. The console program uses its monitor from one worker at a time, under a lock.
/// </para>
/// </summary>
public sealed class ProcessMonitor
{
    // Normal boot-time processes whose parent always exits; not interesting as orphans.
    private static readonly HashSet<string> OrphanIgnore = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Idle Process", "System", "Registry", "Secure System", "Memory Compression",
        "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "explorer.exe",
    };

    /// <summary>
    /// The runaway rule is only applied to a stretch at least this long. A shorter measurement (the window
    /// opening, a click on Refresh) is too noisy to judge, so it neither starts nor resets a hot streak;
    /// its CPU time is counted in the next stretch that is long enough.
    /// </summary>
    public static readonly TimeSpan MinJudgedInterval = TimeSpan.FromSeconds(1);

    // All times below are Snapshot.MonotonicTicks, so a change of the system clock cannot bend them.
    private readonly Dictionary<string, long> _hotSince = new();
    private long? _lastTicks;
    private long? _judgedTicks;                         // the last sample the runaway rule was applied to
    private Dictionary<string, long> _judgedCpu = new();   // each process's CPU ticks at that sample
    private readonly HashSet<string> _notified = new();
    private readonly Dictionary<string, (string? Cmd, string? Path)> _details = new();

    /// <summary>Where details, services and the number of cores are read from.</summary>
    public SystemSource Source { get; }

    public ProcessMonitor(SystemSource source) { Source = source; }

    private readonly OwningThread _thread = new(nameof(ProcessMonitor));

    /// <summary>From now on the monitor may be used on this thread only; any other thread gets an <see cref="InvalidOperationException"/>.</summary>
    public void BindToCurrentThread() => _thread.Bind();

    private IReadOnlyDictionary<string, ProcInfo> _byKey = new Dictionary<string, ProcInfo>();
    private IReadOnlyDictionary<int, ProcInfo> _byPid = new Dictionary<int, ProcInfo>();

    public IReadOnlyDictionary<string, ProcInfo> ByKey { get { _thread.Check(); return _byKey; } }
    public IReadOnlyDictionary<int, ProcInfo> ByPid { get { _thread.Check(); return _byPid; } }
    public DateTime? LastTimeUtc { get; private set; }
    /// <summary>Total CPU load over the last interval, 0..1. Null until two samples exist.</summary>
    public double? Load { get; private set; }
    public double? IntervalSeconds { get; private set; }
    public int OrphanCount { get; private set; }
    public int RunawayCount { get; private set; }

    public bool IsHot(string key)
    {
        _thread.Check();
        return _hotSince.ContainsKey(key);
    }

    /// <summary>Folds in a sample. Returns processes that just became runaway and were not reported before.</summary>
    public List<ProcInfo> Update(Snapshot snap, AppSettings settings)
    {
        _thread.Check();
        long now = snap.MonotonicTicks;
        var prev = _byKey;
        long dt = _lastTicks is { } last ? now - last : 0;
        long judgedDt = _judgedTicks is { } judged ? now - judged : 0;
        bool judge = judgedDt >= MinJudgedInterval.Ticks;
        bool rebase = judge || _judgedTicks == null;
        var judgedCpu = rebase ? new Dictionary<string, long>(snap.Procs.Count) : _judgedCpu;
        double threshold = settings.RunawayCores;

        var byKey = new Dictionary<string, ProcInfo>(snap.Procs.Count);
        var byPid = new Dictionary<int, ProcInfo>(snap.Procs.Count);
        double? idle = null;
        double sum = 0;

        foreach (var p in snap.Procs)
        {
            // What the list shows: use since the previous sample, however short that was.
            prev.TryGetValue(p.Key, out var old);
            double? cores = Usage(p, old?.CpuTicks, dt, snap.TimeUtc, out _);
            p.Cores = cores;
            if (old != null && dt > 0) p.IoBytesPerSec = Math.Max(0, p.IoBytes - old.IoBytes) / (dt / 1e7);

            if (p.Pid == 0) idle = cores;
            else if (cores is { } c) sum += c;

            // What the runaway rule goes by: use since the last judged sample.
            if (judge && p.Pid != 0 &&
                Usage(p, _judgedCpu.TryGetValue(p.Key, out long cpu0) ? cpu0 : null, judgedDt, snap.TimeUtc, out long span) is { } held)
            {
                if (held >= threshold) _hotSince.TryAdd(p.Key, now - span);
                else _hotSince.Remove(p.Key);
            }
            if (rebase) judgedCpu[p.Key] = p.CpuTicks;

            if (_details.TryGetValue(p.Key, out var d)) Apply(p, d);

            byKey[p.Key] = p;
            byPid[p.Pid] = p;
        }
        if (rebase) { _judgedTicks = now; _judgedCpu = judgedCpu; }

        Prune(_hotSince, byKey);
        _notified.RemoveWhere(k => !byKey.ContainsKey(k));
        Prune(_details, byKey);

        var newlyRunaway = new List<ProcInfo>();
        int runaway = 0;
        foreach (var p in byKey.Values)
        {
            if (!_hotSince.TryGetValue(p.Key, out var hotSince)) continue;
            p.HotMinutes = (double)(now - hotSince) / TimeSpan.TicksPerMinute;
            bool longEnough = p.HotMinutes >= settings.RunawayMinutes;
            p.Ignored = longEnough && settings.IsIgnored(p.Name);
            p.Runaway = longEnough && !p.Ignored;
            if (!p.Runaway) continue;
            runaway++;
            if (_notified.Add(p.Key)) newlyRunaway.Add(p);
        }

        int orphans = 0;
        foreach (var p in byKey.Values)
        {
            if (p.Pid <= 4 || p.ParentPid <= 0 || OrphanIgnore.Contains(p.Name)) continue;
            if (!byPid.TryGetValue(p.ParentPid, out var parent) || ProcessTree.IsNewer(parent, p))
            {
                p.Orphan = true;
                orphans++;
            }
        }

        int n = Source.LogicalCores;
        double? load = idle is { } i ? 1.0 - i / n : dt > 0 ? sum / n : null;
        if (load is { } l) load = Math.Clamp(l, 0, 1);

        _byKey = byKey;
        _byPid = byPid;
        LastTimeUtc = snap.TimeUtc;
        _lastTicks = now;
        Load = load;
        IntervalSeconds = dt > 0 ? dt / 1e7 : null;
        OrphanCount = orphans;
        RunawayCount = runaway;
        return newlyRunaway;
    }

    /// <summary>
    /// Cores a process used over the last <paramref name="dt"/> ticks, given its CPU time at the start of
    /// them. A process that started inside that stretch is averaged over its own lifetime instead;
    /// <paramref name="span"/> is the time the figure covers. Null when it cannot be told.
    /// </summary>
    private static double? Usage(ProcInfo p, long? cpuAtStart, long dt, DateTime nowUtc, out long span)
    {
        span = dt;
        if (dt <= 0) return null;
        if (cpuAtStart is { } cpu0) return Math.Max(0, (double)(p.CpuTicks - cpu0) / dt);
        if (p.CreateFileTime <= 0) return null;
        // A start time is wall-clock time, so this one figure still depends on the system clock. If the
        // clock says the process is older than the stretch, it is left unmeasured rather than guessed.
        long life = (nowUtc - DateTime.FromFileTimeUtc(p.CreateFileTime)).Ticks;
        if (life <= 0 || life > dt) return null;
        span = life;
        return (double)p.CpuTicks / life;
    }

    /// <summary>Reads the command line and path once per process instance; later samples reuse them.</summary>
    public void EnsureDetails(ProcInfo p)
    {
        _thread.Check();
        if (p.DetailsRead) return;
        if (!_details.TryGetValue(p.Key, out var d))
        {
            d = Source.ReadDetails(p.Pid);
            _details[p.Key] = d;
        }
        Apply(p, d);
    }

    private void Apply(ProcInfo p, (string? Cmd, string? Path) d)
    {
        p.CommandLine = d.Cmd;
        p.ExePath = d.Path;
        (p.Description, p.Company) = Source.Describe(d.Path);
        p.DetailsRead = true;
    }

    private IReadOnlyDictionary<int, string> _services = new Dictionary<int, string>();
    private long? _servicesReadAt;

    /// <summary>The Windows services running inside a process ("DNS Client, Windows Update"), or null.</summary>
    public string? ServicesOf(int pid)
    {
        _thread.Check();
        return _services.GetValueOrDefault(pid);
    }

    /// <summary>
    /// Reads which services run in which process, at most twice a minute. Called only while the window
    /// shows the list; services come and go rarely.
    /// </summary>
    public void RefreshServices()
    {
        _thread.Check();
        long now = Environment.TickCount64;
        if (_servicesReadAt is { } at && now - at < 30_000) return;
        _servicesReadAt = now;
        try
        {
            _services = Source.ReadServices();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read the service list: " + ex.Message);
        }
    }

    private static void Prune<T>(Dictionary<string, T> dict, Dictionary<string, ProcInfo> live)
    {
        List<string>? gone = null;
        foreach (var k in dict.Keys) if (!live.ContainsKey(k)) (gone ??= new()).Add(k);
        if (gone != null) foreach (var k in gone) dict.Remove(k);
    }
}
