using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Jeek.Avalonia.Localization;
using JeekLauncher.Models;
using JeekLauncher.ViewModels;

namespace JeekLauncher.Views;

public partial class LauncherWindow : Window
{
    /// <summary>Raised when the user picks an item (Enter / double-click).</summary>
    public event Action<AppEntry, string>? LaunchRequested;

    /// <summary>Raised when the window should be dismissed (Esc / focus lost).</summary>
    public event Action? HideRequested;

    /// <summary>Raised when the user binds a quick word to an app and launches it.</summary>
    public event Action<AppEntry, string>? QuickWordSetRequested;

    private bool _editingQuickWord;
    private AppEntry? _quickWordTarget;

    public LauncherWindow()
    {
        InitializeComponent();

        // Intercept navigation keys before the TextBox handles them, so the search
        // box keeps focus while the arrow keys move the selection.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        ResultList.DoubleTapped += (_, _) => LaunchSelected();
        SetQuickWordButton.Click += (_, _) => OpenQuickWordEditor();
        Deactivated += (_, _) => HideRequested?.Invoke();
    }

    public LauncherWindow(LauncherViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    private LauncherViewModel Vm => (LauncherViewModel)DataContext!;

    public void FocusSearch()
    {
        CloseQuickWordEditor();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // While the quick-word editor is open, keep keystrokes in its text box and
        // only react to Enter (save) / Escape (cancel) — never move the list or hide.
        if (_editingQuickWord)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    ConfirmQuickWord();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    FocusSearch();
                    e.Handled = true;
                    break;
            }
            return;
        }

        // Ctrl+G: bind a quick word to the selected app and launch it.
        if (e.Key == Key.G && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            OpenQuickWordEditor();
            e.Handled = true;
            return;
        }

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

    private void OpenQuickWordEditor()
    {
        if (Vm.Selected is not { } entry)
        {
            Vm.SetStatus(Localizer.Get("QuickWordNoSelection"));
            return;
        }

        _quickWordTarget = entry;
        _editingQuickWord = true;
        QuickWordPrompt.Text = string.Format(Localizer.Get("QuickWordEditorPrompt"), entry.Name);
        QuickWordBox.Text = string.Empty;
        QuickWordEditor.IsVisible = true;
        QuickWordBox.Focus();
    }

    private void ConfirmQuickWord()
    {
        var entry = _quickWordTarget;
        string word = (QuickWordBox.Text ?? string.Empty).Trim();

        CloseQuickWordEditor();

        if (entry is not null && word.Length > 0)
            QuickWordSetRequested?.Invoke(entry, word);
        else
            SearchBox.Focus();
    }

    private void CloseQuickWordEditor()
    {
        _editingQuickWord = false;
        _quickWordTarget = null;
        QuickWordEditor.IsVisible = false;
    }
}
