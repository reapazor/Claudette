namespace Claudette.Core.History;

/// <summary>One past session in History (DESIGN.md §9, "History"), read from its Claude Code transcript.</summary>
/// <param name="SessionId">The transcript's file name without <c>.jsonl</c>; what <c>--resume</c> takes.</param>
/// <param name="TranscriptPath">The full path of the transcript.</param>
/// <param name="Folder">The first working folder (<c>cwd</c>) recorded in the session.</param>
/// <param name="Title">The last custom title, else the last AI-generated title.</param>
/// <param name="FirstPrompt">The first thing the user typed, on one line and cut to about 200 characters.</param>
/// <param name="MessageCount">Prompts the user typed plus assistant messages with text.</param>
/// <param name="LastActivity">The latest entry timestamp, or the file's last-write time when there is none.</param>
/// <param name="GitBranch">The last git branch recorded.</param>
/// <param name="ClaudeCodeVersion">The last Claude Code version that wrote to the session.</param>
/// <param name="Prompts">
/// Every prompt the user typed, one per line and each cut short, for History's search (up to about
/// <see cref="HistoryIndex.SearchTextLength"/> characters in all).
/// </param>
public sealed record SessionSummary(
    string SessionId,
    string TranscriptPath,
    string? Folder,
    string? Title,
    string? FirstPrompt,
    int MessageCount,
    DateTimeOffset LastActivity,
    string? GitBranch,
    string? ClaudeCodeVersion,
    string? Prompts = null);
