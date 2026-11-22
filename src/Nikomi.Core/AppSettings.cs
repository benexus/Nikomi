using System.Text.Json;
using Microsoft.Win32;

namespace Nikomi.Core;

public sealed class AppSettings
{
    /// <summary>Cores (1.00 = one full logical core) a process must use to count as hot.</summary>
    public double RunawayCores { get; set; } = 0.9;
    /// <summary>Minutes a process must stay hot, without a break, to be flagged runaway.</summary>
    public double RunawayMinutes { get; set; } = 10;
    /// <summary>Sample interval while the window is closed or minimized.</summary>
    public double BackgroundSeconds { get; set; } = 30;
    /// <summary>Refresh interval while the window is open.</summary>
    public double WindowSeconds { get; set; } = 5;
    /// <summary>Rows in the "Top by CPU" view.</summary>
    public int TopCount { get; set; } = 40;
    /// <summary>Process names that are never flagged runaway, however long they stay hot ("game.exe" or "game").</summary>
    public List<string> IgnoreNames { get; set; } = new();
    /// <summary>Set once the "still running in the tray" hint has been shown.</summary>
    public bool CloseHintShown { get; set; }
    /// <summary>The window stays above other apps' windows.</summary>
    public bool AlwaysOnTop { get; set; }
    /// <summary>
    /// AI agents may read the figures through the console program and ask to end a process (each end is
    /// still confirmed by the user in the window). Off until the user turns it on.
    /// </summary>
    public bool AgentAccess { get; set; }
    /// <summary>Agents also see command lines and file paths, which can hold passwords and the user's name.</summary>
    public bool AgentCommandLines { get; set; }

    public bool IsIgnored(string processName)
    {
        foreach (var entry in IgnoreNames)
            if (NameMatches(entry, processName)) return true;
        return false;
    }

    /// <summary>A list entry matches a process name with or without ".exe", in any case.</summary>
    public static bool NameMatches(string entry, string processName) =>
        entry.Equals(processName, StringComparison.OrdinalIgnoreCase) ||
        (processName.Length == entry.Length + 4 && processName.StartsWith(entry, StringComparison.OrdinalIgnoreCase) &&
         processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    public AppSettings Copy() => new()
    {
        RunawayCores = RunawayCores, RunawayMinutes = RunawayMinutes, BackgroundSeconds = BackgroundSeconds,
        WindowSeconds = WindowSeconds, TopCount = TopCount, IgnoreNames = new(IgnoreNames), CloseHintShown = CloseHintShown,
        AlwaysOnTop = AlwaysOnTop, AgentAccess = AgentAccess, AgentCommandLines = AgentCommandLines,
    };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load(string path) => Load(path, out _);

    /// <param name="unreadable">True when a settings file exists but could not be read or understood.</param>
    public static AppSettings Load(string path, out bool unreadable)
    {
        unreadable = false;
        try
        {
            if (File.Exists(path))
            {
                // Read at once and with every sharing flag: the file is held only for that moment, which
                // Save waits out when it swaps in a new file.
                string text;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                return (JsonSerializer.Deserialize<AppSettings>(text) ?? new()).Clamped();
            }
        }
        catch (FileNotFoundException)
        {
            // Removed between the check and the read: the same as no file.
        }
        catch (Exception ex)
        {
            unreadable = true;
            Log.Write($"Could not read settings, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    /// <summary>
    /// Keeps a copy of a settings file that could not be read (as "settings.json.bad"), before anything
    /// saves over it: the user's never-flag list must not be lost to a file that was only locked or damaged.
    /// </summary>
    public static void KeepUnreadable(string path)
    {
        try
        {
            using var from = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = new FileStream(path + ".bad", FileMode.Create, FileAccess.Write);
            from.CopyTo(to);
            Log.Write($"The settings file could not be read; a copy of it is kept as {path}.bad");
        }
        catch (Exception ex)
        {
            Log.Write($"Could not keep a copy of the unreadable settings file: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the settings to a temporary file and then swaps it in, so the file is always either the old
    /// settings or the new ones: never empty or half written, even after a crash or power loss, and never
    /// read half written by the console program.
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, Json)));
            stream.Flush(flushToDisk: true);
        }
        // Windows refuses to replace a file while anyone has it open (the console program for a moment, an
        // editor, a backup tool), whatever sharing they allow: try again briefly before giving up.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    public AppSettings Clamped()
    {
        RunawayCores = Math.Clamp(RunawayCores, 0.05, 256);
        RunawayMinutes = Math.Clamp(RunawayMinutes, 0.1, 1440);
        BackgroundSeconds = Math.Clamp(BackgroundSeconds, 5, 600);
        WindowSeconds = Math.Clamp(WindowSeconds, 1, 60);
        TopCount = Math.Clamp(TopCount, 5, 2000);
        IgnoreNames = (IgnoreNames ?? new())
            .Select(n => n?.Trim() ?? "").Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList();
        return this;
    }

    public override string ToString() =>
        $"RunawayCores={RunawayCores}, RunawayMinutes={RunawayMinutes}, BackgroundSeconds={BackgroundSeconds}, WindowSeconds={WindowSeconds}, TopCount={TopCount}, IgnoreNames={IgnoreNames.Count}, AlwaysOnTop={AlwaysOnTop}, AgentAccess={AgentAccess}, AgentCommandLines={AgentCommandLines}";
}

/// <summary>Whether Nikomi starts with Windows, as Windows itself sees it.</summary>
public enum StartupState
{
    Off,
    On,
    /// <summary>Registered, but the user turned it off in Task Manager or Windows Settings.</summary>
    OffInWindows,
    /// <summary>Turned on by the organization's policy (Store package only): the user cannot turn it off.</summary>
    OnByPolicy,
    /// <summary>Turned off by the organization's policy (Store package only): the user cannot turn it on.</summary>
    OffByPolicy,
}

public static class StartupStates
{
    /// <summary>Nikomi starts with Windows, whoever turned it on.</summary>
    public static bool IsOn(this StartupState state) => state is StartupState.On or StartupState.OnByPolicy;
    /// <summary>Nikomi may turn it on or off itself; otherwise it is Windows' or the policy's switch.</summary>
    public static bool NikomiCanChange(this StartupState state) => state is StartupState.On or StartupState.Off;
    public static bool ByPolicy(this StartupState state) => state is StartupState.OnByPolicy or StartupState.OffByPolicy;
}

/// <summary>"Start with Windows" as a value under HKCU\...\Run, which Task Manager's Startup apps page also shows.</summary>
public static class StartupRegistration
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Nikomi";

    /// <summary>
    /// The command Windows runs at sign-in. A Nikomi that keeps its files in another folder than the
    /// default (<paramref name="dataDir"/> not null) starts there again, not in the default folder.
    /// </summary>
    public static string Command(string exePath, string? dataDir = null) =>
        dataDir == null ? $"\"{exePath}\" --background" : $"\"{exePath}\" --background --data-dir {Quote(dataDir)}";

    /// <summary>
    /// A path as one quoted argument. Windows paths cannot hold a quote, but a backslash right before the
    /// closing quote (a drive root, "D:\") would escape it, so trailing backslashes are doubled.
    /// </summary>
    private static string Quote(string path)
    {
        int trailing = path.Length - path.TrimEnd('\\').Length;
        return "\"" + path + new string('\\', trailing) + "\"";
    }

    /// <summary>The folder to name at startup: null for the default one, which needs no argument.</summary>
    public static string? DataDirArgument(string dataDir) =>
        string.Equals(DataFolder.FinalPath(dataDir), DataFolder.FinalPath(DataFolder.Default), StringComparison.OrdinalIgnoreCase) ? null : dataDir;

    public static bool IsEnabled(string exePath, string key = RunKey, string? dataDir = null)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return string.Equals(k?.GetValue(ValueName) as string, Command(exePath, dataDir), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where Task Manager and Settings > Apps > Startup record that the user turned an entry off.</summary>
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// The entry can be present and still not run: Windows keeps the user's off switch separately. That
    /// switch belongs to the user, so it is only read here, never changed.
    /// </summary>
    public static StartupState GetState(string exePath, string key = RunKey, string approvedKey = ApprovedKey, string? dataDir = null)
    {
        if (!IsEnabled(exePath, key, dataDir)) return StartupState.Off;
        using var k = Registry.CurrentUser.OpenSubKey(approvedKey);
        // The first byte is even while the entry may run (2) and odd once the user turned it off (3).
        bool off = k?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1;
        return off ? StartupState.OffInWindows : StartupState.On;
    }

    public static void Enable(string exePath, string key = RunKey, string? dataDir = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(ValueName, Command(exePath, dataDir));
    }

    public static void Disable(string key = RunKey)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
        k?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// "Start with Windows" in the Microsoft Store build. A packaged app cannot use the Run key (Windows keeps a
/// packaged app's writes under HKCU in a private copy, and the exe's folder changes with every update), so
/// it declares a startup task in its package manifest and turns that on and off.
/// </summary>
public static class StartupTaskInfo
{
    /// <summary>The TaskId of the windows.startupTask extension in the package manifest.</summary>
    public const string TaskId = "NikomiStartup";

    /// <summary>
    /// The task's state as Nikomi shows it. Neither the user's switch in Settings or Task Manager nor a policy
    /// may be overridden by the app; the user can change their own switch, but not a policy.
    /// </summary>
    public static StartupState StateOf(Windows.ApplicationModel.StartupTaskState state) => state switch
    {
        Windows.ApplicationModel.StartupTaskState.Enabled => StartupState.On,
        Windows.ApplicationModel.StartupTaskState.EnabledByPolicy => StartupState.OnByPolicy,
        Windows.ApplicationModel.StartupTaskState.DisabledByUser => StartupState.OffInWindows,
        Windows.ApplicationModel.StartupTaskState.DisabledByPolicy => StartupState.OffByPolicy,
        _ => StartupState.Off,
    };
}

public static class Log
{
    private static readonly object Gate = new();
    public static string? FilePath { get; set; }

    public static void Write(string message)
    {
        if (FilePath is not { } path) return;
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1 << 20) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
