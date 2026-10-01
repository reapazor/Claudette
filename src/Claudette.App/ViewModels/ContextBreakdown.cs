using System.Globalization;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// What fills a tab's context window, for the flyout its context ring and the composer's indicator open (DESIGN.md §6,
/// "Per-tab context"): a bar of the window, a row for each part of it, and what's inside the larger parts.
/// </summary>
public sealed class ContextBreakdown
{
    /// <summary>
    /// The window's parts in the order the bar and the list show them, each with its color. Checked with the
    /// categorical palette's validator for every pair of parts that can end up side by side (a tab without MCP tools
    /// puts System tools beside Messages, say), so the ones every session has (System prompt, System tools, Messages)
    /// sit between those that come and go. Claude Code's own order would put nearly every pair side by side.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Brush)> Parts =
    [
        ("Memory files", "ContextMemoryBrush"),
        ("Skills", "ContextSkillsBrush"),
        ("System prompt", "ContextPromptBrush"),
        ("Custom agents", "ContextAgentsBrush"),
        ("System tools", "ContextToolsBrush"),
        ("MCP tools", "ContextMcpBrush"),
        ("Messages", "ContextMessagesBrush"),
    ];

    /// <summary>A part this version doesn't know by name.</summary>
    public const string OtherBrush = "ContextOtherBrush";

    public const string FreeBrush = "MeterTrackBrush";

    public const string BufferBrush = "ContextBufferBrush";

    /// <summary>The estimate's single part: it knows how much is in the window, not what.</summary>
    public const string EstimateBrush = "MeterBrush";

    public const string AutoCompactBuffer = "Auto-compact buffer";

    private ContextBreakdown(IReadOnlyList<ContextSegment> segments, IReadOnlyList<ContextRow> rows, IReadOnlyList<ContextSection> sections, string? note)
    {
        Segments = segments;
        Rows = rows;
        Sections = sections;
        Note = note;
    }

    /// <summary>The bar, left to right: what's in the window, then the free space and the auto-compact buffer.</summary>
    public IReadOnlyList<ContextSegment> Segments { get; }

    /// <summary>The list under the bar, in its order, then tools held out of the window.</summary>
    public IReadOnlyList<ContextRow> Rows { get; }

    /// <summary>What's inside the larger parts, each to expand.</summary>
    public IReadOnlyList<ContextSection> Sections { get; }

    public bool HasSections => Sections.Count > 0;

    /// <summary>Why there's no more detail, when there isn't.</summary>
    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>The bar in words, for a screen reader.</summary>
    public string Summary => string.Join("; ", Segments.Select(s => s.Tip));

    public static ContextBreakdown From(ContextUsage usage)
    {
        var window = usage.MaxTokens;
        var used = usage.Categories.Where(c => c.Kind == ContextCategoryKind.Used && c.Tokens > 0).ToList();
        var parts = used
            .Select(c => (Category: c, Order: IndexOf(c.Name)))
            .OrderBy(p => p.Order)
            .Select(p => (p.Category, Brush: p.Order < Parts.Count ? Parts[p.Order].Brush : OtherBrush))
            .ToList();

        // Claude Code named the buffer as a row of its own in some versions and not in others: without one, the space
        // past the auto-compact threshold is the buffer, as /context shows it.
        var usedTokens = used.Sum(c => c.Tokens);
        var buffer = usage.Categories.FirstOrDefault(c => c.Kind == ContextCategoryKind.Buffer)?.Tokens
            ?? (usage is { AutoCompactEnabled: true, AutoCompactThreshold: { } threshold } && threshold < window ? window - threshold : 0);
        var free = usage.Categories.FirstOrDefault(c => c.Kind == ContextCategoryKind.Free)?.Tokens ?? window - usedTokens;
        if (!usage.Categories.Any(c => c.Kind == ContextCategoryKind.Buffer))
        {
            free -= buffer;
        }
        free = Math.Max(0, free);

        var segments = new List<ContextSegment>();
        var rows = new List<ContextRow>();
        foreach (var (category, brush) in parts)
        {
            segments.Add(Segment(category.Name, category.Tokens, window, brush));
            rows.Add(Row(category.Name, category.Tokens, window, brush));
        }
        segments.Add(Segment(ContextCategory.FreeSpace, free, window, FreeBrush));
        rows.Add(Row(ContextCategory.FreeSpace, free, window, FreeBrush));
        if (buffer > 0)
        {
            var name = usage.Categories.FirstOrDefault(c => c.Kind == ContextCategoryKind.Buffer)?.Name ?? AutoCompactBuffer;
            segments.Add(Segment(name, buffer, window, BufferBrush));
            rows.Add(Row(name, buffer, window, BufferBrush));
        }
        // Listed for what they'd take, but not in the window: no color, and no share of it.
        foreach (var held in usage.Categories.Where(c => c.Kind is ContextCategoryKind.Deferred or ContextCategoryKind.Unknown && c.Tokens > 0))
        {
            rows.Add(new ContextRow(held.Name, N(held.Tokens), "", null,
                held.Kind == ContextCategoryKind.Deferred ? "Held out of the context until Claude needs them" : null));
        }
        return new ContextBreakdown(segments.Where(s => s.Tokens > 0).ToList(), rows, SectionsOf(usage), null);
    }

    /// <summary>The breakdown from the last call's usage, when <c>get_context_usage</c> isn't available.</summary>
    public static ContextBreakdown Estimated(long tokens, long window)
    {
        var free = Math.Max(0, window - tokens);
        return new ContextBreakdown(
            [.. new[] { Segment("In the context", tokens, window, EstimateBrush), Segment(ContextCategory.FreeSpace, free, window, FreeBrush) }.Where(s => s.Tokens > 0)],
            [Row("In the context", tokens, window, EstimateBrush), Row(ContextCategory.FreeSpace, free, window, FreeBrush)],
            [],
            "This version of Claude Code doesn't say what's in the context, so this is estimated from the last call.");
    }

    /// <summary>Opens the sections that were open in <paramref name="previous"/>, so a refresh after a turn doesn't close them.</summary>
    public ContextBreakdown KeepingExpanded(ContextBreakdown? previous)
    {
        foreach (var section in Sections)
        {
            section.IsExpanded = previous?.Sections.Any(p => p.Key == section.Key && p.IsExpanded) == true;
        }
        return this;
    }

    private static int IndexOf(string name)
    {
        for (var i = 0; i < Parts.Count; i++)
        {
            if (string.Equals(Parts[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return Parts.Count;
    }

    private static IReadOnlyList<ContextSection> SectionsOf(ContextUsage usage)
    {
        var sections = new List<ContextSection>();
        if (usage.MemoryFiles.Count > 0)
        {
            sections.Add(new ContextSection("memory", $"Memory files ({usage.MemoryFiles.Count})",
                [.. usage.MemoryFiles.OrderByDescending(f => f.Tokens).Select(f => new ContextSectionRow(f.Path, f.Tokens, f.Type, f.Path))]));
        }
        var servers = usage.McpTools.GroupBy(t => t.Server).ToList();
        if (servers.Count > 0)
        {
            sections.Add(new ContextSection("mcp", servers.Count == 1 ? "MCP tools (1 server)" : $"MCP tools ({servers.Count} servers)",
                [.. servers
                    .Select(s => (Server: s.Key, Tools: s.ToList(), Tokens: s.Where(t => t.IsLoaded).Sum(t => t.Tokens)))
                    .OrderByDescending(s => s.Tokens)
                    .Select(s => new ContextSectionRow(s.Server.Length > 0 ? s.Server : "MCP", s.Tokens, ToolCount(s.Tools), null))]));
        }
        if (usage.Agents.Count > 0)
        {
            sections.Add(new ContextSection("agents", $"Custom agents ({usage.Agents.Count})",
                [.. usage.Agents.OrderByDescending(a => a.Tokens).Select(a => new ContextSectionRow(a.AgentType, a.Tokens, a.Source, null))]));
        }
        if (usage.Skills is { Listed.Count: > 0 } skills)
        {
            var header = skills.Included < skills.Total ? $"Skills ({skills.Included} of {skills.Total} listed)" : $"Skills ({skills.Listed.Count})";
            sections.Add(new ContextSection("skills", header,
                [.. skills.Listed.OrderByDescending(s => s.Tokens).Select(s => new ContextSectionRow(s.Name, s.Tokens, s.Source, null))]));
        }
        if (usage.Messages is { } messages)
        {
            List<ContextSectionRow> rows = [];
            void Add(string name, long tokens)
            {
                if (tokens > 0)
                {
                    rows.Add(new ContextSectionRow(name, tokens, null, null));
                }
            }
            Add("Tool results", messages.ToolResultTokens);
            Add("Tool calls", messages.ToolCallTokens);
            Add("Attachments", messages.AttachmentTokens);
            Add("Your messages", messages.UserTokens);
            Add("Claude's replies", messages.AssistantTokens);
            Add("Redirected context", messages.RedirectedTokens);
            Add("Not attributed", messages.UnattributedTokens);
            rows.Sort((a, b) => b.TokenCount.CompareTo(a.TokenCount));
            foreach (var tool in messages.ByTool.Where(t => t.Tokens > 0).OrderByDescending(t => t.Tokens))
            {
                rows.Add(new ContextSectionRow(tool.Name, tool.Tokens, $"calls {N(tool.CallTokens)} · results {N(tool.ResultTokens)}", null, IsDetail: true, Group: "By tool"));
            }
            foreach (var attachment in messages.Attachments.Where(a => a.Tokens > 0).OrderByDescending(a => a.Tokens))
            {
                rows.Add(new ContextSectionRow(attachment.Name, attachment.Tokens, null, null, IsDetail: true, Group: "Attachments"));
            }
            if (rows.Count > 0)
            {
                sections.Add(new ContextSection("messages", "Messages", MarkGroups(rows)));
            }
        }
        return sections;
    }

    /// <summary>The first row of each group carries its heading.</summary>
    private static List<ContextSectionRow> MarkGroups(List<ContextSectionRow> rows)
    {
        string? group = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Group is { } g && g != group)
            {
                rows[i] = rows[i] with { GroupHeading = g };
            }
            group = rows[i].Group;
        }
        return rows;
    }

    private static string ToolCount(IReadOnlyList<ContextMcpTool> tools)
    {
        var count = tools.Count == 1 ? "1 tool" : $"{tools.Count} tools";
        var held = tools.Count(t => !t.IsLoaded);
        return held == 0 ? count : held == tools.Count ? $"{count}, not loaded" : $"{count}, {held} not loaded";
    }

    private static ContextSegment Segment(string name, long tokens, long window, string brush) =>
        new(name, tokens, brush, $"{name}: {N(tokens)} tokens, {Percent(tokens, window)}");

    private static ContextRow Row(string name, long tokens, long window, string brush) =>
        new(name, N(tokens), Percent(tokens, window), brush, null);

    internal static string N(long tokens) => tokens.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>A share of the window, with a part too small to round to 1% still showing as there.</summary>
    internal static string Percent(long tokens, long window) =>
        window <= 0 ? "" : tokens > 0 && 100.0 * tokens / window < 0.5 ? "<1%" : $"{100.0 * tokens / window:0}%";
}

/// <summary>A part of the context bar, as wide as its share of the window.</summary>
/// <param name="Brush">The resource key of its color, so it follows the theme and the style.</param>
public sealed record ContextSegment(string Name, long Tokens, string Brush, string Tip);

/// <summary>A row of the list under the bar.</summary>
/// <param name="Brush">Its color's resource key; null for a row that isn't in the window.</param>
/// <param name="Tip">Why a row that isn't in the window is listed.</param>
public sealed record ContextRow(string Name, string Tokens, string Percent, string? Brush, string? Tip)
{
    public bool HasSwatch => Brush is not null;

    /// <summary>The row's color, drawn as a one-part bar.</summary>
    public IReadOnlyList<ContextSegment> Swatch { get; } = Brush is { } brush ? [new ContextSegment(Name, 1, brush, "")] : [];
}

/// <summary>What's inside a part of the window, such as each memory file. Collapsed until it's clicked.</summary>
/// <param name="key">Which part it is, so it stays open when the breakdown is refreshed after a turn.</param>
public sealed partial class ContextSection(string key, string header, IReadOnlyList<ContextSectionRow> rows) : ObservableObject
{
    public string Key { get; } = key;

    public string Header { get; } = header;

    public IReadOnlyList<ContextSectionRow> Rows { get; } = rows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCollapsed))]
    public partial bool IsExpanded { get; set; }

    public bool IsCollapsed => !IsExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <param name="Detail">A second line: a file's type, a skill's source, a tool's calls and results.</param>
/// <param name="Tip">The whole name, when it's cut to fit (a file's path).</param>
/// <param name="IsDetail">A row within a group, such as one tool under <em>By tool</em>.</param>
/// <param name="GroupHeading">The heading shown above the first row of its group.</param>
public sealed record ContextSectionRow(string Name, long TokenCount, string? Detail, string? Tip, bool IsDetail = false, string? Group = null, string? GroupHeading = null)
{
    public string Tokens => ContextBreakdown.N(TokenCount);

    public bool HasDetail => Detail is not null;

    public bool HasGroupHeading => GroupHeading is not null;
}
