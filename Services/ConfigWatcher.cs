using System.Threading;

namespace JeekLauncher.Services;

/// <summary>
/// Watches the config directory and fires a (debounced) callback when Settings.json
/// or QuickWords.json changes on disk — e.g. hand-edited, or synced from another
/// machine in portable mode — so the app can hot-reload them.
/// </summary>
internal sealed class ConfigWatcher : IDisposable
{
    private const int DebounceMs = 500;

    private static readonly string[] WatchedFiles = { "Settings.json", "QuickWords.json" };

    private readonly Action _onChanged;
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public ConfigWatcher(Action onChanged)
    {
        _onChanged = onChanged;
        _debounce = new Timer(_ => _onChanged(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>(Re)start watching the given directory.</summary>
    public void Watch(string dir)
    {
        Stop();
        if (_disposed || !Directory.Exists(dir))
            return;

        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Inaccessible directory: skip watching.
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        string name = Path.GetFileName(e.Name ?? e.FullPath);
        if (WatchedFiles.Contains(name, StringComparer.OrdinalIgnoreCase))
            Schedule();
    }

    private void Schedule()
    {
        if (!_disposed)
            _debounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void Stop()
    {
        if (_watcher is null)
            return;

        try
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }
        catch
        {
            // Ignore disposal errors.
        }
        _watcher = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _debounce.Dispose();
    }
}
