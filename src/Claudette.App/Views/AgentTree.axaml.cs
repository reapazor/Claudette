using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Claudette.App.Conversation;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

/// <summary>The agent map's tree (DESIGN.md §18). Clicking a node shows it in the conversation, or goes to its prompt.</summary>
public partial class AgentTree : UserControl
{
    public AgentTree()
    {
        InitializeComponent();
        Tree.AddHandler(TappedEvent, OnTreeTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private TabViewModel? ViewModel => DataContext as TabViewModel;

    private void OnTreeTapped(object? sender, TappedEventArgs e)
    {
        // The chevron only expands or collapses.
        if (e.Source is not Control source || source.FindAncestorOfType<ToggleButton>(includeSelf: true) is not null)
        {
            return;
        }
        if (source.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext is AgentNode node && ViewModel is { } tab)
        {
            tab.ShowAgentCommand.Execute(node);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { SelectedAgent: { } node } tab)
        {
            tab.ShowAgentCommand.Execute(node);
            e.Handled = true;
        }
    }
}
