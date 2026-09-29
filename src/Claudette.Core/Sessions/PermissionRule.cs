using System.Text;
using System.Text.Json.Nodes;

namespace Claudette.Core.Sessions;

/// <summary>
/// A Claude Code permission rule, written <c>Tool</c> or <c>Tool(content)</c>, for example <c>Bash(dotnet test:*)</c>
/// (DESIGN.md §7). The user can edit it on the "Always allow" card before it's saved.
/// </summary>
public sealed record PermissionRule(string ToolName, string? RuleContent)
{
    public override string ToString() => RuleContent is null ? ToolName : $"{ToolName}({RuleContent})";

    /// <summary>Parses one rule. The tool name can't contain spaces or parentheses.</summary>
    public static bool TryParse(string? text, out PermissionRule rule)
    {
        rule = null!;
        var value = text?.Trim() ?? "";
        if (value.Length == 0)
        {
            return false;
        }
        var open = value.IndexOf('(');
        if (open < 0)
        {
            if (value.IndexOfAny([' ', ')']) >= 0)
            {
                return false;
            }
            rule = new PermissionRule(value, null);
            return true;
        }
        var tool = value[..open].Trim();
        if (tool.Length == 0 || tool.IndexOf(' ') >= 0 || !value.EndsWith(')'))
        {
            return false;
        }
        var content = value[(open + 1)..^1];
        rule = new PermissionRule(tool, content.Length == 0 ? null : content);
        return true;
    }

    /// <summary>Several rules separated by commas or new lines. Commas inside parentheses belong to the rule.</summary>
    public static bool TryParseList(string? text, out IReadOnlyList<PermissionRule> rules)
    {
        var parsed = new List<PermissionRule>();
        rules = parsed;
        var current = new StringBuilder();
        var depth = 0;
        foreach (var c in text ?? "")
        {
            switch (c)
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth = Math.Max(0, depth - 1);
                    break;
                case ',' or '\n' or '\r' when depth == 0:
                    if (!Flush())
                    {
                        return false;
                    }
                    continue;
            }
            current.Append(c);
        }
        return Flush() && parsed.Count > 0;

        bool Flush()
        {
            var part = current.ToString();
            current.Clear();
            if (part.Trim().Length == 0)
            {
                return true;
            }
            if (!TryParse(part, out var rule))
            {
                return false;
            }
            parsed.Add(rule);
            return true;
        }
    }

    public static string Format(IEnumerable<PermissionRule> rules) => string.Join(", ", rules);
}

/// <summary>Builds the <c>updatedPermissions</c> entries of a permission reply (Agent SDK <c>PermissionUpdate</c>).</summary>
public static class PermissionUpdates
{
    public static JsonObject AddRules(IEnumerable<PermissionRule> rules, string destination)
    {
        var list = new JsonArray();
        foreach (var rule in rules)
        {
            var value = new JsonObject { ["toolName"] = rule.ToolName };
            if (rule.RuleContent is not null)
            {
                value["ruleContent"] = rule.RuleContent;
            }
            list.Add(value);
        }
        return new JsonObject { ["type"] = "addRules", ["rules"] = list, ["behavior"] = "allow", ["destination"] = destination };
    }

    public static JsonObject SetMode(string mode, string destination) =>
        new() { ["type"] = "setMode", ["mode"] = mode, ["destination"] = destination };
}
