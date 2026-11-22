using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Jeek.Avalonia.Localization;
using JeekLauncher.Services;
using JeekTools;

namespace JeekLauncher.Views;

public partial class SettingsWindow : Window
{
    private readonly ObservableCollection<string> _dirs = new();
    private readonly Func<string, bool>? _canRegisterHotkey;
    private readonly Func<AppSettings, LocalSettings, bool>? _onSave;
    private readonly Action<StorageLocation, string, bool>? _onStorageLocationChange;
    private readonly Action? _onCheckUpdates;
    private bool? _initialAutoStart;
    private bool _autoStartUserToggled;

    public SettingsWindow()
    {
        InitializeComponent();
    }

    /// <param name="settings">Current roamable settings (hotkey, language).</param>
    /// <param name="local">Current machine-local settings (scan directories).</param>
    /// <param name="location">The storage location currently in effect.</param>
    /// <param name="customDir">The custom storage root (for <see cref="StorageLocation.CustomDirectory"/>).</param>
    /// <param name="canRegisterHotkey">Checks whether a hotkey can be registered before applying other changes.</param>
    /// <param name="onSave">Applies the settings; returns whether the hotkey registered.</param>
    /// <param name="onStorageLocationChange">Switches storage location; the flag says whether to
    /// move the existing roamable config files along.</param>
    public SettingsWindow(AppSettings settings, LocalSettings local,
        StorageLocation location, string customDir,
        Func<string, bool> canRegisterHotkey,
        Func<AppSettings, LocalSettings, bool> onSave,
        Action<StorageLocation, string, bool> onStorageLocationChange,
        Action onCheckUpdates) : this()
    {
        _canRegisterHotkey = canRegisterHotkey;
        _onSave = onSave;
        _onStorageLocationChange = onStorageLocationChange;
        _onCheckUpdates = onCheckUpdates;

        HotkeyBox.Text = settings.Hotkey;
        // Capture the shortcut directly instead of letting the user type it. Tunnel so
        // we see the keys before the (read-only) TextBox does.
        HotkeyBox.AddHandler(KeyDownEvent, OnHotkeyKeyDown, RoutingStrategies.Tunnel);

        // Query Task Scheduler off the UI thread so opening Settings stays instant.
        StartupCheck.IsEnabled = false;
        StartupCheck.IsCheckedChanged += (_, _) =>
        {
            if (StartupCheck.IsEnabled)
                _autoStartUserToggled = true;
        };
        _ = LoadAutoStartStateAsync();

        // Populate code-built localized items and keep them in sync on language switches.
        PopulateLocalizedChoices(
            settings.Language switch { "en" => 1, "zh" => 2, _ => 0 },
            settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 },
            settings.UpdateCheckIntervalHours switch { 6 => 1, 24 => 2, 168 => 3, _ => 0 },
            (int)location);
        UpdateStartupCheck.IsChecked = settings.CheckUpdateOnStartup;
        CheckUpdatesButton.Click += (_, _) => _onCheckUpdates?.Invoke();
        HomepageButton.Click += (_, _) => OpenHomepage();

        CustomDirBox.Text = customDir;
        UpdateCustomDirVisibility();
        StorageModeBox.SelectionChanged += (_, _) => UpdateCustomDirVisibility();
        BrowseCustomDirButton.Click += OnBrowseCustomDir;

        Localizer.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Localizer.LanguageChanged -= OnLanguageChanged;

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

    private async Task LoadAutoStartStateAsync()
    {
        bool enabled = await Task.Run(AutoStart.IsEnabled);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _initialAutoStart = enabled;
            if (!_autoStartUserToggled)
                StartupCheck.IsChecked = enabled;
            StartupCheck.IsEnabled = true;
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        PopulateLocalizedChoices(
            LanguageBox.SelectedIndex,
            ThemeBox.SelectedIndex,
            UpdateIntervalBox.SelectedIndex,
            StorageModeBox.SelectedIndex);
    }

    private void PopulateLocalizedChoices(int langIndex, int themeIndex, int intervalIndex, int storageIndex)
    {
        LanguageBox.ItemsSource = new[] { Localizer.Get("FollowSystem"), "English", "中文" };
        LanguageBox.SelectedIndex = langIndex >= 0 ? langIndex : 0;

        ThemeBox.ItemsSource = new[] { Localizer.Get("FollowSystem"), Localizer.Get("ThemeLight"), Localizer.Get("ThemeDark") };
        ThemeBox.SelectedIndex = themeIndex >= 0 ? themeIndex : 0;

        VersionText.Text = $"{Localizer.Get("Version")}: " +
            (AutoUpdateService.IsDevBuild ? Localizer.Get("DevBuild") : AutoUpdateService.GetLocalCommitCount().ToString());

        UpdateIntervalBox.ItemsSource = new[]
        {
            Localizer.Get("UpdateIntervalNone"),
            Localizer.Get("UpdateInterval6h"),
            Localizer.Get("UpdateIntervalDaily"),
            Localizer.Get("UpdateIntervalWeekly"),
        };
        UpdateIntervalBox.SelectedIndex = intervalIndex >= 0 ? intervalIndex : 0;

        StorageModeBox.ItemsSource = new[]
        {
            Localizer.Get("StorageModeDefault"),
            Localizer.Get("StorageModePortable"),
            Localizer.Get("StorageModeCustom"),
        };
        StorageModeBox.SelectedIndex = storageIndex >= 0 ? storageIndex : 0;
    }

    /// <summary>
    /// Records the pressed combination into the hotkey box as a canonical
    /// "Ctrl+Alt+Space" string. Only keys <see cref="HotKeyDef.Parse"/> understands are
    /// accepted, and ordinary keys require a modifier so a stray letter can't be bound.
    /// </summary>
    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        // Wait for the real key — a lone modifier press isn't a hotkey yet.
        if (IsModifierKey(e.Key))
            return;

        string? token = KeyToToken(e.Key);
        if (token is null)
            return; // unsupported key (arrows, etc.) — let it pass through

        bool isFunctionKey = e.Key is >= Key.F1 and <= Key.F24;
        if (e.KeyModifiers == KeyModifiers.None && !isFunctionKey)
            return; // bare letter/digit/Space — leave focus navigation intact

        var parts = new List<string>(4);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(token);

        HotkeyBox.Text = string.Join("+", parts);
        ErrorBanner.IsVisible = false;
        e.Handled = true; // swallow so the combo doesn't also act on the box
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    /// <summary>Maps an Avalonia key to a token <see cref="HotKeyDef.Parse"/> accepts, else null.</summary>
    private static string? KeyToToken(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.OemTilde => "`",
        _ => null,
    };

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
        => CustomDirPanel.IsVisible = StorageModeBox.SelectedIndex == (int)StorageLocation.CustomDirectory;

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

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        ErrorBanner.IsVisible = false;

        string hotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Alt+Space" : HotkeyBox.Text.Trim();

        var location = StorageModeBox.SelectedIndex >= 0
            ? (StorageLocation)StorageModeBox.SelectedIndex
            : StorageLocation.UserDirectory;
        string customDir = (CustomDirBox.Text ?? string.Empty).Trim();
        if (location == StorageLocation.CustomDirectory &&
            (!Path.IsPathFullyQualified(customDir) ||
             Storage.WouldMoveRoamableConfigIntoItself(location, customDir)))
        {
            ShowError(Localizer.Get("CustomDirRequired")); // keep the window open
            return;
        }

        // Verify hotkey availability before prompting to move config files or toggling autostart.
        if (_canRegisterHotkey is not null && !_canRegisterHotkey(hotkey))
        {
            ShowHotkeyError(hotkey);
            return;
        }

        if (!await TryApplyStorageLocationAsync(location, customDir))
            return; // cancelled or invalid — keep the window open

        var settings = new AppSettings
        {
            Hotkey = hotkey,
            Language = LanguageBox.SelectedIndex switch { 1 => "en", 2 => "zh", _ => "" },
            Theme = ThemeBox.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "" },
            CheckUpdateOnStartup = UpdateStartupCheck.IsChecked == true,
            UpdateCheckIntervalHours = UpdateIntervalBox.SelectedIndex switch { 1 => 6, 2 => 24, 3 => 168, _ => 0 },
        };

        var local = new LocalSettings
        {
            StorageLocation = location,
            CustomStorageDir = customDir,
            ScanDirectories = _dirs.ToList(),
        };

        bool hotkeyOk = _onSave?.Invoke(settings, local) ?? true;
        if (hotkeyOk)
        {
            bool desiredAutoStart = StartupCheck.IsChecked == true;
            if (StartupCheck.IsEnabled && (_autoStartUserToggled || _initialAutoStart != desiredAutoStart))
                _ = Task.Run(() => AutoStart.SetEnabled(desiredAutoStart));
            Close();
        }
        else
        {
            ShowHotkeyError(hotkey); // keep the window open so the user can fix it
        }
    }

    /// <summary>
    /// Applies a storage-location change before the settings are saved, so they land in the
    /// right folder. Asks first whether to bring the existing roamable config along; leaving
    /// portable mode has to move it, because the executable-side folder is deleted and any
    /// file left there would be lost. Returns false when the user backed out.
    /// </summary>
    private async Task<bool> TryApplyStorageLocationAsync(StorageLocation location, string customDir)
    {
        if (!Storage.WouldChangeRoamableConfigDir(location, customDir))
            return true;

        string title = Localizer.Get("AppName");
        string from = Storage.RoamableConfigDir;
        string to = Storage.ConfigRootFor(location, customDir);
        bool leavingPortable = Storage.EffectiveLocation == StorageLocation.ProgramDirectory
                               && location != StorageLocation.ProgramDirectory;

        bool moveFiles;
        if (leavingPortable)
        {
            // Not a choice: the portable folder is removed on the way out.
            if (!await MessageDialog.ShowConfirmAsync(title,
                    string.Format(Localizer.Get("MoveConfigRequired"), from, to),
                    Localizer.Get("MoveConfigMove"), Localizer.Get("Cancel"), Icon))
            {
                return false;
            }

            moveFiles = true;
        }
        else
        {
            bool? choice = await MessageDialog.ShowChoiceAsync(title,
                string.Format(Localizer.Get("MoveConfigQuestion"), from, to),
                Localizer.Get("MoveConfigMove"), Localizer.Get("MoveConfigKeep"), Localizer.Get("Cancel"), Icon);
            if (choice is null)
                return false;

            moveFiles = choice.Value;
        }

        try
        {
            _onStorageLocationChange?.Invoke(location, customDir, moveFiles);
        }
        catch (InvalidOperationException)
        {
            ShowError(Localizer.Get("CustomDirRequired"));
            return false;
        }
        catch (Exception ex)
        {
            ShowError(string.Format(Localizer.Get("MoveConfigFailed"), ex.Message));
            return false;
        }

        return true;
    }

    private static void OpenHomepage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AutoUpdateService.HomepageUrl) { UseShellExecute = true });
        }
        catch
        {
            // Best effort.
        }
    }
}
