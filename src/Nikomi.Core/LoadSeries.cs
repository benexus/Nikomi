namespace Nikomi.Core;

/// <summary>A point on a load graph: X from 0 (left edge) to 1 (right edge), Y from 0 (idle) to 1 (every core busy).</summary>
public readonly record struct GraphPoint(double X, double Y);

/// <summary>A vertical grid line on a load graph, at X from 0 to 1, with an optional label under it.</summary>
public readonly record struct GraphTick(double X, string? Label);

/// <summary>A stretch of time, in seconds on any one timeline, and the share of the CPU in use over it (0 to 1).</summary>
public readonly record struct LoadSpan(double Start, double End, double Load);

/// <summary>Turns measured stretches into the lines of a graph. No UI here, so it can be checked.</summary>
public static class LoadSeries
{
    /// <summary>
    /// Lays the stretches out on a graph that shows <paramref name="from"/> to <paramref name="to"/>. Stretches
    /// that follow on from each other (within <paramref name="tolerance"/> seconds) make one line; a gap,
    /// such as the PC sleeping or Nikomi not running, starts a new one. Each line starts at the beginning of
    /// its first stretch, so even a single stretch shows. Stretches must come oldest first.
    /// </summary>
    public static List<List<GraphPoint>> Build(IEnumerable<LoadSpan> spans, double from, double to, double tolerance)
    {
        var lines = new List<List<GraphPoint>>();
        double width = to - from;
        if (width <= 0) return lines;
        double X(double t) => Math.Clamp((t - from) / width, 0, 1);

        List<GraphPoint>? line = null;
        double lastEnd = double.NegativeInfinity;
        foreach (var s in spans)
        {
            if (s.End <= s.Start || s.End <= from || s.Start >= to || s.End <= lastEnd) continue;
            double y = Math.Clamp(s.Load, 0, 1);
            if (line == null || s.Start > lastEnd + tolerance)
            {
                line = new List<GraphPoint> { new(X(s.Start), y) };
                lines.Add(line);
            }
            line.Add(new GraphPoint(X(s.End), y));
            lastEnd = s.End;
        }
        return lines;
    }

    /// <summary>The history's lines as stretches, in seconds relative to <paramref name="originUtc"/>.</summary>
    public static IEnumerable<LoadSpan> FromHistory(IEnumerable<HistoryEntry> entries, DateTime originUtc)
    {
        foreach (var e in entries)
        {
            if (e.Load is not { } load) continue;
            double end = (e.TimeUtc - originUtc).TotalSeconds;
            yield return new LoadSpan(end - e.Seconds, end, load);
        }
    }

    /// <summary>
    /// Grid lines for a graph of the <paramref name="span"/> up to <paramref name="nowUtc"/>: one at every
    /// <paramref name="everyHours"/> whole hours of the local clock, labelled with the local time.
    /// </summary>
    public static List<GraphTick> HourTicks(DateTime nowUtc, TimeSpan span, int everyHours)
    {
        var ticks = new List<GraphTick>();
        DateTime start = nowUtc - span;
        // Quarter-hour steps in UTC, so time zones with half or quarter hours and clock changes still line up.
        var t = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        for (; t <= nowUtc; t = t.AddMinutes(15))
        {
            if (t <= start) continue;
            DateTime local = t.ToLocalTime();
            if (local.Minute != 0 || local.Hour % everyHours != 0) continue;
            ticks.Add(new GraphTick((t - start).TotalSeconds / span.TotalSeconds, local.ToString("t")));
        }
        return ticks;
    }
}

/// <summary>
/// The total CPU load, measured about every second while the window is open, for the live graph. It reads
/// the system's own idle and busy times, which costs next to nothing, so it runs apart from the slower
/// process list. Not thread-safe: the app uses it on the UI thread only.
/// </summary>
public sealed class LiveLoad
{
    /// <summary>How much the live graph shows.</summary>
    public static readonly TimeSpan Span = TimeSpan.FromSeconds(60);
    /// <summary>A longer pause between two readings (the window was closed) starts afresh instead of averaging over it.</summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(3);

    private readonly SystemSource _source;
    private readonly Queue<LoadSpan> _spans = new();
    private ulong _idle, _total;
    private long? _readAt;   // Environment.TickCount64

    public LiveLoad(SystemSource source) { _source = source; }

    /// <summary>Oldest first, in seconds of <see cref="Environment.TickCount64"/>.</summary>
    public IReadOnlyCollection<LoadSpan> Spans => _spans;

    /// <summary>The load over the latest stretch, or null until there is one after the window opened.</summary>
    public double? Latest { get; private set; }

    /// <summary>Reads the system times. Returns true when this completed a new stretch.</summary>
    public bool Sample(long nowMs)
    {
        if (!_source.ReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return false;
        ulong total = kernel + user;   // kernel time includes idle time
        bool added = false;
        if (_readAt is { } before && nowMs - before <= MaxStep.TotalMilliseconds)
        {
            if (total > _total && nowMs > before)
            {
                Add(new LoadSpan(before / 1000.0, nowMs / 1000.0, 1 - (double)(idle - _idle) / (total - _total)));
                added = true;
            }
        }
        else
        {
            Latest = null;
        }
        _idle = idle;
        _total = total;
        _readAt = nowMs;
        return added;
    }

    /// <summary>Adds a measured stretch and forgets what has scrolled off the graph.</summary>
    public void Add(LoadSpan span)
    {
        var s = span with { Load = Math.Clamp(span.Load, 0, 1) };
        _spans.Enqueue(s);
        Latest = s.Load;
        // One stretch past the left edge is kept, so the line runs in from the edge.
        while (_spans.Count > 1 && _spans.ElementAt(1).End < s.End - Span.TotalSeconds) _spans.Dequeue();
    }
}
