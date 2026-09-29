using Avalonia.Controls;

namespace Claudette.App.Views;

/// <summary>The selected agent's details in the agent map (DESIGN.md §18).</summary>
public partial class AgentDetails : UserControl
{
    public AgentDetails()
    {
        InitializeComponent();
        // Code blocks in a prompt or report copy through the tab, as in the conversation (DESIGN.md §5).
        CodeBlockCopy.Attach(this);
    }
}
