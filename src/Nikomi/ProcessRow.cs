using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nikomi.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Nikomi;

/// <summary>
/// One row of the process list: a process, a process standing for its tree, or a group of processes with
/// one name. Kept per key so selection survives refreshes.
/// </summary>
public sealed class ProcessRow : INotifyPropertyChanged
{
    public ProcessRow(string key) { Key = key; }

    public string Key { get; }
    // Internal on purpose: the XAML compiler generates type info for public properties.
    internal ViewRow View { get; private set; } = null!;
    internal ProcInfo? Proc => View.Proc;
    internal int StatusRank { get; private set; }

    private string _name = "", _pid = "", _status = "", _cores = "", _cpu = "", _started = "", _memory = "", _io = "";
    private string _threads = "", _handles = "", _chain = "", _description = "", _cmd = "";
    private double _coresBar;
    private Thickness _nameMargin = new(8, 0, 8, 0);
    private Visibility _statusVisibility = Visibility.Collapsed;
    private Brush? _statusBrush;

    public string Name { get => _name; private set => Set(ref _name, value); }
    public Thickness NameMargin { get => _nameMargin; private set => Set(ref _nameMargin, value); }
    public string PidText { get => _pid; private set => Set(ref _pid, value); }
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public Visibility StatusVisibility { get => _statusVisibility; private set => Set(ref _statusVisibility, value); }
    public Brush? StatusBrush { get => _statusBrush; private set => Set(ref _statusBrush, value); }
    public string CoresText { get => _cores; private set => Set(ref _cores, value); }
    public double CoresBar { get => _coresBar; private set => Set(ref _coresBar, value); }
    public string CpuTimeText { get => _cpu; private set => Set(ref _cpu, value); }
    public string StartedText { get => _started; private set => Set(ref _started, value); }
    public string MemoryText { get => _memory; private set => Set(ref _memory, value); }
    public string IoText { get => _io; private set => Set(ref _io, value); }
    public string ThreadsText { get => _threads; private set => Set(ref _threads, value); }
    public string HandlesText { get => _handles; private set => Set(ref _handles, value); }
    public string ChainText { get => _chain; private set => Set(ref _chain, value); }
    public string DescriptionText { get => _description; private set => Set(ref _description, value); }
    public string CommandLine { get => _cmd; private set => Set(ref _cmd, value); }

    public void Update(ViewRow view, ProcessMonitor monitor)
    {
        View = view;
        var p = view.Proc;
        var t = view.Totals;

        Name = view.IsGroup ? $"{view.Name}   ×{t.Count}" : view.Name;
        NameMargin = new Thickness(8 + 12 * Math.Min(view.Depth, 8), 0, 8, 0);
        PidText = p?.Pid.ToString() ?? "";
        CoresText = t.Cores is { } c ? c.ToString("0.00") : "";
        CoresBar = Math.Clamp(t.Cores ?? 0, 0, 1);
        CpuTimeText = FormatCpuTime(t.CpuTicks);
        MemoryText = FormatMb(t.Memory);
        IoText = FormatRate(t.IoPerSec);
        ThreadsText = t.Threads.ToString("N0");
        HandlesText = t.Handles.ToString("N0");
        // "G" is the user's own short date and long time, as set in Windows.
        StartedText = p?.Created is { } d ? d.ToString("G") : "";
        ChainText = p != null ? ProcessTree.Ancestors(p, monitor.ByPid).Format() : "";
        DescriptionText = Describe(p ?? view.Members![0], monitor, group: view.IsGroup);
        CommandLine = p?.CommandLine ?? "";

        string status;
        string? brushKey;
        if (p != null)
        {
            if (p.Runaway) { status = $"Runaway {p.HotMinutes:0}m"; brushKey = "SystemFillColorCriticalBackgroundBrush"; StatusRank = 4; }
            else if (p.Ignored) { status = $"Not flagged {p.HotMinutes:0}m"; brushKey = "ControlFillColorSecondaryBrush"; StatusRank = 2; }
            else if (p.HotMinutes is { } h) { status = $"Hot {h:0.0}m"; brushKey = "SystemFillColorCautionBackgroundBrush"; StatusRank = 3; }
            else { status = ""; brushKey = null; StatusRank = 0; }
        }
        else
        {
            int runaway = view.Members!.Count(m => m.Runaway), hot = view.Members!.Count(m => m.HotMinutes != null && !m.Runaway && !m.Ignored);
            if (runaway > 0) { status = $"{runaway} runaway"; brushKey = "SystemFillColorCriticalBackgroundBrush"; StatusRank = 4; }
            else if (hot > 0) { status = $"{hot} hot"; brushKey = "SystemFillColorCautionBackgroundBrush"; StatusRank = 3; }
            else { status = ""; brushKey = null; StatusRank = 0; }
        }
        StatusText = status;
        StatusVisibility = brushKey == null ? Visibility.Collapsed : Visibility.Visible;
        if (brushKey != null && Application.Current.Resources.TryGetValue(brushKey, out var b)) StatusBrush = b as Brush;
        Summary = Summarize();
    }

    private static string Describe(ProcInfo p, ProcessMonitor monitor, bool group)
    {
        if (!group && monitor.ServicesOf(p.Pid) is { } services) return "Services: " + services;
        return p.Description ?? "";
    }

    public static string FormatCpuTime(long ticks)
    {
        var t = TimeSpan.FromTicks(ticks);
        return t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours:00}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Hours}:{t.Minutes:00}:{t.Seconds:00}";
    }

    public static string FormatMb(long bytes) => (bytes / 1048576.0).ToString("N0") + " MB";

    /// <summary>Bytes per second as text; empty when nothing moved.</summary>
    public static string FormatRate(double? bytesPerSec) => bytesPerSec switch
    {
        null or < 1 => "",
        < 1024 => $"{bytesPerSec:0} B/s",
        < 1048576 => $"{bytesPerSec / 1024:0} KB/s",
        _ => $"{bytesPerSec / 1048576:0.0} MB/s",
    };

    private string _summary = "";

    /// <summary>What Narrator reads for the row. Without it, it would read the class name.</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    private string Summarize()
    {
        var parts = new List<string> { Name };
        if (PidText.Length > 0) parts.Add("PID " + PidText);
        if (StatusText.Length > 0) parts.Add(StatusText);
        if (CoresText.Length > 0) parts.Add(CoresText + " cores");
        parts.Add(MemoryText);
        if (DescriptionText.Length > 0) parts.Add(DescriptionText);
        return string.Join(", ", parts);
    }

    public override string ToString() => Summary;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>One line of the History view.</summary>
public sealed class HistoryRow
{
    public HistoryRow(HistoryEntry e)
    {
        TimeText = e.TimeUtc.ToLocalTime().ToString("G");
        LoadText = e.Load is { } l ? $"{l * 100:0}%" : "";
        LoadValue = (e.Load ?? 0) * 100;
        TopText = e.Top.Count == 0
            ? "nothing above " + UsageHistory.Floor.ToString("0.00") + " cores"
            : string.Join("    ·    ", e.Top.Select(p => $"{p.Name} {p.Cores:0.00}"));
    }

    public string TimeText { get; }
    public string LoadText { get; }
    public double LoadValue { get; }
    public string TopText { get; }
    public string Summary => $"{TimeText}, total CPU {LoadText}, busiest: {TopText}";

    public override string ToString() => Summary;
}
