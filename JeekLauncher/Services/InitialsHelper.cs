using System.Text;

namespace JeekLauncher.Services;

/// <summary>
/// Builds the searchable initials string for a display name, mixing English word
/// initials with Chinese pinyin initials.
/// <list type="bullet">
///   <item>"Visual Studio Code" → "vsc"</item>
///   <item>"网易云音乐" → "wyyyy"</item>
///   <item>"哔哩哔哩" → "blbl"</item>
///   <item>"QQ音乐" → "qyy"</item>
///   <item>"7-Zip" → "7z"</item>
/// </list>
/// </summary>
public static class InitialsHelper
{
    public static string Compute(string name)
    {
        var sb = new StringBuilder(name.Length);
        bool prevWasLatin = false;
        bool prevWasLower = false;

        int i = 0;
        while (i < name.Length)
        {
            char c = name[i];

            if (IsCjk(c))
            {
                // Convert a whole run at once so pinyin stays phrase-aware.
                int start = i;
                while (i < name.Length && IsCjk(name[i]))
                    i++;

                sb.Append(PinyinHelper.GetRunInitials(name[start..i]));
                prevWasLatin = false;
                prevWasLower = false;
                continue;
            }

            if (IsLatinOrDigit(c))
            {
                bool isUpper = c is >= 'A' and <= 'Z';
                // A new "word" starts at a separator boundary or a camelCase hump.
                bool wordStart = !prevWasLatin || (prevWasLower && isUpper);
                if (wordStart)
                    sb.Append(char.ToLowerInvariant(c));

                prevWasLatin = true;
                prevWasLower = c is >= 'a' and <= 'z';
            }
            else
            {
                prevWasLatin = false;
                prevWasLower = false;
            }

            i++;
        }

        return sb.ToString();
    }

    /// <summary>Lower-cased name with whitespace stripped, for substring matching.</summary>
    public static string Normalize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (!char.IsWhiteSpace(c))
                sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static bool IsLatinOrDigit(char c)
        => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';

    private static bool IsCjk(char c)
        => c is >= '一' and <= '鿿' // CJK Unified Ideographs (U+4E00–U+9FFF)
            or >= '㐀' and <= '䶿'; // CJK Extension A (U+3400–U+4DBF)
}
