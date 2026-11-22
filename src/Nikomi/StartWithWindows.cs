using Nikomi.Core;
using Windows.ApplicationModel;

namespace Nikomi;

/// <summary>
/// "Start with Windows", done the way this build can: a value under the Run key when Nikomi runs unpackaged
/// (the zip, winget), a startup task when it runs from its Microsoft Store package.
/// </summary>
public abstract class StartWithWindows
{
    public abstract StartupState State { get; }
    /// <summary>What is changed, for the log.</summary>
    public abstract string Where { get; }
    /// <summary>Reads the current state from Windows, where reading it takes a moment.</summary>
    public virtual Task LoadAsync() => Task.CompletedTask;
    public abstract Task SetAsync(bool enable);

    public static StartWithWindows For(string exePath, string dataDir) =>
        PackageIdentity.IsPackaged ? new PackagedStartup() : new RegistryStartup(exePath, dataDir);
}

/// <summary>HKCU\...\Run with the exe's path and --background, and --data-dir for a folder other than the default.</summary>
public sealed class RegistryStartup(string exePath, string dataDir) : StartWithWindows
{
    private readonly string? _dataDir = StartupRegistration.DataDirArgument(dataDir);

    public override StartupState State => StartupRegistration.GetState(exePath, dataDir: _dataDir);
    public override string Where => exePath;

    public override Task SetAsync(bool enable)
    {
        if (enable) StartupRegistration.Enable(exePath, dataDir: _dataDir);
        else StartupRegistration.Disable();
        return Task.CompletedTask;
    }
}

/// <summary>
/// The startup task declared in the package manifest. It always starts the default data folder, and with
/// no arguments: Program tells a start at sign-in by how Windows activated the app.
/// </summary>
public sealed class PackagedStartup : StartWithWindows
{
    private StartupTask? _task;

    /// <summary>
    /// As of the last <see cref="LoadAsync"/>: the app loads it when it starts, and again before Settings or the
    /// tray menu's Start with Windows use it, since the user can change it in Windows at any time. Off when it
    /// could not be read; the log says why, and turning it on tries again.
    /// </summary>
    public override StartupState State => _task is { } task ? StartupTaskInfo.StateOf(task.State) : StartupState.Off;
    public override string Where => "startup task " + StartupTaskInfo.TaskId;

    public override async Task LoadAsync()
    {
        try
        {
            _task = await StartupTask.GetAsync(StartupTaskInfo.TaskId);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read the startup task {StartupTaskInfo.TaskId}: {ex.Message}");
        }
    }

    public override async Task SetAsync(bool enable)
    {
        _task ??= await StartupTask.GetAsync(StartupTaskInfo.TaskId);
        // Neither call overrides the user's or a policy's switch: Windows keeps those, and the state says so.
        if (enable) await _task.RequestEnableAsync();
        else _task.Disable();
    }
}
