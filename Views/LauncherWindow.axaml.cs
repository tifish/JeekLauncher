using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using JeekLauncher.Models;
using JeekLauncher.ViewModels;

namespace JeekLauncher.Views;

public partial class LauncherWindow : Window
{
    /// <summary>Raised when the user picks an item (Enter / double-click).</summary>
    public event Action<AppEntry, string>? LaunchRequested;

    /// <summary>Raised when the window should be dismissed (Esc / focus lost).</summary>
    public event Action? HideRequested;

    public LauncherWindow()
    {
        InitializeComponent();

        // Intercept navigation keys before the TextBox handles them, so the search
        // box keeps focus while the arrow keys move the selection.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ResultList.DoubleTapped += (_, _) => LaunchSelected();
        Deactivated += (_, _) => HideRequested?.Invoke();
    }

    public LauncherWindow(LauncherViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private LauncherViewModel Vm => (LauncherViewModel)DataContext!;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Tab:
                MoveSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(8);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-8);
                e.Handled = true;
                break;
            case Key.Enter:
                LaunchSelected();
                e.Handled = true;
                break;
            case Key.Escape:
                HideRequested?.Invoke();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        int count = Vm.Results.Count;
        if (count == 0)
            return;

        int next = Math.Clamp(Vm.SelectedIndex + delta, 0, count - 1);
        Vm.SelectedIndex = next;

        if (Vm.Selected is { } item)
            ResultList.ScrollIntoView(item);
    }

    private void LaunchSelected()
    {
        if (Vm.Selected is { } entry)
            LaunchRequested?.Invoke(entry, Vm.Query);
    }
}
