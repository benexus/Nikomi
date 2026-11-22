using Nikomi.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Nikomi;

public partial class App : Application
{
    public static AppController Controller { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        // A tray app must not vanish on one bad refresh: log it and keep running.
        UnhandledException += (_, e) =>
        {
            var data = string.Join("; ", e.Exception.Data.Keys.Cast<object>().Select(k => $"{k}={e.Exception.Data[k]}"));
            Log.Write($"Unhandled error: {e.Message} | {data} | {e.Exception}");
            e.Handled = true;
        };
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log.Write("XAML resource lookup failed: " + e.Message);
        DebugSettings.BindingFailed += (_, e) => Log.Write("XAML binding failed: " + e.Message);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Controller = new AppController(Program.Options, Program.Source, Program.Instance!, DispatcherQueue.GetForCurrentThread());
        Controller.Start();
    }
}
