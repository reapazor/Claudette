using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>Tokens and estimated cost for one model.</summary>
public sealed class ModelTokenTotals
{
    public long Input { get; set; }

    public long Output { get; set; }

    public long CacheWrite { get; set; }

    public long CacheRead { get; set; }

    /// <summary>Claude Code's client-side estimate at list price; not a bill.</summary>
    public double EstimatedCostUsd { get; set; }

    public long Total => Input + Output + CacheWrite + CacheRead;
}

/// <summary>
/// A tab's running token counts, split by model (DESIGN.md §4, "Token stats per tab"). Saved with the tab.
/// </summary>
public sealed class TokenTotals
{
    public Dictionary<string, ModelTokenTotals> Models { get; set; } = [];

    public int Turns { get; set; }

    public long Total => Models.Values.Sum(m => m.Total);

    public double EstimatedCostUsd => Models.Values.Sum(m => m.EstimatedCostUsd);

    /// <summary>Adds one turn's usage, from the result's per-model <c>modelUsage</c>.</summary>
    public void Add(ResultMessage result)
    {
        if (result.ModelUsage is not { Count: > 0 } byModel)
        {
            return;
        }
        Turns++;
        foreach (var (model, node) in byModel)
        {
            if (node is not JsonObject usage)
            {
                continue;
            }
            if (!Models.TryGetValue(model, out var totals))
            {
                totals = new ModelTokenTotals();
                Models[model] = totals;
            }
            totals.Input += Number(usage["inputTokens"]);
            totals.Output += Number(usage["outputTokens"]);
            totals.CacheWrite += Number(usage["cacheCreationInputTokens"]);
            totals.CacheRead += Number(usage["cacheReadInputTokens"]);
            totals.EstimatedCostUsd += usage["costUSD"] is JsonValue cost && cost.GetValueKind() == JsonValueKind.Number ? cost.GetValue<double>() : 0;
        }
    }

    /// <summary>Short form for the composer bar, for example <c>1.2M tok</c>.</summary>
    public static string Short(long tokens) => tokens switch
    {
        >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M tok",
        >= 1_000 => $"{tokens / 1_000.0:0.#}k tok",
        _ => $"{tokens} tok",
    };

    private static long Number(JsonNode? node) => node.AsWholeNumber() ?? 0;
}
