using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;

namespace Claudette.Core.Protocol;

/// <summary>
/// Takes sign-in secrets out of a protocol log line before it's written (DESIGN.md §13, "Logging"): the code pasted to
/// finish signing in and its state, tokens and keys by their usual names, and the <c>code</c> and <c>state</c> of a
/// sign-in address. The rest of the line is kept as it was: a log is for reading what happened.
/// </summary>
public static class ProtocolRedaction
{
    public const string Redacted = "[redacted]";

    /// <summary>Keys whose values are secret wherever they appear.</summary>
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorizationCode", "authorization_code", "code_verifier", "codeVerifier",
        "accessToken", "access_token", "refreshToken", "refresh_token", "idToken", "id_token",
        "apiKey", "api_key", "password", "secret", "client_secret",
    };

    /// <summary>Query parameters of an address that are secret: a sign-in's code and its state.</summary>
    private static readonly string[] SecretParameters = ["code", "state"];

    /// <summary>Quick checks, so a line without any of these isn't parsed at all.</summary>
    private static readonly string[] Hints =
    [
        .. SecretKeys.Select(key => $"\"{key}\""),
        "code=", "state=",
    ];

    private static readonly JsonSerializerOptions Writing = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary><paramref name="line"/> with its secrets replaced by <see cref="Redacted"/>; the same string when it has none.</summary>
    public static string Redact(string line)
    {
        if (!Hints.Any(hint => line.Contains(hint, StringComparison.OrdinalIgnoreCase)))
        {
            return line;
        }
        JsonNode? node;
        try
        {
            node = JsonTree.Parse(line);
        }
        catch (JsonException)
        {
            return line;
        }
        return node is not null && Redact(node) ? node.ToJsonString(Writing) : line;
    }

    /// <summary>Redacts in place; true if anything changed.</summary>
    private static bool Redact(JsonNode node)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                // The pasted code's other half: "state" alongside "authorizationCode" (claude_oauth_callback).
                var signInCallback = obj.ContainsKey("authorizationCode");
                foreach (var (key, value) in obj.ToList())
                {
                    if (value is null)
                    {
                        continue;
                    }
                    if (SecretKeys.Contains(key) || (signInCallback && key == "state"))
                    {
                        if (value.GetValueKind() is not JsonValueKind.String || value.GetValue<string>() != Redacted)
                        {
                            obj[key] = Redacted;
                            changed = true;
                        }
                    }
                    else if (TryRedactAddress(value, out var redacted))
                    {
                        obj[key] = redacted;
                        changed = true;
                    }
                    else
                    {
                        changed |= Redact(value);
                    }
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is not { } item)
                    {
                        continue;
                    }
                    if (TryRedactAddress(item, out var redacted))
                    {
                        array[i] = redacted;
                        changed = true;
                    }
                    else
                    {
                        changed |= Redact(item);
                    }
                }
                break;
        }
        return changed;
    }

    /// <summary>An http or https address with a <c>code</c> or <c>state</c> parameter, with their values redacted.</summary>
    private static bool TryRedactAddress(JsonNode value, out string redacted)
    {
        redacted = "";
        if (value.GetValueKind() is not JsonValueKind.String
            || value.GetValue<string>() is not { } text
            || !(text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        var query = text.IndexOf('?');
        if (query < 0)
        {
            return false;
        }
        var fragment = text.IndexOf('#', query);
        var end = fragment < 0 ? text.Length : fragment;
        var parameters = text[(query + 1)..end].Split('&');
        var changed = false;
        for (var i = 0; i < parameters.Length; i++)
        {
            var equals = parameters[i].IndexOf('=');
            var name = equals < 0 ? parameters[i] : parameters[i][..equals];
            if (equals >= 0 && SecretParameters.Contains(name, StringComparer.OrdinalIgnoreCase) && parameters[i][(equals + 1)..] != Redacted)
            {
                parameters[i] = $"{name}={Redacted}";
                changed = true;
            }
        }
        if (!changed)
        {
            return false;
        }
        redacted = $"{text[..(query + 1)]}{string.Join('&', parameters)}{text[end..]}";
        return true;
    }
}
