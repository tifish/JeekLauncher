using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using JeekTools;

namespace JeekLauncher.Services;

/// <summary>
/// On-disk locations for this app's config, layered over the generic
/// <see cref="SettingsStorage"/> path scheme in JeekTools.
///
/// <para><b>Machine-local</b> data (usage counts, local scan paths, and the chosen
/// storage location itself) never roams — it always lives in
/// <c>%LocalAppData%\JeekLauncher\Config</c>.</para>
///
/// <para><b>Roamable</b> data (settings + quick words) lives in a <c>Config</c> folder
/// whose parent depends on the location: roaming AppData (default), next to the
/// executable (portable), or a custom folder. If the portable <c>Config</c> folder
/// exists at startup, portable mode is forced regardless of the saved location.</para>
/// </summary>
public static class Storage
{
    private static readonly SettingsStorage Backing = new("JeekLauncher");

    // ---- Machine-local (never roams) ---------------------------------------

    public static string LocalConfigDir => Backing.LocalConfigDir;

    /// <summary>Machine-local settings file (<c>settings.json</c> in local AppData).</summary>
    public static string LocalSettingsPath => Backing.MachineSettingsPath;

    public static string UsagePath => Path.Combine(LocalConfigDir, "Usage.json");

    /// <summary>Pre-<see cref="SettingsStorage"/> machine-local file, migrated on first load.</summary>
    internal static string LegacyLocalSettingsPath => Path.Combine(LocalConfigDir, "LocalSettings.json");

    // ---- Roamable (location depends on the mode) ---------------------------

    /// <summary>The saved roamable storage location (before the portable-folder override).</summary>
    public static StorageLocation Location { get; private set; } = StorageLocation.UserDirectory;

    /// <summary>Base folder for <see cref="StorageLocation.CustomDirectory"/>; the "Config" child is appended.</summary>
    public static string CustomDir { get; private set; } = "";

    /// <summary>The portable Config folder exists, forcing portable mode at startup.</summary>
    public static bool PortableForced => Backing.IsPortable;

    /// <summary>The location actually in effect (an existing portable folder overrides the saved one).</summary>
    public static StorageLocation EffectiveLocation => Backing.ResolveEffectiveLocation(Location);

    /// <summary>Sanitized machine name used to name this machine's QuickWords shard.</summary>
    public static string MachineName { get; } = QuickWordStore.SanitizeMachineName(Environment.MachineName);

    internal const string QuickWordsDirName = "QuickWords";
    internal const string LegacyQuickWordsFileName = "QuickWords.json";

    /// <summary>Where roamable settings (settings.json + QuickWords/&lt;MachineName&gt;.json) live right now.</summary>
    public static string RoamableConfigDir => ConfigRootFor(EffectiveLocation, CustomDir);

    public static string SettingsPath => Path.Combine(RoamableConfigDir, "settings.json");

    /// <summary>Subdirectory holding per-machine QuickWords shards.</summary>
    public static string QuickWordsDir => Path.Combine(RoamableConfigDir, QuickWordsDirName);

    /// <summary>This machine's own QuickWords shard file.</summary>
    public static string QuickWordsPath => Path.Combine(QuickWordsDir, $"{MachineName}.json");

    /// <summary>Legacy single-file QuickWords path, read as a fallback layer when present.</summary>
    public static string LegacyQuickWordsPath => Path.Combine(RoamableConfigDir, LegacyQuickWordsFileName);

    public static string ConfigRootFor(StorageLocation location, string? customDir)
        => Backing.ResolveConfigRoot(location, customDir);

    /// <summary>Primes the active location from the saved machine-local settings (no migration).</summary>
    public static void SetLocation(StorageLocation location, string? customDir)
    {
        Location = Backing.NormalizeLocation(location);
        CustomDir = customDir ?? "";
    }

    internal static bool IsValidCustomDirectory(string? customDir)
    {
        if (string.IsNullOrWhiteSpace(customDir) || !Path.IsPathFullyQualified(customDir))
            return false;
        try
        {
            string root = NormalizePath(ConfigRootFor(StorageLocation.CustomDirectory, customDir));
            string local = NormalizePath(LocalConfigDir);
            return !string.Equals(root, local, StringComparison.OrdinalIgnoreCase)
                && !root.StartsWith(local + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !local.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Whether switching would place the new Config directory inside the current one.</summary>
    public static bool WouldMoveRoamableConfigIntoItself(StorageLocation newLocation, string? newCustomDir)
    {
        string from = NormalizePath(RoamableConfigDir);
        string to = NormalizePath(ConfigRootFor(newLocation, newCustomDir ?? ""));
        return !string.Equals(from, to, StringComparison.OrdinalIgnoreCase)
               && to.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a switch to this location would land somewhere other than the current folder.</summary>
    public static bool WouldChangeRoamableConfigDir(StorageLocation newLocation, string? newCustomDir)
        => !string.Equals(
            NormalizePath(RoamableConfigDir),
            NormalizePath(ConfigRootFor(newLocation, newCustomDir ?? "")),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Switches the roamable storage location. With <paramref name="moveFiles"/> the whole
    /// roamable <c>Config</c> folder is moved to the new root; without it the new location is
    /// simply used from now on. Machine-local data is never touched. Leaving portable mode
    /// always deletes the executable-side folder, or the next startup would be forced back
    /// into portable mode.
    /// </summary>
    public static void SwitchLocation(StorageLocation newLocation, string? newCustomDir, bool moveFiles)
    {
        if (newLocation == StorageLocation.CustomDirectory && !IsValidCustomDirectory(newCustomDir))
            throw new InvalidOperationException("Invalid custom configuration directory.");
        if (WouldMoveRoamableConfigIntoItself(newLocation, newCustomDir))
            throw new InvalidOperationException("The new Config directory cannot be inside the current one.");

        string from = RoamableConfigDir;
        string to = ConfigRootFor(newLocation, newCustomDir ?? "");
        bool leavingPortable = EffectiveLocation == StorageLocation.ProgramDirectory
                               && newLocation != StorageLocation.ProgramDirectory;

        if (moveFiles || leavingPortable)
            SettingsStorage.MoveConfigRoot(from, to);
        else
            Directory.CreateDirectory(to);

        SetLocation(newLocation, newCustomDir);
    }

    private sealed record SelfWrite(string Path, string Contents, long Timestamp);
    private static SelfWrite? _lastSettingsWrite;
    private static SelfWrite? _lastQuickWordsWrite;

    internal static void MarkSelfWrite(ConfigChangeKind kind, string path, string contents)
    {
        var write = new SelfWrite(path, contents, Stopwatch.GetTimestamp());
        if (kind == ConfigChangeKind.Settings)
            Volatile.Write(ref _lastSettingsWrite, write);
        else
            Volatile.Write(ref _lastQuickWordsWrite, write);
    }

    internal static bool IsRecentSelfWrite(ConfigChangeKind kind, string path)
    {
        var write = kind == ConfigChangeKind.Settings
            ? Volatile.Read(ref _lastSettingsWrite) : Volatile.Read(ref _lastQuickWordsWrite);
        if (write is null || !string.Equals(write.Path, path, StringComparison.OrdinalIgnoreCase)
            || Stopwatch.GetElapsedTime(write.Timestamp) > TimeSpan.FromSeconds(2))
            return false;
        try { return File.ReadAllText(path) == write.Contents; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void EnsureRoamableConfigDir() => Directory.CreateDirectory(RoamableConfigDir);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>Roamable user preferences: global hotkey, UI language, theme, updates.</summary>
public sealed class AppSettings
{
    /// <summary>Global hotkey, e.g. "Alt+Space", "Ctrl+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>UI language: "" (follow system), "en" or "zh".</summary>
    public string Language { get; set; } = "";

    /// <summary>UI theme: "" (follow system), "Light" or "Dark".</summary>
    public string Theme { get; set; } = "";

    /// <summary>Silently check for updates a few seconds after launch.</summary>
    public bool CheckUpdateOnStartup { get; set; } = true;

    /// <summary>How often to re-check for updates while running, in hours.
    /// Allowed values: 0 (never), 6, 24 (daily), 168 (weekly).</summary>
    public int UpdateCheckIntervalHours { get; set; } = 24;

    /// <summary>Last state written to disk; the merge base that keeps a concurrent
    /// instance's edits when this one saves.</summary>
    private AppSettings? _baseline;

    public static AppSettings Load(AppSettings? lastReadable = null)
    {
        bool existed = JsonSettingsFile.TryLoad(Storage.SettingsPath, out AppSettings settings);
        if (!existed && lastReadable is not null && File.Exists(Storage.SettingsPath))
            return lastReadable;
        Normalize(settings);
        settings._baseline = JsonSettingsFile.Clone(settings);

        // Write the defaults out on first run so the file is there to be found and edited.
        if (!existed && !File.Exists(Storage.SettingsPath))
            settings.Save(forceAllLocal: true);

        return settings;
    }

    /// <param name="forceAllLocal">Ignore whatever is already at the target path — used
    /// right after a storage switch, where the destination is a different folder.</param>
    public void Save(bool forceAllLocal = false)
    {
        if (JsonSettingsFile.TryMergeAndWrite(
                Storage.SettingsPath, _baseline ?? new AppSettings(), this, Normalize, forceAllLocal, out var merged))
        {
            Storage.MarkSelfWrite(ConfigChangeKind.Settings, Storage.SettingsPath, JsonSettingsFile.Serialize(merged));
            CopyFrom(merged);
            _baseline = JsonSettingsFile.Clone(merged);
        }
    }

    /// <summary>Updates settings values in place while preserving the merge baseline.</summary>
    public void ApplyFrom(AppSettings other) => CopyFrom(other);

    private void CopyFrom(AppSettings other)
    {
        Hotkey = other.Hotkey;
        Language = other.Language;
        Theme = other.Theme;
        CheckUpdateOnStartup = other.CheckUpdateOnStartup;
        UpdateCheckIntervalHours = other.UpdateCheckIntervalHours;
    }

    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Hotkey))
            settings.Hotkey = "Alt+Space";
        if (settings.Language is not ("en" or "zh"))
            settings.Language = "";
        if (settings.Theme is not ("Light" or "Dark"))
            settings.Theme = "";
        if (settings.UpdateCheckIntervalHours is not (6 or 24 or 168))
            settings.UpdateCheckIntervalHours = 0;
    }
}

/// <summary>
/// Machine-local settings: the storage location for roamable data, plus machine-specific
/// values (local scan directories). Always stored in local AppData; never roams.
/// </summary>
public sealed class LocalSettings
{
    /// <summary>Where roamable settings are stored.</summary>
    public StorageLocation StorageLocation { get; set; } = StorageLocation.UserDirectory;

    /// <summary>Base folder for <see cref="JeekTools.StorageLocation.CustomDirectory"/>
    /// (the "Config" child is appended).</summary>
    public string CustomStorageDir { get; set; } = "";

    /// <summary>Extra local directories to scan in addition to the Start Menu.</summary>
    public List<string> ScanDirectories { get; set; } = new();

    private LocalSettings? _baseline;

    public static LocalSettings Load()
    {
        bool existed = JsonSettingsFile.TryLoad(Storage.LocalSettingsPath, out LocalSettings settings);
        if (!existed)
            settings = LoadLegacy();

        Normalize(settings);
        settings._baseline = JsonSettingsFile.Clone(settings);

        // First run, or a migration from the old LocalSettings.json: persist right away so
        // the migrated values survive even if the user never opens the settings window.
        if (!existed && !File.Exists(Storage.LocalSettingsPath))
            settings.Save();

        return settings;
    }

    /// <summary>
    /// Reads the pre-<see cref="SettingsStorage"/> <c>LocalSettings.json</c>, whose storage
    /// mode was a JeekLauncher-local enum with the same member order. Returning it here is
    /// enough to migrate: the next <see cref="Save"/> writes the new <c>settings.json</c>.
    /// </summary>
    private static LocalSettings LoadLegacy()
    {
        var settings = new LocalSettings();
        try
        {
            if (!File.Exists(Storage.LegacyLocalSettingsPath))
                return settings;

            using var document = JsonDocument.Parse(File.ReadAllText(Storage.LegacyLocalSettingsPath));
            var root = document.RootElement;

            if (root.TryGetProperty("StorageMode", out var mode)
                && mode.ValueKind == JsonValueKind.String
                && Enum.TryParse(mode.GetString(), out LegacyStorageMode legacy))
            {
                settings.StorageLocation = legacy switch
                {
                    LegacyStorageMode.Portable => StorageLocation.ProgramDirectory,
                    LegacyStorageMode.Custom => StorageLocation.CustomDirectory,
                    _ => StorageLocation.UserDirectory,
                };
            }

            if (root.TryGetProperty("CustomStorageDir", out var custom) && custom.ValueKind == JsonValueKind.String)
                settings.CustomStorageDir = custom.GetString() ?? "";

            if (root.TryGetProperty("ScanDirectories", out var dirs) && dirs.ValueKind == JsonValueKind.Array)
            {
                foreach (var dir in dirs.EnumerateArray())
                {
                    if (dir.ValueKind == JsonValueKind.String && dir.GetString() is { Length: > 0 } path)
                        settings.ScanDirectories.Add(path);
                }
            }
        }
        catch
        {
            // Unreadable legacy file: start from defaults.
        }

        return settings;
    }

    private enum LegacyStorageMode
    {
        Default,
        Portable,
        Custom,
    }

    public void Save()
    {
        if (JsonSettingsFile.TryMergeAndWrite(
                Storage.LocalSettingsPath, _baseline ?? new LocalSettings(), this, Normalize, false, out var merged))
        {
            CopyFrom(merged);
            _baseline = JsonSettingsFile.Clone(merged);
            TryDeleteLegacyFile();
        }
    }

    private static void TryDeleteLegacyFile()
    {
        try
        {
            if (File.Exists(Storage.LegacyLocalSettingsPath))
                File.Delete(Storage.LegacyLocalSettingsPath);
        }
        catch
        {
            // A leftover legacy file is harmless; the new one is authoritative.
        }
    }

    /// <summary>Updates local settings values in place while preserving the merge baseline.</summary>
    public void ApplyFrom(LocalSettings other) => CopyFrom(other);

    private void CopyFrom(LocalSettings other)
    {
        StorageLocation = other.StorageLocation;
        CustomStorageDir = other.CustomStorageDir;
        ScanDirectories = other.ScanDirectories;
    }

    private static void Normalize(LocalSettings settings)
    {
        settings.StorageLocation = Enum.IsDefined(settings.StorageLocation)
            ? settings.StorageLocation
            : StorageLocation.UserDirectory;

        settings.CustomStorageDir ??= "";
        if (settings.StorageLocation == StorageLocation.CustomDirectory
            && !Storage.IsValidCustomDirectory(settings.CustomStorageDir))
        {
            settings.StorageLocation = StorageLocation.UserDirectory;
        }

        settings.ScanDirectories = (settings.ScanDirectories ?? [])
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>Per-item launch counts used to rank items by frequency. Machine-local.</summary>
public sealed class UsageStore
{
    private readonly Dictionary<string, int> _pendingDeltas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps an item id to how many times it has been launched.</summary>
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int GetCount(string id) => Counts.TryGetValue(id, out int c) ? c : 0;

    /// <summary>Record a launch by bumping the item's count.</summary>
    public void Record(string id)
    {
        Counts[id] = AddLaunches(GetCount(id), 1);
        _pendingDeltas[id] = AddLaunches(_pendingDeltas.GetValueOrDefault(id), 1);
    }

    public static UsageStore Load() => LoadFrom(Storage.UsagePath);

    internal static UsageStore LoadFrom(string path)
    {
        JsonSettingsFile.TryLoad(path, out UsageStore store);
        store.Counts = NormalizeCounts(store.Counts);
        return store;
    }

    public void Save() => SaveTo(Storage.UsagePath);

    internal void SaveTo(string path)
    {
        if (_pendingDeltas.Count == 0)
            return;

        try
        {
            using var lease = SharedDataFile.Acquire(path);
            if (JsonSettingsFile.TryLoad(path, out UsageStore disk))
            {
                var merged = NormalizeCounts(disk.Counts);
                foreach (var (id, delta) in _pendingDeltas)
                    merged[id] = AddLaunches(merged.GetValueOrDefault(id), delta);
                foreach (var (id, count) in Counts)
                {
                    if (!merged.ContainsKey(id))
                        merged[id] = count;
                }
                Counts = merged;
            }
            else if (File.Exists(path))
            {
                return; // Keep pending launches for a retry after the file becomes readable.
            }

            SharedDataFile.WriteAllTextAtomic(path, JsonSettingsFile.Serialize(this));
            _pendingDeltas.Clear();
        }
        catch
        {
            // Saving usage is best-effort.
        }
    }

    private static int AddLaunches(int count, int delta) =>
        (int)Math.Min(int.MaxValue, (long)count + delta);

    private static Dictionary<string, int> NormalizeCounts(Dictionary<string, int>? counts)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (counts is not null)
            foreach (var (id, count) in counts)
                result[id] = Math.Max(result.GetValueOrDefault(id), count);
        return result;
    }
}
