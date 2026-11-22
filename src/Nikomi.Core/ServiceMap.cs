using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Nikomi.Core;

/// <summary>
/// Which running Windows services live in which process, so a row that only says "svchost.exe" can say
/// what it is. One call to the service control manager; needs no admin rights.
/// </summary>
public static class ServiceMap
{
    private const uint ScManagerConnect = 0x0001, ScManagerEnumerateService = 0x0004;
    private const uint ServiceWin32 = 0x30, ServiceActive = 1;
    private const int ErrorMoreData = 234;

    // ENUM_SERVICE_STATUS_PROCESS on x64: two string pointers, then SERVICE_STATUS_PROCESS (nine DWORDs).
    private const int OffDisplayName = 8, OffProcessId = 16 + 7 * 4, EntrySize = 56;

    /// <summary>Process ID to the display names of the services in it, joined with ", ".</summary>
    public static Dictionary<int, string> Read()
    {
        var names = new Dictionary<int, List<string>>();
        IntPtr scm = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        const int size = 256 * 1024;   // the largest buffer the call accepts; it resumes when that is not enough
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int resume = 0;
            while (true)
            {
                bool done = EnumServicesStatusEx(scm, 0 /* SC_ENUM_PROCESS_INFO */, ServiceWin32, ServiceActive,
                    buffer, size, out _, out int count, ref resume, null);
                int error = done ? 0 : Marshal.GetLastWin32Error();
                if (!done && error != ErrorMoreData) throw new Win32Exception(error);
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = buffer + i * EntrySize;
                    int pid = Marshal.ReadInt32(entry, OffProcessId);
                    // Whoever installed the service chose its display name: no hidden characters, 200 at most.
                    string? name = AgentText.Clean(Marshal.PtrToStringUni(Marshal.ReadIntPtr(entry, OffDisplayName)));
                    if (pid <= 0 || name == null) continue;
                    if (!names.TryGetValue(pid, out var list)) names[pid] = list = new();
                    list.Add(name);
                }
                if (done || count == 0) break;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }
        var result = new Dictionary<int, string>(names.Count);
        foreach (var (pid, list) in names)
        {
            list.Sort(StringComparer.CurrentCultureIgnoreCase);
            result[pid] = string.Join(", ", list);
        }
        return result;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumServicesStatusExW")]
    private static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr buffer, int bufferSize, out int bytesNeeded, out int servicesReturned, ref int resumeHandle, string? groupName);
}
