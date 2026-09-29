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

/// <summary>What an entry of the project menus is.</summary>
public enum ProjectMenuKind
{
    /// <summary>A label, such as "Editor configuration".</summary>
    Header,
    /// <summary>A project action, such as Launch editor.</summary>
    Action,
    /// <summary>One of a set of choices, such as DebugGame: a radio item.</summary>
    Option,
    Separator,
    /// <summary>Something about the menu itself: Show output…, Add an action…, Refresh.</summary>
    Command,
    /// <summary>A link from the folder's project files, opened in the browser.</summary>
    Link,
}

/// <summary>
/// One entry of the project menus (DESIGN.md §18, "Project tools"): the chip's menu in the composer bar and the
/// project's submenu in the sidebar's tab menu list the same entries.
/// </summary>
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

    /// <summary>A button in the chip's menu: an action, a choice or a command.</summary>
    public bool IsButton => Kind is ProjectMenuKind.Action or ProjectMenuKind.Option or ProjectMenuKind.Command or ProjectMenuKind.Link;

    public bool IsLink => Kind == ProjectMenuKind.Link;

    /// <summary>For a menu item: a label can't be clicked.</summary>
    public bool IsMenuEnabled => IsEnabled && Kind != ProjectMenuKind.Header;

    /// <summary>For a menu item: <c>-</c> makes a separator.</summary>
    public string MenuHeader => IsSeparator ? "-" : Label;

    /// <summary>A radio mark for choices in the chip's menu, and an arrow for links.</summary>
    public string Mark => Kind switch
    {
        ProjectMenuKind.Option => IsChecked ? "●" : "○",
        ProjectMenuKind.Link => "↗",
        _ => "",
    };

    public bool HasMark => Kind is ProjectMenuKind.Option or ProjectMenuKind.Link;

    public override string ToString() => Label;
}

/// <summary>
/// Project tools (DESIGN.md §18): the project detected for the tab's folder, its actions and the folder's custom ones,
/// the chip in the composer bar, the Project page of the side panel, and the job that's running or ran last.
/// </summary>
public sealed partial class TabViewModel
{
    /// <summary>The Project page keeps at most this many lines of a job's output.</summary>
    public const int MaxProjectOutputLines = 5000;

    private ProjectDetection _projectDetection = ProjectDetection.None;
    private ProjectFileContents _projectFile = ProjectFileContents.Empty;
    private IReadOnlyList<(CustomProjectAction Custom, ProjectAction Action)> _customActions = [];
    private ProjectJob? _projectJob;
    private ProjectAction? _projectJobAction;
    private ProcessTree? _projectJobTree;
    private string? _projectNoteSent;
    private string _projectSettingsSeen = "";

    // Reads of the project files and detections, numbered as they start. One that finishes after a later one was shown
    // is dropped, so saves in quick succession (Settings' Links and Actions pages) never leave the tab showing an
    // older file, and a detection from before a choice never undoes it.
    private long _projectFileReads;
    private long _projectFileShown;
    private long _projectDetections;
    private long _projectDetectionShown;

    /// <summary>The project detected for the tab's folder, or null.</summary>
    [ObservableProperty]
    public partial ProjectInfo? Project { get; private set; }

    partial void OnProjectChanged(ProjectInfo? value) => ProjectToolsChanged();

    /// <summary>
    /// The chip shows, and the sidebar has a project submenu: a detected project, custom actions, or a claudette.json
    /// whose entries were skipped (the menu says why).
    /// </summary>
    public bool HasProjectTools => Project is not null || _customActions.Count > 0 || _projectFile.Problems.Count > 0;

    /// <summary>Custom actions, but no project: the menus call it "Actions".</summary>
    public bool HasOnlyCustomActions => Project is null && HasProjectTools;

    /// <summary>The sidebar's tab menu offers <b>Add an action…</b> by itself until the folder has project tools.</summary>
    public bool OffersFirstProjectAction => !HasProjectTools;

    /// <summary>"NightOwl · UE 5.4", "Actions", or while a job runs, its name: "Build editor…".</summary>
    public string ProjectChipText =>
        IsProjectJobRunning && ProjectJobName is { } running ? $"{running}…"
        : Project is { } project ? project.ShortVersion is { } version ? $"{project.Name} · {version}" : project.Name
        : "Actions";

    public string ProjectChipTip
    {
        get
        {
            var name = Project is { } project ? $"{project.Name} ({project.KindName})" : "This folder's actions";
            var main = MainProjectAction is { } action && _services.Tips.Text(KeyboardShortcuts.RunProjectAction) is { } shortcut
                ? $". {shortcut}: {action.Label}"
                : "";
            return name + main;
        }
    }

    /// <summary>The project's submenu in the sidebar's tab menu: named after the project.</summary>
    public string ProjectMenuTitle => Project is { } project ? project.ShortVersion is { } version ? $"{project.Name} ({version})" : project.Name : "Actions";

    /// <summary>The chip menu's header: the project and its kind.</summary>
    public string ProjectHeaderTitle => Project is { } project ? $"{project.Name} · {project.KindName}" : "Actions for this folder";

    /// <summary>The engine's version, folder and kind, or what's wrong.</summary>
    public IReadOnlyList<string> ProjectHeaderLines => Project?.HeaderLines ?? [];

    public string? ProjectProblem => Project?.Problem;

    public bool HasProjectProblem => ProjectProblem is not null;

    /// <summary>Entries of <c>claudette.json</c> or <c>claudette.local.json</c> that were skipped, and why.</summary>
    public IReadOnlyList<string> ProjectFileProblems => _projectFile.Problems;

    public bool HasProjectFileProblems => _projectFile.Problems.Count > 0;

    /// <summary>The links of the folder's project files, filled in for this tab (DESIGN.md §18, "Links").</summary>
    public IReadOnlyList<ResolvedLink> Links => _projectFile.Links.Select(l => ProjectLinks.Resolve(l, LinkValues())).ToArray();

    public bool HasLinks => _projectFile.Links.Count > 0;

    /// <summary>What <c>{branch}</c>, <c>{changelist}</c> and <c>{folderName}</c> are, now.</summary>
    private LinkValues LinkValues() => new(
        Core.Git.GitInfo.TryGetBranch(Folder),
        Changelists.Current?.Number.ToString(CultureInfo.InvariantCulture),
        FolderName);

    /// <summary>A link from the Links section or the chip menu: opened in the browser, when it's allowed to open.</summary>
    [RelayCommand]
    private Task OpenProjectLinkAsync(ResolvedLink? link) =>
        link is { Url: { } url } ? _services.Platform.OpenUrlAsync(url) : Task.CompletedTask;

    public IReadOnlyList<ProjectDetail> ProjectDetails => Project?.Details ?? [];

    /// <summary>The project's actions and the folder's own, for the Project page's buttons.</summary>
    public IReadOnlyList<ProjectAction> ProjectActions => [.. Project?.Actions.Select(ForJob) ?? [], .. _customActions.Select(c => ForJob(c.Action))];

    /// <summary>The chip's menu and the sidebar's project submenu.</summary>
    public IReadOnlyList<ProjectMenuEntry> ProjectMenu => BuildProjectMenu();

    public ProjectAction? MainProjectAction => Project?.MainAction ?? _customActions.Select(c => c.Action).FirstOrDefault();

    // ---- Detection ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Finds the tab folder's project again, and reads its <c>claudette.json</c> and <c>claudette.local.json</c>, off
    /// the UI thread.
    /// </summary>
    public async Task RefreshProjectAsync()
    {
        _projectSettingsSeen = ProjectSettingsKey();
        var detectionNumber = Interlocked.Increment(ref _projectDetections);
        var fileNumber = Interlocked.Increment(ref _projectFileReads);
        var tools = _services.ProjectTools;
        ProjectDetection detection;
        ProjectFileContents file;
        try
        {
            var detecting = tools.DetectAsync(Folder);
            var reading = tools.ReadProjectFileAsync(Folder);
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
            ProjectToolsChanged();
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    private Task RefreshProject() => RefreshProjectAsync();

    /// <summary>
    /// Reads the folder's project files again: when the tab is selected, Claudette comes to the front, a turn ends
    /// (Claude may have edited them, or switched branches), and after the in-app editor saves.
    /// </summary>
    public async Task RefreshProjectFileAsync()
    {
        var fileNumber = Interlocked.Increment(ref _projectFileReads);
        ProjectFileContents file;
        try
        {
            file = await _services.ProjectTools.ReadProjectFileAsync(Folder).ConfigureAwait(false);
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
                ProjectToolsChanged();
            }
        }).ConfigureAwait(false);
    }

    private void UseProjectFile(ProjectFileContents file)
    {
        _projectFile = file;
        _customActions = _services.ProjectTools.CustomActions(file, Folder);
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
    private async Task<ClaudeLaunchOptions> WithProjectToolsAsync(ClaudeLaunchOptions options)
    {
        await RefreshProjectAsync();
        _projectNoteSent = Project?.SystemPromptNote;
        OnPropertyChanged(nameof(InfoRows));
        return _projectNoteSent is { Length: > 0 } note
            ? options with { AppendSystemPrompt = options.AppendSystemPrompt is { Length: > 0 } existing ? $"{existing}\n\n{note}" : note }
            : options;
    }

    /// <summary>The Project row of the tab info card (DESIGN.md §4).</summary>
    private void AddProjectRows(List<InfoRow> rows)
    {
        if (Project is not { } project)
        {
            return;
        }
        var text = project.HeaderLines.Count > 0 ? $"{project.Name}: {project.HeaderLines[0]}" : $"{project.Name} ({project.KindName})";
        if (_projectNoteSent is not null)
        {
            text += ". Claude was told how to build it.";
        }
        rows.Add(new InfoRow("Project", text));
    }

    /// <summary>Settings → Project tools changed: the actions (and the note, from the next session) may be different.</summary>
    private void OnProjectToolSettingsChanged()
    {
        OnPropertyChanged(nameof(ProjectChipTip));
        if (ProjectSettingsKey() != _projectSettingsSeen)
        {
            _ = RefreshProjectAsync();
        }
    }

    private string ProjectSettingsKey() => System.Text.Json.JsonSerializer.Serialize(_services.Settings.ProjectTools, JsonFileStore<AppSettings>.Options);

    private void ProjectToolsChanged()
    {
        OnPropertyChanged(nameof(HasProjectTools));
        OnPropertyChanged(nameof(HasOnlyCustomActions));
        OnPropertyChanged(nameof(OffersFirstProjectAction));
        OnPropertyChanged(nameof(ProjectChipText));
        OnPropertyChanged(nameof(ProjectChipTip));
        OnPropertyChanged(nameof(ProjectMenuTitle));
        OnPropertyChanged(nameof(ProjectHeaderTitle));
        OnPropertyChanged(nameof(ProjectHeaderLines));
        OnPropertyChanged(nameof(ProjectProblem));
        OnPropertyChanged(nameof(HasProjectProblem));
        OnPropertyChanged(nameof(ProjectFileProblems));
        OnPropertyChanged(nameof(HasProjectFileProblems));
        OnPropertyChanged(nameof(Links));
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(ProjectDetails));
        OnPropertyChanged(nameof(ProjectActions));
        OnPropertyChanged(nameof(ProjectMenu));
        OnPropertyChanged(nameof(MainProjectAction));
        OnPropertyChanged(nameof(InfoRows));
    }

    private IReadOnlyList<ProjectMenuEntry> BuildProjectMenu()
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
            Command = RunProjectActionCommand,
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
                        Command = ChooseProjectOptionCommand,
                        Parameter = option,
                    });
                }
            }
            if (project.Fix is { } fix)
            {
                Separator();
                entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = fix.Label, Tip = fix.Title, Command = FixProjectCommand });
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
                Command = OpenProjectLinkCommand,
                Parameter = link,
            }));
        }
        Separator();
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Show output…", Tip = "The Project page of the side panel", Command = OpenProjectPageCommand });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Add an action…", Tip = "A command of your own for this folder", Command = AddProjectActionCommand });
        entries.Add(new ProjectMenuEntry { Kind = ProjectMenuKind.Command, Label = "Refresh", Tip = "Look for the project and its files again", Command = RefreshProjectCommand });
        return entries;
    }

    /// <summary>While a job runs, other jobs wait: one at a time per tab.</summary>
    private ProjectAction ForJob(ProjectAction action) =>
        IsProjectJobRunning && action.IsEnabled && action.Kind is ProjectActionKind.Run or ProjectActionKind.Destructive
            ? action with { DisabledReason = $"{ProjectJobName} is still running. Stop it on the Project page first." }
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
        _services.ProjectTools.State.ChooseProject(Folder, candidate.Path);
        _services.SaveState();
        return RefreshProjectAsync();
    }

    /// <summary>Picks a per-project choice, such as DebugGame. Remembered for the project on this machine.</summary>
    [RelayCommand]
    private Task ChooseProjectOption(ProjectChoiceOption? option)
    {
        if (option is null || Project is not { Choice: { } choice } project)
        {
            return Task.CompletedTask;
        }
        _services.ProjectTools.Remember(project.ProjectPath, choice.Key, option.Value);
        return RefreshProjectAsync();
    }

    /// <summary><b>Choose engine folder…</b> and the like: pick, check, remember for the project, look again.</summary>
    [RelayCommand]
    private async Task FixProjectAsync()
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
            _conversation.AddNote(error ?? $"{picked} can't be used.", NoteKind.Warning);
            return;
        }
        _services.ProjectTools.Remember(project.ProjectPath, fix.Key, path);
        await RefreshProjectAsync();
    }

    // ---- Custom actions (DESIGN.md §18, "Custom actions") --------------------------------------------------------

    /// <summary>
    /// <b>Add an action…</b>: Settings opens on this tab's Actions page (DESIGN.md §14) with a new action started. Its
    /// dialog asks which file the action goes in, then it joins the end of that file's <c>actions</c>.
    /// </summary>
    [RelayCommand]
    private Task AddProjectAction() => _shell.OpenProjectSettingsAsync(this, SettingsViewModel.ActionsPage, startNewAction: true);

    /// <summary>A folder's project files changed through the in-app editor, here or in another tab in the same folder.</summary>
    internal void ReloadCustomActions() => _ = RefreshProjectFileAsync();

    /// <summary>Every project detection found for the folder, nearest first, for Settings' Tools page.</summary>
    internal IReadOnlyList<ProjectCandidate> ProjectCandidates => _projectDetection.Candidates;

    // ---- Running actions -------------------------------------------------------------------------------------------

    /// <summary>"Run the project's main action" (Ctrl/Cmd+Shift+E): Launch editor, for Unreal.</summary>
    [RelayCommand]
    private Task RunMainProjectAction() => MainProjectAction is { } action ? RunProjectActionAsync(action) : Task.CompletedTask;

    [RelayCommand]
    private async Task RunProjectActionAsync(ProjectAction? action)
    {
        if (action is null)
        {
            return;
        }
        action = ForJob(action);
        if (!action.IsEnabled)
        {
            _conversation.AddNote($"{action.Label}: {action.DisabledReason}", NoteKind.Warning);
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
                    _conversation.AddNote($"Couldn't start {action.Label}: {ex.Message}", NoteKind.Error);
                }
                break;
            case ProjectActionKind.Run when action.Process is { } spec:
                StartProjectJob(action, spec);
                break;
            case ProjectActionKind.Open when action.OpenPath is { } path:
                await OpenProjectPathAsync(path, action.OpenWithIde);
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

    private async Task OpenProjectPathAsync(string path, bool withIde)
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
                    _conversation.AddNote($"Couldn't start {Path.GetFileName(spec.FileName)}: {ex.Message}. Opened it with the OS's app instead.", NoteKind.Warning);
                }
            }
            else if (opening.Note is { } note)
            {
                _conversation.AddNote(note, NoteKind.Warning);
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
            _conversation.AddNote($"{action.Label.TrimEnd('…')}: there's nothing to delete.");
            await RefreshProjectAsync();
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
        _shell.Confirm($"{action.Label.TrimEnd('…')}?", message, "Delete", () =>
        {
            var root = delete.Root;
            BeginProjectJob(ProjectJob.Run(action.Label.TrimEnd('…'), (log, token) =>
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
            _conversation.AddNote($"Claudette can't list the processes on this machine, so it can't end the {kill.What}s.", NoteKind.Warning);
            return;
        }
        var running = await Task.Run(() => processes.Find(kill.Names));
        if (running.Count == 0)
        {
            _conversation.AddNote($"No {kill.What} is running.");
            await RefreshProjectAsync();
            return;
        }
        var list = string.Join("\n", running.Select(p => $"• {p.Name} (PID {p.Pid.ToString(CultureInfo.InvariantCulture)})"
            + (kill.ProjectOf?.Invoke(p) is { } project ? $": {project}" : "")));
        var count = running.Count == 1 ? $"1 {kill.What}" : $"{running.Count} {kill.What}s";
        _shell.Confirm(
            $"End {count}?",
            $"{list}\n\nEach one ends at once, with the processes it started, such as shader compilers. Unsaved work in them is lost.",
            running.Count == 1 ? "End it" : "End them",
            async () =>
            {
                await Task.Run(() =>
                {
                    foreach (var process in running)
                    {
                        processes.KillTree(process.Pid);
                    }
                });
                _conversation.AddNote($"Ended {count}.");
                await RefreshProjectAsync();
            });
    }

    // ---- The job and the Project page ----------------------------------------------------------------------------

    /// <summary>The running or last job's output, at most <see cref="MaxProjectOutputLines"/> lines.</summary>
    public ObservableCollection<string> ProjectOutput { get; } = [];

    /// <summary>Lines dropped from the start of the output to keep it to the limit.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectOutputNote))]
    public partial int ProjectOutputDropped { get; private set; }

    public string? ProjectOutputNote => ProjectOutputDropped > 0
        ? $"Showing the last {MaxProjectOutputLines.ToString("N0", CultureInfo.CurrentCulture)} lines; {ProjectOutputDropped.ToString("N0", CultureInfo.CurrentCulture)} earlier ones were dropped."
        : null;

    /// <summary>The running or last job's name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectChipText))]
    public partial string? ProjectJobName { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjectJobRunning), nameof(ProjectChipText), nameof(HasProjectJob))]
    [NotifyCanExecuteChangedFor(nameof(StopProjectJobCommand))]
    public partial ProjectJobState? ProjectJobState { get; private set; }

    public bool IsProjectJobRunning => ProjectJobState == Core.ProjectTools.ProjectJobState.Running;

    public bool HasProjectJob => ProjectJobState is not null;

    /// <summary>"Build editor is running…", "Build editor succeeded.", "Build editor failed (exit code 6)."</summary>
    [ObservableProperty]
    public partial string? ProjectJobStatus { get; private set; }

    [ObservableProperty]
    public partial bool ProjectJobFailed { get; private set; }

    /// <summary>The Project page of the side panel is showing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilesPage))]
    public partial bool IsProjectPage { get; set; }

    partial void OnIsProjectPageChanged(bool value)
    {
        if (value)
        {
            IsProcessesPage = false;
            IsAgentsPage = false;
        }
    }

    [RelayCommand]
    private void ShowProjectPage() => IsProjectPage = true;

    /// <summary><b>Show output…</b>, and a clicked notification: opens the side panel on the Project page.</summary>
    [RelayCommand]
    private void OpenProjectPage()
    {
        IsSidePanelOpen = true;
        IsProjectPage = true;
    }

    private void StartProjectJob(ProjectAction action, Core.Processes.ProcessStartSpec spec)
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
            ProjectOutput.Clear();
            ProjectOutputDropped = 0;
            AppendProjectOutput([$"$ {action.CommandText}", $"Couldn't start it: {ex.Message}"]);
            ProjectJobName = action.Label;
            ProjectJobState = Core.ProjectTools.ProjectJobState.Failed;
            ProjectJobFailed = true;
            ProjectJobStatus = $"{action.Label} couldn't start: {ex.Message}";
            _conversation.AddNote($"Couldn't start {action.Label}: {ex.Message}", NoteKind.Error);
            return;
        }
        BeginProjectJob(job, action, $"$ {action.CommandText}");
    }

    private void BeginProjectJob(ProjectJob job, ProjectAction action, string firstLine)
    {
        _projectJob = job;
        _projectJobAction = action;
        ProjectOutput.Clear();
        ProjectOutputDropped = 0;
        AppendProjectOutput([firstLine]);
        ProjectJobName = job.Name;
        ProjectJobState = Core.ProjectTools.ProjectJobState.Running;
        ProjectJobFailed = false;
        ProjectJobStatus = $"{job.Name} is running…";
        if (job.ProcessId is { } pid && _services.ProcessTrees is { } trees)
        {
            try
            {
                var tree = trees.Find(pid) ?? trees.Track(pid);
                Volatile.Write(ref _projectJobTree, tree);
                job.UseTreeKiller(tree.KillAll);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
            {
                // Without a tree, Stop still ends the process and its children.
            }
        }
        job.Output += lines => _services.Dispatcher.Post(() =>
        {
            if (ReferenceEquals(_projectJob, job))
            {
                AppendProjectOutput(lines);
            }
        });
        _ = WatchProjectJobAsync(job);
        job.Begin();
        ProjectToolsChanged();
    }

    private async Task WatchProjectJobAsync(ProjectJob job)
    {
        var result = await job.Completion.ConfigureAwait(false);
        _services.Dispatcher.Post(() => OnProjectJobEnded(job, result));
    }

    private void OnProjectJobEnded(ProjectJob job, ProjectJobResult result)
    {
        if (!ReferenceEquals(_projectJob, job))
        {
            return;
        }
        var action = _projectJobAction;
        if (Interlocked.Exchange(ref _projectJobTree, null) is { } tree)
        {
            tree.Dispose();
        }
        var summary = result.State == Core.ProjectTools.ProjectJobState.Succeeded || result.ExitCode is not null
            ? action?.Summarize?.Invoke(result.ExitCode ?? 0)
            : null;
        var exit = result.ExitCode is { } code ? $" (exit code {code.ToString(CultureInfo.InvariantCulture)})" : "";
        ProjectJobStatus = result.State switch
        {
            Core.ProjectTools.ProjectJobState.Succeeded => $"{job.Name} succeeded.",
            Core.ProjectTools.ProjectJobState.Stopped => $"{job.Name} was stopped.",
            _ => $"{job.Name} failed{exit}.{(result.Message is { } why ? $" {why}" : "")}",
        } + (summary is null ? "" : $" {summary}");
        ProjectJobFailed = result.State == Core.ProjectTools.ProjectJobState.Failed;
        ProjectJobState = result.State;
        AppendProjectOutput([ProjectJobStatus]);
        if (result.State != Core.ProjectTools.ProjectJobState.Stopped)
        {
            // Only when Claudette isn't in front (DESIGN.md §10); a click opens this Project page.
            _services.Notifications.Notify(NotificationKind.ProjectAction, DisplayName, ProjectJobStatus, Id);
        }
        ProjectToolsChanged();
        if (result.State == Core.ProjectTools.ProjectJobState.Succeeded && action?.ThenOnSuccess is { } next)
        {
            // Build and launch: the editor only starts after a build that worked.
            _ = RunProjectActionAsync(next);
        }
        // A build or project file generation changes what can be opened or launched.
        _ = RefreshProjectAsync();
    }

    private void AppendProjectOutput(IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            ProjectOutput.Add(line);
        }
        var extra = ProjectOutput.Count - MaxProjectOutputLines;
        if (extra > 0)
        {
            for (var i = 0; i < extra; i++)
            {
                ProjectOutput.RemoveAt(0);
            }
            ProjectOutputDropped += extra;
        }
    }

    /// <summary><b>Stop</b>: ends the job's whole process tree (UnrealBuildTool starts children), or its work.</summary>
    [RelayCommand(CanExecute = nameof(IsProjectJobRunning))]
    private void StopProjectJob() => _projectJob?.Stop();

    [RelayCommand]
    private Task CopyProjectOutputAsync() => _services.Platform.SetClipboardTextAsync(string.Join(Environment.NewLine, ProjectOutput));

    /// <summary>The job's processes, for the process monitor: the job's own process isn't the tab's <c>claude</c>.</summary>
    private IReadOnlyList<ProcessSnapshot> ProjectJobProcesses(bool includeCommandLines)
    {
        if (Volatile.Read(ref _projectJobTree) is not { IsDisposed: false } tree)
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
    private ProcessTree? ProjectJobTreeHolding(int pid) =>
        Volatile.Read(ref _projectJobTree) is { IsDisposed: false } tree && ProjectJobProcesses(false).Any(p => p.Pid == pid) ? tree : null;

    /// <summary>Closing the tab: a running job is stopped with the tab's other processes, unless they're kept.</summary>
    private void StopProjectJobOnClose(bool killProcesses)
    {
        if (killProcesses && IsProjectJobRunning)
        {
            _projectJob?.Stop();
        }
    }
}
