using System.Runtime.InteropServices;

namespace Nikomi.Core;

/// <summary>
/// Everything Nikomi reads from Windows about the PC and its processes, in one place. Whoever measures
/// (the app, the console program, the checks) makes one and hands it to the <see cref="ProcessMonitor"/>,
/// <see cref="UsageHistory"/> and <see cref="LiveLoad"/> it uses; nothing in the core library reaches for a
/// shared one. A demo build (-p:Demo=true, see docs\manual.md) hands them a source of made-up figures
/// instead, so that a screenshot shows nothing of the PC it was taken on.
/// </summary>
public class SystemSource : IDisposable
{
    private ProcessSampler? _sampler;

    public virtual Snapshot TakeSnapshot() => (_sampler ??= new ProcessSampler()).Take();

    public virtual (string? CommandLine, string? ExePath) ReadDetails(int pid) => ProcessDetails.Read(pid);

    public virtual (string? Description, string? Company) Describe(string? exePath) => ProcessDetails.Describe(exePath);

    /// <summary>Process ID to the display names of the services in it.</summary>
    public virtual Dictionary<int, string> ReadServices() => ServiceMap.Read();

    /// <summary>The system's idle, kernel and user times in 100 ns ticks. Kernel time includes idle time.</summary>
    public virtual bool ReadSystemTimes(out ulong idle, out ulong kernel, out ulong user) =>
        GetSystemTimes(out idle, out kernel, out user);

    public virtual int LogicalCores => Environment.ProcessorCount;

    public virtual TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public void Dispose()
    {
        _sampler?.Dispose();
        _sampler = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
