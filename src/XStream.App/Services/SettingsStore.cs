using Avalonia.Threading;
using XStream.Core.Config;

namespace XStream.App.Services;

/// <summary>
/// Current settings. A running stream also changes settings.json (from its in-stream menu), so every change
/// here is read-modify-write against the file, and the file is watched so the Settings page shows changes
/// made in the stream. Changed is raised on the UI thread.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly FileSystemWatcher? _watcher;
    private IDisposable? _debounce;

    public AppSettings Current { get; private set; } = AppSettings.Load();

    public event Action<AppSettings>? Changed;

    public SettingsStore()
    {
        try
        {
            _watcher = new FileSystemWatcher(AppPaths.Root, Path.GetFileName(AppPaths.Settings))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => ReloadSoon();
            _watcher.Created += (_, _) => ReloadSoon();
            _watcher.Renamed += (_, _) => ReloadSoon();
        }
        catch { /* no watching: still reloads when a stream ends */ }
    }

    public void Update(Func<AppSettings, AppSettings> change)
    {
        var disk = AppSettings.Load();
        var next = change(disk);
        if (next == disk && next == Current) return;
        Current = next;
        if (next != disk) { try { next.Save(); } catch (IOException) { /* keep in memory; next change retries */ } }
        Changed?.Invoke(Current);
    }

    /// <summary>Pick up changes made by the stream process.</summary>
    public void Reload()
    {
        var disk = AppSettings.Load();
        if (disk == Current) return;
        Current = disk;
        Changed?.Invoke(Current);
    }

    private void ReloadSoon() => Dispatcher.UIThread.Post(() =>
    {
        _debounce?.Dispose();
        _debounce = DispatcherTimer.RunOnce(Reload, TimeSpan.FromMilliseconds(120));
    });

    public void Dispose() => _watcher?.Dispose();
}
