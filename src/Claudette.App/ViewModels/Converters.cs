using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Claudette.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoldIfTrue = new FuncValueConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);
}
