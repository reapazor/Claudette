using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.ProjectTools;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>What an entry of the project's menu is.</summary>
public enum ProjectMenuKind
{
    /// <summary>A label, such as "Editor configuration".</summary>
    Header,
    /// <summary>A project action, such as Launch editor.</summary>
    Action,
    /// <summary>One of a set of choices, such as DebugGame: a radio item.</summary>
    Option,
    Separator,
    /// <summary>Something about the menu itself: Show output…, Add an action…, Add a link…, Refresh.</summary>
    Command,
    /// <summary>A link from the folder's project files, opened in the browser.</summary>
    Link,
}

/// <summary>One entry of the menu of the project's row at the sidebar's foot (DESIGN.md §18, "Project tools").</summary>
public sealed class ProjectMenuEntry
{
    public required ProjectMenuKind Kind { get; init; }

    public string Label { get; init; } = "";

    public string? Tip { get; init; }

    public bool IsEnabled { get; init; } = true;

    public bool IsChecked { get; init; }

    public ICommand? Command { get; init; }

    public object? Parameter { get; init; }

    public bool IsSeparator => Kind == ProjectMenuKind.Separator;

    public bool IsHeader => Kind == ProjectMenuKind.Header;

    public bool IsOption => Kind == ProjectMenuKind.Option;

    /// <summary>A button in the project's menu: an action, a choice or a command.</summary>
    public bool IsButton => Kind is ProjectMenuKind.Action or ProjectMenuKind.Option or ProjectMenuKind.Command or ProjectMenuKind.Link;

    public bool IsLink => Kind == ProjectMenuKind.Link;

    /// <summary>A radio mark for choices in the project's menu, and an arrow for links.</summary>
    public string Mark => Kind switch
    {
        ProjectMenuKind.Option => IsChecked ? "●" : "○",
        ProjectMenuKind.Link => "↗",
        _ => "",
    };

    public bool HasMark => Kind is ProjectMenuKind.Option or ProjectMenuKind.Link;

    public override string ToString() => Label;
}

/// <summary>What project tools need from their tab.</summary>
internal interface IProjectToolsHost
{
    string Id { get; }

    string Folder { get; }

    string FolderName { get; }

    /// <summary>The tab's name, for the "project action finished" notification.</summary>
    string DisplayName { get; }

    /// <summary>The Project page of the side panel shows, on the selected tab.</summary>
    bool IsProjectPageShowing { get; }

    /// <summary>The tab's Perforce changelist, for <c>{changelist}</c> in links (DESIGN.md §18, "Links").</summary>
    long? CurrentChangelist { get; }

    void AddNote(string text, NoteKind kind = NoteKind.Info);

    void Confirm(string title, string message, string confirmText, Func<Task> onConfirm);

    /// <summary>The tab info card's Project row changed.</summary>
    void InfoRowsChanged();

    /// <summary>Opens the side panel on the Project page.</summary>
    void OpenProjectPage();

    /// <summary>Makes the tab the selected one.</summary>
    void SelectTab();

    /// <summary>Settings, on one of this tab's project pages (DESIGN.md §14).</summary>
    Task OpenProjectSettingsAsync(string page, bool startNew);
}

/// <summary>
/// Project tools (DESIGN.md §18): the project detected for the tab's folder, its actions and the folder's custom ones,
/// the project's row at the sidebar's foot, the Project page of the side panel, and the runs of its jobs, each with its log.
/// </summary>
public sealed partial class ProjectToolsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly IProjectToolsHost _host;
    private ProjectDetection _projectDetection = ProjectDetection.None;
    private ProjectFileContents _projectFile = ProjectFileContents.Empty;
    private IReadOnlyList<(CustomProjectAction Custom, ProjectAction Action)> _customActions = [];
    /// <summary>The running job's process tree, for the process monitor and Stop.</summary>
    private ProcessTree? _jobTree;
    private string? _noteSent;
    private string _settingsSeen = "";

    // Reads of the project files and detections, numbered as they start. One that finishes after a later one was shown
    // is dropped, so saves in quick succession (Settings' Links and Actions pages) never leave the tab showing an
    // older file, and a detection from before a choice never undoes it.
    private long _projectFileReads;
    private long _projectFileShown;
    private long _projectDetections;
    private long _projectDetectionShown;

    internal ProjectToolsViewModel(AppServices services, IProjectToolsHost host)
    {
        _services = services;
        _host = host;
    }

    /// <summary>The project detected for the tab's folder, or null.</summary>
    [ObservableProperty]
    public partial ProjectInfo? Project { get; private set; }

    partial void OnProjectChanged(ProjectInfo? value) => ToolsChanged();

    /// <summary>
    /// The side panel has a Project page, and the project's menu <b>Show output…</b>: a detected project, custom
    /// actions, or a claudette.json whose entries were skipped (the menu says why). The sidebar's project row shows
    /// either way.
    /// </summary>
    public bool HasTools => Project is not null || _customActions.Count > 0 || _projectFile.Problems.Count > 0;

    /// <summary>
    /// "NightOwl · UE 5.4", the folder's name when no project was recognized, or while a job runs, its name:
    /// "Build editor…".
    /// </summary>
    public string ButtonText =>
        RunningRun is { } running ? $"{running.Name}…"
        : Project is { } project ? project.ShortVersion is { } version ? $"{project.Name} · {version}" : project.Name
        : _host.FolderName;

    public string ButtonTip
    {
        get
        {
            var name = Project is { } project ? $"{project.Name} ({project.KindName})" : _host.Folder;
            var main = MainAction is { } action && _services.Tips.Text(KeyboardShortcuts.RunProjectAction) is { } shortcut
                ? $". {shortcut}: {action.Label}"
                : "";
            return name + main;
        }
    }

    /// <summary>The project menu's header: the project and its kind, or the folder's name.</summary>
    public string HeaderTitle => Project is { } project ? $"{project.Name} · {project.KindName}" : _host.FolderName;

    /// <summary>The engine's version, folder and kind, or what's wrong; without a project, the folder.</summary>
    public IReadOnlyList<string> HeaderLines => Project?.HeaderLines ?? [_host.Folder];

    public string? Problem => Project?.Problem;

    public bool HasProblem => Problem is not null;

    /// <summary>Entries of <c>claudette.json</c> or <c>claudette.local.json</c> that were skipped, and why.</summary>
    public IReadOnlyList<string> FileProblems => _projectFile.Problems;

    public bool HasFileProblems => _projectFile.Problems.Count > 0;

    /// <summary>The links of the folder's project files, filled in for this tab (DESIGN.md §18, "Links").</summary>
    public IReadOnlyList<ResolvedLink> Links => _projectFile.Links.Select(l => ProjectLinks.Resolve(l, LinkValues())).ToArray();

    public bool HasLinks => _projectFile.Links.Count > 0;

    /// <summary>What <c>{branch}</c>, <c>{changelist}</c> and <c>{folderName}</c> are, now.</summary>
    private LinkValues LinkValues() => new(
        Core.Git.GitInfo.TryGetBranch(_host.Folder),
        _host.CurrentChangelist?.ToString(CultureInfo.InvariantCulture),
        _host.FolderName);

    /// <summary>The tab's changelist changed, which a link can have in it (DESIGN.md §18, "Links").</summary>
    internal void OnLinkValuesChanged() => OnPropertyChanged(nameof(Links));

    /// <summary>A link from the project's menu: opened in the browser, when it's allowed to open.</summary>
    [RelayCommand]
    private Task OpenLinkAsync(ResolvedLink? link) =>
        link is { Url: { } url } ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    public IReadOnlyList<ProjectDetail> Details => Project?.Details ?? [];

    /// <summary>The project's actions and the folder's own, for the Project page's buttons.</summary>
    public IReadOnlyList<ProjectAction> Actions => [.. Project?.Actions.Select(ForJob) ?? [], .. _customActions.Select(c => ForJob(c.Action))];

    /// <summary>The project row's menu.</summary>
    public IReadOnlyList<ProjectMenuEntry> Menu => BuildMenu();

    public ProjectAction? MainAction => Project?.MainAction ?? _customActions.Select(c => c.Action).FirstOrDefault();

    // ---- Detection ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the tab folder's project again, and reads its <c>claudette.json</c> and <c>claudette.local.json</c>, off
    /// the UI thread.
    /// </summary>
    public async Task RefreshAsync()
    {
        _settingsSeen = SettingsKey();
        var detectionNumber = Interlocked.Increment(ref _projectDetections);
        var fileNumber = Interlocked.Increment(ref _projectFileReads);
        var tools = _services.ProjectTools;
        ProjectDetection detection;
        ProjectFileContents file;
        try
        {
            var detecting = tools.DetectAsync(_host.Folder);
            var reading = tools.ReadProjectFileAsync(_host.Folder);
            detection = await detecting.ConfigureAwait(false);
            file = await reading.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            detection = ProjectDetection.None;
            file = ProjectFileContents.Empty;
        }
        await OnUiThreadAsync(() =>
        {
            if (fileNumber > _projectFileShown)
            {
                _projectFileShown = fileNumber;
                UseProjectFile(file);
            }
            if (detectionNumber > _projectDetectionShown)
            {
                _projectDetectionShown = detectionNumber;
                _projectDetection = detection;
                Project = detection.Project;
            }
            ToolsChanged();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>
    /// Reads the folder's project files again: when the tab is selected, Claudette comes to the front, a turn ends
    /// (Claude may have edited them, or switched branches), and after the in-app editor saves.
    /// </summary>
    public async Task RefreshFileAsync()
    {
        var fileNumber = Interlocked.Increment(ref _projectFileReads);
        ProjectFileContents file;
        try
        {
            file = await _services.ProjectTools.ReadProjectFileAsync(_host.Folder).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            file = ProjectFileContents.Empty;
        }
        await OnUiThreadAsync(() =>
        {
            if (fileNumber > _projectFileShown)
            {
                _projectFileShown = fileNumber;
                UseProjectFile(file);
                ToolsChanged();
            }
        }).ConfigureAwait(false);
    }

    private void UseProjectFile(ProjectFileContents file)
    {
        _projectFile = file;
        _customActions = _services.ProjectTools.CustomActions(file, _host.Folder);
    }

    private Task OnUiThreadAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _services.Dispatcher.Post(() =>
        {
            try
            {
                action();
            }
            finally
            {
                done.SetResult();
            }
        });
        return done.Task;
    }

    /// <summary>
    /// Before the tab's <c>claude</c> starts: detects the project again, and adds its note to the system prompt when
    /// Settings → Project tools says so (DESIGN.md §18, "Telling Claude"). Perforce's note is added after it.
    /// </summary>
    internal async Task<ClaudeLaunchOptions> WithNoteAsync(ClaudeLaunchOptions options)
    {
        await RefreshAsync();
        _noteSent = Project?.SystemPromptNote;
        _host.InfoRowsChanged();
        return _noteSent is { Length: > 0 } note
            ? options with { AppendSystemPrompt = options.AppendSystemPrompt is { Length: > 0 } existing ? $"{existing}\n\n{note}" : note }
            : options;
    }

    /// <summary>The Project row of the tab info card (DESIGN.md §4).</summary>
    internal void AddInfoRows(List<InfoRow> rows)
    {
        if (Project is not { } project)
        {
            return;
        }
        var text = project.HeaderLines.Count > 0 ? $"{project.Name}: {project.HeaderLines[0]}" : $"{project.Name} ({project.KindName})";
        if (_noteSent is not null)
        {
            text += ". Claude was told how to build it.";
        }
        rows.Add(new InfoRow("Project", text));
    }

    /// <summary>Settings → Project tools changed: the actions (and the note, from the next session) may be different.</summary>
    internal void OnSettingsChanged()
    {
        OnPropertyChanged(nameof(ButtonTip));
        if (SettingsKey() != _settingsSeen)
        {
            _ = RefreshAsync();
        }
    }

    private string SettingsKey() => _services.ProjectToolsSettingsKey;

    private void ToolsChanged()
    {
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonTip));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderLines));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(HasProblem));
        OnPropertyChanged(nameof(FileProblems));
        OnPropertyChanged(nameof(HasFileProblems));
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Actions));
        OnPropertyChanged(nameof(Menu));
        OnPropertyChanged(nameof(MainAction));
        _host.InfoRowsChanged();
    }

    private IReadOnlyList<ProjectMenuEntry> BuildMenu()
    {
        var entries = new List<ProjectMenuEntry>();
        void Separator()
        {
            if (entries.Count > 0 && !entries[^1].IsSeparator)
            {
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Separator });
            }
        }
        ProjectMenuEntry ActionEntry(ProjectAction action) => new()
        {
            Kind = ProjectMenuKind.Action,
            Label = action.Label,
            Tip = action.Tip,
            IsEnabled = action.IsEnabled,
            Command = RunActionCommand,
            Parameter = action,
        };

        if (_projectDetection.Candidates.Count > 1)
        {
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = "Projects in this folder" });
            foreach (var candidate in _projectDetection.Candidates)
            {
                entries.Add(new ProjectMenuEntry
                {
                    Kind = ProjectMenuKind.Option,
                    Label = candidate.Name,
                    Tip = candidate.Path,
                    IsChecked = Project is { } current && current.ProjectPath == candidate.Path,
                    Command = ChooseProjectCommand,
                    Parameter = candidate,
                });
            }
            Separator();
        }
        if (Project is { } project)
        {
            entries.AddRange(project.Actions.Select(a => ActionEntry(ForJob(a))));
            if (project.Choice is { } choice)
            {
                Separator();
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = choice.Label });
                foreach (var option in choice.Options)
                {
                    entries.Add(new ProjectMenuEntry
                    {
                        Kind = ProjectMenuKind.Option,
                        Label = option.Label,
                        IsChecked = option.Value == choice.Selected,
                        Command = ChooseOptionCommand,
                        Parameter = option,
                    });
                }
            }
            if (project.Fix is { } fix)
            {
                Separator();
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = fix.Label, Tip = fix.Title, Command = FixCommand });
            }
        }
        if (_customActions.Count > 0)
        {
            Separator();
            entries.AddRange(_customActions.Select(c => ActionEntry(ForJob(c.Action))));
        }
        if (HasLinks)
        {
            Separator();
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Header, Label = "Links" });
            entries.AddRange(Links.Select(link => new ProjectMenuEntry
            {
                Kind = ProjectMenuKind.Link,
                Label = link.Name,
                Tip = link.Tip,
                IsEnabled = link.IsEnabled,
                Command = OpenLinkCommand,
                Parameter = link,
            }));
        }
        Separator();
        // Without project tools there's no Project page to show, and no job to have output.
        if (HasTools)
        {
            entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Show output…", Tip = "The Project page of the side panel", Command = ShowOutputCommand });
        }
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Add an action…", Tip = "A command of your own for this folder", Command = AddActionCommand });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Add a link…", Tip = "A web page of your own for this folder", Command = AddLinkCommand });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Refresh", Tip = "Look for the project and its files again", Command = RefreshCommand });
        return entries;
    }

    /// <summary>While a job runs, other jobs wait: one at a time per tab.</summary>
    private ProjectAction ForJob(ProjectAction action) =>
        RunningRun is { } running && action.IsEnabled && action.Kind is ProjectActionKind.Run or ProjectActionKind.Destructive
            ? action with { DisabledReason = $"{running.Name} is still running. Stop it first, in the sidebar or on the Project page." }
            : action;

    // ---- Choices -------------------------------------------------------------------------------------------------

    /// <summary>Picks which project a folder with several uses. Remembered for the folder on this machine.</summary>
    [RelayCommand]
    private Task ChooseProject(ProjectCandidate? candidate)
    {
        if (candidate is null)
        {
            return Task.CompletedTask;
        }
        _services.ProjectTools.State.ChooseProject(_host.Folder, candidate.Path);
        _services.SaveState();
        return RefreshAsync();
    }

    /// <summary>Picks a per-project choice, such as DebugGame. Remembered for the project on this machine.</summary>
    [RelayCommand]
    private Task ChooseOption(ProjectChoiceOption? option)
    {
        if (option is null || Project is not { Choice: { } choice } project)
        {
            return Task.CompletedTask;
        }
        _services.ProjectTools.Remember(project.ProjectPath, choice.Key, option.Value);
        return RefreshAsync();
    }

    /// <summary><b>Choose engine folder…</b> and the like: pick, check, remember for the project, look again.</summary>
    [RelayCommand]
    private async Task FixAsync()
    {
        if (Project is not { Fix: { } fix } project)
        {
            return;
        }
        var picked = fix.PickFolder ? await _services.Platform.PickFolderAsync(fix.Title) : await _services.Platform.PickFileAsync(fix.Title);
        if (picked is null)
        {
            return;
        }
        var (path, error) = fix.Validate(picked);
        if (path is null)
        {
            _host.AddNote(error ?? $"{picked} can't be used.", NoteKind.Warning);
            return;
        }
        _services.ProjectTools.Remember(project.ProjectPath, fix.Key, path);
        await RefreshAsync();
    }

    // ---- Custom actions (DESIGN.md §18, "Custom actions") --------------------------------------------------------

    /// <summary>
    /// <b>Add an action…</b>: Settings opens on this tab's Actions page (DESIGN.md §14) with a new action started. Its
    /// dialog asks which file the action goes in, then it joins the end of that file's <c>actions</c>.
    /// </summary>
    [RelayCommand]
    private Task AddAction() => _host.OpenProjectSettingsAsync(SettingsViewModel.ActionsPage, startNew: true);

    /// <summary>
    /// <b>Add a link…</b>: Settings opens on this tab's Links page (DESIGN.md §14) with a new link started. Its dialog
    /// asks which file the link goes in.
    /// </summary>
    [RelayCommand]
    private Task AddLink() => _host.OpenProjectSettingsAsync(SettingsViewModel.LinksPage, startNew: true);

    /// <summary>A folder's project files changed through the in-app editor, here or in another tab in the same folder.</summary>
    internal void ReloadCustomActions() => _ = RefreshFileAsync();

    /// <summary>Every project detection found for the folder, nearest first, for Settings' Tools page.</summary>
    internal IReadOnlyList<ProjectCandidate> Candidates => _projectDetection.Candidates;

    // ---- Running actions -------------------------------------------------------------------------------------------

    /// <summary>"Run the project's main action" (Ctrl/Cmd+Shift+E): Launch editor, for Unreal.</summary>
    [RelayCommand]
    private Task RunMainAction() => MainAction is { } action ? RunActionAsync(action) : Task.CompletedTask;

    [RelayCommand]
    private async Task RunActionAsync(ProjectAction? action)
    {
        if (action is null)
        {
            return;
        }
        action = ForJob(action);
        if (!action.IsEnabled)
        {
            _host.AddNote($"{action.Label}: {action.DisabledReason}", NoteKind.Warning);
            return;
        }
        // Shared actions from claudette.json run on a click like any other, without asking first: the user's choice
        // (DESIGN.md §18, "Custom actions"). Hovering one shows its whole command.
        switch (action.Kind)
        {
            case ProjectActionKind.Launch when action.Process is { } spec:
                try
                {
                    _services.ProjectTools.Launch(spec);
                }
                catch (Exception ex) when (IsStartFailure(ex))
                {
                    _host.AddNote($"Couldn't start {action.Label}: {ex.Message}", NoteKind.Error);
                }
                break;
            case ProjectActionKind.Run when action.Process is { } spec:
                StartJob(action, spec);
                break;
            case ProjectActionKind.Open when action.OpenPath is { } path:
                await OpenPathAsync(path, action.OpenWithIde, action.WithoutIde);
                break;
            case ProjectActionKind.Destructive when action.Destructive is DeleteFolders delete:
                await ConfirmDeleteAsync(action, delete);
                break;
            case ProjectActionKind.Destructive when action.Destructive is KillProcesses kill:
                await ConfirmKillAsync(kill);
                break;
        }
    }

    private static bool IsStartFailure(Exception ex) =>
        ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException;

    /// <summary>
    /// Opens a project's file or folder: with the chosen IDE when <paramref name="withIde"/>, falling back to the OS's
    /// app, or when <paramref name="withoutIde"/> says why not, to that note instead (a <c>.uproject</c> for Rider).
    /// </summary>
    private async Task OpenPathAsync(string path, bool withIde, string? withoutIde = null)
    {
        if (withIde)
        {
            var opening = _services.ProjectTools.Opener(path);
            if (opening.Spec is { } spec)
            {
                try
                {
                    _services.ProjectTools.Launch(spec);
                    return;
                }
                catch (Exception ex) when (IsStartFailure(ex))
                {
                    if (withoutIde is not null)
                    {
                        _host.AddNote($"Couldn't start {Path.GetFileName(spec.FileName)}: {ex.Message}", NoteKind.Error);
                        return;
                    }
                    _host.AddNote($"Couldn't start {Path.GetFileName(spec.FileName)}: {ex.Message}. Opened it with the OS's app instead.", NoteKind.Warning);
                }
            }
            else if (withoutIde is not null)
            {
                _host.AddNote(withoutIde, NoteKind.Warning);
                return;
            }
            else if (opening.Note is { } note)
            {
                _host.AddNote(note, NoteKind.Warning);
            }
        }
        await _services.Platform.OpenPathAsync(path);
    }

    /// <summary>Clean: lists the folders and their size, warns if the editor has the project open, then deletes them as a job.</summary>
    private async Task ConfirmDeleteAsync(ProjectAction action, DeleteFolders delete)
    {
        var processes = _services.ProjectTools.Processes;
        var (folders, size, editorOpen) = await Task.Run(() =>
        {
            var existing = delete.Folders().Where(Directory.Exists).ToList();
            var open = processes is not null && delete.EditorProcessNames is { Count: > 0 } names && delete.ProjectFile is { } file
                && processes.Find(names).Any(p => SystemProcessNames.CommandLineMentions(p, file));
            return (existing, FolderCleaner.Measure(existing), open);
        });
        if (folders.Count == 0)
        {
            _host.AddNote($"{action.Label.TrimEnd('…')}: there's nothing to delete.");
            await RefreshAsync();
            return;
        }
        var list = string.Join("\n", folders.Select(f => "• " + Path.GetRelativePath(delete.Root, f)));
        var message = $"This deletes {(folders.Count == 1 ? "1 folder" : $"{folders.Count} folders")}, {FolderCleaner.FormatSize(size)} in all:\n{list}";
        if (delete.Warning is { } warning)
        {
            message += $"\n\n{warning}";
        }
        if (editorOpen)
        {
            message += "\n\nThe editor seems to have this project open. Close it first, or files it has loaded can't be deleted.";
        }
        _host.Confirm($"{action.Label.TrimEnd('…')}?", message, "Delete", () =>
        {
            var root = delete.Root;
            BeginJob(ProjectJob.Run(action.Label.TrimEnd('…'), (log, token) =>
            {
                var ok = true;
                foreach (var folder in folders)
                {
                    token.ThrowIfCancellationRequested();
                    var name = Path.GetRelativePath(root, folder);
                    log($"Deleting {name}");
                    try
                    {
                        FolderCleaner.Delete(folder, token);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log($"Couldn't delete {name}: {ex.Message}");
                        ok = false;
                    }
                }
                log(ok ? $"Deleted {folders.Count} folder{(folders.Count == 1 ? "" : "s")}." : "Some folders couldn't be deleted.");
                return Task.FromResult(ok);
            }), action, $"Deleting {folders.Count} folder{(folders.Count == 1 ? "" : "s")} ({FolderCleaner.FormatSize(size)})");
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// <b>Kill all editors</b>: lists every running editor with the project it has open, when its command line says,
    /// then ends each one's whole process tree (DESIGN.md §18).
    /// </summary>
    private async Task ConfirmKillAsync(KillProcesses kill)
    {
        if (_services.ProjectTools.Processes is not { } processes)
        {
            _host.AddNote($"Claudette can't list the processes on this machine, so it can't end the {kill.What}s.", NoteKind.Warning);
            return;
        }
        var running = await Task.Run(() => processes.Find(kill.Names));
        if (running.Count == 0)
        {
            _host.AddNote($"No {kill.What} is running.");
            await RefreshAsync();
            return;
        }
        var list = string.Join("\n", running.Select(p => $"• {p.Name} (PID {p.Pid.ToString(CultureInfo.InvariantCulture)})"
            + (kill.ProjectOf?.Invoke(p) is { } project ? $": {project}" : "")));
        var count = running.Count == 1 ? $"1 {kill.What}" : $"{running.Count} {kill.What}s";
        _host.Confirm(
            $"End {count}?",
            $"{list}\n\nEach one ends at once, with the processes it started, such as shader compilers. Unsaved work in them is lost.",
            running.Count == 1 ? "End it" : "End them",
            async () =>
            {
                await Task.Run(() =>
                {
                    foreach (var process in running)
                    {
                        processes.KillTree(process);
                    }
                });
                _host.AddNote($"Ended {count}.");
                await RefreshAsync();
            });
    }

    // ---- Runs and the Project page ---------------------------------------------------------------------------------

    private ITimer? _runTicker;
    private ProjectRunViewModel? _notifiedRun;

    /// <summary>
    /// Each job this tab has run, newest last (DESIGN.md §18): listed under the tab's row in the sidebar, each with its
    /// own log, until the user closes it. They aren't saved.
    /// </summary>
    public ObservableCollection<ProjectRunViewModel> Runs { get; } = [];

    public bool HasRuns => Runs.Count > 0;

    /// <summary>The run whose job is running. One job runs at a time per tab.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsJobRunning), nameof(ButtonText))]
    public partial ProjectRunViewModel? RunningRun { get; private set; }

    partial void OnRunningRunChanged(ProjectRunViewModel? value) => UpdateRunTicker();

    public bool IsJobRunning => RunningRun is not null;

    /// <summary>The run the Project page shows: the newest, or the one last clicked in the sidebar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRun))]
    public partial ProjectRunViewModel? SelectedRun { get; private set; }

    partial void OnSelectedRunChanged(ProjectRunViewModel? value) => UpdateShownRun();

    public bool HasSelectedRun => SelectedRun is not null;

    /// <summary><b>Show output…</b>: opens the side panel on the Project page.</summary>
    [RelayCommand]
    private void ShowOutput() => _host.OpenProjectPage();

    /// <summary>A click on a run's entry in the sidebar: selects the tab and shows the run's log on the Project page.</summary>
    internal void OpenRun(ProjectRunViewModel run)
    {
        if (!Runs.Contains(run))
        {
            return;
        }
        SelectedRun = run;
        _host.SelectTab();
        _host.OpenProjectPage();
    }

    /// <summary>A clicked "project action finished" notification: the Project page, on the run it was about.</summary>
    internal void OpenNotifiedRun()
    {
        if (_notifiedRun is { } run && Runs.Contains(run))
        {
            SelectedRun = run;
        }
        _host.OpenProjectPage();
    }

    /// <summary>
    /// The × on a finished run's entry: the entry and its log go. A running one can't be closed, only stopped. When it
    /// was the one the Project page showed, the page shows the newest one left.
    /// </summary>
    internal void CloseRun(ProjectRunViewModel run)
    {
        if (run.IsRunning || !Runs.Remove(run))
        {
            return;
        }
        if (ReferenceEquals(_notifiedRun, run))
        {
            // Its notification is about a log that's gone.
            _notifiedRun = null;
            _services.Notifications.ClearTab(_host.Id, NotificationKind.ProjectAction);
        }
        if (ReferenceEquals(SelectedRun, run))
        {
            SelectedRun = Runs.LastOrDefault();
        }
        OnPropertyChanged(nameof(HasRuns));
    }

    /// <summary>Highlights the sidebar entry whose log is on the Project page, while the page shows on the selected tab.</summary>
    internal void UpdateShownRun()
    {
        var shown = _host.IsProjectPageShowing ? SelectedRun : null;
        foreach (var run in Runs)
        {
            run.IsShowing = ReferenceEquals(run, shown);
        }
    }

    /// <summary>A new run, which the Project page shows.</summary>
    private ProjectRunViewModel AddRun(string name, ProjectAction action)
    {
        var run = new ProjectRunViewModel(this, name, action, _services.Time);
        Runs.Add(run);
        OnPropertyChanged(nameof(HasRuns));
        SelectedRun = run;
        return run;
    }

    private void StartJob(ProjectAction action, Core.Processes.ProcessStartSpec spec)
    {
        ProjectJob job;
        try
        {
            if (action.ResultFile is { } result)
            {
                // A fresh result each time, so the summary never reads an old one.
                Directory.CreateDirectory(Path.GetDirectoryName(result)!);
                File.Delete(result);
            }
            job = _services.ProjectTools.StartJob(action.Label, spec);
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            // A run all the same, so its entry and log say why.
            var failed = AddRun(action.Label, action);
            failed.Append([$"$ {action.CommandText}", $"Couldn't start it: {ex.Message}"]);
            failed.EndCouldntStart($"{action.Label} couldn't start: {ex.Message}");
            _host.AddNote($"Couldn't start {action.Label}: {ex.Message}", NoteKind.Error);
            return;
        }
        BeginJob(job, action, $"$ {action.CommandText}");
    }

    private void BeginJob(ProjectJob job, ProjectAction action, string firstLine)
    {
        var run = AddRun(job.Name, action);
        run.Job = job;
        run.Append([firstLine]);
        RunningRun = run;
        if (job.ProcessId is { } pid && _services.ProcessTrees is { } trees)
        {
            try
            {
                var tree = trees.Find(pid) ?? trees.Track(pid);
                Volatile.Write(ref _jobTree, tree);
                job.UseTreeKiller(tree.KillAll);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                // Without a tree, Stop still ends the process and its children.
            }
        }
        // Each run keeps its own lines, whichever run the Project page shows.
        job.Output += lines => _services.Dispatcher.Post(() => run.Append(lines));
        _ = WatchJobAsync(run, job);
        job.Begin();
        ToolsChanged();
    }

    private async Task WatchJobAsync(ProjectRunViewModel run, ProjectJob job)
    {
        var result = await job.Completion.ConfigureAwait(false);
        _services.Dispatcher.Post(() => OnJobEnded(run, job, result));
    }

    private void OnJobEnded(ProjectRunViewModel run, ProjectJob job, ProjectJobResult result)
    {
        if (ReferenceEquals(RunningRun, run))
        {
            if (Interlocked.Exchange(ref _jobTree, null) is { } tree)
            {
                tree.Dispose();
            }
            RunningRun = null;
        }
        var action = run.Action;
        var summary = result.State == ProjectJobState.Succeeded || result.ExitCode is not null
            ? action?.Summarize?.Invoke(result.ExitCode ?? 0)
            : null;
        var exit = result.ExitCode is { } code ? $" (exit code {code.ToString(CultureInfo.InvariantCulture)})" : "";
        var status = result.State switch
        {
            ProjectJobState.Succeeded => $"{job.Name} succeeded.",
            ProjectJobState.Stopped => $"{job.Name} was stopped.",
            _ => $"{job.Name} failed{exit}.{(result.Message is { } why ? $" {why}" : "")}",
        } + (summary is null ? "" : $" {summary}");
        // The entry stays, whatever the result, until the user closes it.
        run.End(result.State, result.ExitCode, status);
        if (!Runs.Contains(run))
        {
            // The tab closed and left it running.
            return;
        }
        if (result.State != ProjectJobState.Stopped
            && _services.Notifications.Notify(NotificationKind.ProjectAction, _host.DisplayName, status, _host.Id))
        {
            // Only when Claudette isn't in front (DESIGN.md §10); a click opens this run on the Project page.
            _notifiedRun = run;
        }
        ToolsChanged();
        if (result.State == ProjectJobState.Succeeded && action?.ThenOnSuccess is { } next)
        {
            // Build and launch: the editor only starts after a build that worked.
            _ = RunActionAsync(next);
        }
        // A build or project file generation changes what can be opened or launched.
        _ = RefreshAsync();
    }

    /// <summary>A running entry's time ticks every second, from the injected clock.</summary>
    private void UpdateRunTicker()
    {
        if (RunningRun is not null)
        {
            _runTicker ??= _services.Time.CreateTimer(_ => _services.Dispatcher.Post(() => RunningRun?.Tick()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        else
        {
            StopRunTicker();
        }
    }

    private void StopRunTicker()
    {
        _runTicker?.Dispose();
        _runTicker = null;
    }

    /// <summary><b>Copy</b> on the Project page: the log it shows.</summary>
    [RelayCommand]
    private Task CopyOutputAsync() =>
        SelectedRun is { } run ? _services.Platform.SetClipboardTextAsync(string.Join(Environment.NewLine, run.Output)) : Task.CompletedTask;

    /// <summary>The job's processes, for the process monitor: the job's own process isn't the tab's <c>claude</c>.</summary>
    internal IReadOnlyList<ProcessSnapshot> JobProcesses(bool includeCommandLines)
    {
        if (Volatile.Read(ref _jobTree) is not { IsDisposed: false } tree)
        {
            return [];
        }
        try
        {
            return tree.Sample(includeCommandLines).Select(s => s.IsRoot ? s with { IsRoot = false } : s).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or ObjectDisposedException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="pid"/> belongs to the running job, so the process monitor's Stop goes to its tree.</summary>
    internal ProcessTree? JobTreeHolding(int pid) =>
        Volatile.Read(ref _jobTree) is { IsDisposed: false } tree && JobProcesses(false).Any(p => p.Pid == pid) ? tree : null;

    /// <summary>
    /// Closing the tab: a running job is stopped with the tab's other processes, unless they're kept, and the runs go
    /// with the tab.
    /// </summary>
    internal void CloseRuns(bool killProcesses)
    {
        if (killProcesses)
        {
            RunningRun?.Job?.Stop();
        }
        StopRunTicker();
        _notifiedRun = null;
        SelectedRun = null;
        Runs.Clear();
        OnPropertyChanged(nameof(HasRuns));
    }
}
