using System.Text;
using ToolGood.Words.Pinyin;

namespace JeekLauncher.Services;

/// <summary>
/// Converts a run of Chinese characters to its pinyin initials (首字母). Backed by
/// ToolGood.Words.Pinyin, which is phrase-aware (so 音乐 → "yy" not "yl") and covers
/// characters beyond GB2312 (so 哔哩哔哩 → "blbl").
/// </summary>
public static class PinyinHelper
{
    /// <summary>
    /// Returns the lower-case pinyin initials for a run of Chinese characters,
    /// one letter per character. Characters that cannot be mapped are dropped.
    /// </summary>
    public static string GetRunInitials(string cjkRun)
    {
        if (string.IsNullOrEmpty(cjkRun))
            return string.Empty;

        try
        {
            // GetFirstPinyin yields one initial per Chinese character and leaves any
            // non-Chinese character unchanged; we keep only the ASCII letters.
            string first = WordsHelper.GetFirstPinyin(cjkRun);
            if (string.IsNullOrEmpty(first))
                return string.Empty;

            var sb = new StringBuilder(first.Length);
            foreach (char c in first)
            {
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns the lower-cased full pinyin for a string: Chinese characters are
    /// expanded to full syllables, latin/digits are kept, everything else dropped.
    /// e.g. "vivo办公套件" → "vivobangongtaojian".
    /// </summary>
    public static string GetFullPinyin(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        try
        {
            string pinyin = WordsHelper.GetPinyin(text, false);
            var sb = new StringBuilder(pinyin.Length);
            foreach (char c in pinyin)
            {
                if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                    sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}
