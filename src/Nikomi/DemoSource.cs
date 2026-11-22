#if DEMO
using System.Diagnostics;
using Nikomi.Core;

namespace Nikomi;

/// <summary>
/// Made-up processes for screenshots: an invented PC with eight cores, a build watcher stuck on one of
/// them and a script that has just become busy. Nothing here is read from the PC it runs on. Compiled
/// only with -p:Demo=true; a normal build contains none of it.
/// </summary>
public sealed class DemoSource : SystemSource
{
    /// <summary>A demo keeps its settings, log and history apart from a real Nikomi's.</summary>
    public static string DataDir => Path.Combine(Path.GetTempPath(), "Nikomi-demo");

    private const int Cores = 8;
    private const string Ms = "Microsoft Corporation";
    private static readonly TimeSpan UptimeAtStart = new(3, 4, 12, 0);
    // The first two samples are placed in the past, so the window opens on a runaway that is 47 minutes
    // old and a process that became hot two minutes ago, instead of on a list with no history.
    private static readonly TimeSpan[] SampleOffsets = { TimeSpan.FromMinutes(-47.4), TimeSpan.FromMinutes(-2.2) };

    private sealed class Fake
    {
        public required int Pid, Parent, Session, Priority, Threads, Handles;
        public required string Name;
        public required long Created;
        public required double Rate, RateNow, IoRate;
        public required long Memory, Committed;
        public string? Path, Cmd, Description, Company, Services;
        public bool Steady;
        public double CpuTicks, IoBytes;
    }

    private readonly List<Fake> _procs = new();
    private readonly Dictionary<int, Fake> _byPid = new();
    private readonly Dictionary<string, (string?, string?)> _described = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new(7);
    private readonly DateTime _startUtc = DateTime.UtcNow;
    private readonly long _startStamp = Stopwatch.GetTimestamp();
    private int _samples;
    private long _lastTicks;
    private double _idleTicks;
    private double _liveIdle, _liveTotal, _liveAt;

    public DemoSource()
    {
        const string sys = @"C:\WINDOWS\System32\";
        const string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
        const string code = @"C:\Program Files\Microsoft VS Code\Code.exe";
        const string node = @"C:\Program Files\nodejs\node.exe";
        double boot = UptimeAtStart.TotalHours;

        // Windows itself. Like the real thing, a process of another account shows no path or command line.
        Add(4, 0, "System", boot, 0.03, 0, session: 0, threads: 214);
        Add(148, 4, "Registry", boot, 0, 62, session: 0);
        Add(536, 4, "smss.exe", boot, 0, 1, session: 0);
        Add(780, 712, "csrss.exe", boot, 0, 2, session: 0);
        Add(872, 712, "wininit.exe", boot, 0, 1, session: 0);
        Add(880, 864, "csrss.exe", boot, 0.02, 3);
        Add(968, 864, "winlogon.exe", boot, 0, 3);
        Add(1012, 872, "services.exe", boot, 0.01, 7, session: 0);
        Add(1036, 872, "lsass.exe", boot, 0.01, 11, session: 0, services: "CNG Key Isolation, Credential Manager, Security Accounts Manager");
        Add(1160, 872, "fontdrvhost.exe", boot, 0, 1, session: 0);
        Add(1168, 968, "fontdrvhost.exe", boot, 0, 4);
        Add(1496, 968, "dwm.exe", boot, 0.11, 88, io: 0);
        Add(2860, 4, "Memory Compression", boot, 0.01, 412, session: 0);
        Add(3408, 1012, "MsMpEng.exe", boot, 0.06, 236, session: 0, services: "Microsoft Defender Antivirus Service", io: 310_000);
        Add(4520, 1012, "spoolsv.exe", boot, 0, 9, session: 0, services: "Print Spooler");
        Add(4688, 1012, "SearchIndexer.exe", boot, 0.03, 54, session: 0, services: "Windows Search", io: 96_000);
        Add(4904, 2364, "audiodg.exe", 6.1, 0.02, 14, session: 0);

        string[] services =
        {
            "DCOM Server Process Launcher", "Remote Procedure Call (RPC), RPC Endpoint Mapper", "Local Session Manager", "Power",
            "Plug and Play", "System Events Broker", "Windows Event Log", "Task Scheduler", "User Profile Service", "Themes",
            "DHCP Client", "DNS Client", "Network Store Interface Service", "Windows Audio Endpoint Builder", "Windows Audio",
            "Base Filtering Engine, Windows Defender Firewall", "Cryptographic Services", "Windows Management Instrumentation",
            "Shell Hardware Detection", "Time Broker", "State Repository Service", "Windows Font Cache Service",
            "Diagnostic Policy Service", "IP Helper", "Workstation", "Server", "Network List Service", "Network Location Awareness",
            "Windows Push Notifications System Service", "Windows Update", "Storage Service", "Web Account Manager",
            "Windows License Manager Service", "Display Enhancement Service", "Application Information",
            "Connected Devices Platform Service", "Bluetooth Support Service", "Windows Time", "Background Intelligent Transfer Service",
            "WinHTTP Web Proxy Auto-Discovery Service", "Windows Security Service", "Delivery Optimization",
        };
        for (int i = 0; i < services.Length; i++)
            Add(1300 + 76 * i, 1012, "svchost.exe", boot - i * 0.0004, (i is 17 or 29) ? 0.02 : 0.0, 4 + i * 7 % 23, session: 0, services: services[i]);

        // The signed-in user's session.
        Add(5236, 5104, "explorer.exe", boot - 0.02, 0.04, 142, sys.Replace(@"System32\", "") + "explorer.exe", @"C:\WINDOWS\Explorer.EXE", "Windows Explorer", io: 18_000);
        Add(5640, 1528, "sihost.exe", boot - 0.02, 0, 12, sys + "sihost.exe", "sihost.exe", "Shell Infrastructure Host");
        Add(5712, 1908, "taskhostw.exe", boot - 0.02, 0, 9, sys + "taskhostw.exe", "taskhostw.exe {222A245B-E637-4AE9-A93F-A59CA119A75E}", "Host Process for Windows Tasks");
        Add(5964, 1300, "StartMenuExperienceHost.exe", boot - 0.02, 0, 41, @"C:\WINDOWS\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe", null, "Windows Start Experience Host");
        Add(6028, 1300, "SearchHost.exe", boot - 0.02, 0.01, 96, @"C:\WINDOWS\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\SearchHost.exe", null, "Search application");
        Add(6072, 1300, "TextInputHost.exe", boot - 0.02, 0, 28, @"C:\WINDOWS\SystemApps\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\TextInputHost.exe", null, "Windows Feature Experience Pack");
        for (int i = 0; i < 4; i++)
            Add(6304 + 44 * i, 1300, "RuntimeBroker.exe", boot - 0.03 - i, 0, 6 + 3 * i, sys + "RuntimeBroker.exe", sys + "RuntimeBroker.exe -Embedding", "Runtime Broker");
        Add(6520, 5236, "SecurityHealthSystray.exe", boot - 0.03, 0, 3, sys + "SecurityHealthSystray.exe", null, "Windows Security notification icon");
        Add(6588, 5236, "ctfmon.exe", boot - 0.03, 0, 6, sys + "ctfmon.exe", null, "CTF Loader");
        Add(11208, 5236, "Nikomi.exe", 27.5, 0.01, 31, @"C:\Tools\Nikomi\Nikomi.exe", @"""C:\Tools\Nikomi\Nikomi.exe"" --background", "Nikomi", company: null, io: 900);

        // A browser.
        Add(8024, 5236, "msedge.exe", 9.4, 0.07, 318, edge, $@"""{edge}""", "Microsoft Edge", io: 240_000);
        string[] kinds = { "gpu-process", "utility --utility-sub-type=network.mojom.NetworkService", "renderer", "renderer", "renderer", "renderer", "utility --utility-sub-type=audio.mojom.AudioService", "renderer" };
        double[] rates = { 0.09, 0.03, 0.18, 0.05, 0.02, 0.01, 0.01, 0 };
        int[] memory = { 164, 38, 421, 236, 187, 122, 21, 94 };
        for (int i = 0; i < kinds.Length; i++)
            Add(8112 + 92 * i, 8024, "msedge.exe", 9.4 - i * 0.6, rates[i], memory[i], edge, $@"""{edge}"" --type={kinds[i]} --lang=en-US", "Microsoft Edge", io: i == 1 ? 1_400_000 : 0);

        // A terminal.
        Add(7644, 5236, "WindowsTerminal.exe", 5.2, 0.01, 74, @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.3231.0_x64__8wekyb3d8bbwe\WindowsTerminal.exe", null, "Windows Terminal");
        Add(7700, 7644, "OpenConsole.exe", 5.2, 0, 11, @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.21.3231.0_x64__8wekyb3d8bbwe\OpenConsole.exe", null, "Windows Terminal Console Host");
        Add(7812, 7644, "pwsh.exe", 5.2, 0, 96, @"C:\Program Files\PowerShell\7\pwsh.exe", @"""C:\Program Files\PowerShell\7\pwsh.exe""", "PowerShell 7");

        // An editor, and the build watcher it started 52 minutes ago that never came back down.
        Add(6120, 5236, "Code.exe", 6.3, 0.05, 212, code, $@"""{code}"" C:\Users\alex\projects\storefront", "Visual Studio Code", io: 12_000);
        string[] parts = { "gpu-process", "utility --utility-sub-type=node.mojom.NodeService", "renderer", "utility --utility-sub-type=node.mojom.NodeService --inspect-port=0", "crashpad-handler" };
        double[] partRates = { 0.04, 0.02, 0.08, 0.06, 0 };
        int[] partMemory = { 96, 118, 384, 506, 8 };
        for (int i = 0; i < parts.Length; i++)
            Add(6212 + 88 * i, 6120, "Code.exe", 6.3, partRates[i], partMemory[i], code, $@"""{code}"" --type={parts[i]}", "Visual Studio Code");
        Add(7312, 6120, "cmd.exe", 0.88, 0, 5, sys + "cmd.exe", @"C:\WINDOWS\system32\cmd.exe /d /s /c ""npm run watch""", "Windows Command Processor");
        Add(7340, 7312, "conhost.exe", 0.88, 0, 7, sys + "conhost.exe", @"\??\C:\WINDOWS\system32\conhost.exe 0x4", "Console Window Host");
        Add(9184, 7312, "node.exe", 0.87, 0.99, 1284, node, $@"""{node}"" C:\Users\alex\projects\storefront\node_modules\webpack\bin\webpack.js --watch --mode development",
            "Node.js JavaScript Runtime", company: "Node.js", io: 22_000, steady: true, threads: 19);
        Add(9260, 9184, "node.exe", 0.86, 0.04, 148, node, $@"""{node}"" C:\Users\alex\projects\storefront\node_modules\fork-ts-checker-webpack-plugin\lib\typescript\worker.js",
            "Node.js JavaScript Runtime", company: "Node.js");
        Add(9312, 9184, "esbuild.exe", 0.86, 0.01, 22, @"C:\Users\alex\projects\storefront\node_modules\@esbuild\win32-x64\esbuild.exe", @"C:\Users\alex\projects\storefront\node_modules\@esbuild\win32-x64\esbuild.exe --service=0.24.0 --ping", null, company: null);

        // A script whose terminal has been closed, and which became busy two minutes ago.
        Add(10452, 9040, "python.exe", 3.1, 0.12, 187, @"C:\Program Files\Python313\python.exe", @"python.exe C:\Users\alex\projects\reports\export_month.py --all",
            "Python", company: "Python Software Foundation", io: 2_600_000, rateNow: 0.96, steady: true);
    }

    private void Add(int pid, int parent, string name, double hoursAgo, double rate, int memoryMb, string? path = null, string? cmd = null,
        string? description = null, string? company = Ms, string? services = null, double io = 0, int session = 1, int threads = 0,
        double? rateNow = null, bool steady = false)
    {
        // Details that are not given are made up from the PID, so every row differs and no run does.
        if (threads == 0) threads = 2 + pid % 7 + memoryMb / 14;
        if (path == null && cmd == null && description == null) company = null;
        var f = new Fake
        {
            Pid = pid, Parent = parent, Name = name, Session = session, Priority = name is "csrss.exe" or "dwm.exe" or "winlogon.exe" ? 13 : 8,
            Threads = threads, Handles = threads * (21 + pid % 13) + memoryMb,
            Created = (_startUtc - TimeSpan.FromHours(hoursAgo)).ToFileTimeUtc(),
            Rate = rate, RateNow = rateNow ?? rate, IoRate = io, Steady = steady,
            Memory = (long)memoryMb << 20, Committed = (long)(memoryMb * 1.34) << 20,
            Path = path, Cmd = cmd ?? (path != null ? $@"""{path}""" : null), Description = description, Company = company, Services = services,
            // CPU time and I/O so far: what the process would have used had it always run as it does now.
            CpuTicks = rate * (hoursAgo * 3600 - 47.4 * 60) * 1e7 * 0.8,
            IoBytes = io * hoursAgo * 3600 * 0.3,
        };
        if (f.CpuTicks < 0) f.CpuTicks = 0;
        _procs.Add(f);
        _byPid[pid] = f;
        if (path != null) _described[path] = (description, company);
    }

    private TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startStamp);

    public override Snapshot TakeSnapshot()
    {
        TimeSpan offset = _samples < SampleOffsets.Length ? SampleOffsets[_samples] : TimeSpan.Zero;
        bool past = _samples < SampleOffsets.Length;   // this sample closes a stretch that lies in the past
        _samples++;
        TimeSpan elapsed = Elapsed + offset;
        long ticks = elapsed.Ticks + TimeSpan.FromHours(1).Ticks;
        long dt = _lastTicks == 0 ? 0 : ticks - _lastTicks;
        _lastTicks = ticks;

        var list = new List<ProcInfo>(_procs.Count + 1);
        double busy = 0;
        foreach (var f in _procs)
        {
            double rate = past ? f.Rate : f.RateNow;
            double wobble = f.Steady ? 0.99 + 0.01 * _random.NextDouble() : 0.7 + 0.6 * _random.NextDouble();
            double used = rate * wobble * dt;
            busy += used;
            f.CpuTicks += used;
            f.IoBytes += f.IoRate * (0.6 + 0.8 * _random.NextDouble()) * dt / 1e7;
            list.Add(new ProcInfo
            {
                Pid = f.Pid, ParentPid = f.Parent, Name = f.Name, CreateFileTime = f.Created, CpuTicks = (long)f.CpuTicks,
                WorkingSet = f.Memory + (f.Memory >> 2), PrivateWorkingSet = f.Memory, PrivateBytes = f.Committed,
                IoBytes = (long)f.IoBytes, Threads = f.Threads, Handles = f.Handles, SessionId = f.Session, BasePriority = f.Priority,
            });
        }
        _idleTicks += Math.Max(0, (double)Cores * dt - busy);
        list.Insert(0, new ProcInfo
        {
            Pid = 0, ParentPid = 0, Name = "System Idle Process", CreateFileTime = 0, CpuTicks = (long)_idleTicks,
            WorkingSet = 8192, Threads = Cores,
        });
        return new Snapshot(_startUtc + elapsed, ticks, list);
    }

    public override (string? CommandLine, string? ExePath) ReadDetails(int pid) =>
        _byPid.TryGetValue(pid, out var f) ? (f.Cmd, f.Path) : (null, null);

    public override (string? Description, string? Company) Describe(string? exePath) =>
        exePath != null && _described.TryGetValue(exePath, out var d) ? d : (null, null);

    public override Dictionary<int, string> ReadServices() =>
        _procs.Where(f => f.Services != null).ToDictionary(f => f.Pid, f => f.Services!);

    /// <summary>The total load for the live graph: what the processes add up to, moving a little as a real one does.</summary>
    public override bool ReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        double now = Elapsed.TotalSeconds;
        double load = _procs.Sum(f => f.RateNow) / Cores
            + 0.035 * Math.Sin(now / 9) + 0.02 * Math.Sin(now / 2.7 + 1) + 0.012 * (_random.NextDouble() - 0.5);
        double step = (now - _liveAt) * 1e7 * Cores;
        _liveAt = now;
        _liveTotal += step;
        _liveIdle += step * (1 - Math.Clamp(load, 0, 1));
        idle = (ulong)_liveIdle;
        kernel = idle;
        user = (ulong)(_liveTotal - _liveIdle);
        return true;
    }

    public override int LogicalCores => Cores;

    public override TimeSpan Uptime => UptimeAtStart + Elapsed;

    /// <summary>
    /// Replaces the demo's history with a made-up last 24 hours, so the History view has something to
    /// show from the first minute: an evening with a scheduled virus scan, a night asleep, and a morning
    /// that ends in the made-up processes of the list (the build watcher's 47 minutes as a runaway).
    /// Written again at every start, so it always ends at the current time.
    /// </summary>
    public static void WriteHistory(string path)
    {
        var random = new Random(7);
        DateTime now = DateTime.UtcNow;
        double Jitter(double cores) => cores * (0.75 + 0.5 * random.NextDouble());
        var lines = new List<string>();
        for (DateTime t = now.AddHours(-24).AddSeconds(30); t < now.AddSeconds(-30); t = t.AddSeconds(25))
        {
            double ago = (now - t).TotalHours;
            if (ago < 16.5 && ago > 10.2) continue;   // the PC was asleep
            var top = new List<HistoryProc>();
            void Busy(string name, int pid, double cores) => top.Add(new HistoryProc(name, pid, Math.Round(cores, 3)));
            if (ago >= 16.5)
            {
                // Yesterday evening.
                Busy("msedge.exe", 7120, Jitter(0.18));
                Busy("explorer.exe", 5236, Jitter(0.03));
                if (ago is >= 19.0 and <= 19.6) Busy("MsMpEng.exe", 3408, Jitter(1.6));
                if (ago is >= 18.2 and <= 18.5) Busy("SearchIndexer.exe", 4688, Jitter(0.45));
                if (ago is >= 21.0 and <= 22.3) Busy("Code.exe", 5904, Jitter(0.22));
            }
            else
            {
                // Today, with the processes of the list as they started (DemoSource's start times).
                Busy("explorer.exe", 5236, Jitter(0.04));
                Busy("dwm.exe", 1496, Jitter(0.09));
                if (ago <= 9.4) Busy("msedge.exe", 8024, Jitter(0.12) + (ago is >= 7.5 and <= 7.7 ? 0.5 : 0));
                if (ago <= 6.3) Busy("Code.exe", 6120, Jitter(0.10));
                if (ago is >= 9.9 and <= 10.15) Busy("MsMpEng.exe", 3408, Jitter(0.9));
                if (ago is <= 3.1 and >= 0.9 && (ago >= 2.2 || ago <= 1.3)) Busy("python.exe", 10452, Jitter(0.7));
                if (ago is >= 4.6 and <= 4.9) Busy("SearchIndexer.exe", 4688, Jitter(0.35));
                if (ago <= 0.79) Busy("node.exe", 9184, 0.97 + 0.04 * random.NextDouble());
            }
            double load = Math.Min(1, 0.02 + 0.015 * random.NextDouble() + top.Sum(p => p.Cores) / Cores);
            var busiest = top.Where(p => p.Cores >= UsageHistory.Floor).OrderByDescending(p => p.Cores).Take(UsageHistory.TopCount).ToList();
            lines.Add(System.Text.Json.JsonSerializer.Serialize(new HistoryEntry(t, 25, Math.Round(load, 4), busiest)));
        }
        try
        {
            File.Delete(path + ".old");
            File.WriteAllText(path, string.Join("\n", lines) + "\n");
        }
        catch (Exception ex)
        {
            Log.Write("Demo: could not write the made-up history: " + ex.Message);
        }
    }
}
#endif
