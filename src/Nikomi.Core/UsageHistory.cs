using System.Text.Json;

namespace Nikomi.Core;

public sealed record HistoryProc(string Name, int Pid, double Cores);

/// <summary>What the CPU was doing over one stretch: total load and the busiest processes.</summary>
public sealed record HistoryEntry(DateTime TimeUtc, double Seconds, double? Load, IReadOnlyList<HistoryProc> Top);

/// <summary>
/// Keeps a line about every half minute for the last 24 hours, so "what was busy while I was away?" has
/// an answer. It measures over its own stretches, independent of how often the window refreshes, and
/// stores process names, PIDs and core figures only: no command lines.
/// <para>
/// Not thread-safe: use it on one thread. The app binds its history to the UI thread, like its
/// <see cref="ProcessMonitor"/>; the lines themselves (<see cref="HistoryEntry"/>) never change once made.
/// </para>
/// </summary>
public sealed class UsageHistory
{
    /// <summary>A line is written once at least this much time has passed since the last one.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(25);
    /// <summary>A stretch longer than this (the PC slept) says nothing useful and is dropped.</summary>
    public static readonly TimeSpan MaxStretch = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public const int TopCount = 5;
    /// <summary>Processes below this many cores are left out of a line.</summary>
    public const double Floor = 0.05;
    private const long MaxFileBytes = 2 << 20;

    private readonly SystemSource _source;
    private readonly List<HistoryEntry> _entries = new();
    private long? _startTicks;
    private Dictionary<string, long> _startCpu = new();

    /// <param name="source">Gives the number of cores the load is measured against.</param>
    public UsageHistory(SystemSource source) { _source = source; }

    private readonly OwningThread _thread = new(nameof(UsageHistory));

    /// <summary>From now on the history may be used on this thread only; any other thread gets an <see cref="InvalidOperationException"/>.</summary>
    public void BindToCurrentThread() => _thread.Bind();

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<HistoryEntry> Entries { get { _thread.Check(); return _entries; } }

    /// <summary>Folds in a sample. Returns the new line when this sample completed a stretch.</summary>
    public HistoryEntry? Add(Snapshot snap)
    {
        _thread.Check();
        if (_startTicks is not { } start) { StartStretch(snap); return null; }
        long span = snap.MonotonicTicks - start;
        if (span < Step.Ticks) return null;

        HistoryEntry? entry = null;
        if (span <= MaxStretch.Ticks)
        {
            double? idle = null;
            var top = new List<HistoryProc>();
            foreach (var p in snap.Procs)
            {
                // A process that is not in the starting sample began during the stretch: all its CPU time counts.
                long before = _startCpu.TryGetValue(p.Key, out long cpu) ? cpu : 0;
                double cores = Math.Max(0, (double)(p.CpuTicks - before) / span);
                if (p.Pid == 0) idle = cores;
                else if (cores >= Floor) top.Add(new HistoryProc(p.Name, p.Pid, Math.Round(cores, 2)));
            }
            top.Sort((a, b) => b.Cores.CompareTo(a.Cores));
            if (top.Count > TopCount) top.RemoveRange(TopCount, top.Count - TopCount);
            double? load = idle is { } i ? Math.Round(Math.Clamp(1 - i / _source.LogicalCores, 0, 1), 3) : null;
            entry = new HistoryEntry(snap.TimeUtc, Math.Round(span / 1e7, 1), load, top);
            _entries.Add(entry);
            Prune(snap.TimeUtc);
        }
        StartStretch(snap);
        return entry;
    }

    private void StartStretch(Snapshot snap)
    {
        _startTicks = snap.MonotonicTicks;
        var cpu = new Dictionary<string, long>(snap.Procs.Count);
        foreach (var p in snap.Procs) cpu[p.Key] = p.CpuTicks;
        _startCpu = cpu;
    }

    private void Prune(DateTime nowUtc)
    {
        DateTime oldest = nowUtc - Keep;
        _entries.RemoveAll(e => e.TimeUtc < oldest);
    }

    /// <summary>Reads back what earlier runs saved, so the history survives a restart.</summary>
    public void Load(string path)
    {
        _thread.Check();
        DateTime now = DateTime.UtcNow, oldest = now - Keep;
        foreach (string file in new[] { path + ".old", path })
        {
            try
            {
                if (!File.Exists(file)) continue;
                foreach (string line in File.ReadLines(file))
                {
                    if (line.Length == 0) continue;
                    HistoryEntry? e;
                    try { e = JsonSerializer.Deserialize<HistoryEntry>(line); }
                    catch (JsonException) { continue; }   // a half-written last line
                    if (e != null && IsSound(e) && e.TimeUtc >= oldest && e.TimeUtc <= now) _entries.Add(e);
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Could not read the history in {file}: {ex.Message}");
            }
        }
        _entries.Sort((a, b) => a.TimeUtc.CompareTo(b.TimeUtc));
    }

    /// <summary>
    /// Whether a line read back from the file could have been written by <see cref="Add"/>. The file is
    /// plain text that anything can edit, and a line that is cut short or changed can still be valid JSON
    /// ("Top":[null], "Name":null): such a line is dropped instead of breaking every later use.
    /// </summary>
    public static bool IsSound(HistoryEntry e)
    {
        if (e.Top is not { Count: <= TopCount } top) return false;
        if (!double.IsFinite(e.Seconds) || e.Seconds <= 0 || e.Seconds > MaxStretch.TotalSeconds) return false;
        if (e.Load is { } load && !(load >= 0 && load <= 1)) return false;
        foreach (var p in top)
            if (p is null || string.IsNullOrWhiteSpace(p.Name) || !double.IsFinite(p.Cores) || p.Cores < 0 || p.Cores > MaxCores)
                return false;
        return true;
    }

    /// <summary>More cores than any Windows PC has: a figure above it cannot be real.</summary>
    private const double MaxCores = 4096;

    /// <summary>Appends one line to the history file, which rolls over to ".old" at 2 MB.</summary>
    public static void Append(string path, HistoryEntry entry)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxFileBytes) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, JsonSerializer.Serialize(entry) + "\n");
        }
        catch (Exception ex)
        {
            Log.Write("Could not save the history: " + ex.Message);
        }
    }
}
