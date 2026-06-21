using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Jeek.Avalonia.Localization;

namespace JeekLauncher.Views;

/// <summary>
/// A tiny code-built confirmation / info dialog. Avalonia has no built-in message
/// box, and the launcher's main window is usually hidden off-screen — so these are
/// shown as their own top-level windows (never <c>ShowDialog</c> over a hidden owner,
/// which would hang).
/// </summary>
public static class MessageDialog
{
    /// <summary>Shows a message with a single OK button; completes when dismissed.</summary>
    public static Task ShowInfoAsync(string title, string message, WindowIcon? icon = null)
        => ShowAsync(title, message, Localizer.Get("OK"), null, icon);

    /// <summary>Shows a confirm dialog; the task is true if the primary button was clicked.</summary>
    public static Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText, string cancelText, WindowIcon? icon = null)
        => ShowAsync(title, message, confirmText, cancelText, icon);

    private static Task<bool> ShowAsync(string title, string message,
        string primaryText, string? secondaryText, WindowIcon? icon)
    {
        var tcs = new TaskCompletionSource<bool>();

        var window = new Window
        {
            Title = title,
            Icon = icon,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            MinWidth = 360,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var primary = new Button { Content = primaryText, MinWidth = 96, IsDefault = true };
        primary.Click += (_, _) => { tcs.TrySetResult(true); window.Close(); };
        buttons.Children.Add(primary);

        if (secondaryText is not null)
        {
            var secondary = new Button { Content = secondaryText, MinWidth = 96, IsCancel = true };
            secondary.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
            buttons.Children.Add(secondary);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                buttons,
            },
        };

        // Closing via the title bar (no button) counts as "not confirmed".
        window.Closed += (_, _) => tcs.TrySetResult(false);
        window.Show();
        window.Activate();
        return tcs.Task;
    }
}
