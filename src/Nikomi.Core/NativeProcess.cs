using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nikomi.Core;

/// <summary>Rights asked for when a process is opened. Only what Nikomi uses.</summary>
[Flags]
internal enum ProcessAccess : uint
{
    Terminate = 0x0001,
    SetInformation = 0x0200,
    QueryLimitedInformation = 0x1000,
    Synchronize = 0x00100000,
}

/// <summary>
/// The one place a process handle is opened. Every caller gets a <see cref="SafeProcessHandle"/>, so the
/// handle is closed on every path, also when an exception is thrown.
/// </summary>
internal static class NativeProcess
{
    /// <summary>OpenProcess: no process has this PID (or it has exited).</summary>
    public const int ErrorInvalidParameter = 87;

    /// <summary>
    /// Opens a process. The handle is invalid when it cannot be opened; <see cref="Marshal.GetLastWin32Error"/>
    /// then says why, as long as nothing else is called in between.
    /// </summary>
    public static SafeProcessHandle Open(ProcessAccess access, int pid) => OpenProcess((uint)access, false, pid);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
}
