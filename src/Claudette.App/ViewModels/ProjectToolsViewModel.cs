using System.ComponentModel;
using System.Globalization;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.ProjectTools;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

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
/// the project's row at the sidebar's foot and its menu (<see cref="ProjectMenu"/>), and the Project page of the side
/// panel. The runs of its jobs, each with its log, are <see cref="Runs"/>.
/// </summary>
public sealed partial class ProjectToolsViewModel : ViewModelBase, IProjectRunsHost
{
    private readonly AppServices _services;
    private readonly IProjectToolsHost _host;
    private ProjectDetection _projectDetection = ProjectDetection.None;
    private ProjectFileContents _projectFile = ProjectFileContents.Empty;
    private IReadOnlyList<(CustomProjectAction Custom, ProjectAction Action)> _customActions = [];
    private ProjectMenuCommands? _menuCommands;
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
        Runs = new ProjectRunsViewModel(services, this);
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
        Runs.RunningRun is { } running ? $"{running.Name}…"
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
    public IReadOnlyList<ProjectAction> Actions => [.. Project?.Actions.Select(Runs.ForJob) ?? [], .. _customActions.Select(c => Runs.ForJob(c.Action))];

    /// <summary>The project row's menu.</summary>
    public IReadOnlyList<ProjectMenuEntry> Menu => ProjectMenu.Build(
        Project, _projectDetection.Candidates, _customActions.Select(c => c.Action).ToArray(), Links, HasTools, Runs.ForJob,
        _menuCommands ??= new ProjectMenuCommands(RunActionCommand, ChooseProjectCommand, ChooseOptionCommand, FixCommand, OpenLinkCommand,
            ShowOutputCommand, AddActionCommand, AddLinkCommand, RefreshCommand));

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
        action = Runs.ForJob(action);
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
                Runs.StartJob(action, spec);
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

    internal static bool IsStartFailure(Exception ex) =>
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
            Runs.BeginJob(ProjectJob.Run(action.Label.TrimEnd('…'), (log, token) =>
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

    /// <summary>The runs of the project's jobs, each with its log, listed under the tab's row and on the Project page.</summary>
    public ProjectRunsViewModel Runs { get; }

    /// <summary><b>Show output…</b>: opens the side panel on the Project page.</summary>
    [RelayCommand]
    private void ShowOutput() => _host.OpenProjectPage();

    // What the runs need: the tab's, passed on, and the project's row, menu and actions, which change with them.

    string IProjectRunsHost.Id => _host.Id;

    string IProjectRunsHost.DisplayName => _host.DisplayName;

    bool IProjectRunsHost.IsProjectPageShowing => _host.IsProjectPageShowing;

    void IProjectRunsHost.AddNote(string text, NoteKind kind) => _host.AddNote(text, kind);

    void IProjectRunsHost.OpenProjectPage() => _host.OpenProjectPage();

    void IProjectRunsHost.SelectTab() => _host.SelectTab();

    void IProjectRunsHost.RunningRunChanged() => OnPropertyChanged(nameof(ButtonText));

    void IProjectRunsHost.JobBegan() => ToolsChanged();

    void IProjectRunsHost.JobEnded(ProjectAction? next)
    {
        ToolsChanged();
        if (next is not null)
        {
            // Build and launch: the editor only starts after a build that worked.
            _ = RunActionAsync(next);
        }
        // A build or project file generation changes what can be opened or launched.
        _ = RefreshAsync();
    }
}
