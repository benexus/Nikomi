using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nikomi.Core;

public sealed record Ancestry(IReadOnlyList<ProcInfo> Chain, int? ExitedParentPid)
{
    /// <summary>Nearest parent first ("started by &lt; started by &lt; ..."), so a narrow column still shows the useful part.</summary>
    public string Format()
    {
        var parts = new List<string>(Chain.Count + 1);
        for (int i = Chain.Count - 1; i >= 0; i--) parts.Add($"{Chain[i].Name} ({Chain[i].Pid})");
        if (ExitedParentPid is { } g) parts.Add($"[exited {g}]");
        return string.Join(" < ", parts);
    }
}

public static class ProcessTree
{
    /// <summary>True when <paramref name="older"/> was created after <paramref name="newer"/>: the PID was reused, so they are unrelated.</summary>
    public static bool IsNewer(ProcInfo older, ProcInfo newer) =>
        older.CreateFileTime > 0 && newer.CreateFileTime > 0 && older.CreateFileTime > newer.CreateFileTime;

    /// <summary>Ancestors oldest first. ExitedParentPid is set when the chain ends at a parent that has exited.</summary>
    public static Ancestry Ancestors(ProcInfo proc, IReadOnlyDictionary<int, ProcInfo> byPid)
    {
        var chain = new List<ProcInfo>();
        var seen = new HashSet<int>();
        int? gone = null;
        var cur = proc;
        for (int i = 0; i < 64; i++)
        {
            int ppid = cur.ParentPid;
            if (ppid <= 0 || ppid == cur.Pid || !seen.Add(ppid)) break;
            if (!byPid.TryGetValue(ppid, out var parent) || IsNewer(parent, cur)) { gone = ppid; break; }
            chain.Insert(0, parent);
            cur = parent;
        }
        return new Ancestry(chain, gone);
    }

    /// <summary>Everything the process started that is still running, children before grandchildren.</summary>
    public static List<ProcInfo> Descendants(ProcInfo root, IEnumerable<ProcInfo> all)
    {
        var children = new Dictionary<int, List<ProcInfo>>();
        foreach (var p in all)
        {
            if (p.ParentPid <= 0 || p.ParentPid == p.Pid) continue;
            if (!children.TryGetValue(p.ParentPid, out var l)) children[p.ParentPid] = l = new();
            l.Add(p);
        }
        var result = new List<ProcInfo>();
        var seen = new HashSet<int> { root.Pid };
        var queue = new Queue<ProcInfo>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (!children.TryGetValue(cur.Pid, out var kids)) continue;
            foreach (var k in kids)
            {
                if (IsNewer(cur, k) || !seen.Add(k.Pid)) continue;
                result.Add(k);
                queue.Enqueue(k);
            }
        }
        return result;
    }
}

public sealed record EndResult(ProcInfo Target, bool Ended, string Message);

/// <summary>Windows priority classes a user can sensibly pick. Realtime is left out on purpose.</summary>
public enum PriorityLevel : uint
{
    Low = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
}

public static class ProcessKiller
{
    /// <summary>
    /// Ends exactly the given processes, in order. Called only after the user confirmed the list.
    /// A PID whose creation time is not exactly the one in the sample is skipped, so a reused PID is never
    /// hit, and so is a process Windows marks as critical.
    /// </summary>
    public static List<EndResult> End(IEnumerable<ProcInfo> targets)
    {
        int self = Environment.ProcessId;
        var results = new List<EndResult>();
        foreach (var t in targets)
            results.Add(t.Pid == self ? new(t, false, "skipped, that is Nikomi itself") : EndOne(t));
        return results;
    }

    /// <summary>
    /// True when <see cref="End"/> would refuse the process because Windows depends on it. Lets the
    /// confirmation dialog say so before the user confirms.
    /// </summary>
    public static bool IsCritical(ProcInfo t)
    {
        if (t.Pid <= SystemPid) return true;
        using var h = NativeProcess.Open(ProcessAccess.QueryLimitedInformation, t.Pid);
        return !h.IsInvalid && SameInstance(h, t) == true && IsProcessCritical(h, out bool critical) && critical;
    }

    private static EndResult EndOne(ProcInfo t)
    {
        if (t.Pid <= SystemPid) return new(t, false, "skipped, Windows itself runs in it");

        // One handle for the whole job. While it is open the PID cannot be handed to another process, so
        // the process that is checked below is the process that is ended.
        using var h = NativeProcess.Open(ProcessAccess.Terminate | ProcessAccess.QueryLimitedInformation | ProcessAccess.Synchronize, t.Pid);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return error == NativeProcess.ErrorInvalidParameter
                ? new(t, true, "already gone")
                : new(t, false, $"could not end it ({new Win32Exception(error).Message})");
        }

        // Anything that cannot be verified is left alone.
        switch (SameInstance(h, t))
        {
            case null: return new(t, false, "skipped, could not verify it is still the same process");
            case false: return new(t, false, "skipped, the PID now belongs to a different process");
        }
        if (WaitForSingleObject(h, 0) == WaitSignaled) return new(t, true, "already gone");
        if (!IsProcessCritical(h, out bool critical))
            return new(t, false, "skipped, could not check whether Windows depends on it");
        if (critical)
            return new(t, false, "skipped, Windows marks it as critical: ending it would crash the system");

        if (TerminateProcess(h, 1))
        {
            WaitForSingleObject(h, 3000);
            return new(t, true, "ended");
        }
        // A process that is already exiting (for example because its parent just ended) refuses
        // termination with "Access is denied". Give it a moment before reporting a failure.
        int refused = Marshal.GetLastWin32Error();
        return WaitForSingleObject(h, 2000) == WaitSignaled
            ? new(t, true, "ended (it exited along with its parent)")
            : new(t, false, $"could not end it ({new Win32Exception(refused).Message})");
    }

    /// <summary>Null when it cannot be told; otherwise whether the handle is the instance in the sample.</summary>
    private static bool? SameInstance(SafeProcessHandle h, ProcInfo t)
    {
        if (t.CreateFileTime <= 0 || !GetProcessTimes(h, out long created, out _, out _, out _)) return null;
        return created == t.CreateFileTime;
    }

    /// <summary>
    /// Changes how much CPU time Windows gives a process compared with the others, a gentler step than
    /// ending it. Returns null when done, otherwise why not. The same checks as for ending apply.
    /// </summary>
    public static string? SetPriority(ProcInfo t, PriorityLevel level)
    {
        if (t.Pid <= SystemPid) return "Windows itself runs in it";
        using var h = NativeProcess.Open(ProcessAccess.SetInformation | ProcessAccess.QueryLimitedInformation, t.Pid);
        if (h.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            return error == NativeProcess.ErrorInvalidParameter ? "it is already gone" : new Win32Exception(error).Message;
        }
        switch (SameInstance(h, t))
        {
            case null: return "could not verify it is still the same process";
            case false: return "the PID now belongs to a different process";
        }
        if (!IsProcessCritical(h, out bool critical) || critical) return "Windows depends on it";
        return SetPriorityClass(h, (uint)level) ? null : new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    public static string PriorityName(int basePriority) => basePriority switch
    {
        <= 4 => "low",
        <= 6 => "below normal",
        <= 8 => "normal",
        <= 10 => "above normal",
        <= 15 => "high",
        _ => "realtime",
    };

    private const int SystemPid = 4;   // 0 is the idle process, 4 is System

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetPriorityClass(SafeProcessHandle h, uint priorityClass);
    private const uint WaitSignaled = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessCritical(SafeProcessHandle h, out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle h, uint milliseconds);
}
