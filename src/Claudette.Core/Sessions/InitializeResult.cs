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
    IReadOnlyList<string> SupportedEffortLevels);

public sealed record SlashCommandInfo(string Name, string? Description, string? ArgumentHint);

public sealed record AccountInfo(string? Email, string? Organization, string? SubscriptionType, string? TokenSource, string? ApiProvider);

/// <summary>Claude Code's reply to the <c>initialize</c> control request.</summary>
public sealed record InitializeResult(
    IReadOnlyList<ModelInfo> Models,
    IReadOnlyList<SlashCommandInfo> Commands,
    AccountInfo Account,
    string? CurrentPermissionMode,
    JsonObject Raw)
{
    public static InitializeResult Parse(JsonObject response)
    {
        var models = response.GetArray("models")?.OfType<JsonObject>()
            .Select(m => new ModelInfo(
                m.GetString("value") ?? "",
                m.GetString("resolvedModel"),
                m.GetString("displayName") ?? m.GetString("value") ?? "",
                m.GetString("description"),
                m.GetBool("supportsEffort") ?? false,
                m.GetStringList("supportedEffortLevels")))
            .Where(m => m.Value.Length > 0)
            .ToArray() ?? [];

        var commands = response.GetArray("commands")?.OfType<JsonObject>()
            .Select(c => new SlashCommandInfo(c.GetString("name") ?? "", c.GetString("description"), c.GetString("argumentHint")))
            .Where(c => c.Name.Length > 0)
            .ToArray() ?? [];

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
