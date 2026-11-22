using System.Runtime.InteropServices;

namespace JeekLauncher.Services;

/// <summary>Thin P/Invoke helpers for foreground/activation handling.</summary>
internal static partial class Win32Interop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [LibraryImport("user32.dll")]
    public static partial IntPtr SetFocus(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out POINT lpPoint);

    /// <summary>
    /// Reliably brings a window to the foreground even when called from a global
    /// hotkey, by briefly attaching to the currently-foreground thread's input.
    /// </summary>
    public static void ForceForeground(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
            return;

        IntPtr foreground = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();

        if (foreThread != thisThread && foreThread != 0)
        {
            bool attached = AttachThreadInput(foreThread, thisThread, true);
            try
            {
                SetForegroundWindow(hWnd);
                SetFocus(hWnd);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(foreThread, thisThread, false);
            }
        }
        else
        {
            SetForegroundWindow(hWnd);
        }
    }
}
