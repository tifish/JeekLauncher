using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekLauncher.Services;

namespace JeekLauncher.Views;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _dirs = new();
    private readonly Func<AppSettings, bool>? _onSave;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <param name="onSave">
    /// Applies the settings and returns whether the hotkey registered successfully.
    /// </param>
    public SettingsWindow(AppSettings current, Func<AppSettings, bool> onSave) : this()
    {
        _onSave = onSave;

        HotkeyBox.Text = current.Hotkey;
        StartupCheck.IsChecked = AutoStart.IsEnabled();

        // Language picker: index 0 = follow system, then "en", "zh".
        LanguageBox.ItemsSource = new[] { Localizer.Get("FollowSystem"), "English", "中文" };
        LanguageBox.SelectedIndex = current.Language switch { "en" => 1, "zh" => 2, _ => 0 };

        foreach (string dir in current.CustomDirectories)
            _dirs.Add(dir);
        DirsList.ItemsSource = _dirs;

        SaveButton.Click += OnSave;
        CancelButton.Click += (_, _) => Close();
        AddDirButton.Click += OnAddDirectory;
        RemoveDirButton.Click += OnRemoveDirectory;

        // Right-click selects the item under the pointer, then the menu acts on it.
        DirsList.AddHandler(PointerPressedEvent, OnDirsPointerPressed, RoutingStrategies.Tunnel);
        OpenDirItem.Click += (_, _) => OpenSelectedDirectory();
        RemoveDirItem.Click += OnRemoveDirectory;
        DirsList.DoubleTapped += (_, _) => OpenSelectedDirectory();
    }

    private void OnDirsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(DirsList).Properties.IsRightButtonPressed)
            return;

        if (e.Source is Visual visual &&
            visual.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: string path })
        {
            DirsList.SelectedItem = path;
        }
    }

    private void OpenSelectedDirectory()
    {
        if (DirsList.SelectedItem is not string dir || !Directory.Exists(dir))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch
        {
            // Best effort.
        }
    }

    /// <summary>Shows the hotkey-conflict banner (used on startup and on a failed save).</summary>
    public void ShowHotkeyError(string hotkeyText)
    {
        ErrorText.Text = string.Format(Localizer.Get("HotkeyError"), hotkeyText);
        ErrorBanner.IsVisible = true;
    }

    private async void OnAddDirectory(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("PickFolderTitle"),
            AllowMultiple = true,
        });

        foreach (var folder in folders)
        {
            string? path = folder.TryGetLocalPath();
            if (!string.IsNullOrEmpty(path) && !_dirs.Contains(path, StringComparer.OrdinalIgnoreCase))
                _dirs.Add(path);
        }
    }

    private void OnRemoveDirectory(object? sender, RoutedEventArgs e)
    {
        if (DirsList.SelectedItem is string selected)
            _dirs.Remove(selected);
    }

    private void OnSave(object? sender, RoutedEventArgs e)
    {
        string hotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Alt+Space" : HotkeyBox.Text.Trim();

        AutoStart.SetEnabled(StartupCheck.IsChecked == true);

        var settings = new AppSettings
        {
            Hotkey = hotkey,
            Language = LanguageBox.SelectedIndex switch { 1 => "en", 2 => "zh", _ => "" },
            CustomDirectories = _dirs.ToList(),
        };

        bool hotkeyOk = _onSave?.Invoke(settings) ?? true;
        if (hotkeyOk)
            Close();
        else
            ShowHotkeyError(hotkey); // keep the window open so the user can fix it
    }
}
