using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class WorkspaceView : UserControl
{
    private const double StickToBottomThreshold = 40;
    private bool _stickToBottom = true;

    public WorkspaceView()
    {
        InitializeComponent();
        // Tunnel, so Enter is seen before the multi-line TextBox turns it into a new line.
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ConversationScroll.ScrollChanged += OnConversationScrollChanged;
    }

    private WorkspaceViewModel? ViewModel => DataContext as WorkspaceViewModel;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && ViewModel?.StopCommand.CanExecute(null) == true)
        {
            ViewModel.StopCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (ViewModel?.SendCommand.CanExecute(null) == true)
            {
                ViewModel.SendCommand.Execute(null);
                _stickToBottom = true;
            }
        }
    }

    /// <summary>Follows new output unless the user has scrolled up (DESIGN.md §5).</summary>
    private void OnConversationScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        var scroll = ConversationScroll;
        var distanceFromBottom = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        if (e.ExtentDelta.Y != 0)
        {
            if (_stickToBottom)
            {
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            }
        }
        else if (e.OffsetDelta.Y != 0)
        {
            _stickToBottom = distanceFromBottom <= StickToBottomThreshold;
        }
    }
}
