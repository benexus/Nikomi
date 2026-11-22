using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Nikomi.Core;

/// <summary>
/// One process as seen in one sample, plus what the monitor works out about it. The init-only figures
/// never change and may be read on any thread (<see cref="ProcessKiller.End"/> reads them on a worker).
/// The ones with an internal setter are filled in, and changed again on later samples, by the
/// <see cref="ProcessMonitor"/>: read them only on that monitor's thread.
/// </summary>
public sealed class ProcInfo
{
    public required int Pid { get; init; }
    public required int ParentPid { get; init; }
    public required string Name { get; init; }
    /// <summary>Creation time as a FILETIME (100 ns ticks since 1601, UTC). 0 when unknown.</summary>
    public required long CreateFileTime { get; init; }
    /// <summary>Kernel + user CPU time in 100 ns ticks.</summary>
    public required long CpuTicks { get; init; }
    public required long WorkingSet { get; init; }
    /// <summary>Memory only this process uses (private working set): the figure Task Manager calls Memory.</summary>
    public long PrivateWorkingSet { get; init; }
    /// <summary>Memory the process has committed (private bytes), whether or not it is in RAM right now.</summary>
    public long PrivateBytes { get; init; }
    /// <summary>Bytes read and written since the process started: files, network and pipes together.</summary>
    public long IoBytes { get; init; }
    public int Threads { get; init; }
    public int Handles { get; init; }
    public int SessionId { get; init; }
    /// <summary>Base priority: 4 low, 6 below normal, 8 normal, 10 above normal, 13 high, 24 realtime.</summary>
    public int BasePriority { get; init; }

    /// <summary>Identifies one process instance: a PID plus its creation time, so a reused PID is a new key.</summary>
    public string Key => $"{Pid}:{CreateFileTime}";
    public DateTime? Created => CreateFileTime > 0 ? DateTime.FromFileTime(CreateFileTime) : null;

    // Filled in by ProcessMonitor.
    public double? Cores { get; internal set; }
    public double? HotMinutes { get; internal set; }
    public bool Runaway { get; internal set; }
    public bool Orphan { get; internal set; }
    /// <summary>Hot for long enough to be a runaway, but its name is on the never-flag list.</summary>
    public bool Ignored { get; internal set; }
    /// <summary>Bytes read and written per second over the last interval. Null until two samples exist.</summary>
    public double? IoBytesPerSec { get; internal set; }

    // Filled in lazily by ProcessMonitor.EnsureDetails (only while the window needs them).
    public string? CommandLine { get; internal set; }
    public string? ExePath { get; internal set; }
    /// <summary>What the executable says it is and who made it. Taken from the file, not verified.</summary>
    public string? Description { get; internal set; }
    public string? Company { get; internal set; }
    public bool DetailsRead { get; internal set; }
}

/// <summary>
/// All processes at one moment. <paramref name="MonotonicTicks"/> is that moment on a clock that only
/// runs forward (100 ns ticks from an arbitrary start) and is what intervals are measured with;
/// <paramref name="TimeUtc"/> is the wall clock, needed only to compare with process start times.
/// </summary>
public sealed record Snapshot(DateTime TimeUtc, long MonotonicTicks, IReadOnlyList<ProcInfo> Procs);

/// <summary>
/// Reads every process in one call to NtQuerySystemInformation(SystemProcessInformation), the same source
/// Get-Process and Task Manager use. No process handles are opened, so a sample costs a millisecond or two
/// and needs no admin rights.
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION field offsets on x64.
    private const int OffNext = 0, OffThreads = 4, OffPrivateWorkingSet = 8, OffCreateTime = 32, OffUserTime = 40, OffKernelTime = 48;
    private const int OffNameLength = 56, OffNameBuffer = 64, OffBasePriority = 72, OffPid = 80, OffParentPid = 88;
    private const int OffHandles = 96, OffSession = 100, OffWorkingSet = 144, OffPrivateBytes = 200;
    private const int OffReadBytes = 232, OffWriteBytes = 240;

    private IntPtr _buffer;
    private int _size;

    public ProcessSampler()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("Nikomi needs a 64-bit process.");
    }

    public Snapshot Take()
    {
        if (_buffer == IntPtr.Zero) { _size = 1 << 20; _buffer = Marshal.AllocHGlobal(_size); }
        DateTime t0;
        long m0, m1;
        while (true)
        {
            t0 = DateTime.UtcNow;
            m0 = Stopwatch.GetTimestamp();
            int status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out int needed);
            m1 = Stopwatch.GetTimestamp();
            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(_buffer);
                _size = Math.Max(needed + (64 << 10), _size * 2);
                _buffer = Marshal.AllocHGlobal(_size);
                continue;
            }
            if (status != 0) throw new Win32Exception($"NtQuerySystemInformation failed: 0x{status:X8}");
            break;
        }

        var list = new List<ProcInfo>(700);
        int offset = 0;
        while (true)
        {
            IntPtr p = _buffer + offset;
            int pid = (int)Marshal.ReadIntPtr(p, OffPid);
            int nameLen = (ushort)Marshal.ReadInt16(p, OffNameLength);
            IntPtr nameBuf = Marshal.ReadIntPtr(p, OffNameBuffer);
            string name = nameBuf != IntPtr.Zero && nameLen > 0
                ? Marshal.PtrToStringUni(nameBuf, nameLen / 2)
                : pid == 0 ? "System Idle Process" : "";
            list.Add(new ProcInfo
            {
                Pid = pid,
                ParentPid = (int)Marshal.ReadIntPtr(p, OffParentPid),
                Name = name,
                CreateFileTime = Marshal.ReadInt64(p, OffCreateTime),
                CpuTicks = Marshal.ReadInt64(p, OffUserTime) + Marshal.ReadInt64(p, OffKernelTime),
                WorkingSet = Marshal.ReadIntPtr(p, OffWorkingSet).ToInt64(),
                PrivateWorkingSet = Marshal.ReadInt64(p, OffPrivateWorkingSet),
                PrivateBytes = Marshal.ReadIntPtr(p, OffPrivateBytes).ToInt64(),
                IoBytes = Marshal.ReadInt64(p, OffReadBytes) + Marshal.ReadInt64(p, OffWriteBytes),
                Threads = Marshal.ReadInt32(p, OffThreads),
                Handles = Marshal.ReadInt32(p, OffHandles),
                SessionId = Marshal.ReadInt32(p, OffSession),
                BasePriority = Marshal.ReadInt32(p, OffBasePriority),
            });
            int next = Marshal.ReadInt32(p, OffNext);
            if (next == 0) break;
            offset += next;
        }
        // The moment of the sample is the middle of the call.
        long half = (long)((m1 - m0) * TicksPerStamp / 2);
        return new Snapshot(t0 + TimeSpan.FromTicks(half), (long)(m0 * TicksPerStamp) + half, list);
    }

    private static readonly double TicksPerStamp = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);
}
