using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Perforce;

/// <summary>A finished Bash tool call: its command, and everything it printed.</summary>
public sealed record BashResult(string ToolUseId, string Command, string Output, bool IsError);

/// <summary>
/// Pairs a session's Bash tool calls with their results, so Perforce handling can read what each <c>p4</c> command
/// printed (DESIGN.md §18): the changelist it used, or an expired session. Subagents' calls count too.
/// </summary>
public sealed class BashCommandLog
{
    private readonly Dictionary<string, string> _commands = new(StringComparer.Ordinal);

    /// <summary>Notes the Bash calls in an assistant message.</summary>
    public void OnAssistantMessage(AssistantMessage message)
    {
        foreach (var block in message.Content.OfType<ToolUseBlock>())
        {
            if (block.Name == "Bash" && block.Input.GetString("command") is { } command)
            {
                _commands[block.Id] = command;
            }
        }
    }

    /// <summary>The Bash calls this message finishes.</summary>
    public IReadOnlyList<BashResult> OnToolResults(UserMessage message)
    {
        List<BashResult>? results = null;
        foreach (var block in message.Content.OfType<ToolResultBlock>())
        {
            if (!_commands.Remove(block.ToolUseId, out var command))
            {
                continue;
            }
            var output = block.Text;
            // The structured result keeps standard output and error apart; the text may be shortened.
            if (message.ToolUseResult is JsonObject details)
            {
                output = string.Join("\n", new[] { output, details.GetString("stdout"), details.GetString("stderr") }.Where(s => !string.IsNullOrEmpty(s)));
            }
            (results ??= []).Add(new BashResult(block.ToolUseId, command, output, block.IsError));
        }
        return results ?? (IReadOnlyList<BashResult>)[];
    }

    /// <summary>A turn ended: calls without a result won't get one.</summary>
    public void Clear() => _commands.Clear();
}
