using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Claudette.App.Themes;

/// <summary>Claudette's color tokens in Claude's colors (DESIGN.md §3, "Visual style"). <see cref="AppColors"/> applies them.</summary>
public partial class ClaudeColors : ResourceDictionary
{
    public ClaudeColors() => AvaloniaXamlLoader.Load(this);
}
