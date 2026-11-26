using System.Threading;
using Avalonia;

namespace JeekLauncher;

internal static class Program
{
    // Keeps the single-instance lock alive for the whole process lifetime.
    private static Mutex? _instanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--check-runtime"))
            return; // Installer probe: the apphost has resolved and loaded the runtime.

        // Headless diagnostics: JeekLauncher.exe --selftest [all|search|watch|autostart|lang|quickwords|storage]
        if (args.Contains("--selftest"))
        {
            string mode = args.Contains("all") ? "all"
                : args.Contains("watch") ? "watch"
                : args.Contains("autostart") ? "autostart"
                : args.Contains("lang") ? "lang"
                : args.Contains("quickwords") ? "quickwords"
                : args.Contains("storage") ? "storage"
                : "search";
            Environment.ExitCode = SelfTest.Run(mode) ? 0 : 1;
            return;
        }

        // Single instance: if we are not the first, signal the running instance
        // to pop up its launcher window and then exit quietly.
        _instanceMutex = new Mutex(initiallyOwned: true, App.MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting(App.ShowEventName);
                ev.Set();
            }
            catch
            {
                // The other instance may still be starting up; nothing else to do.
            }
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
