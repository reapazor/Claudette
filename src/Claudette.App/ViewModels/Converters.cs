using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Claudette.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoldIfTrue = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>An indent, for tree rows.</summary>
    public static readonly IValueConverter LeftMargin = new FuncValueConverter<double, Thickness>(left => new Thickness(left, 0, 0, 0));

    /// <summary>Marks a process that left the tab's tree (DESIGN.md §4).</summary>
    public static readonly IValueConverter DetachedText = new FuncValueConverter<bool, string>(detached => detached ? "· detached" : "");

    /// <summary>A tab's square in the collapsed sidebar: the first letter or digit of its name (DESIGN.md §4).</summary>
    public static readonly IValueConverter Initial = new FuncValueConverter<string?, string>(Initials);

    public static string Initials(string? name) =>
        name?.FirstOrDefault(char.IsLetterOrDigit) is { } first and not '\0' ? char.ToUpperInvariant(first).ToString() : "·";
}
