using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeekLauncher.Services;

/// <summary>Resolves the on-disk locations used for settings and usage data.</summary>
public static class Storage
{
    public static string Dir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "JeekLauncher");

    public static string SettingsPath => Path.Combine(Dir, "settings.json");
    public static string UsagePath => Path.Combine(Dir, "usage.json");

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void EnsureDir() => Directory.CreateDirectory(Dir);
}

/// <summary>User-editable configuration (hotkey + extra folders to scan).</summary>
public sealed class AppSettings
{
    /// <summary>Global hotkey, e.g. "Alt+Space", "Ctrl+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    /// <summary>UI language: "" (follow system), "en" or "zh".</summary>
    public string Language { get; set; } = "";

    /// <summary>Extra directories to scan in addition to the Start Menu.</summary>
    public List<string> CustomDirectories { get; set; } = new();

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
            Storage.EnsureDir();
            File.WriteAllText(Storage.SettingsPath, JsonSerializer.Serialize(this, Storage.JsonOptions));
        }
        catch
        {
            // Saving settings is best-effort.
        }
    }
}

/// <summary>
/// Remembers which item the user launched for a given query (the "quick word"),
/// plus per-item launch counts used for ranking.
/// </summary>
public sealed class UsageStore
{
    /// <summary>Maps a typed query to the item id that was launched for it.</summary>
    public Dictionary<string, string> Learned { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps an item id to how many times it has been launched.</summary>
    public Dictionary<string, int> Counts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int GetCount(string id) => Counts.TryGetValue(id, out int c) ? c : 0;

    public string? GetLearned(string query)
        => Learned.TryGetValue(query, out string? id) ? id : null;

    /// <summary>Record a launch: bump the count and remember the quick word.</summary>
    public void Record(string query, string id)
    {
        Counts[id] = GetCount(id) + 1;

        string q = query.Trim().ToLowerInvariant();
        if (q.Length > 0)
            Learned[q] = id;
    }

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
            Storage.EnsureDir();
            File.WriteAllText(Storage.UsagePath, JsonSerializer.Serialize(this, Storage.JsonOptions));
        }
        catch
        {
            // Saving usage is best-effort.
        }
    }
}
