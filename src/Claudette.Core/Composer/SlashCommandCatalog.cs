using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Composer;

/// <summary>
/// The slash commands a session offers, for the composer's <c>/</c> autocomplete (DESIGN.md §5, "Composer"): built-in,
/// user, project, plugin and MCP commands. Descriptions come from <c>initialize</c>, and from
/// <c>system/commands_changed</c> when the list changes mid-session; each <c>system/init</c> names the commands the
/// session accepts, so a name only it lists is still offered, without a description.
/// </summary>
/// <remarks>Used from the UI thread only.</remarks>
public sealed class SlashCommandCatalog
{
    private List<SlashCommandInfo> _described = [];
    private IReadOnlyList<string> _accepted = [];
    private HashSet<string> _terminalOnly = new(StringComparer.Ordinal);
    private IReadOnlyList<SlashCommandInfo>? _commands;

    /// <summary>What's offered, in Claude Code's order (its own list puts the user's and project's commands first).</summary>
    public IReadOnlyList<SlashCommandInfo> Commands => _commands ??= Build();

    /// <summary>The <c>commands</c> of the <c>initialize</c> reply, or of a <c>commands_changed</c> message: the full list.</summary>
    public void SetDescribed(IReadOnlyList<SlashCommandInfo> commands)
    {
        _described = commands.ToList();
        _commands = null;
    }

    /// <summary>A turn's <c>system/init</c>: which commands the session accepts.</summary>
    public void SetAccepted(SystemInitMessage init)
    {
        if (init.Raw["slash_commands"] is null)
        {
            return;
        }
        _accepted = init.SlashCommands;
        _terminalOnly = new HashSet<string>(init.TerminalSlashCommands, StringComparer.Ordinal);
        _commands = null;
    }

    /// <summary>
    /// The commands that match what's typed after the slash: the name or an alias starting with it first, then the
    /// name containing it, then (from three letters) the description mentioning it. Case doesn't matter.
    /// </summary>
    public IReadOnlyList<SlashCommandInfo> Filter(string query, int limit = 50)
    {
        var commands = Commands;
        if (query.Length == 0)
        {
            return commands.Take(limit).ToArray();
        }
        return commands
            .Select((command, index) => (command, index, rank: Rank(command, query)))
            .Where(c => c.rank >= 0)
            .OrderBy(c => c.rank)
            .ThenBy(c => c.rank == 0 ? c.command.Name.Length : 0)
            .ThenBy(c => c.index)
            .Take(limit)
            .Select(c => c.command)
            .ToArray();
    }

    /// <summary>0 for the exact name, 1 for a name that starts with the query, then aliases, contains, description.</summary>
    private static int Rank(SlashCommandInfo command, string query)
    {
        const StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;
        if (command.Name.StartsWith(query, ignoreCase))
        {
            return command.Name.Length == query.Length ? 0 : 1;
        }
        if (command.Aliases.Any(a => a.StartsWith(query, ignoreCase)))
        {
            return 2;
        }
        if (command.Name.Contains(query, ignoreCase))
        {
            return 3;
        }
        // Descriptions only for longer queries: one or two letters would match nearly everything.
        return query.Length >= 3 && command.Description?.Contains(query, ignoreCase) == true ? 4 : -1;
    }

    private List<SlashCommandInfo> Build()
    {
        var byName = new Dictionary<string, SlashCommandInfo>(StringComparer.Ordinal);
        var ordered = new List<SlashCommandInfo>();
        foreach (var command in _described)
        {
            if (byName.TryAdd(command.Name, command))
            {
                ordered.Add(command);
            }
        }
        foreach (var name in _accepted)
        {
            if (name.Length > 0 && byName.TryAdd(name, new SlashCommandInfo(name, null, null)))
            {
                ordered.Add(byName[name]);
            }
        }
        // Terminal-bound commands (such as doctor) and internal ones (a leading "__") aren't offered.
        return ordered
            .Where(c => !_terminalOnly.Contains(c.Name) && !c.Name.StartsWith("__", StringComparison.Ordinal))
            .ToList();
    }
}
