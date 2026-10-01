using Claudette.App.Conversation;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>Find in the conversation (DESIGN.md §5, "Find"): the bar over the conversation, and the match it shows.</summary>
public sealed partial class TabViewModel
{
    public ConversationSearch Find { get; }

    /// <summary>Asks the view to put the focus in the find bar's box, with its text selected.</summary>
    public event Action? FindFocusRequested;

    /// <summary>Ctrl/Cmd+F, or whatever Settings → Keyboard says: opens the bar, or goes back to it.</summary>
    [RelayCommand]
    private void OpenFind()
    {
        Find.IsOpen = true;
        FindFocusRequested?.Invoke();
    }

    /// <summary>The match the bar moved to: opened if a collapsed card hides it, and brought into view.</summary>
    private void OnFindCurrentChanged(ConversationItem item)
    {
        if (Find.IsHidden(item))
        {
            switch (item)
            {
                case ThinkingItem thinking:
                    thinking.IsExpanded = true;
                    break;
                case ToolUseItem tool:
                    tool.IsExpanded = true;
                    break;
                case HookRunItem hook:
                    hook.IsExpanded = true;
                    break;
            }
        }
        ScrollTo(item);
    }
}
