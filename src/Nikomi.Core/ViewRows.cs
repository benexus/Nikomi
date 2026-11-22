namespace Nikomi.Core;

/// <summary>The figures one line of the list shows: one process's own, or the sum over several.</summary>
public sealed record Totals(int Count, double? Cores, long CpuTicks, long Memory, double? IoPerSec, int Threads, int Handles)
{
    public static Totals Of(ProcInfo p) =>
        new(1, p.Cores, p.CpuTicks, p.PrivateWorkingSet, p.IoBytesPerSec, p.Threads, p.Handles);

    public Totals Plus(Totals other) => new(
        Count + other.Count, Add(Cores, other.Cores), CpuTicks + other.CpuTicks, Memory + other.Memory,
        Add(IoPerSec, other.IoPerSec), Threads + other.Threads, Handles + other.Handles);

    private static double? Add(double? a, double? b) => a is null ? b : b is null ? a : a + b;
}

/// <summary>
/// One line of the list. A process on its own (flat views), a process standing for everything it started
/// (tree view: <see cref="Totals"/> covers the whole subtree and <see cref="Depth"/> is its level), or a
/// group of processes with the same name (<see cref="Proc"/> is null, <see cref="Members"/> lists them).
/// </summary>
public sealed record ViewRow(string Key, string Name, ProcInfo? Proc, int Depth, Totals Totals, IReadOnlyList<ProcInfo>? Members = null)
{
    public bool IsGroup => Proc == null;

    public static ViewRow Flat(ProcInfo p) => new(p.Key, p.Name, p, 0, Totals.Of(p));
}

public static class ProcessViews
{
    /// <summary>
    /// The processes as a tree, written out top to bottom: each process is followed by what it started.
    /// Siblings are put in <paramref name="order"/>. A process whose parent has exited starts a tree of
    /// its own. Every process appears exactly once.
    /// </summary>
    public static List<ViewRow> Tree(IEnumerable<ProcInfo> procs, Comparison<ViewRow> order)
    {
        var list = procs.ToList();
        var byPid = new Dictionary<int, ProcInfo>(list.Count);
        foreach (var p in list) byPid[p.Pid] = p;

        var children = new Dictionary<int, List<ProcInfo>>();
        var roots = new List<ProcInfo>();
        foreach (var p in list)
        {
            if (p.ParentPid > 0 && p.ParentPid != p.Pid && byPid.TryGetValue(p.ParentPid, out var parent) &&
                !ProcessTree.IsNewer(parent, p))
            {
                if (!children.TryGetValue(parent.Pid, out var kids)) children[parent.Pid] = kids = new();
                kids.Add(p);
            }
            else roots.Add(p);
        }

        // Both walks below keep their own stack instead of calling themselves once per level: a chain of
        // thousands of nested processes (a runaway recursive script) would otherwise overflow the thread's
        // stack, which ends the app and cannot be caught.
        var totals = new Dictionary<int, Totals>(list.Count);
        void Sum(ProcInfo root)
        {
            if (totals.ContainsKey(root.Pid)) return;
            totals[root.Pid] = Totals.Of(root);   // also marks it as reached
            var stack = new Stack<(ProcInfo Proc, int NextKid)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (p, next) = stack.Pop();
                if (children.TryGetValue(p.Pid, out var kids) && next < kids.Count)
                {
                    stack.Push((p, next + 1));
                    var k = kids[next];
                    // Already reached means a parent loop: only its own figures count here.
                    if (totals.ContainsKey(k.Pid)) totals[p.Pid] = totals[p.Pid].Plus(Totals.Of(k));
                    else { totals[k.Pid] = Totals.Of(k); stack.Push((k, 0)); }
                    continue;
                }
                // Everything under p is added in: it goes into its parent's figures.
                if (stack.TryPeek(out var parent)) totals[parent.Proc.Pid] = totals[parent.Proc.Pid].Plus(totals[p.Pid]);
            }
        }

        var result = new List<ViewRow>(list.Count);
        var written = new HashSet<int>();
        List<ViewRow> Level(List<ProcInfo> siblings, int depth)
        {
            var rows = new List<ViewRow>(siblings.Count);
            foreach (var p in siblings)
                if (!written.Contains(p.Pid)) rows.Add(new ViewRow(p.Key, p.Name, p, depth, totals[p.Pid]));
            rows.Sort(order);
            return rows;
        }
        void Write(List<ProcInfo> top)
        {
            var stack = new Stack<(List<ViewRow> Rows, int Next)>();
            stack.Push((Level(top, 0), 0));
            while (stack.Count > 0)
            {
                var (rows, next) = stack.Pop();
                if (next >= rows.Count) continue;
                stack.Push((rows, next + 1));
                var row = rows[next];
                if (!written.Add(row.Proc!.Pid)) continue;
                result.Add(row);
                if (children.TryGetValue(row.Proc.Pid, out var kids)) stack.Push((Level(kids, row.Depth + 1), 0));
            }
        }

        foreach (var p in roots) Sum(p);
        // Parents that point at each other have no root. Start a tree at each one that was not reached.
        var loose = list.Where(p => !totals.ContainsKey(p.Pid)).ToList();
        foreach (var p in loose) if (!totals.ContainsKey(p.Pid)) { Sum(p); roots.Add(p); }
        Write(roots);
        return result;
    }

    /// <summary>
    /// Narrows a tree to the lines that match, plus the lines above each match that lead to it, so a
    /// match keeps its place in the tree.
    /// </summary>
    public static List<ViewRow> KeepWithAncestors(IReadOnlyList<ViewRow> tree, Func<ViewRow, bool> match)
    {
        var keep = new bool[tree.Count];
        for (int i = 0; i < tree.Count; i++)
        {
            if (!match(tree[i])) continue;
            keep[i] = true;
            int depth = tree[i].Depth;
            for (int j = i - 1; j >= 0 && depth > 0; j--)
            {
                if (tree[j].Depth != depth - 1) continue;
                if (keep[j]) break;   // everything above it is kept already
                keep[j] = true;
                depth--;
            }
        }
        var result = new List<ViewRow>();
        for (int i = 0; i < tree.Count; i++) if (keep[i]) result.Add(tree[i]);
        return result;
    }

    /// <summary>One line per process name, with the sum over every process of that name.</summary>
    public static List<ViewRow> ByName(IEnumerable<ProcInfo> procs)
    {
        var groups = new Dictionary<string, List<ProcInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (!groups.TryGetValue(p.Name, out var members)) groups[p.Name] = members = new();
            members.Add(p);
        }
        var result = new List<ViewRow>(groups.Count);
        foreach (var (name, members) in groups)
        {
            var total = Totals.Of(members[0]);
            for (int i = 1; i < members.Count; i++) total = total.Plus(Totals.Of(members[i]));
            result.Add(new ViewRow(GroupKey(name), members[0].Name, null, 0, total, members));
        }
        return result;
    }

    public static string GroupKey(string name) => "name:" + name.ToLowerInvariant();
}
