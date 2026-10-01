using Claudette.Core.Claude;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>One line of what a folder's configuration runs, on the trust card.</summary>
public sealed record TrustRow(string Kind, string Text, string File);

public partial class TabViewModel
{
    // ---- Folder trust (DESIGN.md §7, "Folder trust") ----------------------------------------------------------

    /// <summary>Messages sent while the tab asks about the folder: they go once the user has answered.</summary>
    private readonly List<PendingMessage> _heldForTrust = [];

    /// <summary>
    /// What the folder's own configuration would run, while the tab asks whether to trust it before Claude Code starts.
    /// Null when it isn't asking.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAskingTrust))]
    public partial IReadOnlyList<TrustRow>? TrustRows { get; private set; }

    public bool IsAskingTrust => TrustRows is not null;

    public string TrustText =>
        $"{FolderName} has its own Claude Code configuration. Started by Claudette, Claude Code doesn't ask about a folder it hasn't been told to trust, so it would run these as it starts and works here:";

    /// <summary>The tab runs without the folder's own settings: the tab's menu offers to trust it after all.</summary>
    public bool IsWithoutFolderSettings => State.WithoutProjectSettings;

    /// <summary>
    /// Before Claude Code first starts in a folder that isn't trusted, reads what the folder's configuration would run.
    /// True when there's something, and the tab now asks instead of starting.
    /// </summary>
    private async Task<bool> AskTrustAsync()
    {
        if (State.WithoutProjectSettings || _services.IsFolderTrusted(Folder))
        {
            return false;
        }
        var folder = Folder;
        var found = await Task.Run(() => FolderTrust.WhatRuns(folder));
        if (found.Count == 0)
        {
            return false;
        }
        TrustRows = [.. found.Select(s => new TrustRow(KindText(s.Kind), s.Text, s.File))];
        Status = TabStatus.NeedsInput;
        return true;
    }

    private static string KindText(ProjectSettingKind kind) => kind switch
    {
        ProjectSettingKind.Hook => "Hook",
        ProjectSettingKind.Helper => "Command",
        ProjectSettingKind.Environment => "Environment variables",
        ProjectSettingKind.McpServer => "MCP server",
        ProjectSettingKind.Plugin => "Plugin",
        ProjectSettingKind.SkillHook => "Skill with hooks",
        ProjectSettingKind.AllowRule => "Allowed without asking",
        _ => kind.ToString(),
    };

    /// <summary>
    /// <b>Trust this folder</b>: on the card, Claude Code starts with the folder's configuration; from the tab's menu,
    /// in a tab that started without it, the session starts again with it.
    /// </summary>
    [RelayCommand]
    private async Task TrustFolderAsync()
    {
        _services.TrustFolder(Folder);
        var restart = State.WithoutProjectSettings && _session is not null;
        State.WithoutProjectSettings = false;
        _services.SaveState();
        OnPropertyChanged(nameof(IsWithoutFolderSettings));
        if (restart)
        {
            await StopSessionAsync();
            _conversation.AddNote($"Trusted {FolderName}. Started again with its own configuration.");
        }
        TrustRows = null;
        if (_session is null)
        {
            Status = TabStatus.NotStarted;
        }
        await StartAndSendHeldAsync();
    }

    /// <summary><b>Start without it</b>: Claude Code starts without the folder's settings, MCP servers and CLAUDE.md.</summary>
    [RelayCommand]
    private async Task StartWithoutFolderSettingsAsync()
    {
        State.WithoutProjectSettings = true;
        _services.SaveState();
        OnPropertyChanged(nameof(IsWithoutFolderSettings));
        TrustRows = null;
        Status = TabStatus.NotStarted;
        _conversation.AddNote($"Started without {FolderName}'s own settings, MCP servers and CLAUDE.md. To use them, choose Trust this folder in the tab's menu.");
        await StartAndSendHeldAsync();
    }

    private async Task StartAndSendHeldAsync()
    {
        await EnsureStartedAsync();
        var held = _heldForTrust.ToArray();
        _heldForTrust.Clear();
        foreach (var message in held)
        {
            await SendRawAsync(message.Text, message.Images, message.Suffix, message.Stamp);
        }
    }
}
