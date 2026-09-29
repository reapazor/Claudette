using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class ShellView : UserControl
{
    private TopLevel? _topLevel;

    public ShellView()
    {
        InitializeComponent();
    }

    private ShellViewModel? ViewModel => DataContext as ShellViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } shell)
        {
            shell.ShowSettingsWindow = ShowSettingsAsync;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        // Tunnel, so shortcuts work wherever the focus is (DESIGN.md §4, "Keyboard").
        _topLevel?.AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(KeyDownEvent, OnWindowKeyDown);
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } shell)
        {
            return;
        }
        var command = this.GetPlatformSettings()?.HotkeyConfiguration.CommandModifiers ?? KeyModifiers.Control;
        var mods = e.KeyModifiers;

        if (e.Key == Key.Tab && mods.HasFlag(KeyModifiers.Control))
        {
            if (mods.HasFlag(KeyModifiers.Shift))
            {
                shell.SelectPreviousCommand.Execute(null);
            }
            else
            {
                shell.SelectNextCommand.Execute(null);
            }
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && shell.IsPickerOpen)
        {
            shell.ClosePicker();
            e.Handled = true;
            return;
        }
        if (!mods.HasFlag(command) || mods.HasFlag(KeyModifiers.Shift) || mods.HasFlag(KeyModifiers.Alt))
        {
            return;
        }
        switch (e.Key)
        {
            case Key.T:
                shell.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W:
                shell.CloseSelectedTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemComma:
                shell.OpenSettingsCommand.Execute(null);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
                shell.SelectNumber(e.Key - Key.D0);
                e.Handled = true;
                break;
        }
    }

    private void OnTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TabViewModel tab)
        {
            tab.StartRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty && box.IsVisible)
                {
                    box.Focus();
                    box.SelectAll();
                }
            };
        }
    }

    private void OnRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if ((sender as Control)?.DataContext is not TabViewModel tab)
        {
            return;
        }
        if (e.Key == Key.Enter)
        {
            tab.CommitRenameCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelRenameCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is TabViewModel { IsRenaming: true } tab)
        {
            tab.CommitRenameCommand.Execute(null);
        }
    }

    /// <summary>Clicking the dimmed area around the picker closes it.</summary>
    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender))
        {
            ViewModel?.ClosePicker();
        }
    }

    private async Task ShowSettingsAsync()
    {
        if (this.FindAncestorOfType<Window>() is not { } owner || owner.DataContext is not MainWindowViewModel main)
        {
            return;
        }
        var window = new SettingsWindow { DataContext = new SettingsViewModel(main.Services, main.AccountText) };
        await window.ShowDialog(owner);
    }
}
