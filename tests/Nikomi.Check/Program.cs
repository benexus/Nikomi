// Checks Nikomi's measuring, runaway, parent-chain, orphan, end-process and start-with-Windows logic
// against real busy-loop processes. It starts its own test processes (copies of itself running a busy
// loop) and ends only those. Takes about 30 seconds.
//
//   dotnet run --project tests\Nikomi.Check
//   Nikomi.Check.exe busy <marker> [--child]     (the busy loop it starts)

using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Nikomi.Core;

if (args.Length > 0 && args[0] == "busy")
{
    if (args.Contains("--child"))
        Process.Start(Environment.ProcessPath!, args.Where(a => a != "--child"))?.Dispose();
    while (true) { }
}

int failed = 0;
void Check(bool ok, string what)
{
    Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
    Console.Write(ok ? "PASS  " : "FAIL  ");
    Console.ResetColor();
    Console.WriteLine(what);
    if (!ok) failed++;
}

string marker = "nikomi-check-" + Guid.NewGuid().ToString("N")[..8];
string exe = Environment.ProcessPath!;
var settings = new AppSettings { RunawayCores = 0.9, RunawayMinutes = 0.05 };
using var sampler = new ProcessSampler();
// What the monitors and the history read details, services and the number of cores from.
using var source = new SystemSource();

List<ProcInfo> Mine(ProcessMonitor m)
{
    var list = new List<ProcInfo>();
    foreach (var p in m.ByKey.Values)
    {
        if (!p.Name.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)) continue;
        m.EnsureDetails(p);
        if (p.CommandLine?.Contains(marker) == true) list.Add(p);
    }
    return list;
}

try
{
    // Sampler cost
    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 20; i++) sampler.Take();
    double ms = sw.Elapsed.TotalMilliseconds / 20;
    Check(ms < 50, $"one sample of all processes takes {ms:0.0} ms");

    // --- 1. A busy process with a busy child ---------------------------------------------------------
    using var root = Process.Start(new ProcessStartInfo(exe, ["busy", "tree-" + marker, "--child"]) { CreateNoWindow = true })!;
    Thread.Sleep(2000);
    var mon = new ProcessMonitor(source);
    var new0 = mon.Update(sampler.Take(), settings);
    Check(new0.Count == 0, "first sample has no interval yet, so nothing is reported");
    Thread.Sleep(6000);
    var new1 = mon.Update(sampler.Take(), settings);

    var rootP = mon.ByPid.GetValueOrDefault(root.Id);
    Check(rootP != null, $"started process {root.Id} is in the sample");
    var tree = new List<ProcInfo> { rootP! };
    tree.AddRange(ProcessTree.Descendants(rootP!, mon.ByKey.Values));
    Console.WriteLine("      tree: " + string.Join(" | ", tree.Select(p => $"{p.Name} {p.Pid} cores={p.Cores:0.00}")));
    var hot = tree.Where(p => p.Cores >= 0.9).ToList();
    Check(hot.Count == 2, $"two busy loops measured at about one core each ({hot.Count} found)");
    Check(hot.All(p => p.Cores < 1.1), "no busy loop measured above 1.1 cores");
    Check(hot.All(p => p.Runaway), "busy loops are flagged runaway with the lowered threshold");
    Check(new1.Count(p => tree.Contains(p)) == hot.Count, "each busy loop is reported once");
    Check(mon.Load is > 0 and <= 1, $"total load is a fraction: {mon.Load * 100:0.0}%");

    var anc = ProcessTree.Ancestors(hot[^1], mon.ByPid);
    Console.WriteLine("      chain: " + anc.Format());
    Check(anc.Chain.Any(p => p.Pid == Environment.ProcessId), "parent chain of the busy child reaches this check program");

    mon.EnsureDetails(hot[0]);
    Check(hot[0].CommandLine?.Contains(marker) == true, "command line is read: " + hot[0].CommandLine);
    Check(hot[0].ExePath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true, "executable path is read: " + hot[0].ExePath);

    Thread.Sleep(3000);
    var new2 = mon.Update(sampler.Take(), settings);
    Check(Mine(mon).Count(p => p.Runaway) == 2, "next sample keeps both flagged runaway");
    Check(!new2.Any(p => p.CommandLine?.Contains(marker) == true || hot.Any(h => h.Key == p.Key)), "no second notification for the same process");

    // A stale target (PID reused) must be refused.
    var real = hot[0];
    var fake = new ProcInfo
    {
        Pid = real.Pid, ParentPid = real.ParentPid, Name = real.Name, CpuTicks = 0, WorkingSet = 0,
        CreateFileTime = real.CreateFileTime - TimeSpan.FromHours(1).Ticks,
    };
    var r = ProcessKiller.End([fake]);
    bool alive; try { using var pr = Process.GetProcessById(real.Pid); alive = !pr.HasExited; } catch { alive = false; }
    Check(!r[0].Ended && alive, "reused-PID guard: " + r[0].Message);

    // The extra columns come from fixed places in the same system call: compare them with what .NET reports.
    using (var self = Process.GetCurrentProcess())
    {
        var me = mon.ByPid[Environment.ProcessId];
        Check(me.SessionId == self.SessionId && me.BasePriority == self.BasePriority,
            $"session and priority are read: session {me.SessionId}, priority {ProcessKiller.PriorityName(me.BasePriority)}");
        Check(Math.Abs(me.Threads - self.Threads.Count) <= 4 && Math.Abs(me.Handles - self.HandleCount) <= 40,
            $"threads and handles are read: {me.Threads} threads, {me.Handles} handles");
        Check(me.PrivateWorkingSet > 0 && me.PrivateWorkingSet <= me.WorkingSet && me.PrivateBytes > 0,
            $"memory is read: {me.PrivateWorkingSet >> 20} MB private of {me.WorkingSet >> 20} MB, {me.PrivateBytes >> 20} MB committed");
        Check(me.IoBytesPerSec != null, $"I/O rate is measured: {me.IoBytesPerSec:0} bytes/s");
        mon.EnsureDetails(me);
        Check(!string.IsNullOrEmpty(me.Description), "the executable's description is read: " + me.Description);
    }

    var services = ServiceMap.Read();
    var hosted = services.Where(s => mon.ByPid.ContainsKey(s.Key)).ToList();
    Check(hosted.Count > 0, $"services are matched to processes: {hosted.Count} processes host services");
    if (hosted.Count > 0)
        Console.WriteLine($"      for example {mon.ByPid[hosted[0].Key].Name} {hosted[0].Key}: {hosted[0].Value}");

    // A gentler step than ending: lower the priority.
    string? priorityProblem = ProcessKiller.SetPriority(real, PriorityLevel.BelowNormal);
    ProcessPriorityClass priorityNow;
    using (var pr = Process.GetProcessById(real.Pid)) priorityNow = pr.PriorityClass;
    Check(priorityProblem == null && priorityNow == ProcessPriorityClass.BelowNormal, $"priority is changed: now {priorityNow} {priorityProblem}");
    Check(ProcessKiller.SetPriority(fake, PriorityLevel.Low) != null, "priority of a reused PID is not touched");

    // End the whole tree the way the button does.
    var current = new List<ProcInfo> { mon.ByPid[root.Id] };
    current.AddRange(ProcessTree.Descendants(current[0], mon.ByKey.Values));
    r = ProcessKiller.End(current);
    Console.WriteLine("      " + string.Join(" | ", r.Select(x => $"{x.Target.Name} {x.Target.Pid}: {x.Message}")));
    Thread.Sleep(500);
    Check(current.All(p => { try { using var pr = Process.GetProcessById(p.Pid); return pr.HasExited; } catch { return true; } }),
        "End process tree ended every process in the tree");

    // --- 2. An orphan: started through cmd /c start, so its parent exits at once ---------------------
    using (var cmd = Process.Start(new ProcessStartInfo("cmd.exe", $"/c start \"\" /b \"{exe}\" busy orphan-{marker}") { CreateNoWindow = true })!)
    {
        Thread.Sleep(2500);
        var mon2 = new ProcessMonitor(source);
        mon2.Update(sampler.Take(), settings);
        var orphans = Mine(mon2).Where(p => p.Orphan).ToList();
        Check(orphans.Count == 1, $"orphaned busy loop is flagged orphan; orphans in total: {mon2.OrphanCount}");
        if (orphans.Count == 1)
        {
            var a2 = ProcessTree.Ancestors(orphans[0], mon2.ByPid);
            Check(a2.ExitedParentPid == cmd.Id, "its chain shows the exited parent: " + a2.Format());
            r = ProcessKiller.End([orphans[0]]);
            Check(r[0].Ended, "orphan ended: " + r[0].Message);
        }
    }

    // --- 3. Start with Windows, against a throwaway key instead of the real Run key -------------------
    const string testKey = @"Software\Nikomi.Check\Run";
    StartupRegistration.Enable(@"C:\Example\Nikomi.exe", testKey);
    Check(StartupRegistration.IsEnabled(@"C:\Example\Nikomi.exe", testKey), "start-with-Windows entry is written");
    // Windows records the user's own off switch (Task Manager, Settings) in a second key.
    const string approvedKey = @"Software\Nikomi.Check\Approved";
    StartupState State() => StartupRegistration.GetState(@"C:\Example\Nikomi.exe", testKey, approvedKey);
    void Approve(byte first)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(approvedKey);
        k.SetValue(StartupRegistration.ValueName, new byte[] { first, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary);
    }
    Check(State() == StartupState.On, "start with Windows reads as on");
    Approve(3);
    Check(State() == StartupState.OffInWindows, "an entry the user turned off in Windows reads as off in Windows");
    Approve(2);
    Check(State() == StartupState.On, "and as on again once it is turned on there");
    StartupRegistration.Disable(testKey);
    Check(State() == StartupState.Off, "a removed entry reads as off");
    Check(!StartupRegistration.IsEnabled(@"C:\Example\Nikomi.exe", testKey), "start-with-Windows entry is removed");
    Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Nikomi.Check", throwOnMissingSubKey: false);

    // --- 4. Made-up samples: only a stretch of a second or more is judged ----------------------------
    {
        var m = new ProcessMonitor(source);
        var rule = new AppSettings { RunawayCores = 0.9, RunawayMinutes = 10 };
        DateTime t0 = DateTime.UtcNow;
        long created = t0.AddHours(-1).ToFileTimeUtc();
        string key = $"4242:{created}";
        // One process, seen `seconds` after the first sample, having used `cpuSeconds` of CPU by then.
        Snapshot At(double seconds, double cpuSeconds) => new(t0.AddSeconds(seconds), (long)(seconds * TimeSpan.TicksPerSecond),
        [
            new ProcInfo
            {
                Pid = 4242, ParentPid = 0, Name = "made-up.exe", CreateFileTime = created,
                CpuTicks = (long)(cpuSeconds * TimeSpan.TicksPerSecond), WorkingSet = 0,
            },
        ]);

        m.Update(At(0, 0), rule);
        m.Update(At(5, 5), rule);
        Check(m.IsHot(key), "made-up samples: a full core for 5 s is hot");
        m.Update(At(5.2, 5), rule);
        Check(m.IsHot(key), "an idle 0.2 s measurement (a click on Refresh) does not reset the hot streak");
        m.Update(At(10.2, 10), rule);
        double? hotSeconds = m.ByKey[key].HotMinutes * 60;
        Check(m.IsHot(key) && hotSeconds is > 10.1 and < 10.3, $"the streak runs on unbroken: hot for {hotSeconds:0.0} s");
        m.Update(At(15.2, 10), rule);
        Check(!m.IsHot(key), "a full idle interval does reset it");

        // The system clock jumping an hour ahead between two samples must not change the measurement.
        var jumped = At(20.2, 15) with { TimeUtc = t0.AddHours(1) };
        m.Update(jumped, rule);
        Check(m.ByKey[key].Cores is > 0.99 and < 1.01, $"a jump of the system clock does not bend the measurement: {m.ByKey[key].Cores:0.00} cores");

        // Never-flag list: hot for long enough, but its name is on the list (written without ".exe").
        var quick = new AppSettings { RunawayCores = 0.9, RunawayMinutes = 0.05, IgnoreNames = { "Made-Up" } };
        var m2 = new ProcessMonitor(source);
        m2.Update(At(0, 0), quick);
        var flagged = m2.Update(At(5, 5), quick);
        Check(flagged.Count == 0 && m2.RunawayCount == 0 && m2.ByKey[key].Ignored && !m2.ByKey[key].Runaway,
            "a name on the never-flag list is not flagged or reported");
        flagged = m2.Update(At(10, 10), new AppSettings { RunawayCores = 0.9, RunawayMinutes = 0.05 });
        Check(flagged.Count == 1 && m2.ByKey[key].Runaway, "taken off the list, it is flagged and reported");

        // History: one line per stretch of 25 s or more, whatever the sampling rhythm in between.
        var history = new UsageHistory(source);
        DateTime past = DateTime.UtcNow.AddMinutes(-5);
        Snapshot Past(double seconds, double cpuSeconds) => At(seconds, cpuSeconds) with { TimeUtc = past.AddSeconds(seconds) };
        bool early = history.Add(Past(0, 0)) == null && history.Add(Past(10, 10)) == null;
        var line = history.Add(Past(30, 30));
        Check(early && line is { Top.Count: 1 } && line.Top[0].Name == "made-up.exe" && line.Top[0].Cores is > 0.99 and < 1.01,
            $"history writes one line for a 30 s stretch: {line?.Top.FirstOrDefault()}");
        string historyFile = Path.Combine(AppContext.BaseDirectory, "check-history.jsonl");
        File.Delete(historyFile);
        if (line != null) UsageHistory.Append(historyFile, line);
        var reloaded = new UsageHistory(source);
        reloaded.Load(historyFile);
        File.Delete(historyFile);
        Check(reloaded.Entries.Count == 1 && reloaded.Entries[0].Top[0] == line?.Top[0], "history is saved and read back");

        // Lines that are valid JSON but could never have been written (review W2) are dropped on reading.
        string when = DateTime.UtcNow.AddMinutes(-1).ToString("O");
        string Tampered(string rest) => $$"""{"TimeUtc":"{{when}}",{{rest}}}""";
        File.WriteAllLines(historyFile,
        [
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":"good.exe","Pid":1,"Cores":1.5}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[null]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":null,"Pid":1,"Cores":1}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Pid":1,"Cores":1}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":" ","Pid":1,"Cores":1}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":"neg.exe","Pid":1,"Cores":-1}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":"huge.exe","Pid":1,"Cores":1e300}]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":null"""),
            Tampered("""  "Seconds":-30,"Load":0.5,"Top":[]"""),
            Tampered("""  "Seconds":1e9,"Load":0.5,"Top":[]"""),
            Tampered("""  "Seconds":30,"Load":7,"Top":[]"""),
            Tampered("""  "Seconds":30,"Load":-0.1,"Top":[]"""),
            Tampered("""  "Seconds":30,"Load":0.5,"Top":[{"Name":"a","Pid":1,"Cores":1},{"Name":"a","Pid":1,"Cores":1},{"Name":"a","Pid":1,"Cores":1},{"Name":"a","Pid":1,"Cores":1},{"Name":"a","Pid":1,"Cores":1},{"Name":"a","Pid":1,"Cores":1}]"""),
            "null",
            Tampered("""  "Seconds":30,"Load":null,"Top":[]"""),
        ]);
        var tampered = new UsageHistory(source);
        tampered.Load(historyFile);
        File.Delete(historyFile);
        Check(tampered.Entries.Count == 2 && tampered.Entries.Any(e => e.Top.Count == 1 && e.Top[0].Name == "good.exe") &&
              tampered.Entries.Any(e => e.Load == null && e.Top.Count == 0),
            $"history: damaged or tampered lines are dropped on reading, sound ones kept ({tampered.Entries.Count} of 15 kept)");
        var tamperedAnswer = new AgentQueries(new ProcessMonitor(source), () => tampered.Entries, fromApp: false)
            .Answer(new AgentRequest { Command = "history", Name = "good" }, new AppSettings());
        Check(tamperedAnswer["lines_found"]?.GetValue<int>() == 1, "history: an agent's history question still works after reading them");

        // Graphs: stretches that follow on make one line, a gap starts another, and the edges clip.
        var spans = new[]
        {
            new LoadSpan(-130, -100, 0.9),   // starts before the graph's left edge
            new LoadSpan(-100, -70, 0.2),
            new LoadSpan(-30, 0, 1.5),       // after a gap; a load above 100% is drawn at 100%
        };
        var lines = LoadSeries.Build(spans, -120, 0, tolerance: 5);
        string shape = string.Join(" | ", lines.Select(l => string.Join(" ", l.Select(p => $"{p.X:0.###},{p.Y:0.#}"))));
        Check(shape == "0,0.9 0.167,0.9 0.417,0.2 | 0.75,1 1,1", "graph: lines break at a gap and clip at the edges: " + shape);
        var hours = LoadSeries.HourTicks(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), TimeSpan.FromHours(24), everyHours: 3);
        Check(hours.Count is >= 7 and <= 8 && hours.All(t => t.X is > 0 and <= 1 && t.Label is { Length: > 0 }),
            $"graph: 24 hours get a labelled line every 3 hours ({hours.Count})");
        var live = new LiveLoad(source);
        for (int i = 0; i <= 120; i++) live.Add(new LoadSpan(i, i + 1, 0.5));
        Check(live.Spans.Count is 61 or 62 && live.Latest == 0.5, $"live graph keeps the last minute only: {live.Spans.Count} readings");
        long liveMs = Environment.TickCount64;
        bool first = live.Sample(liveMs);
        Thread.Sleep(300);
        bool second = live.Sample(liveMs + 300);
        Check(!first && second && live.Latest is >= 0 and <= 1, $"live load is read from the system: {live.Latest * 100:0}%");
        Check(!live.Sample(liveMs + 10_000) && live.Latest == null, "a long pause starts the live reading afresh");
    }

    // --- 4a. Each monitor reads from the source it was given (review A4) -----------------------------
    {
        Snapshot Idle(double seconds, double idleCoreSeconds) => new(DateTime.UtcNow, (long)(seconds * TimeSpan.TicksPerSecond),
        [
            new ProcInfo { Pid = 0, ParentPid = 0, Name = "System Idle Process", CreateFileTime = 0, CpuTicks = (long)(idleCoreSeconds * TimeSpan.TicksPerSecond), WorkingSet = 0 },
        ]);
        var four = new ProcessMonitor(new FakeSource(cores: 4));
        var sixteen = new ProcessMonitor(new FakeSource(cores: 16));
        foreach (var m in new[] { four, sixteen }) { m.Update(Idle(0, 0), settings); m.Update(Idle(1, 2), settings); }
        Check(four.Load is > 0.49 and < 0.51 && sixteen.Load is > 0.87 and < 0.88,
            $"sources: two monitors side by side each go by their own PC: {four.Load * 100:0}% of 4 cores, {sixteen.Load * 100:0.#}% of 16");

        // One thread per monitor and history (review A2): once bound, another thread is refused at once.
        string? OnOtherThread(Action use)
        {
            string? problem = null;
            var other = new Thread(() => { try { use(); } catch (InvalidOperationException ex) { problem = ex.Message; } });
            other.Start();
            other.Join();
            return problem;
        }
        var bound = new ProcessMonitor(new FakeSource());
        var boundHistory = new UsageHistory(new FakeSource());
        bound.BindToCurrentThread();
        boundHistory.BindToCurrentThread();
        bound.Update(Idle(0, 0), settings);
        boundHistory.Add(Idle(0, 0));
        string?[] refused =
        [
            OnOtherThread(() => bound.Update(Idle(1, 1), settings)), OnOtherThread(() => _ = bound.ByKey.Count),
            OnOtherThread(() => bound.EnsureDetails(bound.ByPid[0])), OnOtherThread(() => bound.RefreshServices()),
            OnOtherThread(() => boundHistory.Add(Idle(30, 1))), OnOtherThread(() => _ = boundHistory.Entries.Count),
        ];
        Check(refused.All(r => r?.Contains("not thread-safe") == true) && bound.ByKey.Count == 1 && bound.IsHot("0:0") == false,
            "threads: a bound monitor and history refuse every other thread, and still work on their own");
        Check(OnOtherThread(() => four.Update(Idle(2, 4), settings)) == null && OnOtherThread(() => _ = four.ByKey.Count) == null,
            "threads: an unbound monitor may be handed from one thread to the next (the console program does, under a lock)");
    }

    // --- 4a'. Ending: what is confirmed, and when (review A1) -----------------------------------------
    // Made-up processes, a stand-in dialog and a stand-in for ProcessKiller: nothing here ends anything.
    {
        static string U(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));
        long born = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
        ProcInfo P(int pid, int parent, string name, long created) => new()
        {
            Pid = pid, ParentPid = parent, Name = name, CreateFileTime = created, CpuTicks = 0, WorkingSet = 0,
        };
        var madeUp = new FakeSource();
        madeUp.Details[7001] = ($"editor.exe --open{U(0x200B)} notes.txt", @"C:\made-up\editor.exe");
        var procs = new List<ProcInfo>
        {
            P(7001, 1, "editor.exe", born + 10),
            P(7002, 7001, $"build{U(0x202E)}exe.cmd", born + 20),   // the name hides a direction change
            P(7003, 7002, "worker.exe", born + 30),
            P(7004, 7001, "reused.exe", born + 5),     // older than 7001: its PID was reused, not its child
            P(7005, 7001, "critical.exe", born + 40),  // Windows depends on it (said so below)
        };
        var endMon = new ProcessMonitor(madeUp);
        endMon.Update(new Snapshot(DateTime.UtcNow, 0, procs), settings);
        var editor = endMon.ByPid[7001];
        bool IsCritical(ProcInfo p) => p.Pid == 7005;

        var ended = new List<IReadOnlyList<ProcInfo>>();
        IReadOnlyList<EndResult> FakeEnd(IReadOnlyList<ProcInfo> targets)
        {
            ended.Add(targets);
            return targets.Select(t => new EndResult(t, true, "ended")).ToList();
        }
        bool agentsAllowed = true;
        var dialog = new FakeDialog();
        var ending = new EndConfirmation(endMon, dialog, () => agentsAllowed, FakeEnd, IsCritical) { MinimumOpen = TimeSpan.FromMilliseconds(50) };

        var one = ending.Plan(editor, tree: false, agentReason: null);
        Check(one.Targets.Count == 1 && one.Targets[0] == editor && one.Title == "End process?" && one.ConfirmText == "End process" &&
              one.CancelText == "Cancel" && one.ArmDelay == null && !one.FromAgent && one.ListHeading == "This process will be ended:",
            "end: the user's own end lists that one process, with Cancel and no delay on the end button");
        var treePlan = ending.Plan(editor, tree: true, agentReason: null);
        Check(string.Join(" ", treePlan.Targets.Select(t => t.Pid)) == "7001 7002 7005 7003" && treePlan.ConfirmText == "End 4 processes" &&
              treePlan.Title == "End process tree?" && treePlan.ListHeading.Contains("the 3 processes it started"),
            "end: a tree lists what the process started, children first, and not a process whose PID was reused: " + string.Join(" ", treePlan.Targets.Select(t => t.Pid)));
        Check(treePlan.Lines[2].Critical && treePlan.Lines[2].Label.Contains("Windows depends on it") && !treePlan.Lines[0].Critical &&
              treePlan.Lines[1].Label == "buildexe.cmd   (PID 7002)" && treePlan.Lines[0].Detail == "editor.exe --open notes.txt" &&
              treePlan.Lines[3].Detail == "(command line not available)",
            "end: the list names each process cleaned of hidden characters, and says which ones Windows depends on");

        dialog.Answer = (_, _) => Task.FromResult(true);
        var confirmed = await ending.ConfirmAndEndAsync(editor, tree: true, agentReason: null, timeout: null);
        Check(confirmed.Answer == EndAnswer.Confirmed && ended.Count == 1 && ended[0].Select(t => t.Key).SequenceEqual(treePlan.Targets.Select(t => t.Key)) &&
              dialog.Shown?.Title == "End process tree?" && dialog.Report is { AllEnded: true, Title: "4 processes ended" },
            "end: once confirmed, exactly the listed processes are ended and the result is shown");
        dialog.Answer = (_, _) => Task.FromResult(false);
        var declined = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: null, timeout: null);
        Check(declined.Answer == EndAnswer.Declined && ended.Count == 1, "end: declined, nothing is ended");
        dialog.Asked = 0;
        dialog.IsBusy = true;
        var busy = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: null, timeout: null);
        dialog.IsBusy = false;
        dialog.Ready = false;
        var notReady = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: "test", timeout: TimeSpan.FromSeconds(1));
        dialog.Ready = true;
        Check(busy.Answer == EndAnswer.NotShown && notReady.Answer == EndAnswer.NotShown && dialog.Asked == 0 && ended.Count == 1,
            "end: with another dialog open, or a window that cannot show one, nothing is asked or ended");

        var agentPlan = ending.Plan(editor, tree: false, agentReason: "It is stuck.");
        Check(agentPlan.FromAgent && agentPlan.Title == "An AI agent asks to end a process" && agentPlan.CancelText == "Do not end" &&
              agentPlan.ArmDelay == EndConfirmation.AgentArmDelay && agentPlan.AgentReason == "It is stuck.",
            "end: an agent's request says who asks and why, and its end button waits a second");
        agentsAllowed = false;
        var off = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: "It is stuck.", timeout: TimeSpan.FromMinutes(5));
        agentsAllowed = true;
        Check(off.Answer == EndAnswer.Withdrawn && dialog.Asked == 0, "end: with agent access off, an agent's request is withdrawn before it is shown");

        // Nobody answers: the dialog closes when its time is up (at least MinimumOpen after it appears).
        dialog.Answer = async (_, close) => { try { await Task.Delay(Timeout.Infinite, close); } catch (OperationCanceledException) { } return false; };
        var clock = Stopwatch.StartNew();
        var expired = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: "It is stuck.", timeout: TimeSpan.FromMilliseconds(10));
        Check(expired.Answer == EndAnswer.Expired && clock.ElapsedMilliseconds >= 45 && ended.Count == 1,
            $"end: an unanswered agent's request expires, but stays open a while first ({clock.ElapsedMilliseconds} ms), and nothing is ended");
        // The user turns agent access off while the dialog is open, and presses End in the same moment.
        dialog.Answer = (_, close) => { ending.Withdraw(); return Task.FromResult(close.IsCancellationRequested); };
        var withdrawn = await ending.ConfirmAndEndAsync(editor, tree: false, agentReason: "It is stuck.", timeout: TimeSpan.FromMinutes(5));
        Check(withdrawn.Answer == EndAnswer.Withdrawn && ended.Count == 1, "end: a withdrawn request ends nothing, even if End was pressed as it closed");

        var many = Enumerable.Range(1, 70).Select(i => P(8000 + i, 8000, "child.exe", born + 100 + i)).Prepend(P(8000, 1, "parent.exe", born + 100)).ToList();
        var bigMon = new ProcessMonitor(madeUp);
        bigMon.Update(new Snapshot(DateTime.UtcNow, 0, many), settings);
        var big = new EndConfirmation(bigMon, dialog, () => true, FakeEnd, IsCritical).Plan(bigMon.ByPid[8000], tree: true, agentReason: null);
        Check(big.Targets.Count == 71 && big.Lines.Count == EndConfirmation.MaxListed && big.More == "... and 11 more" && big.ConfirmText == "End 71 processes",
            "end: a long list names the first 60 and counts the rest, and all 71 are what would be ended");

        // An agent's requests, from asking to the outcome.
        long now = 1_000_000;
        int refreshed = 0;
        var asked = new List<(ProcInfo P, bool Tree, string Reason)>();
        TaskCompletionSource<EndOutcome> answer = new();
        Func<ProcInfo, bool, string, Task<EndOutcome>> confirm = (p, t, r) => { asked.Add((p, t, r)); return answer.Task; };
        var requests = new AgentEndRequests(endMon, () => refreshed++, (p, t, r) => confirm(p, t, r), () => now, IsCritical, selfPid: 7003,
            answerWait: TimeSpan.FromMilliseconds(50));
        async Task<string?> Refused(AgentRequest r) => (await requests.RequestAsync(r))["error"]?.GetValue<string>();
        AgentRequest End(string? id, string? reason = "It is stuck.", bool tree = false) => new() { Command = "end", Id = id, Reason = reason, Tree = tree };

        Check((await requests.ResultAsync())["error"] != null, "agent end: with no request yet, end_result says there is none");
        Check((await Refused(new AgentRequest { Command = "end", Pid = 7001, Reason = "x" }))?.Contains("PID alone") == true &&
              (await Refused(End("7001:1")))?.Contains("No running process") == true &&
              (await Refused(End(endMon.ByPid[7003].Key)))?.Contains("Nikomi itself") == true &&
              (await Refused(End(endMon.ByPid[7005].Key)))?.Contains("Windows depends") == true &&
              (await Refused(End(editor.Key, reason: $" {U(0x200B, 0x202E)} ")))?.Contains("Give a reason") == true && asked.Count == 0,
            "agent end: no id, an unknown id, Nikomi itself, a process Windows depends on and an empty reason are refused without asking");
        var first = await requests.RequestAsync(End(editor.Key, reason: $"It is{U(0x202E)} stuck.\n\nReally.", tree: true));
        Check(first["pending"]?.GetValue<bool>() == true && asked.Count == 1 && asked[0].P == editor && asked[0].Tree &&
              asked[0].Reason == "It is stuck. Really." && refreshed > 0,
            "agent end: a sound request asks the user with the cleaned reason, and is pending while the user thinks");
        Check((await Refused(End(editor.Key)))?.Contains("still waiting") == true && (await requests.ResultAsync())["pending"] != null && asked.Count == 1,
            "agent end: one request at a time; end_result says it is still pending");
        answer.SetResult(EndOutcome.Declined);
        var outcome = await requests.ResultAsync();
        Check(outcome["confirmed"]?.GetValue<bool>() == false && outcome["message"]!.GetValue<string>().Contains("declined") &&
              (await requests.ResultAsync()).ToJsonString() == outcome.ToJsonString(),
            "agent end: a declined request is reported as such, as often as it is asked for");
        Check((await Refused(End(editor.Key)))?.Contains("less than a minute") == true && asked.Count == 1,
            "agent end: after a refusal the next request waits a minute");
        now += (long)AgentEndCooldown.Length.TotalMilliseconds;
        answer = new();
        _ = await requests.RequestAsync(End(editor.Key));
        answer.SetResult(new EndOutcome(EndAnswer.Confirmed, [new EndResult(editor, true, "ended")]));
        var done = await requests.ResultAsync();
        Check(asked.Count == 2 && done["confirmed"]?.GetValue<bool>() == true && done["all_ended"]?.GetValue<bool>() == true &&
              done["results"]![0]!["pid"]!.GetValue<int>() == 7001,
            "agent end: after the minute it may ask again, and a confirmed end reports each process");
        confirm = (_, _, _) => throw new InvalidOperationException("no window");
        var failed2 = await requests.RequestAsync(End(editor.Key));
        Check(failed2["error"]?.GetValue<string>()?.Contains("could not show") == true && (await Refused(End(editor.Key)))?.Contains("Wait at least a minute") == true,
            "agent end: a confirmation that cannot be shown is an error, and the next request still waits a minute");
    }

    // --- 4b. Made-up processes: the tree and by-name views -------------------------------------------
    {
        long t = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
        ProcInfo P(int pid, int parent, string name, int age, long memoryMb) => new()
        {
            Pid = pid, ParentPid = parent, Name = name, CreateFileTime = t + age, CpuTicks = 0, WorkingSet = 0,
            PrivateWorkingSet = memoryMb << 20, Threads = 1, Handles = 10,
        };
        var procs = new[]
        {
            P(300, 200, "worker.exe", 3, 20),
            P(100, 1, "shell.exe", 1, 0),        // its parent is not running: starts a tree
            P(600, 300, "reused.exe", 0, 0),     // older than PID 300: not its child, the PID was reused
            P(400, 200, "worker.exe", 4, 30),
            P(500, 9999, "lonely.exe", 5, 5),
            P(200, 100, "app.exe", 2, 10),
        };
        var shown = ProcessViews.Tree(procs, (a, b) => b.Totals.Memory.CompareTo(a.Totals.Memory));
        string shape = string.Join(" ", shown.Select(v => $"{new string('>', v.Depth)}{v.Proc!.Pid}"));
        Check(shape == "100 >200 >>400 >>300 500 600", "tree view: parents before children, biggest first: " + shape);
        Check(shown[0].Totals is { Count: 4 } && shown[0].Totals.Memory == 60L << 20 && shown[1].Totals.Memory == 60L << 20,
            $"tree view: a row adds up everything under it: {shown[0].Totals.Count} processes, {shown[0].Totals.Memory >> 20} MB");
        var found = ProcessViews.KeepWithAncestors(shown, v => v.Proc!.Pid == 300);
        Check(string.Join(" ", found.Select(v => v.Proc!.Pid)) == "100 200 300", "tree view: a filter keeps the path down to a match");
        var workers = ProcessViews.ByName(procs).Single(g => g.Name == "worker.exe");
        Check(workers.IsGroup && workers.Totals is { Count: 2 } && workers.Totals.Memory == 50L << 20,
            $"by-name view: worker.exe is one row for {workers.Totals.Count} processes, {workers.Totals.Memory >> 20} MB");
    }

    // --- 4c. What AI agents get: answers as JSON, the pipe to the app, and the MCP server -------------
    {
        var agentMon = new ProcessMonitor(source);
        var closed = new AppSettings { AgentAccess = true };
        var open = new AppSettings { AgentAccess = true, AgentCommandLines = true };
        agentMon.Update(sampler.Take(), closed);
        Thread.Sleep(1100);
        agentMon.Update(sampler.Take(), closed);
        var made = new List<HistoryEntry>
        {
            new(DateTime.UtcNow.AddMinutes(-3), 30, 0.50, [new("a.exe", 1, 1.0), new("b.exe", 2, 0.5)]),
            new(DateTime.UtcNow.AddMinutes(-2), 30, 0.25, [new("a.exe", 1, 1.0)]),
            new(DateTime.UtcNow.AddHours(-5), 30, 1.00, [new("old.exe", 3, 8.0)]),
        };
        var queries = new AgentQueries(agentMon, () => made, fromApp: true);
        JsonObject Ask(string command, AppSettings s, Action<AgentRequest>? set = null)
        {
            var r = new AgentRequest { Command = command };
            set?.Invoke(r);
            return queries.Answer(r, s);
        }
        int self = Environment.ProcessId;

        var status = Ask("status", closed);
        Check(status["logical_cores"]!.GetValue<int>() == Environment.ProcessorCount && status["cpu_load_percent"] != null,
            $"agent: status gives the load: {status["cpu_load_percent"]}% on {status["logical_cores"]} cores");
        var top = Ask("top", closed, r => r.Count = 5)["rows"]!.AsArray();
        var topCores = top.Select(r => r!["cores"]!.GetValue<double>()).ToList();
        Check(top.Count == 5 && topCores.SequenceEqual(topCores.OrderByDescending(c => c)), "agent: top lists the asked number of rows, busiest first");
        var byMemory = Ask("top", closed, r => { r.Count = 5; r.Sort = "memory"; })["rows"]!.AsArray().Select(r => r!["memory_mb"]!.GetValue<long>()).ToList();
        Check(byMemory.SequenceEqual(byMemory.OrderByDescending(m => m)), "agent: top can rank by memory instead");

        var hidden = Ask("process", closed, r => r.Pid = self);
        Check(hidden["name"]?.GetValue<string>() == Path.GetFileName(exe) && !hidden.ContainsKey("command_line") && !hidden.ContainsKey("path"),
            "agent: a process is described without its command line or path by default");
        var shownTo = Ask("process", open, r => r.Pid = self);
        Check(string.Equals(shownTo["path"]?.GetValue<string>(), exe, StringComparison.OrdinalIgnoreCase) && shownTo.ContainsKey("command_line"),
            "agent: path and command line are included once the user allows them");
        string selfId = hidden["id"]!.GetValue<string>();
        Check(Ask("process", closed, r => r.Id = selfId)["pid"]?.GetValue<int>() == self, "agent: a process is found by its id (PID and start time)");
        Check(Ask("process", closed, r => r.Id = self + ":1").ContainsKey("error"), "agent: an id with another start time finds nothing");
        var under = Ask("tree", closed, r => r.Pid = self)["rows"]!.AsArray();
        Check(under[0]!["pid"]!.GetValue<int>() == self && under[0]!["depth"]!.GetValue<int>() == 0, "agent: the tree of one process starts at that process");
        Check(Ask("names", closed, r => r.Count = 200)["rows"]!.AsArray().Any(r => r!["name"]!.GetValue<string>() == Path.GetFileName(exe)),
            "agent: by-name rows include this check program");
        var past = Ask("history", closed, r => r.Minutes = 60);
        var busiest = past["busiest"]!.AsArray();
        Check(past["lines_found"]!.GetValue<int>() == 2 && busiest[0]!["name"]!.GetValue<string>() == "a.exe" &&
              busiest[0]!["average_cores"]!.GetValue<double>() == 1.0 && busiest[1]!["average_cores"]!.GetValue<double>() == 0.25,
            $"agent: history sums up the last hour: {busiest[0]!["name"]} {busiest[0]!["average_cores"]}, {busiest[1]!["name"]} {busiest[1]!["average_cores"]}");
        Check(Ask("nonsense", closed).ContainsKey("error"), "agent: an unknown command is an error, not a crash");

        // Text the programs (or the agent) wrote: nothing hidden, nothing reordered, nothing endless.
        // The test strings are built from code points, so this file itself holds no invisible characters.
        static string U(params int[] codePoints) => string.Concat(codePoints.Select(char.ConvertFromUtf32));
        Check(AgentText.Clean($"Photo{U(0x202E)}gpj.exe") == "Photogpj.exe" &&
              AgentText.Clean($"a{U(0x2066)}b{U(0x2069)}c{U(0x200E)}d{U(0x061C)}e") == "abcde",
            "text: bidi overrides, isolates and marks are dropped");
        Check(AgentText.Clean($"ig{U(0x200B)}no{U(0x200C)}re{U(0x200D, 0x2060, 0xFEFF, 0x00AD)} me") == "ignore me" &&
              AgentText.Clean($"ok{U(0xE0049, 0xE0047, 0xE004E, 0xFE0F, 0xE0100)}") == "ok",
            "text: zero-width, tag and variation-selector characters are dropped");
        Check(AgentText.Clean($"  one\r\ntwo\tthree{U(0x07, 0x2028)}four  ") == "one  two three  four",
            "text: control characters and line breaks become spaces");
        Check(AgentText.Clean($" {U(0x200B)}\t ") == null && AgentText.Clean(null) == null, "text: nothing visible left means no text");
        string longText = AgentText.Clean(new string('x', 4096))!;
        Check(longText.Length == AgentText.MaxLabel + 4 && longText.EndsWith(" ..."), $"text: a 4 KB description is cut to {longText.Length} characters");
        string emoji = AgentText.Clean("a" + string.Concat(Enumerable.Repeat(U(0x1F600), 150)))!;
        Check(emoji.EndsWith(" ...") && !char.IsHighSurrogate(emoji[^5]) && emoji.Length <= AgentText.MaxLabel + 4,
            "text: a cut never splits a character in two");
        string reason = AgentText.Reason($"  Free {U(0x202E)}the{U(0x200B)}   CPU\n\nnow " + new string('!', 1000));
        Check(reason.StartsWith("Free the CPU now !") && reason.Length == AgentText.MaxReason + 4 && AgentText.Reason($" {U(0x200F)} ") == "",
            "text: an agent's reason is cleaned, its spaces collapsed and it is cut at 400 characters");

        // After an end request the user did not confirm, the agent waits a minute, whatever became of it.
        {
            var cooldown = new AgentEndCooldown();
            long minute = (long)AgentEndCooldown.Length.TotalMilliseconds;
            Check(cooldown.Refusal(0) == null, "cooldown: the first request may be shown");
            var refusedAfter = new List<string>();
            foreach (var answer in new[] { EndAnswer.Declined, EndAnswer.Expired, EndAnswer.NotShown, EndAnswer.Withdrawn })
            {
                cooldown.Record(answer, 1_000);
                if (cooldown.Refusal(1_000 + minute - 1) is { Length: > 0 } && cooldown.Refusal(1_000 + minute) == null) refusedAfter.Add(answer.ToString());
            }
            Check(refusedAfter.Count == 4, "cooldown: a request is refused for a minute after " + string.Join(", ", refusedAfter));
            Check(cooldown.Refusal(1_000)!.Contains("turned agent access off"), "cooldown: the refusal says why");
            cooldown.Record(EndAnswer.Confirmed, 2_000);
            Check(cooldown.Refusal(2_000) == null, "cooldown: none after a confirmed request");
        }

        // Who may talk to the app: only programs of the same Windows account.
        using (var me = System.Security.Principal.WindowsIdentity.GetCurrent())
            Check(me.User != null && ProcessUser.Of(self) == me.User && ProcessUser.IsCurrentUser(self),
                $"pipe: this program is seen as running under this account ({me.User})");
        var system = agentMon.ByKey.Values.FirstOrDefault(p => p.Name.Equals("services.exe", StringComparison.OrdinalIgnoreCase) ||
                                                              p.Name.Equals("wininit.exe", StringComparison.OrdinalIgnoreCase));
        Check(system != null && !ProcessUser.IsCurrentUser(system.Pid),
            $"pipe: a process of another account is not taken for this one: {system?.Name} {system?.Pid} runs as {(system == null ? "?" : ProcessUser.Of(system.Pid)?.ToString() ?? "an account that cannot be read")}");
        Check(!ProcessUser.IsCurrentUser(0) && !ProcessUser.IsCurrentUser(0x3FFFFFFC), "pipe: a client whose process cannot be found is refused");

        // The pipe between the console program and the app.
        string pipeName = "Nikomi.Check." + Guid.NewGuid().ToString("N");
        Check(await AgentPipe.TryAskAsync(pipeName, new AgentRequest { Command = "top" }, TimeSpan.FromSeconds(5)) == null,
            "pipe: with no app listening, the question is not answered (the console program then measures by itself)");
        using (var server = new AgentPipeServer(pipeName, r => Task.FromResult(new JsonObject { ["echo"] = r.Command, ["count"] = r.Count })))
        {
            server.Start();
            Thread.Sleep(300);
            var echoed = await AgentPipe.TryAskAsync(pipeName, new AgentRequest { Command = "top", Count = 7 }, TimeSpan.FromSeconds(5));
            Check(echoed?["echo"]?.GetValue<string>() == "top" && echoed["count"]?.GetValue<int>() == 7,
                "pipe: a question from this PC and this user reaches the app and its answer comes back");
            async Task<string> Raw(byte[] bytes)
            {
                using var raw = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await raw.ConnectAsync(2000);
                try { await raw.WriteAsync(bytes); } catch (IOException) { }
                using var reader = new StreamReader(raw);
                try { return await reader.ReadLineAsync() ?? ""; } catch (IOException) { return ""; }
            }
            Check((await Raw("this is not json\n"u8.ToArray())).Contains("error"), "pipe: a request that is not JSON gets an error answer");
            var huge = new byte[AgentPipe.MaxRequestBytes + 20_000];
            Array.Fill(huge, (byte)'x');
            Check(!(await Raw(huge)).Contains("echo"), "pipe: an oversized request is not handed to the app");
        }
        Thread.Sleep(300);
        Check(await AgentPipe.TryAskAsync(pipeName, new AgentRequest { Command = "top" }, TimeSpan.FromSeconds(5)) == null,
            "pipe: once agent access is turned off, nothing listens any more");

        // The MCP server, fed messages directly.
        AgentRequest? got = null;
        var mcp = new McpServer(AgentTools.All, r => { got = r; return Task.FromResult(new JsonObject { ["ok"] = true }); }, "1.2.3");
        async Task<JsonObject?> Mcp(string message) => await mcp.HandleAsync(message) is { } reply ? JsonNode.Parse(reply)!.AsObject() : null;
        var hello = await Mcp("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"check","version":"1"}}}""");
        Check(hello?["id"]?.GetValue<int>() == 1 && hello["result"]?["protocolVersion"]?.GetValue<string>() == "2025-06-18" &&
              hello["result"]?["capabilities"]?["tools"] != null && hello["result"]?["serverInfo"]?["version"]?.GetValue<string>() == "1.2.3",
            "mcp: initialize answers with the client's protocol version and offers tools");
        var newer = await Mcp("""{"jsonrpc":"2.0","id":"a","method":"initialize","params":{"protocolVersion":"2099-01-01"}}""");
        Check(newer?["result"]?["protocolVersion"]?.GetValue<string>() == "2025-11-25" && newer["id"]?.GetValue<string>() == "a",
            "mcp: an unknown protocol version gets the newest one this server speaks");
        Check(await Mcp("""{"jsonrpc":"2.0","method":"notifications/initialized"}""") == null, "mcp: a notification gets no reply");
        var listed = (await Mcp("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""))?["result"]?["tools"]?.AsArray();
        Check(listed?.Count == 9 && listed.All(t => t!["inputSchema"]?["type"]?.GetValue<string>() == "object" && t["description"] != null) &&
              listed.Count(t => t!["annotations"]!["readOnlyHint"]!.GetValue<bool>()) == 8,
            $"mcp: {listed?.Count} tools are listed, all read-only but end_process");
        var called = await Mcp("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"top_processes","arguments":{"count":"3","sort":"memory"}}}""");
        Check(got is { Command: "top", Count: 3, Sort: "memory" } && called?["result"]?["isError"]?.GetValue<bool>() == false &&
              called["result"]?["content"]?[0]?["text"]?.GetValue<string>() == """{"ok":true}""",
            "mcp: a tool call reaches the app as a question and its answer comes back as text");
        await Mcp("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"end_process","arguments":{"id":"12:34","reason":"test","tree":true}}}""");
        Check(got is { Command: "end", Id: "12:34", Reason: "test", Tree: true, Pid: null }, "mcp: end_process passes the id, the reason and the tree flag, and no bare PID");
        Check((await Mcp("""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"format_disk"}}"""))?["error"]?["code"]?.GetValue<int>() == -32602 &&
              (await Mcp("""{"jsonrpc":"2.0","id":6,"method":"resources/list"}"""))?["error"]?["code"]?.GetValue<int>() == -32601 &&
              (await Mcp("{not json"))?["error"]?["code"]?.GetValue<int>() == -32700,
            "mcp: an unknown tool, an unknown method and broken JSON each get the right error");
        var wire = new StringWriter();
        await mcp.RunAsync(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}\n\n{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}\n"), wire);
        Check(wire.ToString().Trim() == """{"jsonrpc":"2.0","id":7,"result":{}}""", "mcp: over a stream, one line in gives one line out: " + wire.ToString().Trim());

        // Malformed messages (review W1): each is answered with an error, and none takes the server down.
        int? Code(JsonObject? reply) => reply?["error"]?["code"]?.GetValue<int>();
        var twiceMethod = await Mcp("""{"jsonrpc":"2.0","id":1,"method":"ping","method":"ping"}""");
        var twiceId = await Mcp("""{"jsonrpc":"2.0","id":3,"id":4,"method":"ping"}""");
        Check(Code(twiceMethod) == -32600 && twiceMethod!["id"] == null && Code(twiceId) == -32600,
            "mcp: a message with a key written twice is answered as an invalid request");
        var objectId = await Mcp("""{"jsonrpc":"2.0","id":{"x":1},"method":"ping"}""");
        var boolId = await Mcp("""{"jsonrpc":"2.0","id":true,"method":"ping"}""");
        Check(Code(objectId) == -32600 && objectId!["id"] == null && Code(boolId) == -32600,
            "mcp: an id that is not a string or a number is refused, not echoed back");
        var nullId = await Mcp("""{"jsonrpc":"2.0","id":null,"method":"ping"}""");
        var noMethod = await Mcp("""{"jsonrpc":"2.0","id":8}""");
        Check(nullId?["result"] != null && nullId.ContainsKey("id") && nullId["id"] == null &&
              Code(noMethod) == -32600 && noMethod!["id"]?.GetValue<int>() == 8,
            "mcp: a null id is answered with a null id, and a request without a method gets an error with its id");
        Check(Code(await Mcp("""[{"jsonrpc":"2.0","id":1,"method":"ping"}]""")) == -32600 && Code(await Mcp("null")) == -32600,
            "mcp: a batch or a bare value is an invalid request");
        var missingParams = await Mcp("""{"jsonrpc":"2.0","id":10,"method":"tools/call"}""");
        Check(Code(missingParams) == -32602, "mcp: a tool call without params gets an error");
        var rough = new StringWriter();
        string stream =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"method\":\"ping\"}\r\n" +
            new string('x', McpServer.MaxLineChars + 10) + "\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"ping\"}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"ping\"}";   // the last line has no line feed
        Exception? fell = null;
        try { await mcp.RunAsync(new StringReader(stream), rough); } catch (Exception ex) { fell = ex; }
        var replies = rough.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Check(fell == null && replies.Count == 4 && replies.Count(r => Code(r) == -32600) == 2 &&
              replies.Where(r => r["result"] != null).Select(r => r["id"]!.GetValue<int>()).Order().SequenceEqual([5, 6]),
            $"mcp: over a stream, a key written twice and an oversized line are answered and the rest still is ({replies.Count} replies{(fell == null ? "" : ", " + fell.GetType().Name)})");

        // The console program's own side: the user's switch, and measuring alone when the app is not running.
        string agentDir = Path.Combine(AppContext.BaseDirectory, "check-agent");
        if (Directory.Exists(agentDir)) Directory.Delete(agentDir, recursive: true);
        using (var client = new AgentClient(agentDir))
        {
            var refused = await client.AskAsync(new AgentRequest { Command = "top" });
            Check(refused["error"]?.GetValue<string>() == AgentQueries.OffMessage, "console program: answers nothing until the user turns agent access on");
            closed.Save(DataFolder.SettingsPath(agentDir));
            var alone = await client.AskAsync(new AgentRequest { Command = "top", Count = 3 });
            Check(alone["rows"]?.AsArray().Count == 3 && alone["rows"]![0]!["cores"] != null, "console program: without the app it measures by itself");
            var noEnd = await client.AskAsync(new AgentRequest { Command = "end", Id = selfId, Reason = "test" });
            Check(noEnd.ContainsKey("error") && !noEnd.ContainsKey("confirmed"), "console program: without the app nothing can be ended: " + noEnd["error"]);
            using var app = new AgentPipeServer(AgentPipe.Name(agentDir), r => Task.FromResult(new JsonObject { ["from"] = "app" }));
            app.Start();
            Thread.Sleep(300);
            Check((await client.AskAsync(new AgentRequest { Command = "top" }))["from"]?.GetValue<string>() == "app", "console program: with the app running, the app answers");
        }
        Thread.Sleep(300);
        // An app that keeps an end request waiting (the user has not answered): the agent gives up on it (review A6).
        using (var client = new AgentClient(agentDir))
        using (var slowApp = new AgentPipeServer(AgentPipe.Name(agentDir), async r => { await Task.Delay(30_000); return new JsonObject { ["late"] = true }; }))
        {
            slowApp.Start();
            Thread.Sleep(300);
            using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var waited = Stopwatch.StartNew();
            Exception? stopped = null;
            try { await client.AskAsync(new AgentRequest { Command = "end", Id = selfId, Reason = "test" }, giveUp.Token); }
            catch (Exception ex) { stopped = ex; }
            Check(stopped is OperationCanceledException && waited.ElapsedMilliseconds < 3000,
                $"console program: a cancelled call to the app stops waiting at once ({waited.ElapsedMilliseconds} ms, not up to 75 s)");
        }
        Directory.Delete(agentDir, recursive: true);

        // MCP cancellation: the request stops waiting and gets no answer; anything else still does.
        var sawCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellable = new McpServer(AgentTools.All, async (r, cancel) =>
        {
            try { await Task.Delay(30_000, cancel); }
            catch (OperationCanceledException) { sawCancel.TrySetResult(true); throw; }
            return new JsonObject { ["late"] = true };
        }, "1.2.3");
        var cancelWire = new StringWriter();
        var cancelWatch = Stopwatch.StartNew();
        await cancellable.RunAsync(new StringReader(
            """{"jsonrpc":"2.0","id":"end-1","method":"tools/call","params":{"name":"end_process","arguments":{"id":"1:2","reason":"x"}}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"end-1","reason":"user stopped it"}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":99}}""" + "\n" +
            """{"jsonrpc":"2.0","method":"notifications/cancelled"}""" + "\n" +
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""" + "\n"), cancelWire);
        var cancelReplies = cancelWire.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Check(sawCancel.Task.IsCompleted && cancelWatch.ElapsedMilliseconds < 5000 && cancelReplies.Length == 1 &&
              cancelReplies[0] == """{"jsonrpc":"2.0","id":2,"result":{}}""",
            $"mcp: a cancelled tool call stops waiting and gets no answer; unknown or empty cancellations are passed over ({cancelWatch.ElapsedMilliseconds} ms, {cancelReplies.Length} reply)");
    }

    // --- 4d. Settings are written whole or not at all (review W3) ------------------------------------
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "check-settings");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        string file = DataFolder.SettingsPath(dir);
        var wanted = new AppSettings { RunawayCores = 2.5, IgnoreNames = { "game.exe" } };
        // What a crash during an earlier save leaves behind: a half-written temporary file.
        Directory.CreateDirectory(dir);
        File.WriteAllText(file + ".tmp", "{\"RunawayCo");
        wanted.Save(file);
        var back = AppSettings.Load(file, out bool unreadable);
        Check(!unreadable && back.RunawayCores == 2.5 && back.IgnoreNames.SequenceEqual(["game.exe"]) && !File.Exists(file + ".tmp"),
            "settings: saved through a temporary file that is swapped in, a leftover one is replaced");
        // A reader (the console program, an editor) holding the file blocks the swap: the save waits it out.
        foreach (var (share, cores) in new[] { (FileShare.ReadWrite | FileShare.Delete, 3.0), (FileShare.Read, 4.0) })
        {
            var holder = new FileStream(file, FileMode.Open, FileAccess.Read, share);
            string before = new StreamReader(holder).ReadToEnd();
            var release = Task.Run(() => { Thread.Sleep(200); holder.Dispose(); });
            new AppSettings { RunawayCores = cores }.Save(file);
            release.Wait();
            Check(before.Contains("RunawayCores") && AppSettings.Load(file).RunawayCores == cores && !File.Exists(file + ".tmp"),
                $"settings: a save blocked for a moment by a reader ({share}) is retried, and the reader saw a whole file");
        }

        File.WriteAllText(file, "{\"RunawayCores\": 1.5, \"IgnoreNames\": [\"keep.exe\"");   // cut short
        var fallback = AppSettings.Load(file, out unreadable);
        AppSettings.KeepUnreadable(file);
        Check(unreadable && fallback.RunawayCores == new AppSettings().RunawayCores && File.Exists(file + ".bad") &&
              File.ReadAllText(file + ".bad").Contains("keep.exe"),
            "settings: a file that cannot be read gives defaults and is kept as settings.json.bad");
        AppSettings.Load(Path.Combine(dir, "missing.json"), out bool missingUnreadable);
        Check(!missingUnreadable, "settings: no file at all is not counted as unreadable");
        Directory.Delete(dir, recursive: true);
    }

    // --- 4e. Robustness: deep trees, caches, folder spellings, rate limit, startup command ------------
    {
        // A made-up chain of 20,000 nested processes (review W6), walked on a thread with a small stack.
        const int deep = 20_000;
        long born = DateTime.UtcNow.AddHours(-1).ToFileTimeUtc();
        var chain = Enumerable.Range(1, deep).Select(i => new ProcInfo
        {
            Pid = 100_000 + i, ParentPid = i == 1 ? 0 : 100_000 + i - 1, Name = "nested.exe", CreateFileTime = born + i,
            CpuTicks = 0, WorkingSet = 0, PrivateWorkingSet = 1 << 20, Threads = 1, Handles = 1,
        }).Reverse().ToList();
        List<ViewRow>? deepTree = null;
        Exception? deepFailure = null;
        var walker = new Thread(() =>
        {
            try { deepTree = ProcessViews.Tree(chain, (a, b) => b.Totals.Memory.CompareTo(a.Totals.Memory)); }
            catch (Exception ex) { deepFailure = ex; }
        }, maxStackSize: 256 * 1024);
        walker.Start();
        walker.Join();
        Check(deepFailure == null && deepTree?.Count == deep && deepTree[0].Depth == 0 && deepTree[^1].Depth == deep - 1 &&
              deepTree[0].Totals.Count == deep && deepTree[0].Totals.Memory == (long)deep << 20 && deepTree[^1].Totals.Count == 1,
            $"tree view: a chain of {deep} nested processes is walked without running out of stack ({deepTree?.Count} rows)");

        // Rate limit on agents' questions (review W5): a burst, then a steady few a second.
        var rate = new AgentRateLimit();
        int burst = Enumerable.Range(0, 50).Count(_ => rate.TryTake(1_000));
        bool waitsThenAnswers = !rate.TryTake(1_100) && rate.TryTake(1_300);
        int afterPause = Enumerable.Range(0, 50).Count(_ => rate.TryTake(60_000));
        Check(burst == AgentRateLimit.Burst && waitsThenAnswers && afterPause == AgentRateLimit.Burst,
            $"agent: {burst} questions at once are answered, then about {AgentRateLimit.PerSecond:0} a second; a pause never saves up more than the burst");

        // The description cache stops growing (review W7).
        for (int i = 0; i < ProcessDetails.MaxDescribed + 200; i++) ProcessDetails.Describe($@"C:\nikomi-check-missing\tool-{i}.exe");
        Check(ProcessDetails.DescribedCount <= ProcessDetails.MaxDescribed,
            $"descriptions: the cache holds at most {ProcessDetails.MaxDescribed} executables ({ProcessDetails.DescribedCount} after {ProcessDetails.MaxDescribed + 200})");

        // One folder, many spellings, one id (review W8).
        string folder = Path.Combine(AppContext.BaseDirectory, "check-folder", "Data Folder");
        string link = Path.Combine(AppContext.BaseDirectory, "check-folder", "link");
        Directory.CreateDirectory(folder);
        string id = DataFolder.InstanceId(folder);
        var spellings = new List<(string How, string Path)>
        {
            ("upper case", folder.ToUpperInvariant()),
            ("a trailing separator", folder + @"\"),
            ("a \\\\?\\ prefix", @"\\?\" + folder),
            ("a detour through ..", Path.Combine(folder, "..", "Data Folder")),
        };
        var shortName = new System.Text.StringBuilder(1024);
        if (GetShortPathName(folder, shortName, 1024) is > 0 and < 1024 && shortName.ToString() != folder)
            spellings.Add(("its short 8.3 name", shortName.ToString()));
        else Console.WriteLine("      (8.3 names are off on this drive: that spelling is not checked)");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            mklink.WaitForExit();
        if (Directory.Exists(link)) spellings.Add(("a junction", link));
        else Console.WriteLine("      (no junction could be made: that spelling is not checked)");
        var differ = spellings.Where(s => DataFolder.InstanceId(s.Path) != id).Select(s => s.How).ToList();
        Check(differ.Count == 0, $"data folder: {spellings.Count} other spellings of one folder give the same id ({string.Join(", ", spellings.Select(s => s.How))})" +
                                 (differ.Count > 0 ? "; differs for " + string.Join(", ", differ) : ""));
        Check(DataFolder.InstanceId(folder + "-other") != id && DataFolder.FinalPath(Path.Combine(folder, "not yet")) == Path.Combine(folder, "not yet"),
            "data folder: another folder gets another id, and a folder that does not exist yet keeps its path");
        if (Directory.Exists(link)) Directory.Delete(link);   // removes the junction only, not what it points to
        Directory.Delete(Path.Combine(AppContext.BaseDirectory, "check-folder"), recursive: true);

        // Start with Windows keeps the data folder (review W10), quoted so Windows reads it back whole.
        string Arg(string command, int index)
        {
            IntPtr argv = CommandLineToArgvW(command, out int argc);
            try { return index < argc ? System.Runtime.InteropServices.Marshal.PtrToStringUni(System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, index * IntPtr.Size))! : ""; }
            finally { LocalFree(argv); }
        }
        string plain = StartupRegistration.Command(@"C:\Program Files\Nikomi\Nikomi.exe");
        string withDir = StartupRegistration.Command(@"C:\Program Files\Nikomi\Nikomi.exe", @"D:\My Data\Nikomi");
        string atRoot = StartupRegistration.Command(@"C:\Program Files\Nikomi\Nikomi.exe", @"D:\");
        Check(plain == "\"C:\\Program Files\\Nikomi\\Nikomi.exe\" --background" &&
              Arg(withDir, 2) == "--data-dir" && Arg(withDir, 3) == @"D:\My Data\Nikomi" && Arg(atRoot, 3) == @"D:\",
            $"start with Windows: a data folder other than the default is passed on: {withDir}");
        Check(StartupRegistration.DataDirArgument(DataFolder.Default) == null &&
              StartupRegistration.DataDirArgument(DataFolder.Default.ToUpperInvariant() + @"\") == null &&
              StartupRegistration.DataDirArgument(@"D:\My Data\Nikomi") == @"D:\My Data\Nikomi",
            "start with Windows: the default folder needs no argument, however it is written");
        const string dirKey = @"Software\Nikomi.Check\RunDir";
        StartupRegistration.Enable(@"C:\Example\Nikomi.exe", dirKey, @"D:\Other");
        Check(StartupRegistration.IsEnabled(@"C:\Example\Nikomi.exe", dirKey, @"D:\Other") &&
              !StartupRegistration.IsEnabled(@"C:\Example\Nikomi.exe", dirKey),
            "start with Windows: an entry for another folder is not taken for the default folder's");
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Nikomi.Check", throwOnMissingSubKey: false);

        // One parser for both programs' command lines (review A5).
        var cli = Arguments.Parse(["top", "--count", "7", "--tree", "--data-dir", @"D:\x", "--count", "nine", "extra", "--what"],
            switches: ["--tree"], options: ["--count", "--data-dir"], command: true);
        Check(cli.Command == "top" && cli.Has("--tree") && cli.Value("--data-dir") == @"D:\x" && cli.Number("--count") == null &&
              cli.Value("--count") == "nine" && cli.Unknown.SequenceEqual(["extra", "--what"]),
            "arguments: switches, options with values, the last of a repeated option, and what is unknown are told apart");
        var appArgs = Arguments.Parse(["start", "--background", "--data-dir"], switches: ["--background"], options: ["--data-dir"], command: false);
        Check(appArgs.Command == null && appArgs.Has("--background") && appArgs.Value("--data-dir") == null && appArgs.Unknown.SequenceEqual(["start"]),
            "arguments: without a command word a bare word is unknown, and an option at the end has no value");
    }

    // --- 5. Processes Windows depends on are refused -------------------------------------------------
    {
        var mon3 = new ProcessMonitor(source);
        mon3.Update(sampler.Take(), settings);
        var system = mon3.ByPid[4];
        var refused = ProcessKiller.End([system]);
        Check(!refused[0].Ended && ProcessKiller.IsCritical(system), "the System process is refused: " + refused[0].Message);
        // Read-only, and not a pass/fail: without admin rights most of them cannot even be opened.
        var critical = mon3.ByKey.Values.Where(p => p.Pid > 4 && ProcessKiller.IsCritical(p)).Select(p => p.Name).Distinct().ToList();
        Console.WriteLine($"      marked critical by Windows and visible from here: {(critical.Count == 0 ? "none" : string.Join(", ", critical))}");
    }
}
finally
{
    // Safety net: end anything this check started that is still running.
    var mon = new ProcessMonitor(source);
    mon.Update(sampler.Take(), settings);
    var stray = Mine(mon);
    foreach (var p in stray) { try { using var pr = Process.GetProcessById(p.Pid); pr.Kill(); } catch { } }
    Check(stray.Count == 0, $"no test processes left over ({stray.Count} cleaned up)");
}

Console.WriteLine(failed == 0 ? "All checks passed" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;

[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint length);

[System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
static extern IntPtr CommandLineToArgvW(string commandLine, out int count);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
static extern IntPtr LocalFree(IntPtr memory);

/// <summary>A made-up PC: a fixed number of cores, and nothing read about any process.</summary>
sealed class FakeSource(int cores = 8) : SystemSource
{
    public Dictionary<int, (string? CommandLine, string? ExePath)> Details { get; } = new();
    public override (string? CommandLine, string? ExePath) ReadDetails(int pid) => Details.GetValueOrDefault(pid);
    public override (string? Description, string? Company) Describe(string? exePath) => (null, null);
    public override Dictionary<int, string> ReadServices() => new();
    public override int LogicalCores => cores;
}

/// <summary>Stands in for the window's confirmation dialog.</summary>
sealed class FakeDialog : IEndDialog
{
    public bool Ready { get; set; } = true;
    public bool IsBusy { get; set; }
    public int Asked { get; set; }
    public EndPlan? Shown { get; private set; }
    public EndReport? Report { get; private set; }
    /// <summary>What the user does with the dialog: true when they press the end button.</summary>
    public Func<EndPlan, CancellationToken, Task<bool>> Answer { get; set; } = (_, _) => Task.FromResult(false);

    public Task<bool> WaitUntilReadyAsync(TimeSpan? limit) => Task.FromResult(Ready);

    public Task<bool> AskAsync(EndPlan plan, CancellationToken close)
    {
        Asked++;
        Shown = plan;
        return Answer(plan, close);
    }

    public void ShowEnded(EndReport report) => Report = report;
}
