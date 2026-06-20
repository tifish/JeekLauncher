using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Win32;
using Jeek.Avalonia.Localization;
using JeekLauncher.Models;
using JeekLauncher.Services;
using JeekLauncher.ViewModels;
using JeekLauncher.Views;

namespace JeekLauncher;

public partial class App : Application
{
    public const string MutexName = "JeekLauncher.SingleInstance.{B3F9A2C1-7E54-4D8B-9A1F-6C2E0D5B8E11}";
    public const string ShowEventName = "JeekLauncher.Show.{B3F9A2C1-7E54-4D8B-9A1F-6C2E0D5B8E11}";

    private static readonly Uri IconUri = new("avares://JeekLauncher/Assets/jeek.png");

    private AppSettings _settings = null!;
    private UsageStore _usage = null!;
    private LauncherViewModel _viewModel = null!;
    private LauncherWindow _window = null!;
    private readonly HotKey _hotKey = new();
    private TrayIcon? _tray;
    private WindowIcon? _appIcon;
    private string? _hotkeyFailedText;
    private EventWaitHandle? _showEvent;
    private DirectoryWatcher? _watcher;
    private IReadOnlyList<AppEntry> _entries = Array.Empty<AppEntry>();
    private SettingsWindow? _settingsWindow;
    private QuickWordsWindow? _quickWordsWindow;
    private bool _initialized;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The launcher lives in the tray; never quit just because the window hides.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Localizer.SetLocalizer(new TabLocalizer(
                Path.Combine(AppContext.BaseDirectory, "Data", "Languages.tab")));

            _settings = AppSettings.Load();
            ApplyLanguage(_settings.Language);
            _usage = UsageStore.Load();
            _viewModel = new LauncherViewModel(_usage);

            // Refresh code-built strings (tray labels, status line) on language change.
            Localizer.LanguageChanged += (_, _) =>
            {
                BuildTrayMenu();
                _viewModel.Refresh();
            };

            _window = new LauncherWindow(_viewModel);
            _window.LaunchRequested += OnLaunchRequested;
            _window.QuickWordSetRequested += OnQuickWordSetRequested;
            _window.HideRequested += HideLauncher;
            _window.Opened += OnWindowFirstOpened;

            _hotKey.Pressed = () => Dispatcher.UIThread.Post(ToggleLauncher);

            _appIcon = LoadAppIcon();

            SetUpTray();
            SetUpShowSignal();
            StartScan();
            SetUpWatcher();

            // Realize the native window off-screen so we have a handle for the
            // global hotkey, then hide it until summoned (done in OnWindowFirstOpened).
            _window.Position = new PixelPoint(-32000, -32000);
            _window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnWindowFirstOpened(object? sender, EventArgs e)
    {
        if (_initialized)
            return;
        _initialized = true;

        _window.Opened -= OnWindowFirstOpened;

        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            Win32Properties.AddWndProcHookCallback(_window, _hotKey.WndProc);

            var hotkey = HotKeyDef.Parse(_settings.Hotkey);
            if (!_hotKey.Register(handle, hotkey.Modifiers, hotkey.VirtualKey))
            {
                _hotkeyFailedText = hotkey.Text;
                BuildTrayMenu();
                Dispatcher.UIThread.Post(() => OpenSettings(hotkey.Text));
            }
        }

        _window.Hide();
    }

    // ---- Show / hide -------------------------------------------------------

    private void ToggleLauncher()
    {
        if (_window.IsVisible)
            HideLauncher();
        else
            ShowLauncher();
    }

    private void ShowLauncher()
    {
        PositionWindow();
        _viewModel.Reset();

        _window.Show();
        _window.Activate();

        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Win32Interop.ForceForeground(handle);

        // Focus after the show has been processed so the caret lands in the box.
        Dispatcher.UIThread.Post(() => _window.FocusSearch(), DispatcherPriority.Input);
    }

    private void HideLauncher() => _window.Hide();

    private void PositionWindow()
    {
        // Prefer the screen under the mouse cursor for multi-monitor setups.
        Screen? screen = null;
        if (Win32Interop.GetCursorPos(out var p))
            screen = _window.Screens.ScreenFromPoint(new PixelPoint(p.X, p.Y));
        screen ??= _window.Screens.Primary ?? _window.Screens.All.FirstOrDefault();
        if (screen is null)
            return;

        var area = screen.WorkingArea;
        double scale = screen.Scaling;
        int width = (int)(_window.Width * scale);
        int height = (int)(_window.Height * scale);

        int x = area.X + (area.Width - width) / 2;
        int y = area.Y + (int)(area.Height * 0.22);
        _window.Position = new PixelPoint(x, y);
    }

    // ---- Launch ------------------------------------------------------------

    private void OnLaunchRequested(AppEntry entry, string query)
    {
        HideLauncher();

        try
        {
            if (entry.Aumid is not null)
            {
                // Packaged / AppsFolder app: activate by AUMID via the shell.
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{entry.Aumid}")
                {
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
            }
        }
        catch
        {
            // Target may be missing/blocked; nothing actionable to show here.
            return;
        }

        // Remember the quick word so this query jumps to the top next time.
        _usage.Record(query, entry.Id);
        _usage.Save();
    }

    // Bind a quick word to an app explicitly, without launching it.
    private void OnQuickWordSetRequested(AppEntry entry, string word)
    {
        string w = word.Trim().ToLowerInvariant();
        if (w.Length == 0)
            return;

        _usage.Learned[w] = entry.Id;
        _usage.Save();

        _quickWordsWindow?.Reload();
        _viewModel.SetStatus(string.Format(Localizer.Get("QuickWordSaved"), w, entry.Name));
    }

    // ---- Scanning ----------------------------------------------------------

    private void StartScan()
    {
        var settings = _settings;
        var thread = new Thread(() =>
        {
            var entries = ShortcutScanner.Scan(settings);
            Dispatcher.UIThread.Post(() =>
            {
                _entries = entries;
                _viewModel.SetEntries(entries);
            });
        })
        {
            IsBackground = true,
            Name = "JeekLauncher.Scan",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    // ---- Directory monitoring ----------------------------------------------

    private void SetUpWatcher()
    {
        _watcher = new DirectoryWatcher(() => Dispatcher.UIThread.Post(StartScan));
        _watcher.Watch(ShortcutScanner.GetDirectories(_settings));
    }

    // ---- Tray icon ---------------------------------------------------------

    private void SetUpTray()
    {
        if (_appIcon is not null)
            _window.Icon = _appIcon;

        _tray = new TrayIcon
        {
            Icon = _appIcon,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Dispatcher.UIThread.Post(ShowLauncher);

        BuildTrayMenu();
    }

    /// <summary>(Re)builds the tray menu and tooltip with the current language.</summary>
    private void BuildTrayMenu()
    {
        if (_tray is null)
            return;

        _tray.ToolTipText = _hotkeyFailedText is null
            ? Localizer.Get("AppName")
            : string.Format(Localizer.Get("HotkeyRegisterFailedTip"), _hotkeyFailedText);

        var menu = new NativeMenu();
        menu.Items.Add(TrayItem("TrayShow", () => Dispatcher.UIThread.Post(ShowLauncher)));
        menu.Items.Add(TrayItem("Rescan", () => Dispatcher.UIThread.Post(RescanFromDisk)));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("ManageQuickWords", () => Dispatcher.UIThread.Post(OpenQuickWords)));
        menu.Items.Add(TrayItem("Settings", () => Dispatcher.UIThread.Post(() => OpenSettings())));
        menu.Items.Add(TrayItem("OpenConfigFolder", OpenConfigFolder));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(TrayItem("TrayExit", Shutdown));
        _tray.Menu = menu;
    }

    private static NativeMenuItem TrayItem(string key, Action onClick)
    {
        var item = new NativeMenuItem(Localizer.Get(key));
        item.Click += (_, _) => onClick();
        return item;
    }

    private void RescanFromDisk()
    {
        // Reload settings (custom dirs may have been hand-edited) then rescan + re-watch.
        _settings = AppSettings.Load();
        _watcher?.Watch(ShortcutScanner.GetDirectories(_settings));
        StartScan();
    }

    // ---- Settings & quick-string windows -----------------------------------

    private void OpenSettings(string? hotkeyError = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings, ApplySettings) { Icon = _appIcon };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
        if (hotkeyError is not null)
            _settingsWindow.ShowHotkeyError(hotkeyError);
    }

    private void OpenQuickWords()
    {
        if (_quickWordsWindow is not null)
        {
            _quickWordsWindow.Activate();
            return;
        }

        _quickWordsWindow = new QuickWordsWindow(_usage, () => _entries, () => _viewModel.Refresh())
        {
            Icon = _appIcon,
        };
        _quickWordsWindow.Closed += (_, _) => _quickWordsWindow = null;
        _quickWordsWindow.Show();
        _quickWordsWindow.Activate();
    }

    /// <summary>Applies new settings; returns whether the hotkey registered successfully.</summary>
    private bool ApplySettings(AppSettings newSettings)
    {
        _settings = newSettings;
        _settings.Save();

        // Switch language (raises LanguageChanged -> rebuilds tray + status line).
        ApplyLanguage(_settings.Language);

        // Re-register the hotkey live.
        bool hotkeyOk = true;
        IntPtr handle = _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            var hotkey = HotKeyDef.Parse(_settings.Hotkey);
            hotkeyOk = _hotKey.Register(handle, hotkey.Modifiers, hotkey.VirtualKey);
            _hotkeyFailedText = hotkeyOk ? null : hotkey.Text;
        }

        BuildTrayMenu(); // refresh tooltip (and labels, if language was unchanged)

        // Re-watch the new directory set and rescan.
        _watcher?.Watch(ShortcutScanner.GetDirectories(_settings));
        StartScan();

        return hotkeyOk;
    }

    /// <summary>
    /// Sets the UI language. An empty value follows the system UI culture
    /// (Chinese → zh, otherwise en).
    /// </summary>
    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrEmpty(language))
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";

        if (Localizer.Languages.Contains(language))
            Localizer.Language = language;
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(IconUri);
            return new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    private void OpenConfigFolder()
    {
        try
        {
            Storage.EnsureDir();
            Process.Start(new ProcessStartInfo(Storage.Dir) { UseShellExecute = true });
        }
        catch
        {
            // Best effort.
        }
    }

    // ---- Single-instance "show me" signal ----------------------------------

    private void SetUpShowSignal()
    {
        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.UIThread.Post(ShowLauncher);
            })
            {
                IsBackground = true,
                Name = "JeekLauncher.ShowSignal",
            };
            thread.Start();
        }
        catch
        {
            // Without the signal a second launch simply does nothing.
        }
    }

    // ---- Shutdown ----------------------------------------------------------

    private void Shutdown()
    {
        _hotKey.Unregister();
        _watcher?.Dispose();
        _settingsWindow?.Close();
        _quickWordsWindow?.Close();

        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
