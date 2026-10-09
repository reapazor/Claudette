using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Threads;

/// <summary>
/// A <c>SendMessage</c> tool call, as Claudette's PreToolUse hook sees it (DESIGN.md §18, "Threads"). Claude Code calls
/// the hook before it looks the recipient up (checked against 2.1.284), so a thread's sub-threads go by their tabs'
/// names rather than their sessions'.
/// </summary>
/// <param name="To">The recipient, without the <c> [ref]</c> a listing adds when two sessions share a name.</param>
/// <param name="Message">What to send; empty for a subscription on its own.</param>
/// <param name="NotifyWhenIdle">It asks for one notice when the recipient next goes idle.</param>
public sealed record SendMessageCall(string To, string Message, bool NotifyWhenIdle)
{
    public const string ToolName = "SendMessage";

    /// <summary>Reads the hook's input; null for another tool, or a call with no recipient.</summary>
    public static SendMessageCall? From(HookInput input)
    {
        if (input.ToolName != ToolName || input.ToolInput is not { } tool)
        {
            return null;
        }
        // Claude Code adds recipient and content, the same values under other names; either will do.
        var to = tool.GetString("to") ?? tool.GetString("recipient");
        if (string.IsNullOrWhiteSpace(to))
        {
            return null;
        }
        return new SendMessageCall(Recipient(to), tool.GetString("message") ?? tool.GetString("content") ?? "", tool.GetBool("notify_when_idle") == true);
    }

    /// <summary><c>Art page [3fa9c1]</c>, <c>"Art page"</c> and <c>@Art page</c> are all <c>Art page</c>.</summary>
    internal static string Recipient(string to)
    {
        var name = to.Trim();
        var reference = name.LastIndexOf(" [", StringComparison.Ordinal);
        if (name.EndsWith(']') && reference > 0)
        {
            name = name[..reference].TrimEnd();
        }
        if (name.StartsWith('@'))
        {
            name = name[1..];
        }
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
        {
            name = name[1..^1];
        }
        return name.Trim();
    }
}
