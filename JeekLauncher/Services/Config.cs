using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekLauncher.Services;

/// <summary>How roamable settings are stored. Machine-local data ignores this.</summary>
public enum StorageMode
{
    /// <summary>Roaming AppData: <c>%AppData%\JeekLauncher\Config</c>.</summary>
    Default,

    /// <summary>Next to the executable: <c>&lt;app&gt;\Config</c> (carry on a USB stick).</summary>
    Portable,

    /// <summary>A user-chosen folder: <c>&lt;custom&gt;\Config</c>.</summary>
    Custom,
}

/// <summary>
/// Resolves on-disk config locations and the active roamable storage mode.
///
/// <para><b>Machine-local</b> data (usage counts, local scan paths, and the chosen
/// storage mode itself) never roams — it always lives in
/// <c>%LocalAppData%\JeekLauncher\Config</c>.</para>
///
/// <para><b>Roamable</b> data (settings + quick words) lives in a <c>Config</c> folder
/// whose parent depends on the mode: roaming AppData (default), next to the executable
/// (portable), or a custom folder. If the portable <c>Config</c> folder exists at
/// startup, portable mode is forced regardless of the saved mode.</para>
/// </summary>
public static class Storage
{
    private const string AppName = "JeekLauncher";
    private const string ConfigFolder = "Config";

    // ---- Machine-local (never roams) ---------------------------------------

    /// <summary>Local AppData base for this app.</summary>
    public static string LocalDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    /// <summary>Machine-local config folder — always here, whatever the mode.</summary>
    public static string LocalConfigDir => Path.Combine(LocalDir, ConfigFolder);

    public static string LocalSettingsPath => Path.Combine(LocalConfigDir, "LocalSettings.json");
    public static string UsagePath => Path.Combine(LocalConfigDir, "Usage.json");

    // ---- Roamable (location depends on the mode) ---------------------------

    /// <summary>Default (roaming AppData) config folder.</summary>
    public static string AppDataConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, ConfigFolder);

    /// <summary>Portable config folder next to the executable.</summary>
    public static string PortableConfigDir { get; } = Path.Combine(AppContext.BaseDirectory, ConfigFolder);

    /// <summary>The active roamable mode (before the portable-folder override).</summary>
    public static StorageMode Mode { get; private set; } = StorageMode.Default;

    /// <summary>Custom storage root for <see cref="StorageMode.Custom"/>; the "Config" child is appended.</summary>
    public static string CustomDir { get; private set; } = "";

    /// <summary>The portable Config folder exists, forcing portable mode at startup.</summary>
    public static bool PortableForced => Directory.Exists(PortableConfigDir);

    /// <summary>The mode actually in effect (an existing portable folder overrides the saved mode).</summary>
    public static StorageMode EffectiveMode => PortableForced ? StorageMode.Portable : Mode;

    /// <summary>Where roamable settings (Settings.json + QuickWords.json) live right now.</summary>
    public static string RoamableConfigDir => ConfigDirFor(EffectiveMode, CustomDir);

    public static string SettingsPath => Path.Combine(RoamableConfigDir, "Settings.json");
    public static string QuickWordsPath => Path.Combine(RoamableConfigDir, "QuickWords.json");

    private static string ConfigDirFor(StorageMode mode, string customDir) => mode switch
    {
        StorageMode.Portable => PortableConfigDir,
        StorageMode.Custom when customDir.Length > 0 => Path.Combine(customDir, ConfigFolder),
        _ => AppDataConfigDir,
    };

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void EnsureLocalConfigDir() => Directory.CreateDirectory(LocalConfigDir);
    public static void EnsureRoamableConfigDir() => Directory.CreateDirectory(RoamableConfigDir);

    /// <summary>Primes the active mode from the saved machine-local settings (no migration).</summary>
    public static void SetMode(StorageMode mode, string? customDir)
    {
        Mode = mode;
        CustomDir = customDir ?? "";
    }

    /// <summary>Whether switching would place the new Config directory inside the current one.</summary>
    public static bool WouldMoveRoamableConfigIntoItself(StorageMode newMode, string? newCustomDir)
    {
        string from = RoamableConfigDir;
        string to = ConfigDirFor(newMode, newCustomDir ?? "");
        return !PathsEqual(from, to) && IsSubPathOf(to, from);
    }

    /// <summary>
    /// Switches the roamable storage mode, moving the roamable Config contents from
    /// the current folder to the new one. Machine-local data is left untouched. When
    /// leaving portable mode, the executable-side Config folder is deleted so the next
    /// startup is not forced back into portable.
    /// </summary>
    public static void SwitchMode(StorageMode newMode, string? newCustomDir)
    {
        if (WouldMoveRoamableConfigIntoItself(newMode, newCustomDir))
            throw new InvalidOperationException("The new Config directory cannot be inside the current one.");

        string from = RoamableConfigDir;
        string to = ConfigDirFor(newMode, newCustomDir ?? "");
        bool leavingPortable = EffectiveMode == StorageMode.Portable && newMode != StorageMode.Portable;

        if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(to);
            MoveDirectoryContents(from, to);
        }

        if (leavingPortable)
        {
            try { Directory.Delete(PortableConfigDir, recursive: false); }
            catch { /* Not empty or locked: leave it (stays portable). */ }
        }

        SetMode(newMode, newCustomDir);
    }

    private static void MoveDirectoryContents(string from, string to)
    {
        if (!Directory.Exists(from))
            return;

        Directory.CreateDirectory(to);

        foreach (string src in Directory.EnumerateFiles(from))
        {
            string dst = Path.Combine(to, Path.GetFileName(src));
            try
            {
                File.Move(src, dst, overwrite: true);
            }
            catch
            {
                // A locked file just stays where it is; best effort.
            }
        }

        foreach (string srcDir in Directory.EnumerateDirectories(from))
        {
            string dstDir = Path.Combine(to, Path.GetFileName(srcDir));
            MoveDirectoryContents(srcDir, dstDir);

            try { Directory.Delete(srcDir, recursive: false); }
            catch { /* Not empty or locked: leave it. */ }
        }
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsSubPathOf(string path, string parent)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedParent = NormalizePath(parent);
        return normalizedPath.StartsWith(
            normalizedParent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
        => Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Storage.SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(Storage.SettingsPath), Storage.JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt file: fall back to defaults below.
        }

        var settings = new AppSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Storage.EnsureRoamableConfigDir();
            File.WriteAllText(Storage.SettingsPath, JsonSerializer.Serialize(this, Storage.JsonOptions));
        }
        catch
        {
            // Saving settings is best-effort.
        }
    }
}

/// <summary>
/// Machine-local settings: the storage mode for roamable data, plus machine-specific
/// values (local scan directories). Always stored in local AppData; never roams.
/// </summary>
public sealed class LocalSettings
{
    /// <summary>Where roamable settings are stored.</summary>
    public StorageMode StorageMode { get; set; } = StorageMode.Default;

    /// <summary>Root folder for <see cref="Services.StorageMode.Custom"/> (the "Config" child is appended).</summary>
    public string CustomStorageDir { get; set; } = "";

    /// <summary>Extra local directories to scan in addition to the Start Menu.</summary>
    public List<string> ScanDirectories { get; set; } = new();

    public static LocalSettings Load()
    {
        try
        {
            if (File.Exists(Storage.LocalSettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<LocalSettings>(
                    File.ReadAllText(Storage.LocalSettingsPath), Storage.JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt file: fall back to defaults below.
        }

        var settings = new LocalSettings();
        settings.Save();
        return settings;
    }

    public void Save()
    {
        try
        {
            Storage.EnsureLocalConfigDir();
            File.WriteAllText(Storage.LocalSettingsPath, JsonSerializer.Serialize(this, Storage.JsonOptions));
        }
        catch
        {
            // Saving settings is best-effort.
        }
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
        try
        {
            if (File.Exists(Storage.QuickWordsPath))
            {
                var loaded = JsonSerializer.Deserialize<QuickWordStore>(
                    File.ReadAllText(Storage.QuickWordsPath), Storage.JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt file: start fresh.
        }

        return new QuickWordStore();
    }

    /// <summary>Re-read the words from disk in place (keeps this shared instance valid).</summary>
    public void ReloadFromDisk() => Words = Load().Words;

    public void Save()
    {
        try
        {
            Storage.EnsureRoamableConfigDir();
            File.WriteAllText(Storage.QuickWordsPath, JsonSerializer.Serialize(this, Storage.JsonOptions));
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
        try
        {
            if (File.Exists(Storage.UsagePath))
            {
                var loaded = JsonSerializer.Deserialize<UsageStore>(
                    File.ReadAllText(Storage.UsagePath), Storage.JsonOptions);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch
        {
            // Corrupt file: start fresh.
        }

        return new UsageStore();
    }

    public void Save()
    {
        try
        {
            Storage.EnsureLocalConfigDir();
            File.WriteAllText(Storage.UsagePath, JsonSerializer.Serialize(this, Storage.JsonOptions));
        }
        catch
        {
            // Saving usage is best-effort.
        }
    }
}
