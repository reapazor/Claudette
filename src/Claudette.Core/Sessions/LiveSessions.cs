using System.Text.Json.Nodes;
using Claudette.Core.Protocol;

namespace Claudette.Core.Sessions;

/// <summary>
/// A Claude Code session running on this machine, as its entry in Claude Code's sessions folder describes it
/// (DESIGN.md §13, "Session naming").
/// </summary>
/// <param name="Name">What other sessions message it by: a name it was given, or one Claude Code made from its folder (<c>claudette-3f</c>).</param>
/// <param name="IsNamed">It was given its name (<c>--name</c>, <c>/rename</c>, <c>rename_session</c>), rather than Claude Code making one up.</param>
/// <param name="Status">Such as <c>idle</c> or <c>busy</c>; null when the entry doesn't say.</param>
public sealed record LiveSession(int Pid, string SessionId, string Name, string? Cwd, bool IsNamed, string? Status);

/// <summary>
/// The Claude Code sessions running on this machine under one config folder: Claude Code keeps an entry for each in
/// <c>sessions/&lt;pid&gt;.json</c>, which <c>/list-agents</c> and <c>ListAgents</c> read too. Undocumented (checked
/// against 2.1.284), so anything unexpected is skipped. The folder also has each session's messaging key, in
/// <c>.key</c> files, which are never read.
/// </summary>
public static class LiveSessions
{
    public static string Folder(string configDirectory) => Path.Combine(configDirectory, "sessions");

    /// <summary>
    /// The sessions in <paramref name="configDirectory"/>'s folder whose process still runs: a session that crashed
    /// leaves its entry behind. Empty when there's no folder.
    /// </summary>
    /// <param name="isRunning">Whether a process with this PID is running.</param>
    public static IReadOnlyList<LiveSession> Read(string? configDirectory, Func<int, bool> isRunning)
    {
        if (configDirectory is null)
        {
            return [];
        }
        string[] files;
        try
        {
            files = Directory.GetFiles(Folder(configDirectory), "*.json");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var sessions = new List<LiveSession>();
        foreach (var file in files)
        {
            // Only <pid>.json: the messaging keys are <pid>.<hash>.key, never read.
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out _))
            {
                continue;
            }
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // Removed as its session ended, or being written.
            }
            if (Parse(json) is { } session && isRunning(session.Pid))
            {
                sessions.Add(session);
            }
        }
        return [.. sessions.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>One entry; null when it isn't JSON or lacks the PID, session ID or name.</summary>
    public static LiveSession? Parse(string json)
    {
        JsonObject? entry;
        try
        {
            entry = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (entry is null || entry.GetLong("pid") is not { } pid || pid is <= 0 or > int.MaxValue
            || entry.GetString("sessionId") is not { Length: > 0 } sessionId || entry.GetString("name") is not { Length: > 0 } name)
        {
            return null;
        }
        return new LiveSession((int)pid, sessionId, name, entry.GetString("cwd"), entry.GetString("nameSource") is { } source && source != "derived",
            entry.GetString("status"));
    }
}
