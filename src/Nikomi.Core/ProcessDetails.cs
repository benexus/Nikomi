using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Nikomi.Core;

/// <summary>Reads a process's command line and executable path with a limited-query handle (no admin).</summary>
public static class ProcessDetails
{
    private const int ProcessCommandLineInformation = 60;

    public static (string? CommandLine, string? ExePath) Read(int pid)
    {
        if (pid <= 4) return (null, null);
        using var h = NativeProcess.Open(ProcessAccess.QueryLimitedInformation, pid);
        return h.IsInvalid ? (null, null) : (ReadCommandLine(h), ReadImagePath(h));
    }

    private static readonly ConcurrentDictionary<string, (string? Description, string? Company)> Described =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The most executables whose description is kept. A PC that runs many short-lived, uniquely named
    /// programs (build tools, installers unpacked to temporary folders) would otherwise make the list grow
    /// for as long as Nikomi runs. When it is full it starts over: the programs still running are read again
    /// once, which costs a moment, and the list cannot grow without end.
    /// </summary>
    public const int MaxDescribed = 1024;

    /// <summary>How many executables' descriptions are kept right now.</summary>
    public static int DescribedCount => Described.Count;

    /// <summary>
    /// What an executable says about itself in its version resource. Read once per file. This is the
    /// file's own claim: nothing here checks a signature.
    /// </summary>
    public static (string? Description, string? Company) Describe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return (null, null);
        if (Described.Count >= MaxDescribed && !Described.ContainsKey(exePath)) Described.Clear();
        return Described.GetOrAdd(exePath, static path =>
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                return (Clean(v.FileDescription), Clean(v.CompanyName));
            }
            catch
            {
                return (null, null);
            }
        });
    }

    /// <summary>Whoever built the file wrote this text: no hidden characters, at most 200 characters.</summary>
    private static string? Clean(string? text) => AgentText.Clean(text);

    private const int ErrorInsufficientBuffer = 122;
    /// <summary>The longest path Windows has: 32,767 characters and the terminating null.</summary>
    private const int MaxLongPath = 32768;

    /// <summary>
    /// The executable's full path. Most fit in 1,024 characters; one in a deeper folder (long paths are on)
    /// is read again with room for the longest path Windows allows.
    /// </summary>
    private static string? ReadImagePath(SafeProcessHandle h)
    {
        foreach (int capacity in new[] { 1024, MaxLongPath })
        {
            var sb = new StringBuilder(capacity);
            int size = capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString(0, size);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
        }
        return null;
    }

    private static string? ReadCommandLine(SafeProcessHandle h)
    {
        int len = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out int needed);
                if (status == unchecked((int)0xC0000004) || status == unchecked((int)0x80000005) || status == unchecked((int)0xC0000023))
                {
                    len = Math.Max(needed, len * 2);
                    continue;
                }
                if (status != 0) return null;
                // The buffer starts with a UNICODE_STRING whose Buffer points into the same block.
                int bytes = (ushort)Marshal.ReadInt16(buf, 0);
                IntPtr text = Marshal.ReadIntPtr(buf, 8);
                if (text == IntPtr.Zero || bytes == 0) return "";
                return Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle h, int flags, StringBuilder name, ref int size);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle h, int infoClass, IntPtr buffer, int length, out int returnLength);
}
