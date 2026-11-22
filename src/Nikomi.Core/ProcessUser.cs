using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Nikomi.Core;

/// <summary>Which Windows account a process runs as, read from its token.</summary>
public static class ProcessUser
{
    private const uint TokenQuery = 0x0008;
    private const int TokenUser = 1;

    /// <summary>The account of the process, or null when it cannot be read (it has exited, or access is denied).</summary>
    public static SecurityIdentifier? Of(int pid)
    {
        if (pid <= 0) return null;
        using var process = NativeProcess.Open(ProcessAccess.QueryLimitedInformation, pid);
        if (process.IsInvalid) return null;
        if (!OpenProcessToken(process, TokenQuery, out var token)) return null;
        using (token)
        {
            GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, TokenUser, buffer, needed, out _)) return null;
                // TOKEN_USER starts with SID_AND_ATTRIBUTES, whose first field points to the SID.
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>True only when the process is known to run as the same account as this one.</summary>
    public static bool IsCurrentUser(int pid)
    {
        if (Of(pid) is not { } sid) return false;
        using var me = WindowsIdentity.GetCurrent();
        return me.User != null && sid == me.User;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, IntPtr info, int length, out int returnLength);
}
