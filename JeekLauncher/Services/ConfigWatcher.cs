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
    private const int DefaultQuietPeriodMs = 10_000;

    private readonly Action<ConfigChangeKind> _onChanged;
    private readonly int _quietPeriodMs;
    private readonly Timer _debounce;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string _settingsPath = "";
    private string _legacyQuickWordsPath = "";
    private string _quickWordsDir = "";
    private string _ownQuickWordsPath = "";
    private ConfigChangeKind _pendingChanges;
    private bool _disposed;

    public ConfigWatcher(Action<ConfigChangeKind> onChanged, int quietPeriodMs = DefaultQuietPeriodMs)
    {
        _onChanged = onChanged;
        _quietPeriodMs = Math.Max(50, quietPeriodMs);
        _debounce = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>(Re)start watching the given directory.</summary>
    public void Watch(string dir)
    {
        Stop();
        if (_disposed || !Directory.Exists(dir))
            return;

        _settingsPath = NormalizePath(Path.Combine(dir, "settings.json"));
        _legacyQuickWordsPath = NormalizePath(Path.Combine(dir, Storage.LegacyQuickWordsFileName));
        _quickWordsDir = NormalizePath(Path.Combine(dir, Storage.QuickWordsDirName));
        _ownQuickWordsPath = NormalizePath(Path.Combine(_quickWordsDir, $"{Storage.MachineName}.json"));

        try
        {
            _watcher = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
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
        var changes = ChangeKindFor(e.FullPath, e.ChangeType);
        if (e is RenamedEventArgs renamed)
            changes |= ChangeKindFor(renamed.OldFullPath, e.ChangeType);

        if (changes != ConfigChangeKind.None)
            Schedule(changes);
    }

    private ConfigChangeKind ChangeKindFor(string path, WatcherChangeTypes changeType)
    {
        string fullPath;
        try { fullPath = NormalizePath(path); }
        catch { return ConfigChangeKind.None; }

        if (string.Equals(fullPath, _settingsPath, StringComparison.OrdinalIgnoreCase))
            return Storage.IsRecentSelfWrite(ConfigChangeKind.Settings) ? ConfigChangeKind.None : ConfigChangeKind.Settings;

        if (string.Equals(fullPath, _legacyQuickWordsPath, StringComparison.OrdinalIgnoreCase))
            return ConfigChangeKind.QuickWords;

        if (string.Equals(fullPath, _quickWordsDir, StringComparison.OrdinalIgnoreCase))
        {
            // Ignore directory LastWrite noise caused by unrelated files (like .tmp) inside QuickWords/.
            // Only react when the QuickWords folder itself is created, renamed, or deleted.
            if (changeType == WatcherChangeTypes.Changed)
                return ConfigChangeKind.None;

            return Storage.IsRecentSelfWrite(ConfigChangeKind.QuickWords) ? ConfigChangeKind.None : ConfigChangeKind.QuickWords;
        }

        string? parentDir = Path.GetDirectoryName(fullPath);
        if (parentDir is not null
            && string.Equals(NormalizePath(parentDir), _quickWordsDir, StringComparison.OrdinalIgnoreCase)
            && fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            // Only suppress self-write for this machine's own shard; never suppress
            // another machine's shard arriving in the same window.
            if (string.Equals(fullPath, _ownQuickWordsPath, StringComparison.OrdinalIgnoreCase)
                && Storage.IsRecentSelfWrite(ConfigChangeKind.QuickWords))
            {
                return ConfigChangeKind.None;
            }

            return ConfigChangeKind.QuickWords;
        }

        return ConfigChangeKind.None;
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private void Schedule(ConfigChangeKind changes)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _pendingChanges |= changes;
            _debounce.Change(_quietPeriodMs, Timeout.Infinite);
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
