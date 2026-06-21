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
    private readonly Func<AppSettings, LocalSettings, bool>? _onSave;
    private readonly Action<StorageMode, string>? _onStorageModeChange;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <param name="settings">Current roamable settings (hotkey, language).</param>
    /// <param name="local">Current machine-local settings (scan directories).</param>
    /// <param name="mode">The storage mode currently in effect.</param>
    /// <param name="customDir">The custom storage root (for <see cref="StorageMode.Custom"/>).</param>
    /// <param name="onSave">Applies the settings; returns whether the hotkey registered.</param>
    /// <param name="onStorageModeChange">Switches storage mode (moves the roamable config files).</param>
    public SettingsWindow(AppSettings settings, LocalSettings local,
        StorageMode mode, string customDir,
        Func<AppSettings, LocalSettings, bool> onSave,
        Action<StorageMode, string> onStorageModeChange) : this()
    {
        _onSave = onSave;
        _onStorageModeChange = onStorageModeChange;

        HotkeyBox.Text = settings.Hotkey;
        StartupCheck.IsChecked = AutoStart.IsEnabled();

        // Language picker: index 0 = follow system, then "en", "zh".
        LanguageBox.ItemsSource = new[] { Localizer.Get("FollowSystem"), "English", "中文" };
        LanguageBox.SelectedIndex = settings.Language switch { "en" => 1, "zh" => 2, _ => 0 };

        // Storage mode picker: index matches the enum (Default, Portable, Custom).
        StorageModeBox.ItemsSource = new[]
        {
            Localizer.Get("StorageModeDefault"),
            Localizer.Get("StorageModePortable"),
            Localizer.Get("StorageModeCustom"),
        };
        StorageModeBox.SelectedIndex = (int)mode;
        CustomDirBox.Text = customDir;
        UpdateCustomDirVisibility();
        StorageModeBox.SelectionChanged += (_, _) => UpdateCustomDirVisibility();
        BrowseCustomDirButton.Click += OnBrowseCustomDir;

        foreach (string dir in local.ScanDirectories)
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
        => ShowError(string.Format(Localizer.Get("HotkeyError"), hotkeyText));

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.IsVisible = true;
    }

    private void UpdateCustomDirVisibility()
        => CustomDirPanel.IsVisible = StorageModeBox.SelectedIndex == (int)StorageMode.Custom;

    private async void OnBrowseCustomDir(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localizer.Get("PickFolderTitle"),
            AllowMultiple = false,
        });

        string? path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (!string.IsNullOrEmpty(path))
            CustomDirBox.Text = path;
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

        var mode = (StorageMode)StorageModeBox.SelectedIndex;
        string customDir = (CustomDirBox.Text ?? string.Empty).Trim();
        if (mode == StorageMode.Custom && !Path.IsPathFullyQualified(customDir))
        {
            ShowError(Localizer.Get("CustomDirRequired")); // keep the window open
            return;
        }

        AutoStart.SetEnabled(StartupCheck.IsChecked == true);

        // Switch storage mode first so the settings below are saved to the right place.
        _onStorageModeChange?.Invoke(mode, customDir);

        var settings = new AppSettings
        {
            Hotkey = hotkey,
            Language = LanguageBox.SelectedIndex switch { 1 => "en", 2 => "zh", _ => "" },
        };

        var local = new LocalSettings
        {
            StorageMode = mode,
            CustomStorageDir = customDir,
            ScanDirectories = _dirs.ToList(),
        };

        bool hotkeyOk = _onSave?.Invoke(settings, local) ?? true;
        if (hotkeyOk)
            Close();
        else
            ShowHotkeyError(hotkey); // keep the window open so the user can fix it
    }
}
