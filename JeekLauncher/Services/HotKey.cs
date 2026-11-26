using System.Runtime.InteropServices;

namespace JeekLauncher.Services;

/// <summary>
/// Registers a system-wide hotkey against an existing window handle and surfaces
/// <see cref="Pressed"/> when it fires. The <see cref="WndProc"/> method is meant
/// to be plugged into Avalonia's Win32 message hook.
/// </summary>
internal sealed class HotKey
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4A4B; // arbitrary, unique within our window
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr _hwnd;
    private bool _registered;
    private uint _modifiers;
    private uint _virtualKey;

    public Action? Pressed { get; set; }
    public bool IsRegistered => _registered;

    /// <summary>
    /// Registers the hotkey. If the new combination is taken, the previously working
    /// binding (if any) is restored so the launcher keeps a usable hotkey.
    /// </summary>
    public bool Register(IntPtr hwnd, uint modifiers, uint virtualKey)
    {
        if (_registered)
            UnregisterHotKey(_hwnd, HotkeyId);

        _hwnd = hwnd;
        if (RegisterHotKey(hwnd, HotkeyId, modifiers | ModNoRepeat, virtualKey))
        {
            _registered = true;
            _modifiers = modifiers;
            _virtualKey = virtualKey;
            return true;
        }

        // New combination unavailable — roll back to the last one that worked.
        _registered = (_modifiers != 0 || _virtualKey != 0)
            && RegisterHotKey(hwnd, HotkeyId, _modifiers | ModNoRepeat, _virtualKey);
        return false;
    }

    /// <summary>
    /// Tests whether a hotkey combination can be registered without leaving it registered
    /// or altering the saved rollback binding. Expects the hotkey to be currently unregistered.
    /// </summary>
    public bool CanRegister(IntPtr hwnd, uint modifiers, uint virtualKey)
    {
        if (hwnd == IntPtr.Zero)
            return true;

        if (!RegisterHotKey(hwnd, HotkeyId, modifiers | ModNoRepeat, virtualKey))
            return false;

        UnregisterHotKey(hwnd, HotkeyId);
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    /// <summary>Avalonia <c>Win32Properties.CustomWndProcHookCallback</c> handler.</summary>
    public IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam == (IntPtr)HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }

        return IntPtr.Zero;
    }
}

/// <summary>Parses a textual hotkey such as "Ctrl+Alt+Space" into Win32 codes.</summary>
internal sealed class HotKeyDef
{
    public uint Modifiers { get; private init; }
    public uint VirtualKey { get; private init; }
    public string Text { get; private init; } = "Alt+Space";

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static HotKeyDef Parse(string text)
    {
        uint modifiers = 0;
        uint vk = 0;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string token = raw.ToLowerInvariant();
            switch (token)
            {
                case "alt": modifiers |= ModAlt; break;
                case "ctrl" or "control": modifiers |= ModControl; break;
                case "shift": modifiers |= ModShift; break;
                case "win" or "windows" or "super" or "meta": modifiers |= ModWin; break;
                default:
                    if (TryMapKey(token, out uint mapped))
                        vk = mapped;
                    break;
            }
        }

        // Fall back to a sane default if the string was unusable.
        if (vk == 0)
        {
            modifiers = ModAlt;
            vk = 0x20; // Space
            text = "Alt+Space";
        }

        return new HotKeyDef { Modifiers = modifiers, VirtualKey = vk, Text = text };
    }

    private static bool TryMapKey(string token, out uint vk)
    {
        vk = 0;

        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c; // VK codes for A-Z and 0-9 match their ASCII values
                return true;
            }
        }

        switch (token)
        {
            case "space": vk = 0x20; return true;
            case "enter" or "return": vk = 0x0D; return true;
            case "tab": vk = 0x09; return true;
            case "esc" or "escape": vk = 0x1B; return true;
            case "backtick" or "grave" or "`": vk = 0xC0; return true;
        }

        if (token.Length >= 2 && token[0] == 'f' && int.TryParse(token.AsSpan(1), out int n) && n is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + (n - 1)); // F1 = 0x70
            return true;
        }

        return false;
    }
}
