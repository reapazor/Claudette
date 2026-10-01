using Claudette.Core.Sessions;

namespace Claudette.App.Conversation;

/// <summary>
/// The prompts of a conversation that Claude Code can still withdraw: permission prompts, clarifying questions and plans
/// to approve (DESIGN.md §7), and MCP servers' requests for input (DESIGN.md §7, "MCP servers asking for input"), by
/// request id. Each closes when Claude Code withdraws it, and every one still open when the session ends.
/// </summary>
internal sealed class PendingPrompts
{
    private readonly Dictionary<string, PromptItem> _permissions = [];
    private readonly Dictionary<string, McpInputItem> _mcpInputs = [];

    /// <summary>What a prompt Claude Code withdraws says. Null, or no function, keeps <see cref="PromptItem.WithdrawnOutcome"/>.</summary>
    public Func<string?>? WithdrawnOutcome { get; set; }

    /// <summary>The card for a permission request: a question or a plan for those tools, else a permission prompt.</summary>
    public PromptItem Add(PermissionRequest request)
    {
        PromptItem item = request.ToolName switch
        {
            "AskUserQuestion" => new QuestionItem(request),
            "ExitPlanMode" => new PlanItem(request),
            _ => new PermissionItem(request),
        };
        _permissions[request.RequestId] = item;
        return item;
    }

    /// <summary>The card for an MCP server's request for input.</summary>
    /// <param name="openUrl">Opens the server's link in the browser, for a request to finish something there.</param>
    public McpInputItem Add(ElicitationRequest request, Func<string, Task>? openUrl)
    {
        var input = new McpInputItem(request, openUrl);
        _mcpInputs[request.RequestId] = input;
        return input;
    }

    /// <summary>Claude Code withdrew a permission request, for example after an interrupt.</summary>
    public void CancelPermission(string requestId)
    {
        if (_permissions.TryGetValue(requestId, out var item))
        {
            item.Cancel(WithdrawnOutcome?.Invoke());
        }
    }

    /// <summary>Claude Code withdrew an MCP server's request before it was answered.</summary>
    public void WithdrawMcpInput(string requestId)
    {
        if (_mcpInputs.Remove(requestId, out var input))
        {
            input.Withdraw(WithdrawnOutcome?.Invoke());
        }
    }

    /// <summary>A URL request the server says is done: the user finished in the browser.</summary>
    public void CompleteMcpInput(string elicitationId)
    {
        if (_mcpInputs.Values.FirstOrDefault(i => i.IsUrl && i.Request.ElicitationId == elicitationId) is { } done)
        {
            done.Complete();
        }
    }

    /// <summary>The session ended: nothing still open can be answered.</summary>
    public void OnSessionExited()
    {
        foreach (var pending in _permissions.Values)
        {
            pending.Cancel();
        }
        foreach (var pending in _mcpInputs.Values)
        {
            pending.Withdraw();
        }
        _mcpInputs.Clear();
    }

    /// <summary>
    /// The conversation was cleared: forgets its permission prompts. MCP servers' requests stay until they're withdrawn
    /// or the session ends.
    /// </summary>
    public void OnConversationCleared() => _permissions.Clear();
}
