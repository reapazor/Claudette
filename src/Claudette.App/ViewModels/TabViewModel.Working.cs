using System.Text.Json.Nodes;
using Claudette.App.Conversation;
using Claudette.Core.Claude;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.App.ViewModels;

/// <summary>The working line above the composer while a turn runs (DESIGN.md §5, "Working line").</summary>
public sealed partial class TabViewModel
{
    private IReadOnlyList<string> _spinnerVerbs = SpinnerVerbs.BuiltIn;

    /// <summary>The glyph, verb, time and tokens of the turn in progress.</summary>
    public WorkingLine Working => field ??= new WorkingLine(
        _services.Time,
        _services.Dispatcher,
        () => _spinnerVerbs,
        () => _services.Settings.Appearance.FunWorkingWords,
        () => _callUsage.TurnTokens,
        () => _services.Tips.Text(Core.Settings.KeyboardShortcuts.Stop),
        _services.Random,
        () => _services.Settings.Appearance.ShowToolInWorkingLine);

    /// <summary>The main agent's tool calls that haven't had their result yet, oldest first.</summary>
    private readonly List<(string Id, string Name, JsonObject Input)> _runningTools = [];

    /// <summary>
    /// Follows the main agent's tool calls for the working line: a call runs from its <c>tool_use</c> to its result.
    /// A subagent's own calls show on the agent map instead; its <c>Agent</c> call is what runs here.
    /// </summary>
    private void TrackToolsForWorkingLine(SessionEvent sessionEvent)
    {
        var changed = false;
        switch (sessionEvent)
        {
            case AssistantMessageReceived { Message: { ParentToolUseId: null } message }:
                foreach (var block in message.Content.OfType<ToolUseBlock>())
                {
                    if (_runningTools.All(t => t.Id != block.Id))
                    {
                        _runningTools.Add((block.Id, block.Name, block.Input));
                        changed = true;
                    }
                }
                break;
            case ToolResultsReceived { Message: { ParentToolUseId: null } results }:
                foreach (var result in results.Content.OfType<ToolResultBlock>())
                {
                    changed |= _runningTools.RemoveAll(t => t.Id == result.ToolUseId) > 0;
                }
                break;
            case TurnCompleted or SessionExited:
                changed = _runningTools.Count > 0;
                _runningTools.Clear();
                break;
        }
        if (changed)
        {
            (string Name, JsonObject Input)[] running = [.. _runningTools.Select(t => (t.Name, t.Input))];
            Working.SetActivity(ToolActivity.Describe(running), ToolActivity.Details(running));
        }
    }

    /// <summary>Shown while Claude works; hidden while a prompt waits on the user, though the turn's time runs on.</summary>
    public bool IsWorkingLineShown => Status == TabStatus.Working;

    partial void OnStatusChanged(TabStatus oldValue, TabStatus newValue)
    {
        switch (newValue)
        {
            case TabStatus.Working:
                Working.Start();
                break;
            case TabStatus.NeedsInput:
                break;
            default:
                Working.Stop();
                break;
        }
        OnPropertyChanged(nameof(IsWorkingLineShown));
    }

    /// <summary>
    /// Reads the <c>spinnerVerbs</c> of Claude Code's settings for this folder, in the background, as the session
    /// starts: a verb added for the terminal shows here from the next turn.
    /// </summary>
    internal Task LoadSpinnerVerbsAsync()
    {
        var files = SpinnerVerbs.SettingsFiles(_services.ClaudeConfigDirectory, Folder);
        return Task.Run(() => _spinnerVerbs = SpinnerVerbs.Resolve(files));
    }
}
