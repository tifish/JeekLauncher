using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekLauncher.Services;

/// <summary>
/// Keeps this process's environment block synchronized with Windows system/user environment
/// variable changes. Listens for top-level <c>WM_SETTINGCHANGE</c> broadcasts with
/// <c>lParam == "Environment"</c> and rebuilds the effective environment via
/// <c>CreateEnvironmentBlock</c>, so applications launched by JeekLauncher inherit the
/// latest variables without restarting the launcher.
/// </summary>
internal sealed partial class EnvironmentSync : IDisposable
{
    private const uint WmSettingChange = 0x001A;
    private const uint TokenQuery = 0x0008;
    private const uint TokenDuplicate = 0x0002;
    private const int MaxRecordedChanges = 32;

    private static readonly ILogger Log = LogManager.CreateLogger(nameof(EnvironmentSync));

    private readonly object _refreshLock = new();
    private readonly object _scheduleLock = new();
    private HashSet<string>? _knownSystemKeys;
    private int _pending; // 0 = idle, 1 = running, 2 = running + another notification arrived
    private bool _disposed;

    private int _notificationCount;
    private int _refreshCount;
    private DateTimeOffset? _lastRefreshUtc;
    private int _lastAdded;
    private int _lastUpdated;
    private int _lastRemoved;
    private IReadOnlyList<string> _lastChanges = Array.Empty<string>();
    private string? _lastError;

    public bool HookAttached { get; set; }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateEnvironmentBlock(
        out IntPtr lpEnvironment,
        IntPtr hToken,
        [MarshalAs(UnmanagedType.Bool)] bool bInherit);

    [LibraryImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    /// <summary>
    /// Snapshots the system/user variable names present at startup so variables deleted
    /// later are removed from this process while parent-only variables stay untouched.
    /// </summary>
    public void CaptureBaseline()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return;

            try
            {
                var baseline = ReadSystemEnvironmentBlock();
                _knownSystemKeys = new HashSet<string>(baseline.Keys, StringComparer.OrdinalIgnoreCase);
                _lastError = null;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.ZLogWarning($"Failed to capture baseline environment block: {ex.Message}");
            }
        }
    }

    /// <summary>Avalonia <c>Win32Properties.CustomWndProcHookCallback</c> handler.</summary>
    public IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_disposed && msg == WmSettingChange && lParam != IntPtr.Zero)
        {
            string? area = Marshal.PtrToStringUni(lParam);
            if (string.Equals(area, "Environment", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _notificationCount);
                ScheduleRefresh();
            }
        }

        // Never mark WM_SETTINGCHANGE as handled: Avalonia and other hooks also listen for it.
        return IntPtr.Zero;
    }

    /// <summary>
    /// Synchronously refreshes the current process's environment block from Windows.
    /// Safe to call from any thread; concurrent callers are serialized.
    /// </summary>
    public EnvironmentSyncSnapshot Refresh()
    {
        lock (_refreshLock)
        {
            if (_disposed)
                return BuildSnapshotLocked();

            try
            {
                var fresh = ReadSystemEnvironmentBlock();
                var current = ReadCurrentProcessEnvironment();
                var previousKnown = _knownSystemKeys;

                int added = 0;
                int updated = 0;
                int removed = 0;
                var changes = new List<string>();

                foreach (var (key, newValue) in fresh)
                {
                    if (!current.TryGetValue(key, out string? oldValue))
                    {
                        Environment.SetEnvironmentVariable(key, newValue, EnvironmentVariableTarget.Process);
                        added++;
                        if (changes.Count < MaxRecordedChanges)
                            changes.Add("+" + key);
                    }
                    else if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    {
                        Environment.SetEnvironmentVariable(key, newValue, EnvironmentVariableTarget.Process);
                        updated++;
                        if (changes.Count < MaxRecordedChanges)
                            changes.Add("~" + key);
                    }
                }

                if (previousKnown is not null)
                {
                    foreach (string key in previousKnown)
                    {
                        if (!fresh.ContainsKey(key) && current.ContainsKey(key))
                        {
                            Environment.SetEnvironmentVariable(key, null, EnvironmentVariableTarget.Process);
                            removed++;
                            if (changes.Count < MaxRecordedChanges)
                                changes.Add("-" + key);
                        }
                    }
                }

                _knownSystemKeys = new HashSet<string>(fresh.Keys, StringComparer.OrdinalIgnoreCase);
                _refreshCount++;
                _lastRefreshUtc = DateTimeOffset.UtcNow;
                _lastAdded = added;
                _lastUpdated = updated;
                _lastRemoved = removed;
                _lastChanges = changes;
                _lastError = null;

                if (added + updated + removed > 0)
                {
                    Log.ZLogInformation(
                        $"Refreshed process environment: +{added} ~{updated} -{removed} ({string.Join(", ", changes)})");
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                Log.ZLogWarning($"Failed to refresh process environment: {ex.Message}");
            }

            return BuildSnapshotLocked();
        }
    }

    public EnvironmentSyncSnapshot GetSnapshot()
    {
        lock (_refreshLock)
            return BuildSnapshotLocked();
    }

    public void Dispose()
    {
        lock (_scheduleLock)
        {
            _disposed = true;
            _pending = 0;
        }
    }

    private void ScheduleRefresh()
    {
        lock (_scheduleLock)
        {
            if (_disposed)
                return;

            if (_pending != 0)
            {
                _pending = 2;
                return;
            }

            _pending = 1;
        }

        // Run off the UI thread so SendMessageTimeout(HWND_BROADCAST) never stalls on us.
        _ = Task.Run(ProcessRefreshLoop);
    }

    private void ProcessRefreshLoop()
    {
        while (true)
        {
            Refresh();

            lock (_scheduleLock)
            {
                if (_disposed || _pending != 2)
                {
                    _pending = 0;
                    return;
                }

                _pending = 1;
            }
        }
    }

    private EnvironmentSyncSnapshot BuildSnapshotLocked() => new(
        HookAttached,
        _knownSystemKeys?.Count ?? 0,
        Volatile.Read(ref _notificationCount),
        _refreshCount,
        _lastRefreshUtc,
        _lastAdded,
        _lastUpdated,
        _lastRemoved,
        _lastChanges,
        _lastError);

    private static Dictionary<string, string> ReadSystemEnvironmentBlock()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery | TokenDuplicate, out IntPtr token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");

        try
        {
            if (!CreateEnvironmentBlock(out IntPtr envBlock, token, bInherit: false))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");

            try
            {
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                IntPtr ptr = envBlock;
                while (true)
                {
                    string? entry = Marshal.PtrToStringUni(ptr);
                    if (string.IsNullOrEmpty(entry))
                        break;

                    ptr = IntPtr.Add(ptr, (entry.Length + 1) * sizeof(char));

                    int eq = entry.IndexOf('=');
                    if (eq > 0)
                        result[entry[..eq]] = entry[(eq + 1)..];
                }

                return result;
            }
            finally
            {
                DestroyEnvironmentBlock(envBlock);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static Dictionary<string, string> ReadCurrentProcessEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables(EnvironmentVariableTarget.Process))
        {
            if (entry.Key is string key && key.Length > 0 && key[0] != '=' && entry.Value is string value)
                result[key] = value;
        }

        return result;
    }
}

internal sealed record EnvironmentSyncSnapshot(
    bool HookAttached,
    int BaselineCount,
    int NotificationCount,
    int RefreshCount,
    DateTimeOffset? LastRefreshUtc,
    int LastAdded,
    int LastUpdated,
    int LastRemoved,
    IReadOnlyList<string> LastChanges,
    string? LastError);
