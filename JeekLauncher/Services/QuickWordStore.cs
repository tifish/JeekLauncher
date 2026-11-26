using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using JeekTools;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace JeekLauncher.Services;

/// <summary>
/// One quick-word entry inside a per-machine shard, carrying LWW metadata so shards
/// synced across devices merge deterministically without file conflicts.
/// </summary>
public sealed class QuickWordEntry
{
    public string Target { get; set; } = "";
    public long UpdatedAtUtcMs { get; set; }
    public string UpdatedBy { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Deleted { get; set; }
}

/// <summary>
/// Maps a typed query (the "quick word") to the item id launched for it, so the same
/// query jumps straight to that item next time.
///
/// <para>To avoid sync conflicts across devices, each machine writes only its own shard
/// under <c>Config/QuickWords/&lt;MachineName&gt;.json</c>, while reads merge every shard
/// in that folder (plus any legacy <c>Config/QuickWords.json</c> as a <c>ts = 0</c>
/// fallback) using entry-level Last-Write-Wins with a minimal Hybrid Logical Clock and
/// deletion tombstones.</para>
/// </summary>
public sealed class QuickWordStore
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(QuickWordStore));

    /// <summary>Minimum age before a tombstone can be pruned when no other shard still has an active entry.</summary>
    internal static readonly long TombstoneRetentionMs = (long)TimeSpan.FromDays(90).TotalMilliseconds;

    private readonly string _machineName;
    private Dictionary<string, QuickWordEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _activeWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, QuickWordEntry> _pendingLocalEdits = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _loadedShards = new();
    private long _maxSeenTimestamp;

    public QuickWordStore() : this(Storage.MachineName)
    {
    }

    internal QuickWordStore(string machineName)
    {
        _machineName = SanitizeMachineName(machineName);
    }

    public string MachineName => _machineName;
    internal bool HasPendingEdits => _pendingLocalEdits.Count > 0;

    /// <summary>Active (non-deleted) quick-word bindings merged across all machines.</summary>
    public IReadOnlyDictionary<string, string> Words => _activeWords;

    /// <summary>All merged entries including deletion tombstones (for diagnostics/MCP).</summary>
    public IReadOnlyDictionary<string, QuickWordEntry> Entries => _entries;

    /// <summary>Paths of shard / legacy files that contributed to the current merged state.</summary>
    public IReadOnlyList<string> LoadedShardFiles => _loadedShards;

    public string? GetTarget(string query)
        => _activeWords.TryGetValue(query, out string? id) ? id : null;

    /// <summary>Remember "query -> id" (no-op for an empty query or unchanged binding). Returns true if changed.</summary>
    public bool Set(string query, string id)
    {
        string q = NormalizeWord(query);
        if (q.Length == 0 || string.IsNullOrWhiteSpace(id))
            return false;

        if (_activeWords.TryGetValue(q, out string? existing)
            && string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var entry = new QuickWordEntry
        {
            Target = id,
            UpdatedAtUtcMs = NextTimestamp(),
            UpdatedBy = _machineName,
            Deleted = false,
        };

        ApplyLocalEdit(q, entry);
        return true;
    }

    /// <summary>Delete a quick word by recording a tombstone so other shards will not resurrect it.</summary>
    public bool Remove(string query)
    {
        string q = NormalizeWord(query);
        if (q.Length == 0 || !_activeWords.ContainsKey(q))
            return false;

        var tombstone = new QuickWordEntry
        {
            Target = "",
            UpdatedAtUtcMs = NextTimestamp(),
            UpdatedBy = _machineName,
            Deleted = true,
        };

        ApplyLocalEdit(q, tombstone);
        return true;
    }

    /// <summary>Rename an existing quick word while tombstoning the old word.</summary>
    public bool Rename(string oldWord, string newWord)
    {
        string oldQ = NormalizeWord(oldWord);
        string newQ = NormalizeWord(newWord);
        if (oldQ.Length == 0 || newQ.Length == 0 || string.Equals(oldQ, newQ, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!_activeWords.TryGetValue(oldQ, out string? target))
            return false;

        Remove(oldQ);
        Set(newQ, target);
        return true;
    }

    /// <summary>Delete all currently active quick words by recording tombstones for each.</summary>
    public bool Clear()
    {
        if (_activeWords.Count == 0)
            return false;

        foreach (string word in _activeWords.Keys.ToList())
            Remove(word);
        return true;
    }

    public static QuickWordStore Load() => LoadFrom(Storage.RoamableConfigDir, Storage.MachineName);

    internal static QuickWordStore LoadFrom(string roamableConfigDir, string machineName)
    {
        var store = new QuickWordStore(machineName);
        store.ReloadFrom(roamableConfigDir);
        return store;
    }

    /// <summary>Refresh from disk and retry any outstanding local edits.</summary>
    public void ReloadFromDisk()
    {
        ReloadFrom(Storage.RoamableConfigDir);
        if (HasPendingEdits)
            Save();
    }

    internal void ReloadFrom(string roamableConfigDir)
    {
        var disk = LoadMergedFromDisk(roamableConfigDir);
        if (!disk.Complete && _loadedShards.Count > 0)
            return; // Keep the last readable snapshot while a sync or edit is in progress.
        long maxTs = Math.Max(_maxSeenTimestamp, disk.MaxTimestamp);

        // Preserve any unsaved local edits if a previous save failed.
        foreach (var (word, edit) in _pendingLocalEdits)
        {
            MergeEntry(disk.Entries, word, CloneEntry(edit));
            if (edit.UpdatedAtUtcMs > maxTs)
                maxTs = edit.UpdatedAtUtcMs;
        }

        _entries = disk.Entries;
        _loadedShards = disk.LoadedFiles;
        _maxSeenTimestamp = maxTs;
        RebuildActiveWords();
    }

    /// <summary>
    /// Synchronizes in-memory quick words after switching to a different roamable config
    /// directory without moving files: merges with any existing shards at the destination,
    /// writing this machine's shard only when there were local entries to carry over.
    /// </summary>
    public void SyncWithCurrentLocation() => SyncWithLocation(Storage.RoamableConfigDir);

    internal void SyncWithLocation(string roamableConfigDir)
    {
        if (_entries.Count == 0 && _pendingLocalEdits.Count == 0)
        {
            ReloadFrom(roamableConfigDir);
            return;
        }

        var carryOver = _entries.ToDictionary(
            pair => pair.Key,
            pair => CloneEntry(pair.Value),
            StringComparer.OrdinalIgnoreCase);

        SaveTo(roamableConfigDir, forceWrite: true, carryOverEntries: carryOver);
    }

    public void Save(bool forceWrite = false) => SaveTo(Storage.RoamableConfigDir, forceWrite);

    internal void SaveTo(
        string roamableConfigDir,
        bool forceWrite = false,
        Dictionary<string, QuickWordEntry>? carryOverEntries = null,
        long? nowUtcMsOverride = null)
    {
        if (!forceWrite && _pendingLocalEdits.Count == 0)
            return;

        try
        {
            string quickWordsDir = Path.Combine(roamableConfigDir, Storage.QuickWordsDirName);
            string shardPath = Path.Combine(quickWordsDir, $"{_machineName}.json");

            Directory.CreateDirectory(quickWordsDir);
            using var lease = SharedDataFile.Acquire(shardPath);

            var disk = LoadMergedFromDisk(roamableConfigDir, ownShardPathToExcludeFromOtherActive: shardPath);
            if (!disk.Complete)
                throw new IOException("The quick-word files could not all be read. Local edits are pending.");
            long maxTs = Math.Max(_maxSeenTimestamp, disk.MaxTimestamp);

            if (carryOverEntries is not null)
            {
                foreach (var (word, entry) in carryOverEntries)
                {
                    MergeEntry(disk.Entries, word, CloneEntry(entry));
                    if (entry.UpdatedAtUtcMs > maxTs)
                        maxTs = entry.UpdatedAtUtcMs;
                }
            }

            // Local edits performed right now must strictly beat any remote shard that
            // arrived on disk before the watcher debounced a reload (handles clock skew too).
            foreach (var (word, edit) in _pendingLocalEdits)
            {
                var toApply = CloneEntry(edit);
                if (toApply.UpdatedAtUtcMs <= maxTs)
                {
                    toApply.UpdatedAtUtcMs = ++maxTs;
                }
                else
                {
                    maxTs = toApply.UpdatedAtUtcMs;
                }

                MergeEntry(disk.Entries, word, toApply);
            }

            long nowMs = nowUtcMsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PruneExpiredTombstones(disk.Entries, disk.OtherActiveWords, nowMs);

            var sortedEntries = new Dictionary<string, QuickWordEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in disk.Entries.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                sortedEntries[pair.Key] = pair.Value;

            var shard = new QuickWordShard
            {
                Machine = _machineName,
                Entries = sortedEntries,
            };

            string json = JsonSettingsFile.Serialize(shard);

            WriteAllTextAtomicWithRetry(shardPath, json);
            if (string.Equals(shardPath, Storage.QuickWordsPath, StringComparison.OrdinalIgnoreCase))
                Storage.MarkSelfWrite(ConfigChangeKind.QuickWords, shardPath, json);

            _entries = sortedEntries;
            _maxSeenTimestamp = maxTs;
            _pendingLocalEdits.Clear();

            var loaded = new List<string>(disk.LoadedFiles);
            if (!loaded.Contains(shardPath, StringComparer.OrdinalIgnoreCase))
                loaded.Add(shardPath);
            loaded.Sort(StringComparer.OrdinalIgnoreCase);
            _loadedShards = loaded;

            RebuildActiveWords();
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to save QuickWords shard: {ex.Message}");
        }
    }

    private void ApplyLocalEdit(string normalizedWord, QuickWordEntry entry)
    {
        _entries[normalizedWord] = entry;
        _pendingLocalEdits[normalizedWord] = CloneEntry(entry);

        if (entry.Deleted || string.IsNullOrEmpty(entry.Target))
            _activeWords.Remove(normalizedWord);
        else
            _activeWords[normalizedWord] = entry.Target;
    }

    private void RebuildActiveWords()
    {
        var active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (word, entry) in _entries)
        {
            if (!entry.Deleted && !string.IsNullOrEmpty(entry.Target))
                active[word] = entry.Target;
        }
        _activeWords = active;
    }

    private long NextTimestamp()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long next = now > _maxSeenTimestamp ? now : _maxSeenTimestamp + 1;
        _maxSeenTimestamp = next;
        return next;
    }

    private static DiskMergeResult LoadMergedFromDisk(
        string roamableConfigDir,
        string? ownShardPathToExcludeFromOtherActive = null)
    {
        var merged = new Dictionary<string, QuickWordEntry>(StringComparer.OrdinalIgnoreCase);
        var otherActive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedFiles = new List<string>();
        bool complete = true;
        long maxTs = 0;

        string? excludedFullPath = ownShardPathToExcludeFromOtherActive is not null
            ? Path.GetFullPath(ownShardPathToExcludeFromOtherActive)
            : null;

        // 1. Legacy Config/QuickWords.json as a ts = 0 fallback layer so upgrades stay seamless.
        string legacyPath = Path.GetFullPath(Path.Combine(roamableConfigDir, Storage.LegacyQuickWordsFileName));
        if (File.Exists(legacyPath))
        {
            complete = TryReadLegacyWords(legacyPath, out var legacyWords);
            if (complete)
                loadedFiles.Add(legacyPath);
            foreach (var (rawWord, target) in legacyWords)
            {
                string word = NormalizeWord(rawWord);
                if (word.Length == 0 || string.IsNullOrWhiteSpace(target))
                    continue;

                otherActive.Add(word);
                MergeEntry(merged, word, new QuickWordEntry
                {
                    Target = target,
                    UpdatedAtUtcMs = 0,
                    UpdatedBy = "",
                    Deleted = false,
                });
            }
        }

        // 2. All per-machine shards under Config/QuickWords/*.json.
        string quickWordsDir = Path.Combine(roamableConfigDir, Storage.QuickWordsDirName);
        if (Directory.Exists(quickWordsDir))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(quickWordsDir, "*.json", SearchOption.TopDirectoryOnly);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                complete = false;
                Log.ZLogWarning($"Failed to enumerate QuickWords shards in {quickWordsDir}: {ex.Message}");
                files = Array.Empty<string>();
            }

            foreach (string rawPath in files)
            {
                string filePath = Path.GetFullPath(rawPath);
                if (!TryReadShard(filePath, out var shardEntries))
                {
                    complete = false;
                    continue;
                }

                loadedFiles.Add(filePath);
                bool isOtherShard = excludedFullPath is null
                    || !string.Equals(filePath, excludedFullPath, StringComparison.OrdinalIgnoreCase);

                foreach (var (rawWord, entry) in shardEntries)
                {
                    string word = NormalizeWord(rawWord);
                    if (word.Length == 0)
                        continue;

                    if (isOtherShard && !entry.Deleted && !string.IsNullOrEmpty(entry.Target))
                        otherActive.Add(word);

                    if (entry.UpdatedAtUtcMs > maxTs)
                        maxTs = entry.UpdatedAtUtcMs;

                    MergeEntry(merged, word, entry);
                }
            }
        }

        return new DiskMergeResult(merged, otherActive, loadedFiles, maxTs, complete);
    }

    private static bool TryReadLegacyWords(string path, out Dictionary<string, string> words)
    {
        words = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryReadAllTextWithRetry(path, out string? text) || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyQuickWordsFile>(text, JsonSettingsFile.JsonOptions);
            if (legacy?.Words is null)
                return false;

            words = legacy.Words;
            return true;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to parse legacy QuickWords file {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryReadShard(string path, out Dictionary<string, QuickWordEntry> entries)
    {
        entries = new Dictionary<string, QuickWordEntry>(StringComparer.OrdinalIgnoreCase);
        if (!TryReadAllTextWithRetry(path, out string? text) || string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string fallbackMachine = SanitizeMachineName(Path.GetFileNameWithoutExtension(path));
            if (root.TryGetProperty("Machine", out var machineProp)
                && machineProp.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(machineProp.GetString()))
            {
                fallbackMachine = machineProp.GetString()!;
            }

            if (root.TryGetProperty("Entries", out _))
            {
                var shard = JsonSerializer.Deserialize<QuickWordShard>(text, JsonSettingsFile.JsonOptions);
                if (shard?.Entries is null)
                    return false;

                foreach (var (word, rawEntry) in shard.Entries)
                {
                    if (rawEntry is null)
                        continue;

                    entries[word] = new QuickWordEntry
                    {
                        Target = rawEntry.Target ?? "",
                        UpdatedAtUtcMs = Math.Max(0, rawEntry.UpdatedAtUtcMs),
                        UpdatedBy = string.IsNullOrWhiteSpace(rawEntry.UpdatedBy) ? fallbackMachine : rawEntry.UpdatedBy,
                        Deleted = rawEntry.Deleted,
                    };
                }

                return true;
            }

            // Forgiving fallback: if someone copied a legacy { "Words": { ... } } file into QuickWords/.
            if (root.TryGetProperty("Words", out _))
            {
                var legacy = JsonSerializer.Deserialize<LegacyQuickWordsFile>(text, JsonSettingsFile.JsonOptions);
                if (legacy?.Words is null)
                    return false;

                long mtimeMs = 0;
                try { mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeMilliseconds(); }
                catch { /* fallback to 0 */ }

                foreach (var (word, target) in legacy.Words)
                {
                    if (string.IsNullOrWhiteSpace(target))
                        continue;

                    entries[word] = new QuickWordEntry
                    {
                        Target = target,
                        UpdatedAtUtcMs = Math.Max(0, mtimeMs),
                        UpdatedBy = fallbackMachine,
                        Deleted = false,
                    };
                }

                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.ZLogWarning($"Failed to parse QuickWords shard {path}: {ex.Message}");
            return false;
        }
    }

    private static void MergeEntry(Dictionary<string, QuickWordEntry> target, string word, QuickWordEntry candidate)
    {
        if (!target.TryGetValue(word, out var existing) || IsNewer(candidate, existing))
            target[word] = candidate;
    }

    /// <summary>
    /// Strict total order for LWW merging so any merge order across devices converges
    /// to the exact same result regardless of locale.
    /// </summary>
    internal static bool IsNewer(QuickWordEntry candidate, QuickWordEntry current)
    {
        if (candidate.UpdatedAtUtcMs != current.UpdatedAtUtcMs)
            return candidate.UpdatedAtUtcMs > current.UpdatedAtUtcMs;

        int byCmp = StringComparer.Ordinal.Compare(candidate.UpdatedBy, current.UpdatedBy);
        if (byCmp != 0)
            return byCmp > 0;

        if (candidate.Deleted != current.Deleted)
            return candidate.Deleted;

        return StringComparer.Ordinal.Compare(candidate.Target, current.Target) > 0;
    }

    private static void PruneExpiredTombstones(
        Dictionary<string, QuickWordEntry> entries,
        HashSet<string> otherActiveWords,
        long nowUtcMs)
    {
        var toRemove = new List<string>();
        foreach (var (word, entry) in entries)
        {
            if (!entry.Deleted)
                continue;
            if (nowUtcMs - entry.UpdatedAtUtcMs < TombstoneRetentionMs)
                continue;
            // Never drop a tombstone while another shard (or legacy file) still advertises the word as active.
            if (otherActiveWords.Contains(word))
                continue;

            toRemove.Add(word);
        }

        foreach (string word in toRemove)
            entries.Remove(word);
    }

    private static bool TryReadAllTextWithRetry(string path, out string? content)
    {
        const int maxAttempts = 4;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                content = reader.ReadToEnd();
                return true;
            }
            catch (FileNotFoundException)
            {
                content = null;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                content = null;
                return false;
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (Exception ex)
            {
                Log.ZLogWarning($"Could not read {path}: {ex.Message}");
                content = null;
                return false;
            }
        }

        content = null;
        return false;
    }

    private static void WriteAllTextAtomicWithRetry(string path, string contents)
    {
        const int maxAttempts = 5;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                SharedDataFile.WriteAllTextAtomic(path, contents);
                return;
            }
            catch (IOException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt + 1 < maxAttempts)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    internal static string SanitizeMachineName(string? rawName)
    {
        string trimmed = (rawName ?? "").Trim();
        if (trimmed.Length == 0)
            return "default";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = trimmed.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or ':')
                chars[i] = '_';
        }

        string cleaned = new string(chars).Trim().TrimEnd('.', ' ');
        if (cleaned.Length == 0)
            return "default";

        if (IsReservedWindowsDeviceName(cleaned))
            cleaned = "_" + cleaned;

        return cleaned;
    }

    private static bool IsReservedWindowsDeviceName(string name)
    {
        string upper = name.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
    }

    private static string NormalizeWord(string? query) => (query ?? "").Trim().ToLowerInvariant();

    private static QuickWordEntry CloneEntry(QuickWordEntry source) => new()
    {
        Target = source.Target,
        UpdatedAtUtcMs = source.UpdatedAtUtcMs,
        UpdatedBy = source.UpdatedBy,
        Deleted = source.Deleted,
    };

    private sealed class QuickWordShard
    {
        public string Machine { get; set; } = "";
        public Dictionary<string, QuickWordEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class LegacyQuickWordsFile
    {
        public Dictionary<string, string> Words { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record DiskMergeResult(
        Dictionary<string, QuickWordEntry> Entries,
        HashSet<string> OtherActiveWords,
        List<string> LoadedFiles,
        long MaxTimestamp,
        bool Complete);
}
