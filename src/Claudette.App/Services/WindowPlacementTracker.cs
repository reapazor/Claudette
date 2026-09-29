using Avalonia;
using Avalonia.Controls;
using Claudette.Core.Development;

namespace Claudette.App.Services;

/// <summary>
/// The main window's position and size, remembered on this machine between launches (DESIGN.md §14, "What stays on each
/// machine") and carried across a source build's restart (§9). A maximized window keeps its normal bounds, so it
/// comes back maximized and un-maximizes to where it was.
/// </summary>
public sealed class WindowPlacementTracker
{
    /// <summary>How much of the title bar must be on a screen for a saved position to be used.</summary>
    private static readonly PixelSize VisibleTitleBar = new(120, 32);

    private readonly Window _window;
    private PixelPoint _position;
    private Size _size;

    public WindowPlacementTracker(Window window)
    {
        _window = window;
        _position = window.Position;
        _size = new Size(window.Width, window.Height);
        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal)
            {
                _position = e.Point;
            }
        };
        window.PropertyChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal && e.Property == TopLevel.ClientSizeProperty && window.ClientSize is { Width: > 0, Height: > 0 } size)
            {
                _size = size;
            }
        };
    }

    /// <summary>The window's normal bounds, and whether it's maximized.</summary>
    public WindowPlacement Current() =>
        new(_position.X, _position.Y, _size.Width, _size.Height, _window.WindowState == WindowState.Maximized);

    /// <summary>
    /// Puts the window where it was, before it's shown. A position no longer on any screen (a monitor unplugged since)
    /// keeps the size and lets the OS place the window.
    /// </summary>
    public void Apply(WindowPlacement placement)
    {
        if (placement.Width >= _window.MinWidth && placement.Height >= _window.MinHeight)
        {
            _window.Width = placement.Width;
            _window.Height = placement.Height;
            _size = new Size(placement.Width, placement.Height);
        }
        var screens = _window.Screens.All.Select(s => s.WorkingArea).ToArray();
        if (screens.Length == 0 || IsOnScreen(placement, screens))
        {
            _window.WindowStartupLocation = WindowStartupLocation.Manual;
            _window.Position = _position = new PixelPoint(placement.X, placement.Y);
        }
        if (placement.IsMaximized)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Whether enough of the title bar at the placement's position is on one of the screens to grab it.</summary>
    public static bool IsOnScreen(WindowPlacement placement, IEnumerable<PixelRect> workingAreas)
    {
        var titleBar = new PixelRect(new PixelPoint(placement.X, placement.Y), VisibleTitleBar);
        return workingAreas.Any(area => area.Intersect(titleBar) is { Width: >= 40, Height: >= 16 });
    }
}
