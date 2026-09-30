using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>
/// How full the session's context window is, and what fills it, from <c>get_context_usage</c>: the data Claude Code's
/// <c>/context</c> shows (DESIGN.md §6, "Per-tab context").
/// </summary>
public sealed record ContextUsage(
    long TotalTokens,
    long MaxTokens,
    double Percentage,
    long? AutoCompactThreshold,
    bool AutoCompactEnabled,
    JsonObject Raw)
{
    /// <summary>The window's rows, in Claude Code's order: what's in it, the free space, the auto-compact buffer, and tools held out of it.</summary>
    public IReadOnlyList<ContextCategory> Categories { get; init; } = [];

    public IReadOnlyList<ContextMemoryFile> MemoryFiles { get; init; } = [];

    public IReadOnlyList<ContextMcpTool> McpTools { get; init; } = [];

    /// <summary>Custom agents, whose descriptions are in the prompt.</summary>
    public IReadOnlyList<ContextAgent> Agents { get; init; } = [];

    public ContextSkills? Skills { get; init; }

    /// <summary>What the conversation's messages are made of. Null when Claude Code doesn't say.</summary>
    public ContextMessages? Messages { get; init; }

    public static ContextUsage Parse(JsonObject response)
    {
        var total = (long)(response.GetDouble("totalTokens") ?? 0);
        var max = (long)(response.GetDouble("maxTokens") ?? 0);
        var percentage = response.GetDouble("percentage") ?? (max > 0 ? 100.0 * total / max : 0);
        var threshold = response.GetDouble("autoCompactThreshold") is { } t ? (long)t : (long?)null;
        return new ContextUsage(total, max, percentage, threshold, response.GetBool("isAutoCompactEnabled") ?? false, response)
        {
            Categories = Objects(response, "categories")
                .Where(c => c.GetString("name") is not null)
                .Select(c => new ContextCategory(c.GetString("name")!, Tokens(c), ContextCategory.KindOf(c)))
                .ToList(),
            MemoryFiles = Objects(response, "memoryFiles")
                .Where(f => f.GetString("path") is not null)
                .Select(f => new ContextMemoryFile(f.GetString("path")!, f.GetString("type"), Tokens(f)))
                .ToList(),
            McpTools = Objects(response, "mcpTools")
                .Where(m => m.GetString("name") is not null)
                .Select(m => new ContextMcpTool(m.GetString("name")!, m.GetString("serverName") ?? "", Tokens(m), m.GetBool("isLoaded") ?? true))
                .ToList(),
            Agents = Objects(response, "agents")
                .Where(a => a.GetString("agentType") is not null)
                .Select(a => new ContextAgent(a.GetString("agentType")!, a.GetString("source"), Tokens(a)))
                .ToList(),
            Skills = response.GetObject("skills") is { } skills
                ? new ContextSkills(
                    (int)(skills.GetDouble("totalSkills") ?? 0),
                    (int)(skills.GetDouble("includedSkills") ?? 0),
                    Tokens(skills),
                    Objects(skills, "skillFrontmatter")
                        .Where(s => s.GetString("name") is not null)
                        .Select(s => new ContextSkill(s.GetString("name")!, s.GetString("source"), Tokens(s)))
                        .ToList())
                : null,
            Messages = response.GetObject("messageBreakdown") is { } messages
                ? new ContextMessages(
                    Tokens(messages, "toolCallTokens"),
                    Tokens(messages, "toolResultTokens"),
                    Tokens(messages, "attachmentTokens"),
                    Tokens(messages, "assistantMessageTokens"),
                    Tokens(messages, "userMessageTokens"),
                    Tokens(messages, "redirectedContextTokens"),
                    Tokens(messages, "unattributedTokens"),
                    Objects(messages, "toolCallsByType")
                        .Where(u => u.GetString("name") is not null)
                        .Select(u => new ContextToolUse(u.GetString("name")!, Tokens(u, "callTokens"), Tokens(u, "resultTokens")))
                        .ToList(),
                    Objects(messages, "attachmentsByType")
                        .Where(a => a.GetString("name") is not null)
                        .Select(a => new ContextAttachment(a.GetString("name")!, Tokens(a)))
                        .ToList())
                : null,
        };
    }

    private static IEnumerable<JsonObject> Objects(JsonObject obj, string name) => obj.GetArray(name)?.OfType<JsonObject>() ?? [];

    private static long Tokens(JsonObject obj, string name = "tokens") => obj.GetDouble(name) is { } tokens and > 0 ? (long)tokens : 0;
}

/// <summary>What a row of <c>/context</c> counts.</summary>
public enum ContextCategoryKind
{
    /// <summary>A kind this version of Claudette doesn't know.</summary>
    Unknown,

    /// <summary>Something in the window: the system prompt, tools, memory files, messages.</summary>
    Used,

    /// <summary>The window left.</summary>
    Free,

    /// <summary>The reserve Claude Code keeps for compacting.</summary>
    Buffer,

    /// <summary>Tool schemas Claude Code holds out of the window until they're needed. Not counted in its usage.</summary>
    Deferred,
}

/// <summary>A row of <c>/context</c>, such as <em>Messages</em> or <em>Free space</em>.</summary>
public sealed record ContextCategory(string Name, long Tokens, ContextCategoryKind Kind)
{
    public const string FreeSpace = "Free space";

    /// <summary>
    /// The row's <c>kind</c>. Claude Code before it sent one marked deferred tools with <c>isDeferred</c>, and named the
    /// free space and the auto-compact buffer.
    /// </summary>
    internal static ContextCategoryKind KindOf(JsonObject category) => category.GetString("kind") switch
    {
        "used" => ContextCategoryKind.Used,
        "free" => ContextCategoryKind.Free,
        "buffer" => ContextCategoryKind.Buffer,
        "deferred" => ContextCategoryKind.Deferred,
        null => category.GetBool("isDeferred") == true ? ContextCategoryKind.Deferred
            : category.GetString("name") is { } name && name.Equals(FreeSpace, StringComparison.OrdinalIgnoreCase) ? ContextCategoryKind.Free
            : category.GetString("name") is { } buffer && buffer.Contains("buffer", StringComparison.OrdinalIgnoreCase) ? ContextCategoryKind.Buffer
            : ContextCategoryKind.Used,
        _ => ContextCategoryKind.Unknown,
    };
}

/// <summary>A memory file in the prompt, such as a <c>CLAUDE.md</c>.</summary>
/// <param name="Type">Where it's from, as Claude Code names it (<c>Project</c>, <c>User</c> and so on).</param>
public sealed record ContextMemoryFile(string Path, string? Type, long Tokens);

/// <param name="IsLoaded">False for a tool held out of the window until it's needed.</param>
public sealed record ContextMcpTool(string Name, string Server, long Tokens, bool IsLoaded);

public sealed record ContextAgent(string AgentType, string? Source, long Tokens);

public sealed record ContextSkill(string Name, string? Source, long Tokens);

/// <param name="Total">The skills Claude Code found.</param>
/// <param name="Included">How many of them made it into the listing.</param>
/// <param name="Listed">Each included skill's listing entry.</param>
public sealed record ContextSkills(int Total, int Included, long Tokens, IReadOnlyList<ContextSkill> Listed);

/// <summary>A tool's calls and their results in the conversation.</summary>
public sealed record ContextToolUse(string Name, long CallTokens, long ResultTokens)
{
    public long Tokens => CallTokens + ResultTokens;
}

/// <summary>A kind of attachment Claude Code adds to messages, such as file snapshots or reminders.</summary>
public sealed record ContextAttachment(string Name, long Tokens);

/// <summary>What the conversation's messages are made of.</summary>
public sealed record ContextMessages(
    long ToolCallTokens,
    long ToolResultTokens,
    long AttachmentTokens,
    long AssistantTokens,
    long UserTokens,
    long RedirectedTokens,
    long UnattributedTokens,
    IReadOnlyList<ContextToolUse> ByTool,
    IReadOnlyList<ContextAttachment> Attachments);
