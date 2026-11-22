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
    public static async Task ShowInfoAsync(string title, string message, WindowIcon? icon = null)
        => await ShowAsync(title, message, Localizer.Get("OK"), null, null, icon);

    /// <summary>Shows a confirm dialog; the task is true if the primary button was clicked.</summary>
    public static async Task<bool> ShowConfirmAsync(string title, string message,
        string confirmText, string cancelText, WindowIcon? icon = null)
        => await ShowAsync(title, message, confirmText, null, cancelText, icon) == true;

    /// <summary>
    /// Shows a three-way choice dialog: primary returns <c>true</c>, secondary returns
    /// <c>false</c>, and cancel or closing the window returns <c>null</c>.
    /// </summary>
    public static Task<bool?> ShowChoiceAsync(string title, string message,
        string primaryText, string secondaryText, string cancelText, WindowIcon? icon = null)
        => ShowAsync(title, message, primaryText, secondaryText, cancelText, icon);

    private static Task<bool?> ShowAsync(string title, string message,
        string primaryText, string? secondaryText, string? cancelText, WindowIcon? icon)
    {
        var tcs = new TaskCompletionSource<bool?>();

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
            var secondary = new Button { Content = secondaryText, MinWidth = 96 };
            secondary.Click += (_, _) => { tcs.TrySetResult(false); window.Close(); };
            buttons.Children.Add(secondary);
        }

        if (cancelText is not null)
        {
            var cancel = new Button { Content = cancelText, MinWidth = 96, IsCancel = true };
            cancel.Click += (_, _) => { tcs.TrySetResult(null); window.Close(); };
            buttons.Children.Add(cancel);
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

        // Closing via the title bar (no button) cancels the dialog.
        window.Closed += (_, _) => tcs.TrySetResult(null);
        window.Show();
        window.Activate();
        return tcs.Task;
    }
}
