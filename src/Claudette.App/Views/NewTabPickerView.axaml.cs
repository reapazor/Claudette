using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class NewTabPickerView : UserControl
{
    public NewTabPickerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AttachedToVisualTree += (_, _) => SearchBox.Focus();
    }

    private NewTabPickerViewModel? ViewModel => DataContext as NewTabPickerViewModel;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } picker)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                picker.OpenCommand.Execute(picker.Selected);
                e.Handled = true;
                break;
            case Key.Down or Key.Up:
                var all = picker.All;
                if (all.Count > 0)
                {
                    var index = picker.Selected is null ? -1 : IndexOf(all, picker.Selected);
                    index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, all.Count - 1);
                    picker.Selected = all[index];
                    Folders.ScrollIntoView(picker.Selected);
                }
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9 when picker.Search.Length == 0 && e.KeyModifiers == KeyModifiers.None:
                _ = picker.OpenNumberAsync(e.Key - Key.D0);
                e.Handled = true;
                break;
        }
    }

    private void OnFolderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } picker)
        {
            picker.OpenCommand.Execute(picker.Selected);
        }
    }

    private static int IndexOf(IReadOnlyList<FolderEntry> list, FolderEntry item)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
            {
                return i;
            }
        }
        return -1;
    }
}
