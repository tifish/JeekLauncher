using System.Threading;

namespace JeekLauncher.Services;

[Flags]
internal enum ConfigChangeKind
{
    None = 0,
    Settings = 1,
    QuickWords = 2,
}

/// <summary>
/// Watches the config directory and fires a callback after changes have been quiet
/// for a while, so externally edited or synced portable config can be hot-reloaded.
/// </summary>
internal sealed class ConfigWatcher : IDisposable
{
    private const int QuietPeriodMs = 10_000;

    private readonly Action<ConfigChangeKind> _onChanged;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string _settingsPath = "";
    private string _quickWordsPath = "";
    private ConfigChangeKind _pendingChanges;
    private bool _disposed;

    public ConfigWatcher(Action<ConfigChangeKind> onChanged)
    {
        _onChanged = onChanged;
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>(Re)start watching the given directory.</summary>
    public void Watch(string dir)
    {
        Stop();
        if (_disposed || !Directory.Exists(dir))
            return;

        _settingsPath = Path.GetFullPath(Path.Combine(dir, "settings.json"));
        _quickWordsPath = Path.GetFullPath(Path.Combine(dir, "QuickWords.json"));

        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            };
            _watcher.Changed += OnEvent;
            _watcher.Created += OnEvent;
            _watcher.Deleted += OnEvent;
            _watcher.Renamed += OnEvent;
            _watcher.Error += (_, _) => Schedule(ConfigChangeKind.Settings | ConfigChangeKind.QuickWords);
            _watcher.EnableRaisingEvents = true;
        }
        catch
        {
            // Inaccessible directory: skip watching.
        }
    }

    private void OnEvent(object sender, FileSystemEventArgs e)
    {
        var changes = ChangeKindFor(e.FullPath);
        if (e is RenamedEventArgs renamed)
            changes |= ChangeKindFor(renamed.OldFullPath);

        if (changes.HasFlag(ConfigChangeKind.Settings) && Storage.IsRecentSelfWrite(ConfigChangeKind.Settings))
            changes &= ~ConfigChangeKind.Settings;
        if (changes.HasFlag(ConfigChangeKind.QuickWords) && Storage.IsRecentSelfWrite(ConfigChangeKind.QuickWords))
            changes &= ~ConfigChangeKind.QuickWords;

        if (changes != ConfigChangeKind.None)
            Schedule(changes);
    }

    private ConfigChangeKind ChangeKindFor(string path)
    {
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch { return ConfigChangeKind.None; }

        if (string.Equals(fullPath, _settingsPath, StringComparison.OrdinalIgnoreCase))
            return ConfigChangeKind.Settings;
        if (string.Equals(fullPath, _quickWordsPath, StringComparison.OrdinalIgnoreCase))
            return ConfigChangeKind.QuickWords;
        return ConfigChangeKind.None;
    }

    private void Schedule(ConfigChangeKind changes)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _pendingChanges |= changes;
            _debounce.Change(QuietPeriodMs, Timeout.Infinite);
        }
    }

    private void Flush()
    {
        ConfigChangeKind changes;
        lock (_gate)
        {
            if (_disposed || _pendingChanges == ConfigChangeKind.None)
                return;

            changes = _pendingChanges;
            _pendingChanges = ConfigChangeKind.None;
        }

        _onChanged(changes);
    }

    private void Stop()
    {
        lock (_gate)
        {
            _pendingChanges = ConfigChangeKind.None;
            if (!_disposed)
                _debounce.Change(Timeout.Infinite, Timeout.Infinite);
        }

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
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pendingChanges = ConfigChangeKind.None;
            _debounce.Dispose();
        }

        Stop();
    }
}
