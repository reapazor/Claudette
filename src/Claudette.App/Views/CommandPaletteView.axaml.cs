using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

/// <summary>The command palette (DESIGN.md §4): type to filter, Up and Down to choose, Enter to run, Escape to close.</summary>
public partial class CommandPaletteView : UserControl
{
    public CommandPaletteView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => QueryBox.Focus());
    }

    private CommandPaletteViewModel? ViewModel => DataContext as CommandPaletteViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } palette)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                _ = palette.RunCommand.ExecuteAsync(null);
                break;
            case Key.Down:
                palette.MoveDownCommand.Execute(null);
                Results.ScrollIntoView(palette.Selected!);
                break;
            case Key.Up:
                palette.MoveUpCommand.Execute(null);
                Results.ScrollIntoView(palette.Selected!);
                break;
            case Key.Escape:
                palette.CloseCommand.Execute(null);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } palette)
        {
            _ = palette.RunCommand.ExecuteAsync(null);
        }
    }
}
