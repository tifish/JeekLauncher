using JeekLauncher.Models;

namespace JeekLauncher.Services;

/// <summary>
/// Ranks <see cref="AppEntry"/> items against a query. Matching is permissive — the
/// query only needs to match a part, and letters from inside a word count too — but
/// items whose <b>initials</b> match are ranked first, as requested.
///
/// Match tiers (high → low):
/// <list type="number">
///   <item>6 — initials equal the query            (e.g. "vsc" → Visual Studio Code)</item>
///   <item>5 — initials start with the query       (e.g. "wm"  → 完美解码)</item>
///   <item>4 — name / full pinyin starts with query(e.g. "visual", "wangyi")</item>
///   <item>3 — initials contain the query in order (e.g. "vc"  → "vsc")</item>
///   <item>2 — name / full pinyin contains query   (e.g. "code", "bangong")</item>
///   <item>1 — name / full pinyin fuzzy subsequence (letters from inside words)</item>
/// </list>
/// A remembered "quick word" always wins; launch frequency orders items within a tier.
/// </summary>
public static class SearchEngine
{
    private const long TierUnit = 10_000_000;
    private const long LearnedBoost = 1_000_000_000;

    public static List<AppEntry> Search(string rawQuery, IReadOnlyList<AppEntry> all, UsageStore usage, int limit = 40)
    {
        string query = rawQuery.Trim().ToLowerInvariant();

        if (query.Length == 0)
        {
            // No query yet: surface the most frequently used items.
            return all
                .OrderByDescending(e => usage.GetCount(e.Id))
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToList();
        }

        string? learnedId = usage.GetLearned(query);

        var scored = new List<(AppEntry Entry, long Score)>();
        foreach (var entry in all)
        {
            int tier = MatchTier(query, entry);
            bool isLearned = learnedId is not null && entry.Id == learnedId;
            if (tier == 0 && !isLearned)
                continue;

            long score = (long)tier * TierUnit;
            if (isLearned)
                score += LearnedBoost;                              // remembered word floats to the top
            score += Math.Min(usage.GetCount(entry.Id), 100) * 1000L; // frequency orders within a tier
            score += Math.Max(0, 64 - entry.Name.Length);             // gentle nudge for shorter names

            scored.Add((entry, score));
        }

        return scored
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(t => t.Entry)
            .ToList();
    }

    /// <summary>Returns the match tier (6 best … 1 worst), or 0 for no match.</summary>
    private static int MatchTier(string q, AppEntry e)
    {
        string initials = e.Initials;
        if (initials.Length > 0)
        {
            if (initials == q)
                return 6;
            if (initials.StartsWith(q, StringComparison.Ordinal))
                return 5;
        }

        if (StartsWith(e.SearchName, q) || StartsWith(e.PinyinFull, q))
            return 4;

        if (initials.Length > 0 && IsSubsequence(q, initials))
            return 3;

        if (Contains(e.SearchName, q) || Contains(e.PinyinFull, q))
            return 2;

        if (IsSubsequence(q, e.SearchName) || IsSubsequence(q, e.PinyinFull))
            return 1;

        return 0;
    }

    private static bool StartsWith(string s, string q)
        => s.Length > 0 && s.StartsWith(q, StringComparison.Ordinal);

    private static bool Contains(string s, string q)
        => s.Length > 0 && s.Contains(q, StringComparison.Ordinal);

    private static bool IsSubsequence(string needle, string haystack)
    {
        if (haystack.Length == 0)
            return false;

        int i = 0;
        foreach (char c in haystack)
        {
            if (i < needle.Length && c == needle[i])
                i++;
        }
        return i == needle.Length;
    }
}
