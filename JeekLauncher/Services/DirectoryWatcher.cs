using System.Threading;

namespace JeekLauncher.Services;

/// <summary>
/// Watches the scanned directories and fires a (debounced) callback whenever a
/// shortcut is added, removed or renamed — so the catalog stays in sync without a
/// manual rescan. Installing/uninstalling apps touches many files at once, hence
/// the debounce.
/// </summary>
internal sealed class DirectoryWatcher : IDisposable
{
    private const int DebounceMs = 800;

    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Timer _debounce;
    private readonly Action _onChanged;
    private readonly object _gate = new();
    private bool _disposed;

    public DirectoryWatcher(Action onChanged)
    {
        _onChanged = onChanged;
        _debounce = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>(Re)start watching the given directories.</summary>
    public void Watch(IEnumerable<string> directories)
    {
        Stop();
        if (_disposed)
            return;

        foreach (string dir in directories)
        {
            if (!Directory.Exists(dir))
                continue;

            AddWatcher(dir, NotifyFilters.DirectoryName);
            AddWatcher(dir, NotifyFilters.FileName | NotifyFilters.LastWrite, "*.lnk", "*.url");
        }
    }

    private void AddWatcher(string dir, NotifyFilters events, params string[] filters)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = events };
            foreach (var filter in filters)
                watcher.Filters.Add(filter);
            watcher.Created += OnChange;
            watcher.Deleted += OnChange;
            watcher.Renamed += OnChange;
            watcher.Changed += OnChange;
            watcher.Error += (_, _) => ScheduleRefresh();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch
        {
            watcher?.Dispose();
        }
    }

    private void OnChange(object sender, FileSystemEventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        lock (_gate)
        {
            if (!_disposed)
                _debounce.Change(DebounceMs, Timeout.Infinite);
        }
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
        }

        _onChanged();
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (!_disposed)
                _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        }

        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }
        }
        _watchers.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounce.Dispose();
        }

        Stop();
    }
}
