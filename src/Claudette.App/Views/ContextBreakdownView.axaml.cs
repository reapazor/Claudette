using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Claudette.App.Views;

/// <summary>What fills a tab's context window: the flyout of its context ring and of the composer's indicator (DESIGN.md §6).</summary>
public partial class ContextBreakdownView : UserControl
{
    public ContextBreakdownView() => InitializeComponent();

    /// <summary>Compact now closes the flyout once its command has run, as the composer's menus do.</summary>
    private void OnCompactClick(object? sender, RoutedEventArgs e)
    {
        if (this.FindAncestorOfType<FlyoutPresenter>()?.Parent is Popup popup)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(popup.Close);
        }
    }
}
