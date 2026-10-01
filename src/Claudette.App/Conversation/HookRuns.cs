using Claudette.Core.Protocol;

namespace Claudette.App.Conversation;

/// <summary>
/// The rows of a conversation's hook runs (DESIGN.md §5, "Hook runs"): a row from a run's start when every run shows,
/// else from the moment it fails or prints something. Its output and how it ended fill in as they come.
/// </summary>
/// <param name="show">Adds a new row to the conversation.</param>
internal sealed class HookRuns(Action<HookRunItem> show)
{
    // The rows of runs that haven't ended, by hook id.
    private readonly Dictionary<string, HookRunItem> _running = [];

    /// <summary>Show a row for every run, not only the ones that fail or print something.</summary>
    public bool ShowAll { get; set; }

    public void Apply(HookRunNotice hook)
    {
        if (!_running.TryGetValue(hook.HookId, out var item))
        {
            var failed = hook.Stage == HookRunStage.Response && hook.Outcome == HookOutcome.Error;
            var printed = hook.Stage == HookRunStage.Progress && !string.IsNullOrEmpty(hook.Output ?? hook.Stdout);
            if (!ShowAll && !failed && !printed)
            {
                return;
            }
            item = new HookRunItem(hook.HookId, hook.HookName ?? "", hook.HookEvent ?? "Hook");
            _running[hook.HookId] = item;
            show(item);
        }
        switch (hook.Stage)
        {
            case HookRunStage.Progress:
                // Each progress message has the output so far.
                item.Output = "";
                item.Append(Output(hook));
                break;
            case HookRunStage.Response:
                item.Output = "";
                item.Append(Output(hook));
                item.ExitCode = hook.ExitCode;
                item.State = hook.Outcome switch
                {
                    HookOutcome.Error => HookRunState.Failed,
                    HookOutcome.Cancelled => HookRunState.Cancelled,
                    _ => HookRunState.Succeeded,
                };
                // A failure opens, so what went wrong is in view.
                item.IsExpanded = item.IsFailed && item.HasOutput;
                _running.Remove(hook.HookId);
                break;
        }
    }

    /// <summary>Forgets the rows of a cleared conversation.</summary>
    public void Clear() => _running.Clear();

    private static string Output(HookRunNotice hook)
    {
        if (!string.IsNullOrEmpty(hook.Output))
        {
            return hook.Output.TrimEnd();
        }
        var stdout = hook.Stdout ?? "";
        var stderr = hook.Stderr ?? "";
        return string.Join('\n', new[] { stdout.TrimEnd(), stderr.TrimEnd() }.Where(s => s.Length > 0));
    }
}
