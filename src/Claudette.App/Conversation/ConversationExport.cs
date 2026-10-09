using System.Globalization;
using System.Net;
using System.Text;

namespace Claudette.App.Conversation;

/// <summary>
/// A tab's conversation as a document to save or share (DESIGN.md §5, "Export"): Markdown, or a single HTML file with
/// its style inline. Prompts and replies in full; tool calls as one line each with their result's summary, and a
/// Bash command with its output; thinking, subagents' detail and Claudette's own notes are left out unless asked for.
/// </summary>
public static class ConversationExport
{
    /// <summary>How much of a command's output an export keeps.</summary>
    public const int OutputLimit = 4_000;

    /// <param name="IncludeThinking">Claude's thinking, as quoted blocks.</param>
    /// <param name="IncludeSubagents">What each subagent did, under its call.</param>
    public sealed record Options(bool IncludeThinking = false, bool IncludeSubagents = false);

    public static string ToMarkdown(string title, IEnumerable<ConversationItem> items, string? folder = null, Options? options = null)
    {
        options ??= new Options();
        var md = new StringBuilder();
        md.Append("# ").AppendLine(OneLine(title)).AppendLine();
        if (folder is not null)
        {
            md.Append('`').Append(folder.Replace("`", "'", StringComparison.Ordinal)).AppendLine("`").AppendLine();
        }
        WriteMarkdown(md, items, options, depth: 0);
        // One kind of line ending on every OS: AppendLine writes \r\n on Windows, and a reply may have either.
        return md.ToString().TrimEnd().ReplaceLineEndings("\n") + "\n";
    }

    public static string ToHtml(string title, IEnumerable<ConversationItem> items, string? folder = null, Options? options = null)
    {
        options ??= new Options();
        var html = new StringBuilder();
        html.Append("""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="generator" content="Claudette">
            """).Append("<title>").Append(Encode(OneLine(title))).AppendLine("</title>").Append(Style).AppendLine("</head>").AppendLine("<body>");
        html.Append("<h1>").Append(Encode(OneLine(title))).AppendLine("</h1>");
        if (folder is not null)
        {
            html.Append("<p class=\"folder\"><code>").Append(Encode(folder)).AppendLine("</code></p>");
        }
        WriteHtml(html, items, options);
        html.AppendLine("</body>").AppendLine("</html>");
        return html.ToString().ReplaceLineEndings("\n");
    }

    private static void WriteMarkdown(StringBuilder md, IEnumerable<ConversationItem> items, Options options, int depth)
    {
        var quote = new string('>', depth) + (depth > 0 ? " " : "");
        void Block(string text)
        {
            foreach (var line in text.TrimEnd().Split('\n'))
            {
                md.Append(depth > 0 ? (line.Length == 0 ? quote.TrimEnd() : quote) : "").AppendLine(line.TrimEnd('\r'));
            }
            md.Append(depth > 0 ? quote.TrimEnd() : "").AppendLine();
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case UserMessageItem user:
                    Block($"**{(user.AutomaticLabel ?? "You")}**{Time(user)}");
                    Block(Fence(user.CopyText.Length > 0 ? user.CopyText : "(images)", "text"));
                    break;
                case AssistantTextItem reply when reply.Text.Trim().Length > 0:
                    Block($"**Claude**{Time(reply)}");
                    Block(reply.Text);
                    break;
                case ThinkingItem thinking when options.IncludeThinking && thinking.HasText:
                    Block(string.Join('\n', thinking.Text.TrimEnd().Split('\n').Select(l => "> " + l)));
                    break;
                case SubagentItem agent:
                    Block($"- **{agent.Name}** {OneLine(agent.Summary)}{Result(agent)}");
                    if (options.IncludeSubagents)
                    {
                        WriteMarkdown(md, agent.Items, options, depth + 1);
                    }
                    break;
                case ToolUseItem tool:
                    Block($"- **{tool.Name}** {Inline(tool.FullSummary)}{Result(tool)}");
                    if (tool.IsBash && tool.HasOutput)
                    {
                        Block(Fence(Cut(tool.Output!), "text"));
                    }
                    break;
                case PromptItem prompt when prompt.HasOutcome:
                    Block($"*{PromptTitle(prompt)}: {OneLine(prompt.Outcome)}*");
                    if (prompt is PlanItem { HasPlan: true } plan)
                    {
                        Block(plan.Plan.ToString());
                    }
                    break;
                case HookRunItem { IsFailed: true } hook:
                    Block($"*{hook.Title} {hook.StatusText}*");
                    break;
                case NoteItem { IsError: true } note:
                    Block($"*{OneLine(note.Text)}*");
                    break;
                case TaskStartItem start:
                    Block($"*{OneLine(StartedTask(start))}*");
                    break;
                case TurnSummaryItem summary:
                    Block($"<sub>{WebUtility.HtmlEncode(summary.Text)}</sub>");
                    break;
            }
        }
    }

    private static void WriteHtml(StringBuilder html, IEnumerable<ConversationItem> items, Options options)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case UserMessageItem user:
                    html.Append("<section class=\"user\"><h2>").Append(Encode(user.AutomaticLabel ?? "You")).Append(Encode(Time(user))).AppendLine("</h2>")
                        .Append("<pre>").Append(Encode(user.CopyText)).AppendLine("</pre></section>");
                    break;
                case AssistantTextItem reply when reply.Text.Trim().Length > 0:
                    // The reply's Markdown as written: readable as text, and nothing in it runs.
                    html.Append("<section class=\"claude\"><h2>Claude").Append(Encode(Time(reply))).AppendLine("</h2>")
                        .Append("<div class=\"md\">").Append(Encode(reply.Text.TrimEnd())).AppendLine("</div></section>");
                    break;
                case ThinkingItem thinking when options.IncludeThinking && thinking.HasText:
                    html.Append("<details class=\"thinking\"><summary>Thinking</summary><div class=\"md\">").Append(Encode(thinking.Text.TrimEnd())).AppendLine("</div></details>");
                    break;
                case SubagentItem agent:
                    html.Append("<div class=\"tool\"><b>").Append(Encode(agent.Name)).Append("</b> ").Append(Encode(OneLine(agent.Summary))).Append(Encode(Result(agent)));
                    if (options.IncludeSubagents && agent.Items.Count > 0)
                    {
                        html.AppendLine("<details class=\"agent\"><summary>What it did</summary>");
                        WriteHtml(html, agent.Items, options);
                        html.Append("</details>");
                    }
                    html.AppendLine("</div>");
                    break;
                case ToolUseItem tool:
                    html.Append("<div class=\"tool\"><b>").Append(Encode(tool.Name)).Append("</b> <code>").Append(Encode(OneLine(tool.FullSummary))).Append("</code>")
                        .Append(Encode(Result(tool)));
                    if (tool.IsBash && tool.HasOutput)
                    {
                        html.Append("<pre>").Append(Encode(Cut(tool.Output!))).Append("</pre>");
                    }
                    html.AppendLine("</div>");
                    break;
                case PromptItem prompt when prompt.HasOutcome:
                    html.Append("<p class=\"note\">").Append(Encode($"{PromptTitle(prompt)}: {OneLine(prompt.Outcome)}")).AppendLine("</p>");
                    if (prompt is PlanItem { HasPlan: true } plan)
                    {
                        html.Append("<div class=\"md plan\">").Append(Encode(plan.Plan.ToString().TrimEnd())).AppendLine("</div>");
                    }
                    break;
                case HookRunItem { IsFailed: true } hook:
                    html.Append("<p class=\"note error\">").Append(Encode($"{hook.Title} {hook.StatusText}")).AppendLine("</p>");
                    break;
                case NoteItem { IsError: true } note:
                    html.Append("<p class=\"note error\">").Append(Encode(note.Text)).AppendLine("</p>");
                    break;
                case TaskStartItem start:
                    html.Append("<p class=\"note\">").Append(Encode(StartedTask(start))).AppendLine("</p>");
                    break;
                case TurnSummaryItem summary:
                    html.Append("<p class=\"summary\">").Append(Encode(summary.Text)).AppendLine("</p>");
                    break;
            }
        }
    }

    /// <summary>"Started task #3: Run the migration" (DESIGN.md §5, "Export").</summary>
    private static string StartedTask(TaskStartItem start) =>
        start.Todo.NumberText.Length > 0 ? $"Started task {start.Todo.NumberText}: {start.Todo.Content}" : $"Started task: {start.Todo.Content}";

    private static string PromptTitle(PromptItem prompt) => prompt switch
    {
        PermissionItem permission => permission.Title,
        QuestionItem question => question.Title,
        PlanItem => "Plan",
        _ => "Prompt",
    };

    private static string Result(ToolUseItem tool) =>
        tool.IsError ? " (failed)" : tool.HasResultSummary ? $" → {OneLine(tool.ResultSummary!)}" : "";

    private static string Time(MessageItem message) =>
        message.SentAt is { } sent ? string.Create(CultureInfo.InvariantCulture, $" · {sent.ToLocalTime():yyyy-MM-dd HH:mm}") : "";

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string Inline(string text) => $"`{OneLine(text).Replace("`", "'", StringComparison.Ordinal)}`";

    private static string Cut(string output) => output.Length > OutputLimit ? output[..OutputLimit].TrimEnd() + "\n…" : output.TrimEnd();

    /// <summary>A fenced block that the text can't close early: a longer fence than any run of backticks in it.</summary>
    private static string Fence(string text, string language)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{text.TrimEnd()}\n{fence}";
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private const string Style = """
        <style>
        :root { --text: #1f1f1f; --muted: #6b6b6b; --line: #e3e3e3; --user: #f4f4f4; --bg: #ffffff; --error: #b3261e; }
        @media (prefers-color-scheme: dark) { :root { --text: #e8e8e8; --muted: #a0a0a0; --line: #333; --user: #232323; --bg: #161616; --error: #f2b8b5; } }
        body { margin: 0 auto; max-width: 52rem; padding: 2rem 1rem; background: var(--bg); color: var(--text);
               font: 15px/1.55 system-ui, -apple-system, "Segoe UI", sans-serif; }
        h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
        h2 { font-size: .8rem; color: var(--muted); font-weight: 600; margin: 0 0 .35rem; }
        section { margin: 1.25rem 0; }
        section.user { background: var(--user); border-radius: 10px; padding: .75rem 1rem; }
        pre, code { font: 13px/1.45 ui-monospace, "Cascadia Mono", Menlo, Consolas, monospace; }
        pre { white-space: pre-wrap; overflow-wrap: anywhere; margin: .35rem 0 0; }
        .md { white-space: pre-wrap; overflow-wrap: anywhere; }
        .tool { color: var(--muted); font-size: .9rem; margin: .3rem 0; overflow-wrap: anywhere; }
        .tool pre { border-left: 2px solid var(--line); padding-left: .75rem; color: var(--text); }
        .note, .summary, .folder { color: var(--muted); font-size: .85rem; }
        .error { color: var(--error); }
        details { margin: .4rem 0; } details.agent { border-left: 2px solid var(--line); padding-left: .75rem; }
        </style>
        """;
}
