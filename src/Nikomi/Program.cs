using Nikomi.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Nikomi;

/// <summary>Command line: [--background] [--data-dir &lt;folder&gt;]</summary>
public sealed record Options(bool Background, string DataDir, IReadOnlyList<string> Ignored)
{
    public string SettingsPath => DataFolder.SettingsPath(DataDir);
    public string LogPath => DataFolder.LogPath(DataDir);
    public string HistoryPath => DataFolder.HistoryPath(DataDir);

    /// <param name="defaultDataDir">The folder used when none is given; null for <see cref="DataFolder.Default"/>.</param>
    public static Options Parse(string[] args, string? defaultDataDir = null)
    {
        var parsed = Arguments.Parse(args, switches: ["--background"], options: ["--data-dir"], command: false);
        return new Options(parsed.Has("--background"), DataFolder.Resolve(parsed.Value("--data-dir") ?? defaultDataDir), parsed.Unknown);
    }
}

/// <summary>One Nikomi per data folder. Starting it again just opens the running one's window.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    public EventWaitHandle ShowRequested { get; }

    private SingleInstance(Mutex mutex, EventWaitHandle show) { _mutex = mutex; ShowRequested = show; }

    private static string Id(string dataDir) => DataFolder.InstanceId(dataDir);

    /// <summary>
    /// Null when another Nikomi already runs on this folder. Throws <see cref="BlockedException"/> when the
    /// names are taken by something this account may not open (for example another account that created
    /// them first, or an object of another kind with the same name).
    /// </summary>
    public static SingleInstance? TryClaim(string dataDir)
    {
        Mutex mutex;
        bool created;
        try
        {
            mutex = new Mutex(true, $@"Local\Nikomi.Instance.{Id(dataDir)}", out created);
        }
        catch (Exception ex) when (IsBlocked(ex))
        {
            throw new BlockedException(ex);
        }
        if (!created) { mutex.Dispose(); return null; }
        try
        {
            return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Nikomi.Show.{Id(dataDir)}"));
        }
        catch (Exception ex) when (IsBlocked(ex))
        {
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            throw new BlockedException(ex);
        }
    }

    public static void SignalExisting(string dataDir)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting($@"Local\Nikomi.Show.{Id(dataDir)}", out var ev)) using (ev) ev.Set();
        }
        catch (Exception ex) when (IsBlocked(ex))
        {
            Log.Write("Could not ask the running Nikomi to open its window: " + ex.Message);
        }
    }

    private static bool IsBlocked(Exception ex) => ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException;

    public sealed class BlockedException(Exception inner) : Exception(inner.Message, inner);

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
        ShowRequested.Dispose();
    }
}

public static class Program
{
    public static Options Options { get; private set; } = null!;
    /// <summary>What the app measures: this PC, or made-up processes in a demo build.</summary>
    public static SystemSource Source { get; private set; } = null!;
    public static SingleInstance? Instance { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
#if DEMO
        // A demo build shows made-up processes (DemoSource) and keeps its files apart from a real Nikomi's.
        Source = new DemoSource();
        Options = Options.Parse(args, DemoSource.DataDir);
#else
        Source = new SystemSource();
        Options = Options.Parse(args);
#endif
        Directory.CreateDirectory(Options.DataDir);
        Log.FilePath = Options.LogPath;
#if DEMO
        DemoSource.WriteHistory(DataFolder.HistoryPath(Options.DataDir));
#endif
        // There is no console to complain on: an argument Nikomi does not know is noted and passed over.
        if (Options.Ignored.Count > 0) Log.Write("Ignored unknown arguments: " + string.Join(" ", Options.Ignored));
        if (PackageIdentity.LocalFolderError is { } why)
            Log.Write("Windows did not give the package's own folder, so the unpackaged default folder is used: " + why);
        // The Store package's startup task passes no arguments: a start at sign-in is told by how Windows started it.
        if (!Options.Background && StartedAtSignIn()) Options = Options with { Background = true };

        try
        {
            Instance = SingleInstance.TryClaim(Options.DataDir);
        }
        catch (SingleInstance.BlockedException ex)
        {
            // Another account (or another program) holds the names Nikomi uses to run once per folder, and
            // will not let this one open them. Say so and stop, instead of crashing at every start.
            Log.Write("Not started: the single-instance lock for this data folder is held by something this account cannot open: " + ex.Message);
            MessageBox(IntPtr.Zero,
                "Nikomi could not start: another program or Windows account holds the lock Nikomi uses to run only once, " +
                "and does not let this account open it.\n\nSigning out of Windows and back in usually clears it. Details are in " + Options.LogPath,
                "Nikomi", 0x10 /* MB_ICONERROR */);
            return 1;
        }
        if (Instance == null)
        {
            if (!Options.Background) SingleInstance.SignalExisting(Options.DataDir);
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(initParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        Instance.Dispose();
        return 0;
    }

    /// <summary>True when the Store package's startup task started Nikomi at sign-in. Always false unpackaged.</summary>
    private static bool StartedAtSignIn()
    {
        if (!PackageIdentity.IsPackaged) return false;
        try
        {
            var kind = Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind;
            return kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask;
        }
        catch (Exception ex)
        {
            Log.Write("Could not tell how Windows started Nikomi; opening the window: " + ex.Message);
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);
}
