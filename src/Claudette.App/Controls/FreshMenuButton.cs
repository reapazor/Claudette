using Avalonia.Controls;

namespace Claudette.App.Controls;

/// <summary>
/// A button whose menu is made each time it opens, from <see cref="MenuItems"/>, for a menu whose entries change between
/// one opening and the next, such as the side panel's pages that don't fit (DESIGN.md §3). A <see cref="MenuFlyout"/>
/// can't be filled as it opens: Avalonia 12.1 makes its presenter before raising <c>Opening</c>, and items added then
/// never show. A new menu, made with its items, does.
/// </summary>
public sealed class FreshMenuButton : Button
{
    /// <summary>Styled as a button, its classes included.</summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>The menu's entries, asked for each time it opens.</summary>
    public Func<IReadOnlyList<MenuItem>>? MenuItems { get; set; }

    public PlacementMode MenuPlacement { get; set; } = PlacementMode.BottomEdgeAlignedLeft;

    protected override void OpenFlyout()
    {
        if (MenuItems is { } items)
        {
            // The button closes a menu it has open, so this one is never replaced while it shows.
            Flyout = new MenuFlyout { Placement = MenuPlacement, ItemsSource = items() };
        }
        base.OpenFlyout();
    }
}
