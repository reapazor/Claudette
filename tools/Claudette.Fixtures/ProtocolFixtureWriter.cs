using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Claudette.Fixtures;

/// <summary>
/// Record mode for protocol fixtures (DESIGN.md §15, "Protocol fixtures"): turns a protocol log, from the Live suite, a
/// recording test or a developer session with protocol logging on, into a fixture file that's safe to check in.
/// <list type="bullet">
/// <item>Log lines are <c>&lt;time&gt; &lt; {json}</c> from Claude Code and <c>&lt;time&gt; &gt; {json}</c> to it; fixture
/// lines are <c>{"dir":"out","msg":…}</c> and <c>{"dir":"in","msg":…}</c>.</item>
/// <item>Paths under the given roots become <c>&lt;ROOT&gt;</c>, the home folder <c>&lt;HOME&gt;</c>, session ids
/// <c>session-1</c>, <c>session-2</c>…, emails <c>user@example.com</c>, and account and organization details
/// <c>&lt;redacted&gt;</c>.</item>
/// </list>
/// </summary>
public sealed partial class ProtocolFixtureWriter
{
    private static readonly HashSet<string> SessionIdKeys = new(StringComparer.Ordinal) { "session_id", "sessionId", "new_conversation_id" };

    private static readonly HashSet<string> AccountKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "email", "emailAddress", "organization", "organizationName", "organization_name", "orgName", "organizationUuid",
        "organization_uuid", "accountUuid", "account_uuid", "userID", "user_id", "subscriptionType", "subscription_type",
    };

    /// <summary>Readable like Claude Code's own output: no escaping of <c>&lt;ROOT&gt;</c> or non-ASCII text.</summary>
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly IReadOnlyList<(string From, string To)> _paths;
    private readonly Dictionary<string, string> _sessionIds = new(StringComparer.Ordinal);

    /// <param name="roots">Folders to hide, such as the test's temporary folder; longest first wins.</param>
    public ProtocolFixtureWriter(IEnumerable<string> roots)
    {
        var paths = new List<(string, string)>();
        foreach (var root in roots.Where(r => r.Length > 0).OrderByDescending(r => r.Length))
        {
            paths.Add((root.TrimEnd('/', '\\'), "<ROOT>"));
            // Claude Code names a project's folder after its path, with every other character a dash.
            paths.Add((NonAlphanumeric().Replace(root.TrimEnd('/', '\\'), "-"), "<ROOT>"));
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1)
        {
            paths.Add((home.TrimEnd('/', '\\'), "<HOME>"));
        }
        _paths = paths;
    }

    /// <summary>Converts a protocol log to fixture lines. Lines that aren't log entries are skipped.</summary>
    public IReadOnlyList<string> FromProtocolLog(IEnumerable<string> logLines)
    {
        var entries = new List<(string Direction, JsonObject Message)>();
        foreach (var line in logLines)
        {
            var match = LogLine().Match(line);
            if (!match.Success || JsonNode.Parse(match.Groups["json"].Value) is not JsonObject message)
            {
                continue;
            }
            entries.Add((match.Groups["dir"].Value == "<" ? "out" : "in", message));
        }
        // Every session id first, so an id inside a path or text is replaced too.
        foreach (var (_, message) in entries)
        {
            CollectSessionIds(message);
        }
        return entries.Select(e => new JsonObject { ["dir"] = e.Direction, ["msg"] = Scrub(e.Message) }.ToJsonString(Output)).ToArray();
    }

    private void CollectSessionIds(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (SessionIdKeys.Contains(key) && value is JsonValue v && v.TryGetValue<string>(out var id) && id.Length > 0 && !_sessionIds.ContainsKey(id))
                    {
                        _sessionIds[id] = $"session-{_sessionIds.Count + 1}";
                    }
                    CollectSessionIds(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectSessionIds(item);
                }
                break;
        }
    }

    private JsonNode? Scrub(JsonNode? node, string? key = null)
    {
        switch (node)
        {
            case JsonObject obj:
                var copy = new JsonObject();
                foreach (var (name, value) in obj)
                {
                    copy[name] = Scrub(value, name);
                }
                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(item => Scrub(item)).ToArray());
            case JsonValue value when value.TryGetValue<string>(out var text):
                return key is not null && AccountKeys.Contains(key) && text.Length > 0 ? JsonValue.Create("<redacted>") : JsonValue.Create(ScrubText(text));
            default:
                return node?.DeepClone();
        }
    }

    private string ScrubText(string text)
    {
        foreach (var (from, to) in _paths)
        {
            text = text.Replace(from, to, StringComparison.OrdinalIgnoreCase);
            // JSON inside strings (tool inputs, results) escapes backslashes once more.
            if (from.Contains('\\', StringComparison.Ordinal))
            {
                text = text.Replace(from.Replace("\\", "\\\\", StringComparison.Ordinal), to, StringComparison.OrdinalIgnoreCase);
            }
        }
        foreach (var (id, placeholder) in _sessionIds)
        {
            text = text.Replace(id, placeholder, StringComparison.Ordinal);
        }
        return Email().Replace(text, "user@example.com");
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"^\S+ (?<dir>[<>]) (?<json>\{.*\})\s*$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex Email();
}
