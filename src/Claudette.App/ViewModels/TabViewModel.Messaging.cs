using Claudette.Core.Composer;
using Claudette.Core.Sessions;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// The tab among the Claude Code sessions on this machine, which can message each other: its session named after the
/// tab in Claude Code (DESIGN.md §13, "Session naming"), the name other sessions reach it by, for the info card, and
/// the sessions and agents the composer's <c>@</c> can name (DESIGN.md §5, "Autocomplete").
/// </summary>
public partial class TabViewModel
{
    /// <summary>The name the running session was given, when it started or since; null for none yet.</summary>
    private string? _claudeName;

    /// <summary>The rename under way, which a rename in the tab waits for.</summary>
    private Task _claudeNameSync = Task.CompletedTask;

    /// <summary>
    /// The name the tab's session has in Claude Code: the tab's own, generated or the user's, while Settings → General
    /// says so. Null leaves the one Claude Code makes from the folder (<c>claudette-3f</c>).
    /// </summary>
    private string? NameForClaudeCode => _services.Settings.General.RenameInClaudeCode ? State.UserName ?? State.AutoName : null;

    /// <summary>The tab's name changed: the running session takes it too. Claude Code keeps it until it exits.</summary>
    private void SyncClaudeName()
    {
        if (_session is not { } session || NameForClaudeCode is not { } name || name == _claudeName)
        {
            return;
        }
        _claudeName = name;
        _claudeNameSync = RenameInClaudeCodeAsync(session, name);
    }

    private async Task RenameInClaudeCodeAsync(ClaudeSession session, string name)
    {
        try
        {
            await session.RenameSessionAsync(name);
        }
        catch (Exception)
        {
            // The tab keeps its name either way; the next change tries again.
            if (_claudeName == name)
            {
                _claudeName = null;
            }
            return;
        }
        // A name another session has already gets a variant of it (DESIGN.md §13): the info card says which.
        _services.LiveSessions.Refresh();
    }

    /// <summary>The running session's entry among the sessions on this machine, once Claude Code has written it.</summary>
    private LiveSession? OwnLiveSession => _session is { } session ? _services.LiveSessions.Find(State.SessionId, session.ProcessId) : null;

    /// <summary>What other sessions message this one by: the info card's <b>Reached as</b>.</summary>
    public string? ReachedAs => OwnLiveSession?.Name;

    private void OnLiveSessionsChanged()
    {
        OnPropertyChanged(nameof(InfoRows));
        _completions?.RefreshTargets();
    }

    /// <summary><b>Copy</b> on an info card row: the session's ID, or the name it's reached by.</summary>
    [RelayCommand]
    private Task CopyInfoRowAsync(InfoRow? row) => row is { CanCopy: true } ? _services.Platform.SetClipboardTextAsync(row.Value) : Task.CompletedTask;

    /// <summary>Whether the tab has a session yet: not before its first turn, nor after starting afresh until the next.</summary>
    public bool HasSessionId => State.SessionId is not null;

    /// <summary>The tab's menu's <b>Copy session ID</b>: what <c>claude --resume</c> takes.</summary>
    [RelayCommand]
    private Task CopySessionIdAsync() => State.SessionId is { } id ? _services.Platform.SetClipboardTextAsync(id) : Task.CompletedTask;

    /// <summary>
    /// Who an <c>@</c> in the composer can name: a thread's sub-threads, the sessions in the other tabs, the other
    /// sessions on this machine, then this tab's subagents, newest first. A tab's session goes by the tab's name in the
    /// list and its own when Claude messages it.
    /// </summary>
    internal IReadOnlyList<MentionTarget> MentionTargets()
    {
        var targets = new List<MentionTarget>();
        var claimed = new HashSet<LiveSession>();
        if (OwnLiveSession is { } own)
        {
            claimed.Add(own);
        }
        // Sub-threads go by the names the thread knows them by: Claudette delivers what's sent to those (DESIGN.md §18).
        var subThreads = IsThread ? SubThreads.ToArray() : [];
        if (subThreads.Length > 0)
        {
            var names = SubThreadNames;
            for (var i = 0; i < subThreads.Length; i++)
            {
                targets.Add(new MentionTarget(names[i], names[i], MentionKind.SubThread));
                if (subThreads[i].OwnLiveSession is { } entry)
                {
                    claimed.Add(entry);
                }
            }
        }
        foreach (var tab in _shell.AllTabs.Where(t => !ReferenceEquals(t, this) && !subThreads.Contains(t)))
        {
            if (tab.OwnLiveSession is { } entry)
            {
                claimed.Add(entry);
                targets.Add(new MentionTarget(tab.DisplayName, entry.Name, MentionKind.Tab));
            }
            else if (tab._session is not null && tab._claudeName is { } name)
            {
                // Running under the tab's name, and Claude Code's entry for it can't be read.
                targets.Add(new MentionTarget(tab.DisplayName, name, MentionKind.Tab));
            }
        }
        targets.AddRange(_services.LiveSessions.Current.Where(s => !claimed.Contains(s))
            .Select(s => new MentionTarget(s.Name, s.Name, MentionKind.Session, s.Cwd)));
        targets.AddRange(Agents.Subagents.Reverse().Where(a => a.AgentId is not null)
            .Select(a => new MentionTarget(a.Title, a.AgentId!, MentionKind.Subagent, a.AgentType)));
        return SessionMentions.Unique(targets);
    }

    /// <summary>A message's mentions of sessions and agents, resolved for Claude; null when it has none.</summary>
    private ResolvedMentions? ResolveMentions(string text) =>
        text.Contains('@') && SessionMentions.Resolve(text, MentionTargets()) is { Mentioned.Count: > 0 } resolved ? resolved : null;
}
