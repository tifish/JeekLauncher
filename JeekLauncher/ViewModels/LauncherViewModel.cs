using System.ComponentModel;
using System.Runtime.CompilerServices;
using Jeek.Avalonia.Localization;
using JeekLauncher.Models;
using JeekLauncher.Services;
using JeekTools;

namespace JeekLauncher.ViewModels;

public sealed class LauncherViewModel : INotifyPropertyChanged
{
    private readonly UsageStore _usage;
    private readonly QuickWordStore _quickWords;
    private IReadOnlyList<AppEntry> _all = Array.Empty<AppEntry>();

    private string _query = string.Empty;
    private int _selectedIndex = -1;
    private bool _hasScanned;

    public LauncherViewModel(UsageStore usage, QuickWordStore quickWords)
    {
        _usage = usage;
        _quickWords = quickWords;
    }

    public FastObservableCollection<AppEntry> Results { get; } = new();

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            _query = value;
            OnPropertyChanged();
            Refresh();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_selectedIndex == value)
                return;
            _selectedIndex = value;
            OnPropertyChanged();
        }
    }

    public string StatusText { get; private set; } = string.Empty;

    public AppEntry? Selected =>
        _selectedIndex >= 0 && _selectedIndex < Results.Count ? Results[_selectedIndex] : null;

    /// <summary>Show a transient message in the status bar (until the next refresh).</summary>
    public void SetStatus(string text)
    {
        StatusText = text;
        OnPropertyChanged(nameof(StatusText));
    }

    /// <summary>Replace the full catalog (called after a scan completes).</summary>
    public void SetEntries(IReadOnlyList<AppEntry> entries)
    {
        _all = entries;
        _hasScanned = true;
        Refresh();
    }

    /// <summary>Reset for a fresh appearance of the window.</summary>
    public void Reset()
    {
        if (_query.Length > 0)
        {
            _query = string.Empty;
            OnPropertyChanged(nameof(Query));
        }

        Refresh();
    }

    public void Refresh()
    {
        var matches = SearchEngine.Search(_query, _all, _usage, _quickWords);
        Results.Replace(matches);

        SelectedIndex = Results.Count > 0 ? 0 : -1;

        StatusText = !_hasScanned
            ? Localizer.Get("StatusScanning")
            : Results.Count == 0
                ? Localizer.Get("StatusNoMatches")
                : string.Format(Localizer.Get("StatusResults"), Results.Count);
        OnPropertyChanged(nameof(StatusText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
