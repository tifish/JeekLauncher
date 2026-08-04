using System.Text.Json;
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

    /// <summary>Where roamable settings (settings.json + QuickWords.json) live right now.</summary>
    public static string RoamableConfigDir => ConfigRootFor(EffectiveLocation, CustomDir);

    public static string SettingsPath => Path.Combine(RoamableConfigDir, "settings.json");
    public static string QuickWordsPath => Path.Combine(RoamableConfigDir, "QuickWords.json");

    public static string ConfigRootFor(StorageLocation location, string? customDir)
        => Backing.ResolveConfigRoot(location, customDir);

    /// <summary>Primes the active location from the saved machine-local settings (no migration).</summary>
    public static void SetLocation(StorageLocation location, string? customDir)
    {
        Location = Backing.NormalizeLocation(location);
        CustomDir = customDir ?? "";
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
        if (WouldMoveRoamableConfigIntoItself(newLocation, newCustomDir))
            throw new InvalidOperationException("The new Config directory cannot be inside the current one.");

        string from = RoamableConfigDir;
        string to = ConfigRootFor(newLocation, newCustomDir ?? "");
        bool leavingPortable = EffectiveLocation == StorageLocation.ProgramDirectory
                               && newLocation != StorageLocation.ProgramDirectory;

        if (moveFiles)
            SettingsStorage.MoveConfigRoot(from, to);
        else
            Directory.CreateDirectory(to);

        // The executable-side folder is what forces portable mode, so it has to go even
        // when MoveConfigRoot already emptied it.
        if (leavingPortable)
            Backing.TryDeleteProgramConfig(out _);

        SetLocation(newLocation, newCustomDir);
    }

    public static void EnsureLocalConfigDir() => Directory.CreateDirectory(LocalConfigDir);
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

    public static AppSettings Load()
    {
        bool existed = JsonSettingsFile.TryLoad(Storage.SettingsPath, out AppSettings settings);
        Normalize(settings);
        settings._baseline = JsonSettingsFile.Clone(settings);

        // Write the defaults out on first run so the file is there to be found and edited.
        if (!existed)
            settings.Save(forceAllLocal: true);

        return settings;
    }

    /// <param name="forceAllLocal">Ignore whatever is already at the target path — used
    /// right after a storage switch, where the destination is a different folder.</param>
    public void Save(bool forceAllLocal = false)
    {
        Storage.EnsureRoamableConfigDir();
        if (JsonSettingsFile.TryMergeAndWrite(
                Storage.SettingsPath, _baseline ?? new AppSettings(), this, Normalize, forceAllLocal, out var merged))
        {
            CopyFrom(merged);
            _baseline = JsonSettingsFile.Clone(merged);
        }
    }

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
        if (!existed)
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
        Storage.EnsureLocalConfigDir();
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

        if (settings.StorageLocation == StorageLocation.CustomDirectory
            && !Path.IsPathFullyQualified(settings.CustomStorageDir))
        {
            settings.StorageLocation = StorageLocation.UserDirectory;
        }

        settings.ScanDirectories = settings.ScanDirectories
            .Where(dir => !string.IsNullOrWhiteSpace(dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>
/// Maps a typed query (the "quick word") to the item id launched for it, so the
/// same query jumps straight to that item next time. Roamable.
/// </summary>
public sealed class QuickWordStore
{
    /// <summary>Maps a typed query to the item id that was launched for it.</summary>
    public Dictionary<string, string> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? GetTarget(string query)
        => Words.TryGetValue(query, out string? id) ? id : null;

    /// <summary>Remember "query -> id" (no-op for an empty query).</summary>
    public void Set(string query, string id)
    {
        string q = query.Trim().ToLowerInvariant();
        if (q.Length > 0)
            Words[q] = id;
    }

    public static QuickWordStore Load()
    {
        JsonSettingsFile.TryLoad(Storage.QuickWordsPath, out QuickWordStore store);
        store.Words = new Dictionary<string, string>(store.Words, StringComparer.OrdinalIgnoreCase);
        return store;
    }

    /// <summary>Re-read the words from disk in place (keeps this shared instance valid).</summary>
    public void ReloadFromDisk() => Words = Load().Words;

    public void Save()
    {
        try
        {
            Storage.EnsureRoamableConfigDir();
            SharedDataFile.WriteAllTextAtomic(Storage.QuickWordsPath, JsonSettingsFile.Serialize(this));
        }
        catch
        {
            // Saving quick words is best-effort.
        }
    }
}

/// <summary>Per-item launch counts used to rank items by frequency. Machine-local.</summary>
public sealed class UsageStore
{
    /// <summary>Maps an item id to how many times it has been launched.</summary>
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int GetCount(string id) => Counts.TryGetValue(id, out int c) ? c : 0;

    /// <summary>Record a launch by bumping the item's count.</summary>
    public void Record(string id) => Counts[id] = GetCount(id) + 1;

    public static UsageStore Load()
    {
        JsonSettingsFile.TryLoad(Storage.UsagePath, out UsageStore store);
        store.Counts = new Dictionary<string, int>(store.Counts, StringComparer.OrdinalIgnoreCase);
        return store;
    }

    public void Save()
    {
        try
        {
            Storage.EnsureLocalConfigDir();
            SharedDataFile.WriteAllTextAtomic(Storage.UsagePath, JsonSettingsFile.Serialize(this));
        }
        catch
        {
            // Saving usage is best-effort.
        }
    }
}
