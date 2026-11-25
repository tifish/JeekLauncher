using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using JeekLauncher.Models;
using JeekLauncher.Services;
using JeekLauncher.ViewModels;

namespace JeekLauncher.Views;

public partial class QuickWordsWindow : Window
{
    private readonly QuickWordStore? _quickWords;
    private readonly Func<IReadOnlyList<AppEntry>>? _entriesProvider;
    private readonly Action? _onChanged;
    private readonly ObservableCollection<QuickWordRow> _rows = new();

    public QuickWordsWindow()
    {
        InitializeComponent();
    }

    public QuickWordsWindow(QuickWordStore quickWords, Func<IReadOnlyList<AppEntry>> entriesProvider, Action onChanged) : this()
    {
        _quickWords = quickWords;
        _entriesProvider = entriesProvider;
        _onChanged = onChanged;

        RowsList.ItemsSource = _rows;
        RowsList.SelectionChanged += (_, _) =>
        {
            if (RowsList.SelectedItem is QuickWordRow row)
                EditBox.Text = row.Word;
        };

        // Autocomplete picker over the scanned apps for adding a mapping manually.
        AppBox.ItemsSource = _entriesProvider?.Invoke();
        AppBox.ValueMemberBinding = new Binding(nameof(AppEntry.Name));

        AddButton.Click += OnAdd;
        RenameButton.Click += OnRename;
        DeleteButton.Click += OnDelete;
        ClearButton.Click += OnClear;

        Reload();
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (_quickWords is null || AppBox.SelectedItem is not AppEntry app)
            return;

        string word = (NewWordBox.Text ?? string.Empty).Trim().ToLowerInvariant();
        if (word.Length == 0)
            return;

        bool changed = _quickWords.Set(word, app.Id);
        NewWordBox.Text = string.Empty;
        AppBox.Text = string.Empty;
        AppBox.SelectedItem = null;
        if (changed)
            Persist();
    }

    public void Reload()
    {
        if (_quickWords is null)
            return;

        var entries = _entriesProvider?.Invoke();
        AppBox.ItemsSource = entries;

        var byId = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        if (entries is not null)
        {
            foreach (var entry in entries)
                byId.TryAdd(entry.Id, entry);
        }

        _rows.Clear();
        foreach (var pair in _quickWords.Words.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            string path = pair.Value;
            byId.TryGetValue(path, out var liveEntry);

            _rows.Add(new QuickWordRow
            {
                Word = pair.Key,
                TargetName = liveEntry?.Name ?? SafeName(path),
                TargetPath = path,
                Exists = liveEntry is not null || SafeExists(path),
            });
        }
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (_quickWords is null || RowsList.SelectedItem is not QuickWordRow row)
            return;

        string newWord = (EditBox.Text ?? string.Empty).Trim().ToLowerInvariant();
        if (newWord.Length == 0 || newWord == row.Word)
            return;

        if (_quickWords.Rename(row.Word, newWord))
            Persist();
    }

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (_quickWords is null || RowsList.SelectedItem is not QuickWordRow row)
            return;

        if (_quickWords.Remove(row.Word))
            Persist();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        if (_quickWords is null)
            return;

        if (_quickWords.Clear())
            Persist();
    }

    private void Persist()
    {
        _quickWords!.Save();
        _onChanged?.Invoke();
        Reload();
    }

    private static string SafeName(string path)
    {
        try { return Path.GetFileNameWithoutExtension(path); }
        catch { return path; }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}
