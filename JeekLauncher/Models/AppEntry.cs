using Avalonia.Media.Imaging;

namespace JeekLauncher.Models;

/// <summary>
/// A single launchable item discovered while scanning shortcut folders.
/// </summary>
public sealed class AppEntry
{
    /// <summary>Display name (shortcut file name without extension).</summary>
    public required string Name { get; init; }

    /// <summary>The path that is actually launched (the .lnk / .url / .exe).</summary>
    public required string Path { get; init; }

    /// <summary>
    /// For Windows "AppsFolder" apps (packaged / Store apps that have no shortcut),
    /// the App User Model ID used to launch them. Null for normal shortcuts.
    /// </summary>
    public string? Aumid { get; init; }

    /// <summary>Resolved target of a .lnk, used for the subtitle and de-duplication.</summary>
    public string? Target { get; init; }

    /// <summary>Initials key, e.g. "vsc" for "Visual Studio Code", "wx" for "微信".</summary>
    public required string Initials { get; init; }

    /// <summary>Lower-cased name with whitespace removed, used for substring matching.</summary>
    public required string SearchName { get; init; }

    /// <summary>
    /// Lower-cased full pinyin (CJK expanded) + latin, e.g. "网易云音乐" → "wangyiyunyinyue".
    /// Lets the user match a partial pinyin spelling, not just the initials.
    /// </summary>
    public required string PinyinFull { get; init; }

    /// <summary>Small icon extracted from the shortcut.</summary>
    public Bitmap? Icon { get; set; }

    /// <summary>Stable identity used by the usage store.</summary>
    public string Id => Path;

    /// <summary>Second line shown under the name in the result list.</summary>
    public string Subtitle => Target ?? Aumid ?? Path;
}
