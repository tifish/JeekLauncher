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

            try
            {
                var watcher = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch
            {
                // A directory may be inaccessible; just skip watching it.
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (IsRelevant(e.FullPath))
            ScheduleRefresh();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsRelevant(e.FullPath) || IsRelevant(e.OldFullPath))
            ScheduleRefresh();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or the folder went away: a rescan will resynchronise.
        ScheduleRefresh();
    }

    private static bool IsRelevant(string path)
    {
        string ext = Path.GetExtension(path);
        // Empty extension => a directory event (app folder added/removed).
        return ext.Length == 0
            || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

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
