using Avalonia.Media;
using Jeek.Avalonia.Localization;

namespace JeekLauncher.ViewModels;

/// <summary>One row in the quick-string manager: a learned word and its target.</summary>
public sealed class QuickWordRow
{
    public required string Word { get; init; }
    public required string TargetName { get; init; }
    public required string TargetPath { get; init; }
    public required bool Exists { get; init; }

    public string Status => Localizer.Get(Exists ? "StatusExists" : "StatusMissing");

    public IBrush StatusBrush => Exists
        ? new SolidColorBrush(Color.FromRgb(0x5A, 0xC8, 0x7A))
        : new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
}
