using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Nikomi.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace Nikomi;

public sealed partial class MainWindow : Window, IEndDialog
{
    private const StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;

    private readonly AppController _c;
    private readonly Dictionary<string, ProcessRow> _rowsByKey = new();
    private readonly Dictionary<string, ProcessRow> _fresh = new();   // rows brought up to date in the current refresh
    private readonly Dictionary<string, Button> _headers = new();
    private readonly IntPtr _hwnd;
    private ScrollViewer? _listScroll;
    private string _view = "top";
    private string _sort = "Cores";
    private bool _descending = true;
    private string? _selectedKey;
    private bool _scrollToSelected;
    private bool _syncing;
    private bool _dialogOpen;
    private string _historyShown = "";

    public ObservableCollection<ProcessRow> Rows { get; } = new();

    /// <summary>Every end goes through it: the user's own and an AI agent's requests alike.</summary>
    internal EndConfirmation Confirmation { get; }

    public MainWindow(AppController controller)
    {
        _c = controller;
        Confirmation = new EndConfirmation(_c.Monitor, this, () => _c.Settings.AgentAccess);
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        string assets = Path.Combine(AppContext.BaseDirectory, "Assets");
        AppWindow.SetIcon(Path.Combine(assets, "Nikomi.ico"));
        TitleIcon.Source = new BitmapImage(new Uri(Path.Combine(assets, "Nikomi.png")));

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = Math.Min((int)(1440 * scale), area.Width), h = Math.Min((int)(900 * scale), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the cards and the details pane no longer fit.
            presenter.PreferredMinimumWidth = Math.Min((int)(940 * scale), area.Width);
            presenter.PreferredMinimumHeight = Math.Min((int)(640 * scale), area.Height);
        }
        ApplyAlwaysOnTop();

        // Closing only hides the window; the app keeps watching from the tray. Exit is in the tray menu.
        AppWindow.Closing += (s, e) =>
        {
            if (_c.IsExiting) return;
            e.Cancel = true;
            s.Hide();
            _c.WindowStateChanged();
            _c.WindowHidden();
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange || e.DidSizeChange) _c.WindowStateChanged();
        };

        foreach (var child in HeaderRow.Children)
            if (child is Button b && b.Tag is string tag) _headers[tag] = b;
        UpdateHeaderArrows();

        ProcList.ContextFlyout = BuildProcessMenu();
        MoreButton.Flyout = BuildProcessMenu();
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, () => _c.Sample());
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => FilterBox.Focus(FocusState.Keyboard));

        _c.Sampled += Refresh;
        _c.LiveSampled += UpdateLive;
        HistoryGraph.Hovered += HistoryGraph_Hovered;
        SetText(HistoryHover, HistoryHoverHint);
        _ready = true;
    }

    private readonly bool _ready;

    /// <summary>Keeps the window above other apps' windows, or not, as the settings say.</summary>
    private void ApplyAlwaysOnTop()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _c.Settings.AlwaysOnTop;
    }

    public bool IsOpen =>
        AppWindow.IsVisible && (AppWindow.Presenter as OverlappedPresenter)?.State != OverlappedPresenterState.Minimized;

    public void ShowAndActivate(string? selectKey)
    {
        if (selectKey != null)
        {
            _selectedKey = selectKey;
            _scrollToSelected = true;
            FilterBox.Text = "";
            if (_view is not ("top" or "hot")) SelectView("top");
        }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(_hwnd);
        _c.WindowStateChanged();
        Refresh();
    }

    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, e) => { action(); e.Handled = true; };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ---------------------------------------------------------------------------------------------
    // Refresh

    private void Refresh()
    {
        UpdateStats();
        if (PauseButton.IsChecked == true || !IsOpen) return;

        bool history = _view == "history";
        ProcPanel.Visibility = history ? Visibility.Collapsed : Visibility.Visible;
        HistoryPanel.Visibility = history ? Visibility.Visible : Visibility.Collapsed;
        if (history)
        {
            RefreshHistory();
            UpdateDetails();
            return;
        }
        string? note = _view switch
        {
            "tree" => "Tree: each row's cores, CPU time, memory, I/O, threads and handles include everything that process started.",
            "names" => "By name: each row adds up every process with that name. Select one to list its processes.",
            _ => null,
        };
        ViewNote.Visibility = note == null ? Visibility.Collapsed : Visibility.Visible;
        if (note != null) SetText(ViewNote, note);

        var mon = _c.Monitor;
        mon.RefreshServices();
        var all = mon.ByKey.Values;
        string filter = FilterBox.Text.Trim();
        if (filter.Length > 0) foreach (var p in all) mon.EnsureDetails(p);
        bool Matches(ProcInfo p) =>
            filter.Length == 0 ||
            p.Name.Contains(filter, IgnoreCase) ||
            (p.CommandLine?.Contains(filter, IgnoreCase) ?? false) ||
            (p.Description?.Contains(filter, IgnoreCase) ?? false) ||
            (mon.ServicesOf(p.Pid)?.Contains(filter, IgnoreCase) ?? false) ||
            p.Pid.ToString() == filter;

        _fresh.Clear();
        List<ProcessRow> desired;
        switch (_view)
        {
            case "tree":
            {
                // Siblings are ordered by the chosen column; the tree itself decides the rest.
                var tree = ProcessViews.Tree(all.Where(p => p.Pid != 0), (a, b) => CompareRows(RowFor(a), RowFor(b)));
                if (filter.Length > 0) tree = ProcessViews.KeepWithAncestors(tree, v => Matches(v.Proc!));
                desired = tree.Select(RowFor).ToList();
                break;
            }
            case "names":
                desired = ProcessViews.ByName(all.Where(p => p.Pid != 0 && Matches(p))).Select(RowFor).ToList();
                desired.Sort(CompareRows);
                break;
            default:
            {
                IEnumerable<ProcInfo> procs = _view switch
                {
                    "all" => all,
                    "orphans" => all.Where(p => p.Orphan),
                    "hot" => all.Where(p => mon.IsHot(p.Key)),
                    _ => all.Where(p => p.Pid != 0).OrderByDescending(p => p.Cores ?? -1).Take(_c.Settings.TopCount)
                            .Concat(all.Where(p => mon.IsHot(p.Key))).Distinct(),
                };
                desired = procs.Where(Matches).Select(p => RowFor(ViewRow.Flat(p))).ToList();
                desired.Sort(CompareRows);
                break;
            }
        }
        var keep = new HashSet<string>(desired.Count);
        foreach (var row in desired) keep.Add(row.Key);

        _syncing = true;
        try
        {
            for (int i = Rows.Count - 1; i >= 0; i--) if (!keep.Contains(Rows[i].Key)) Rows.RemoveAt(i);
            foreach (var k in _rowsByKey.Keys.Where(k => !keep.Contains(k)).ToList()) _rowsByKey.Remove(k);
            for (int i = 0; i < desired.Count; i++)
            {
                if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i])) continue;
                int j = Rows.IndexOf(desired[i]);
                if (j < 0) Rows.Insert(i, desired[i]); else Rows.Move(j, i);
            }
            if (_selectedKey != null && _rowsByKey.TryGetValue(_selectedKey, out var sel))
            {
                if (!ReferenceEquals(ProcList.SelectedItem, sel)) ProcList.SelectedItem = sel;
                if (_scrollToSelected) { ProcList.ScrollIntoView(sel); _scrollToSelected = false; }
            }
            else
            {
                ProcList.SelectedItem = null;
                if (_selectedKey != null && !mon.ByKey.ContainsKey(_selectedKey)) _selectedKey = null;
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateDetails();
    }

    /// <summary>The row for a line of the list, brought up to date once per refresh.</summary>
    private ProcessRow RowFor(ViewRow view)
    {
        if (_fresh.TryGetValue(view.Key, out var row)) return row;
        if (!_rowsByKey.TryGetValue(view.Key, out row)) _rowsByKey[view.Key] = row = new ProcessRow(view.Key);
        var mon = _c.Monitor;
        mon.EnsureDetails(view.Proc ?? view.Members![0]);
        row.Update(view, mon);
        _fresh[view.Key] = row;
        return row;
    }

    private int CompareRows(ProcessRow a, ProcessRow b)
    {
        Totals x = a.View.Totals, y = b.View.Totals;
        int r = _sort switch
        {
            "Status" => a.StatusRank.CompareTo(b.StatusRank),
            "Name" => string.Compare(a.View.Name, b.View.Name, IgnoreCase),
            "Pid" => (a.Proc?.Pid ?? 0).CompareTo(b.Proc?.Pid ?? 0),
            "CpuTime" => x.CpuTicks.CompareTo(y.CpuTicks),
            "Memory" => x.Memory.CompareTo(y.Memory),
            "Io" => (x.IoPerSec ?? -1).CompareTo(y.IoPerSec ?? -1),
            "Threads" => x.Threads.CompareTo(y.Threads),
            "Handles" => x.Handles.CompareTo(y.Handles),
            "Started" => (a.Proc?.CreateFileTime ?? 0).CompareTo(b.Proc?.CreateFileTime ?? 0),
            "Chain" => string.Compare(a.ChainText, b.ChainText, IgnoreCase),
            "Description" => string.Compare(a.DescriptionText, b.DescriptionText, IgnoreCase),
            "Command" => string.Compare(a.CommandLine, b.CommandLine, IgnoreCase),
            // Rounded, so rows showing the same value do not swap places on every refresh.
            _ => Math.Round(x.Cores ?? -1, 2).CompareTo(Math.Round(y.Cores ?? -1, 2)),
        };
        if (_descending) r = -r;
        if (r == 0) r = string.Compare(a.View.Name, b.View.Name, IgnoreCase);
        return r != 0 ? r : (a.Proc?.Pid ?? 0).CompareTo(b.Proc?.Pid ?? 0);
    }

    // ---------------------------------------------------------------------------------------------
    // Graphs: the live one in the Total CPU card, the 24-hour one on top of the History view.

    private const string HistoryHoverHint = "Point at the graph to see what was busy then";
    private static readonly GraphTick[] LiveTicks = Enumerable.Range(1, 5).Select(i => new GraphTick(i / 6.0, null)).ToArray();
    private DateTime _historyGraphNowUtc;

    /// <summary>The live reading, measured every second, when there is one; otherwise the list's own figure.</summary>
    private void ShowCpuValue()
    {
        double? load = _c.LiveLoad.Latest ?? _c.Monitor.Load;
        SetText(CpuValue, load is { } l ? $"{l * 100:0.0}%" : "-");
    }

    private void UpdateLive()
    {
        if (!IsOpen) return;
        double now = Environment.TickCount64 / 1000.0;
        LiveGraph.SetData(LoadSeries.Build(_c.LiveLoad.Spans, now - LiveLoad.Span.TotalSeconds, now, tolerance: 0.25), LiveTicks);
        ShowCpuValue();
    }

    private void UpdateHistoryGraph()
    {
        _historyGraphNowUtc = DateTime.UtcNow;
        double span = UsageHistory.Keep.TotalSeconds;
        // Lines follow on from each other exactly while Nikomi runs; a gap means it was not measuring.
        var lines = LoadSeries.Build(LoadSeries.FromHistory(_c.History.Entries, _historyGraphNowUtc), -span, 0, tolerance: 5);
        HistoryGraph.SetData(lines, LoadSeries.HourTicks(_historyGraphNowUtc, UsageHistory.Keep, everyHours: 3));
    }

    private void HistoryGraph_Hovered(double? x)
    {
        HistoryGraph.SetMarker(x);
        if (x is not { } across)
        {
            SetText(HistoryHover, HistoryHoverHint);
            return;
        }
        // The line whose stretch is nearest the pointer, if one is within a minute of it.
        DateTime at = _historyGraphNowUtc - UsageHistory.Keep * (1 - across);
        HistoryEntry? best = null;
        double bestDistance = 60;
        foreach (var e in _c.History.Entries)
        {
            DateTime start = e.TimeUtc.AddSeconds(-e.Seconds);
            double distance = at < start ? (start - at).TotalSeconds : at > e.TimeUtc ? (at - e.TimeUtc).TotalSeconds : 0;
            if (distance <= bestDistance) { best = e; bestDistance = distance; }
        }
        if (best == null)
        {
            SetText(HistoryHover, $"{at.ToLocalTime():t}: not measured (Nikomi was not running or the PC was asleep)");
            return;
        }
        var row = new HistoryRow(best);
        SetText(HistoryHover, $"{best.TimeUtc.ToLocalTime():t}   ·   {row.LoadText} CPU   ·   {row.TopText}");
    }

    private void RefreshHistory()
    {
        var entries = _c.History.Entries;
        string filter = FilterBox.Text.Trim();
        string signature = $"{entries.Count}|{(entries.Count > 0 ? entries[^1].TimeUtc.Ticks : 0)}|{filter}";
        if (signature == _historyShown) return;
        _historyShown = signature;
        UpdateHistoryGraph();

        var rows = new List<HistoryRow>();
        for (int i = entries.Count - 1; i >= 0; i--)   // newest first
        {
            var e = entries[i];
            if (filter.Length > 0 && !e.Top.Any(t => t.Name.Contains(filter, IgnoreCase) || t.Pid.ToString() == filter)) continue;
            rows.Add(new HistoryRow(e));
        }
        HistoryList.ItemsSource = rows;
        HistoryEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetText(HistoryEmpty, entries.Count == 0
            ? "History fills in while Nikomi runs: one line about every half minute, kept for 24 hours."
            : "No line in the last 24 hours has a process that matches the filter.");
    }

    private void UpdateStats()
    {
        var mon = _c.Monitor;
        var s = _c.Settings;
        ShowCpuValue();
        CoresValue.Text = _c.Source.LogicalCores.ToString();
        var up = _c.Source.Uptime;
        UptimeValue.Text = $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes:00}m";
        BootText.Text = "since " + (DateTime.Now - up).ToString("g");
        ProcsValue.Text = mon.ByKey.Count.ToString();
        IntervalText.Text = mon.IntervalSeconds is { } iv ? $"measured over {iv:0.0} s" : "measuring...";
        OrphansValue.Text = mon.OrphanCount.ToString();
        RunawayValue.Text = mon.RunawayCount.ToString();
        RunawayValue.Foreground = mon.RunawayCount > 0 ? Brush("SystemFillColorCriticalBrush") : Brush("TextFillColorPrimaryBrush");
        RunawayRule.Text = $"{s.RunawayCores:0.00}+ cores for {s.RunawayMinutes:0.#}+ min";
        UpdatedText.Text = $"Updated {DateTime.Now:T}" + (PauseButton.IsChecked == true ? "  ·  list paused" : "") +
                           (_c.IsMuted ? "  ·  notifications muted" : "");
    }

    private ProcInfo? SelectedProc() =>
        _selectedKey != null && _c.Monitor.ByKey.TryGetValue(_selectedKey, out var p) ? p : null;

    private ProcessRow? SelectedGroup() =>
        _selectedKey != null && _rowsByKey.TryGetValue(_selectedKey, out var row) && row.View.IsGroup ? row : null;

    private void UpdateDetails()
    {
        var mon = _c.Monitor;
        ProcInfo? p = SelectedProc();
        DetailEmptyPanel.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
        DetailContent.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
        if (p == null)
        {
            var group = _view == "names" ? SelectedGroup() : null;
            GroupButton.Visibility = group == null ? Visibility.Collapsed : Visibility.Visible;
            SetText(DetailEmpty,
                group != null ? $"{group.View.Name}: {group.View.Totals.Count} processes, {group.View.Totals.Cores ?? 0:0.00} cores now, {ProcessRow.FormatMb(group.View.Totals.Memory)} of memory."
                : _view == "history" ? "Each line is the total load over about half a minute and the processes that used the most CPU in it."
                : "Select a process to see its full command line and the processes that started it.");
            return;
        }
        mon.EnsureDetails(p);

        SetText(DetailTitle, p.Name);
        string cores = p.Cores is { } c ? $"{c:0.00} cores now" : "cores not measured yet";
        string io = ProcessRow.FormatRate(p.IoBytesPerSec);
        SetText(DetailSubtitle,
            $"PID {p.Pid}   ·   {cores}   ·   CPU time {ProcessRow.FormatCpuTime(p.CpuTicks)}   ·   " +
            $"{ProcessRow.FormatMb(p.PrivateWorkingSet)} memory ({ProcessRow.FormatMb(p.PrivateBytes)} committed)   ·   " +
            (io.Length > 0 ? $"I/O {io}   ·   " : "") +
            $"{p.Threads:N0} threads   ·   {p.Handles:N0} handles   ·   priority {ProcessKiller.PriorityName(p.BasePriority)}   ·   " +
            $"session {p.SessionId}   ·   started {p.Created:G}");
        var s = _c.Settings;
        (string badge, string? brush) =
            p.Runaway ? ($"Runaway: {s.RunawayCores:0.00}+ cores for {p.HotMinutes:0} min", "SystemFillColorCriticalBackgroundBrush")
            : p.Ignored ? ($"On your never-flag list: {s.RunawayCores:0.00}+ cores for {p.HotMinutes:0} min", "ControlFillColorSecondaryBrush")
            : p.HotMinutes is { } hm ? ($"Hot: {s.RunawayCores:0.00}+ cores for {hm:0.0} min so far", "SystemFillColorCautionBackgroundBrush")
            : p.Orphan ? ("Orphan: the process that started it has exited, which is common and harmless on its own", "ControlFillColorSecondaryBrush")
            : ("", null);
        DetailBadge.Visibility = brush == null ? Visibility.Collapsed : Visibility.Visible;
        if (brush != null) { DetailBadge.Background = Brush(brush); SetText(DetailBadgeText, badge); }

        SetText(DetailPath, p.ExePath ?? "(not available)");
        var what = new List<string>();
        if (p.Description != null) what.Add(p.Description);
        if (p.Company != null) what.Add(p.Company);
        string whatText = what.Count > 0 ? string.Join("   ·   ", what) : "(the file does not say)";
        if (mon.ServicesOf(p.Pid) is { } services) whatText += "\nServices in it: " + services;
        SetText(DetailWhat, whatText);
        SetText(DetailCmd, p.CommandLine is { Length: > 0 } cmd ? cmd
            : "(not available: the process belongs to another user or is protected)");

        var anc = ProcessTree.Ancestors(p, mon.ByPid);
        var sb = new StringBuilder();
        string indent = "";
        if (anc.ExitedParentPid is { } gone) { sb.AppendLine($"PID {gone} (has exited)"); indent = "  "; }
        foreach (var a in anc.Chain)
        {
            mon.EnsureDetails(a);
            sb.AppendLine($"{indent}{a.Name} ({a.Pid})  {a.Created:g}");
            if (a.CommandLine is { Length: > 0 } ac) sb.AppendLine($"{indent}   {Shorten(ac, 110)}");
            indent += "  ";
        }
        sb.Append($"{indent}> {p.Name} ({p.Pid})");
        SetText(DetailChain, sb.ToString());
        int children = ProcessTree.Descendants(p, mon.ByKey.Values).Count;
        SetText(DetailChildren, children == 0 ? "It has no running child processes." : $"It started {children} {(children == 1 ? "process that is" : "processes that are")} still running.");
    }

    // ---------------------------------------------------------------------------------------------
    // Right-click menu and the More button: things to do with the selected process short of ending it.

    private MenuFlyout BuildProcessMenu()
    {
        MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => action();
            return item;
        }

        var copyCommand = Item("Copy command line", () => Copy(SelectedProc()?.CommandLine));
        var copyPath = Item("Copy executable path", () => Copy(SelectedProc()?.ExePath));
        var openLocation = Item("Open file location", OpenFileLocation);
        var priority = new MenuFlyoutSubItem { Text = "Set priority" };
        foreach (var (text, level) in new[]
                 {
                     ("High", PriorityLevel.High), ("Above normal", PriorityLevel.AboveNormal), ("Normal", PriorityLevel.Normal),
                     ("Below normal", PriorityLevel.BelowNormal), ("Low", PriorityLevel.Low),
                 })
            priority.Items.Add(Item(text, () => SetPriority(level, text)));
        var neverFlag = Item("", ToggleNeverFlag);
        var end = Item("End process...", async () => await EndSelectedAsync(tree: false));
        var endTree = Item("End process tree...", async () => await EndSelectedAsync(tree: true));

        var menu = new MenuFlyout();
        foreach (var item in new MenuFlyoutItemBase[]
                 {
                     copyCommand, copyPath, openLocation, new MenuFlyoutSeparator(), priority, neverFlag,
                     new MenuFlyoutSeparator(), end, endTree,
                 })
            menu.Items.Add(item);

        menu.Opening += (_, _) =>
        {
            var p = SelectedProc();
            if (p != null) _c.Monitor.EnsureDetails(p);
            bool user = p is { Pid: > 4 };   // not the idle process or System
            copyCommand.IsEnabled = !string.IsNullOrEmpty(p?.CommandLine);
            copyPath.IsEnabled = openLocation.IsEnabled = !string.IsNullOrEmpty(p?.ExePath);
            priority.IsEnabled = neverFlag.IsEnabled = end.IsEnabled = endTree.IsEnabled = user;
            neverFlag.Text = p == null ? "Never flag as runaway"
                : _c.Settings.IsIgnored(p.Name) ? $"Flag {p.Name} as runaway again"
                : $"Never flag {p.Name} as runaway";
        };
        return menu;
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
    }

    private void OpenFileLocation()
    {
        if (SelectedProc()?.ExePath is not { Length: > 0 } path) return;
        try
        {
            // The full path to Explorer, so nothing else named explorer.exe can be picked up.
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Warning, "Could not open the file location", ex.Message);
        }
    }

    private void SetPriority(PriorityLevel level, string levelText)
    {
        if (SelectedProc() is not { } p) return;
        string? problem = ProcessKiller.SetPriority(p, level);
        string who = $"{p.Name} (PID {p.Pid})";
        Log.Write(problem == null ? $"Priority set by user: {who} to {levelText}" : $"Priority not changed: {who}: {problem}");
        if (problem == null) ShowResult(InfoBarSeverity.Success, "Priority changed", $"{who} now runs at {levelText.ToLowerInvariant()} priority.");
        else ShowResult(InfoBarSeverity.Warning, "Priority not changed", $"{who}: {problem}");
        _c.Sample();
    }

    private void ToggleNeverFlag()
    {
        if (SelectedProc() is not { } p) return;
        bool never = !_c.Settings.IsIgnored(p.Name);
        _c.SetNeverFlag(p.Name, never);
        ShowResult(InfoBarSeverity.Informational, never ? "Added to the never-flag list" : "Removed from the never-flag list",
            never ? $"{p.Name} will not be flagged as a runaway or trigger a notification. The list is in Settings."
                  : $"{p.Name} is flagged as a runaway again when it stays hot.");
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    // ---------------------------------------------------------------------------------------------
    // Ending processes: always confirmed, and the dialog names everything that will be ended. What is
    // ended, and when, is decided by EndConfirmation in the core library; the window only shows it.

    private async void End_Click(object sender, RoutedEventArgs e) => await EndSelectedAsync(ReferenceEquals(sender, EndTreeButton));

    private async Task EndSelectedAsync(bool tree)
    {
        if (Root.XamlRoot != null && SelectedProc() is { } p) await Confirmation.ConfirmAndEndAsync(p, tree, agentReason: null, timeout: null);
    }

    bool IEndDialog.IsBusy => _dialogOpen;

    async Task<bool> IEndDialog.WaitUntilReadyAsync(TimeSpan? limit)
    {
        if (Root.XamlRoot == null)
        {
            // The window was created a moment ago for this request and has not been laid out yet.
            var loaded = new TaskCompletionSource();
            void OnLoaded(object s, RoutedEventArgs e) { Root.Loaded -= OnLoaded; loaded.TrySetResult(); }
            Root.Loaded += OnLoaded;
            if (limit is { } loadLimit)
            {
                if (await Task.WhenAny(loaded.Task, Task.Delay(loadLimit)) != loaded.Task)
                {
                    Root.Loaded -= OnLoaded;
                    Log.Write("The confirmation could not be shown: the window did not finish loading in time");
                    return false;
                }
            }
            else await loaded.Task;
        }
        return Root.XamlRoot != null;
    }

    async Task<bool> IEndDialog.AskAsync(EndPlan plan, CancellationToken close)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var line in plan.Lines)
        {
            var item = new StackPanel();
            item.Children.Add(new TextBlock { Text = line.Label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            item.Children.Add(new TextBlock
            {
                Text = line.Detail,
                TextWrapping = TextWrapping.Wrap,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Brush("TextFillColorSecondaryBrush"),
            });
            list.Children.Add(item);
        }
        if (plan.More != null) list.Children.Add(new TextBlock { Text = plan.More });

        var content = new StackPanel { Spacing = 12, MaxWidth = 560 };
        if (plan.FromAgent)
        {
            content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = EndPlan.AgentIntro });
            var why = new StackPanel { Spacing = 2 };
            why.Children.Add(new TextBlock
            {
                Text = EndPlan.AgentReasonLabel,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Brush("TextFillColorSecondaryBrush"),
            });
            why.Children.Add(new TextBlock { Text = plan.AgentReason, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            content.Children.Add(why);
        }
        content.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = plan.ListHeading });
        content.Children.Add(new ScrollViewer { Content = list, MaxHeight = plan.FromAgent ? 220 : 300 });
        content.Children.Add(new TextBlock
        {
            Text = plan.Warning,
            Foreground = Brush("SystemFillColorCriticalBrush"),
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = plan.Title,
            Content = content,
            PrimaryButtonText = plan.ConfirmText,
            CloseButtonText = plan.CancelText,
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = plan.ArmDelay == null,
        };

        // The end button works only once the dialog has been in view for the plan's delay.
        Microsoft.UI.Dispatching.DispatcherQueueTimer? arm = null;
        if (plan.ArmDelay is { } delay)
        {
            arm = DispatcherQueue.CreateTimer();
            arm.Interval = delay;
            arm.IsRepeating = false;
            arm.Tick += (_, _) => dialog.IsPrimaryButtonEnabled = true;
            dialog.Opened += (_, _) => arm.Start();
        }

        // Expired or withdrawn: the dialog closes. The token may fire on any thread.
        using var closing = close.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        _dialogOpen = true;
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary; }
        finally { _dialogOpen = false; arm?.Stop(); }
    }

    void IEndDialog.ShowEnded(EndReport report)
    {
        ShowResult(report.AllEnded ? InfoBarSeverity.Success : InfoBarSeverity.Warning, report.Title, report.Summary);
        _c.Sample();
    }

    // ---------------------------------------------------------------------------------------------
    // Settings

    public void OpenSettings()
    {
        if (Root.XamlRoot == null)
        {
            void OnLoaded(object s, RoutedEventArgs e) { Root.Loaded -= OnLoaded; OpenSettings(); }
            Root.Loaded += OnLoaded;
            return;
        }
        _ = ShowSettingsAsync();
    }

    private async Task ShowSettingsAsync()
    {
        if (_dialogOpen) return;
        var s = _c.Settings;
        NumberBox Box(string header, double value, double min, double max, double step) => new()
        {
            Header = header, Value = value, Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var cores = Box("Runaway when a process uses at least this many cores", s.RunawayCores, 0.05, 256, 0.05);
        var minutes = Box("...without a break for at least this many minutes", s.RunawayMinutes, 0.1, 1440, 1);
        var bg = Box("Measure every N seconds while the window is closed", s.BackgroundSeconds, 5, 600, 5);
        var win = Box("Refresh every N seconds while the window is open", s.WindowSeconds, 1, 60, 1);
        var top = Box("Rows in the \"Top by CPU\" view", s.TopCount, 5, 2000, 5);
        var neverFlag = new TextBox
        {
            Header = "Never flag these as runaway (one process name per line)",
            PlaceholderText = "for example a game or a compiler",
            AcceptsReturn = true, Height = 92, Text = string.Join("\r", s.IgnoreNames),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(neverFlag, ScrollBarVisibility.Auto);
        var onTop = new ToggleSwitch { Header = "Keep the window on top of other apps", IsOn = s.AlwaysOnTop };

        // Windows keeps its own off switch for startup entries. When the user used it, it is theirs to
        // turn back on: the toggle is shown as it is and points to the place to change it.
        var state = _c.StartupState;
        var startup = new ToggleSwitch
        {
            Header = "Start with Windows", IsOn = state == StartupState.On, IsEnabled = state != StartupState.OffInWindows,
        };
        var info = new TextBlock
        {
            Text = $"Settings, log and history are kept in {_c.DataDir}. \"Start with Windows\" adds Nikomi to Startup apps " +
                   "(Task Manager > Startup apps shows it too).",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };
        // AI agents: off until turned on here, and command lines stay hidden unless allowed separately.
        var agents = new ToggleSwitch { Header = "Let AI agents read this data", IsOn = s.AgentAccess };
        var agentCommands = new ToggleSwitch
        {
            Header = "Also show agents command lines and file paths", IsOn = s.AgentCommandLines, IsEnabled = s.AgentAccess,
        };
        agents.Toggled += (_, _) => agentCommands.IsEnabled = agents.IsOn;
        var agentInfo = new TextBlock
        {
            Text = "Agents connect through NikomiCli.exe, next to the app. They can read the process list and the history, " +
                   "and ask to end a process: every such request is shown here for you to confirm first. " +
                   "Command lines can contain passwords, and what an agent reads goes to its AI provider.",
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Brush("TextFillColorSecondaryBrush"),
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 420, MaxWidth = 520 };
        foreach (var el in new UIElement[] { cores, minutes, bg, win, top, neverFlag, onTop, startup }) panel.Children.Add(el);
        if (state == StartupState.OffInWindows)
        {
            var link = new HyperlinkButton { Content = "Turned off in Windows. Open Startup apps to turn it on", Padding = new Thickness(0) };
            link.Click += (_, _) => _c.OpenStartupSettings();
            panel.Children.Add(link);
        }
        panel.Children.Add(info);
        foreach (var el in new UIElement[] { agents, agentCommands, agentInfo }) panel.Children.Add(el);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = "Settings",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        _dialogOpen = true;
        ContentDialogResult answer;
        try { answer = await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
        if (answer != ContentDialogResult.Primary) return;

        double V(NumberBox b, double fallback) => double.IsNaN(b.Value) ? fallback : b.Value;
        var changed = _c.Settings.Copy();
        changed.RunawayCores = V(cores, s.RunawayCores);
        changed.RunawayMinutes = V(minutes, s.RunawayMinutes);
        changed.BackgroundSeconds = V(bg, s.BackgroundSeconds);
        changed.WindowSeconds = V(win, s.WindowSeconds);
        changed.TopCount = (int)V(top, s.TopCount);
        changed.IgnoreNames = neverFlag.Text.Split('\r', '\n').ToList();
        changed.AlwaysOnTop = onTop.IsOn;
        changed.AgentAccess = agents.IsOn;
        changed.AgentCommandLines = agents.IsOn && agentCommands.IsOn;
        _c.SaveSettings(changed);
        ApplyAlwaysOnTop();
        if (state != StartupState.OffInWindows && startup.IsOn != (state == StartupState.On)) _c.SetStartWithWindows(startup.IsOn);
        _c.Sample();
    }

    // ---------------------------------------------------------------------------------------------
    // Toolbar and list events

    private void SelectView(string tag)
    {
        foreach (var item in ViewBar.Items)
            if ((string)item.Tag == tag) { ViewBar.SelectedItem = item; break; }
    }

    private void ViewBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _view = sender.SelectedItem?.Tag as string ?? "top";
        if (_ready) Refresh();   // also fires while InitializeComponent is still building the window
    }

    private void ShowOrphans_Click(object sender, RoutedEventArgs e) => SelectView("orphans");
    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) Refresh(); }
    private void PauseButton_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Refresh_Click(object sender, RoutedEventArgs e) => _c.Sample();
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void GroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedGroup() is not { } group) return;
        FilterBox.Text = group.View.Name;
        SelectView("all");
    }

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (_sort == tag) _descending = !_descending;
        else { _sort = tag; _descending = tag is "Cores" or "CpuTime" or "Memory" or "Io" or "Threads" or "Handles" or "Started" or "Status"; }
        UpdateHeaderArrows();
        Refresh();
    }

    private void UpdateHeaderArrows()
    {
        foreach (var (tag, button) in _headers)
        {
            string label = ((string)button.Content).TrimEnd(' ', '↑', '↓');
            button.Content = tag == _sort ? label + (_descending ? "  ↓" : "  ↑") : label;
        }
    }

    private void ProcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _selectedKey = (ProcList.SelectedItem as ProcessRow)?.Key;
        UpdateDetails();
    }

    /// <summary>A right-click selects the row under the pointer first, so the menu acts on what was clicked.</summary>
    private void ProcList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is ProcessRow row) ProcList.SelectedItem = row;
    }

    private async void ProcList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Delete) return;
        e.Handled = true;
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        await EndSelectedAsync(tree: shift);
    }

    // The list scrolls sideways inside itself; the header sits outside it and is moved along.
    private void ProcList_Loaded(object sender, RoutedEventArgs e)
    {
        if (_listScroll != null) return;
        _listScroll = FindDescendant<ScrollViewer>(ProcList);
        if (_listScroll != null)
            _listScroll.ViewChanged += (_, _) => SyncScroll(HeaderScroll, _listScroll.HorizontalOffset);
    }

    private void HeaderScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_listScroll != null) SyncScroll(_listScroll, HeaderScroll.HorizontalOffset);
    }

    private static void SyncScroll(ScrollViewer target, double offset)
    {
        if (Math.Abs(target.HorizontalOffset - offset) > 0.5) target.ChangeView(offset, null, null, disableAnimation: true);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private static void SetText(TextBlock tb, string text)
    {
        if (tb.Text != text) tb.Text = text;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..(max - 3)] + "...";

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
