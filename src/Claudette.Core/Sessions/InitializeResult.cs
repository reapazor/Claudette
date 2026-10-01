using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>A model Claude Code offers, with the effort levels it supports (DESIGN.md §5, "Model &amp; effort").</summary>
public sealed record ModelInfo(
    string Value,
    string? ResolvedModel,
    string DisplayName,
    string? Description,
    bool SupportsEffort,
    IReadOnlyList<string> SupportedEffortLevels)
{
    /// <summary>Auto mode works with this model (DESIGN.md §7). Claude Code leaves the field out for one that it doesn't.</summary>
    public bool SupportsAutoMode { get; init; }
}

/// <summary>A slash command, as <c>initialize</c> and <c>system/commands_changed</c> list them (DESIGN.md §5, "Composer").</summary>
/// <param name="Name">Without the leading slash.</param>
public sealed record SlashCommandInfo(string Name, string? Description, string? ArgumentHint)
{
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>One of Claude Code's own commands, rather than a user, project, plugin or MCP one.</summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>Reads a <c>commands</c> array; entries without a name are skipped.</summary>
    public static IReadOnlyList<SlashCommandInfo> ParseList(JsonArray? commands) =>
        commands?.OfType<JsonObject>()
            .Select(c => new SlashCommandInfo(c.GetString("name") ?? "", c.GetString("description"), c.GetString("argumentHint"))
            {
                Aliases = c.GetStringList("aliases"),
                IsBuiltIn = c.GetBool("builtin") ?? false,
            })
            .Where(c => c.Name.Length > 0)
            .ToArray() ?? [];
}

public sealed record AccountInfo(string? Email, string? Organization, string? SubscriptionType, string? TokenSource, string? ApiProvider);

/// <summary>Claude Code's reply to the <c>initialize</c> control request.</summary>
public sealed record InitializeResult(
    IReadOnlyList<ModelInfo> Models,
    IReadOnlyList<SlashCommandInfo> Commands,
    AccountInfo Account,
    string? CurrentPermissionMode,
    JsonObject Raw)
{
    /// <summary>The output style the session uses (<c>output_style</c>), such as <c>default</c> or <c>Explanatory</c>.</summary>
    public string? OutputStyle => Raw.GetString("output_style");

    /// <summary>The output styles the session can use (<c>available_output_styles</c>), built-in and the user's own.</summary>
    public IReadOnlyList<string> AvailableOutputStyles => Raw.GetStringList("available_output_styles");

    public static InitializeResult Parse(JsonObject response)
    {
        var models = response.GetArray("models")?.OfType<JsonObject>()
            .Select(m => new ModelInfo(
                m.GetString("value") ?? "",
                m.GetString("resolvedModel"),
                m.GetString("displayName") ?? m.GetString("value") ?? "",
                m.GetString("description"),
                m.GetBool("supportsEffort") ?? false,
                m.GetStringList("supportedEffortLevels"))
            {
                SupportsAutoMode = m.GetBool("supportsAutoMode") ?? false,
            })
            .Where(m => m.Value.Length > 0)
            .ToArray() ?? [];

        var commands = SlashCommandInfo.ParseList(response.GetArray("commands"));

        var account = response.GetObject("account") ?? [];
        return new InitializeResult(
            models,
            commands,
            new AccountInfo(
                account.GetString("email"),
                account.GetString("organization"),
                account.GetString("subscriptionType"),
                account.GetString("tokenSource"),
                account.GetString("apiProvider")),
            response.GetString("current_permission_mode"),
            response);
    }
}
