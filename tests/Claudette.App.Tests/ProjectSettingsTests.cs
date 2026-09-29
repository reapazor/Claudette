using System.Text.Json.Nodes;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.Core.ProjectTools;
using Claudette.Core.ProjectTools.Unity;
using Claudette.Core.ProjectTools.Unreal;
using Claudette.Core.Settings;

namespace Claudette.App.Tests;

/// <summary>
/// The selected tab's project in Settings (DESIGN.md §14, "The project's pages"): its Links, Actions and Tools pages, and
/// the group that heads them. Project files are written in temporary folders; engines are a few files; nothing runs.
/// </summary>
public class ProjectSettingsTests
{
    private static void Write(string path, string text) => UnrealFixture.WriteFile(path, text);

    private static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private static string[] Names(JsonNode root, string key) => [.. root[key]!.AsArray().Select(e => e!["name"]?.GetValue<string>() ?? "")];

    /// <summary>Opens Settings the way the gear does, and makes the window's view model from what the shell asked for.</summary>
    private static async Task<SettingsViewModel> OpenSettingsAsync(TabTestHarness h)
    {
        SettingsOpening? opening = null;
        h.Shell.ShowSettingsWindow = o =>
        {
            opening = o;
            return Task.CompletedTask;
        };
        await h.Shell.OpenSettingsCommand.ExecuteAsync(null);
        return new SettingsViewModel(h.Services, null, opening: opening);
    }

    /// <summary>A tab that isn't started: its folder is gone, so selecting it doesn't start a session.</summary>
    private static TabViewModel AddUnstartedTab(TabTestHarness h, string name)
    {
        var tab = new TabViewModel(h.Services, h.Shell, new TabState { Folder = Path.Combine(h.Root, name) }, isRestored: false);
        var group = new TabGroupViewModel(tab.Folder, 1, isCollapsed: false);
        group.Tabs.Add(tab);
        h.Shell.Groups.Add(group);
        return tab;
    }

    // ---- The group -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_group_follows_the_selected_tab_is_named_after_its_project_and_is_hidden_with_no_tab()
    {
        await using var h = new TabTestHarness();

        using (var none = await OpenSettingsAsync(h))
        {
            Assert.False(none.HasProject);
            Assert.Null(none.SelectedProjectPage);
            none.SelectedProjectPage = SettingsViewModel.LinksPage;
            Assert.Equal("General", none.SelectedCategory);
            Assert.False(none.IsLinksPage);
        }

        var tab = await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;
        Assert.Same(tab, project.Tab);
        Assert.Equal("work", project.Heading);
        Assert.Equal(h.WorkFolder, project.Folder);
        Assert.Equal($"For the tab in {h.WorkFolder}", project.ForTabText);

        // Picking a page selects it in the project group, and nothing in the categories.
        settings.SelectedProjectPage = SettingsViewModel.ToolsPage;
        Assert.True(settings.IsToolsPage);
        Assert.Null(settings.SelectedMainCategory);
        settings.SelectedMainCategory = null;
        Assert.True(settings.IsToolsPage);
        settings.SelectedMainCategory = "Appearance";
        Assert.True(settings.IsAppearance);
        Assert.Null(settings.SelectedProjectPage);

        // A provider finds a project: the group is named after it, in this window and the next.
        UnrealFixture.Write(h.Root, h.WorkFolder);
        await tab.RefreshProjectAsync();
        await TabTestHarness.Eventually(() => project.Heading == "NightOwl", "the project's name");
        using (var next = await OpenSettingsAsync(h))
        {
            Assert.Equal("NightOwl", next.Project!.Heading);
        }

        // Another tab selected: the next window follows it; this one keeps its tab.
        var other = AddUnstartedTab(h, "elsewhere");
        h.Shell.SelectedTab = other;
        using var third = await OpenSettingsAsync(h);
        Assert.Same(other, third.Project!.Tab);
        Assert.Equal("elsewhere", third.Project.Heading);
        Assert.Same(tab, project.Tab);
    }

    [Fact]
    public async Task Search_finds_the_project_pages_by_their_names_and_the_projects()
    {
        var (h, _, _) = ProjectToolsTests.UnrealHarness();
        await using var _h = h;
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        using var settings = await OpenSettingsAsync(h);

        settings.SearchText = "web links";
        var links = Assert.Single(settings.SearchResults);
        Assert.Equal((SettingsViewModel.LinksPage, "NightOwl → Links"), (links.Category, links.Where));
        Assert.True(settings.IsLinksPage);

        settings.SearchText = "links";
        Assert.All(settings.SearchResults, r => Assert.Equal(SettingsViewModel.LinksPage, r.Category));

        settings.SearchText = "add an action";
        Assert.Equal(SettingsViewModel.ActionsPage, Assert.Single(settings.SearchResults).Category);

        settings.SearchText = "engine";
        Assert.Contains(settings.SearchResults, r => r is { Category: SettingsViewModel.ToolsPage, Label: "Engine folder" });
        settings.SelectedSearchResult = settings.SearchResults.First(r => r.Category == SettingsViewModel.ToolsPage);
        Assert.True(settings.IsToolsPage);

        settings.SearchText = "editor configuration";
        Assert.Contains(settings.SearchResults, r => r.Category == SettingsViewModel.ToolsPage);
        Assert.Contains(settings.SearchResults, r => r.Category == "Project tools");

        // The project's name finds its pages too; the categories read as before.
        settings.SearchText = "nightowl actions";
        Assert.All(settings.SearchResults, r => Assert.Equal(SettingsViewModel.ActionsPage, r.Category));
        settings.SearchText = "theme";
        Assert.Equal("Appearance", Assert.Single(settings.SearchResults).Where);

        // No tab, no project pages to find.
        await using var empty = new TabTestHarness();
        using var none = await OpenSettingsAsync(empty);
        none.SearchText = "web links";
        Assert.Empty(none.SearchResults);
    }

    [Fact]
    public async Task Reset_to_defaults_leaves_the_project_files_and_the_projects_choices_alone()
    {
        var (h, _, uproject) = ProjectToolsTests.UnrealHarness();
        await using var _h = h;
        var shared = Path.Combine(h.WorkFolder, ProjectFile.SharedName);
        Write(shared, """{ "links": [ { "name": "Board", "url": "https://example.com/board" } ] }""");
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        h.Services.ProjectTools.Remember(uproject, UnrealProvider.ConfigurationKey, "DebugGame");
        h.Services.Settings.ProjectTools.UnrealConfiguration = UnrealConfiguration.DebugGame;
        using var settings = await OpenSettingsAsync(h);
        var before = File.ReadAllText(shared);

        settings.ResetProjectToolsCommand.Execute(null);
        settings.ResetGeneralCommand.Execute(null);
        settings.ResetAdvancedCommand.Execute(null);

        Assert.Equal(UnrealConfiguration.Development, h.Services.Settings.ProjectTools.UnrealConfiguration);
        Assert.Equal(before, File.ReadAllText(shared));
        Assert.False(File.Exists(Path.Combine(h.WorkFolder, ProjectFile.LocalName)));
        Assert.Equal("DebugGame", h.Services.State.ProjectTools.Get(uproject, UnrealProvider.ConfigurationKey));
    }

    // ---- Links -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Links_are_added_edited_moved_and_removed_in_their_own_file_which_keeps_its_other_keys()
    {
        await using var h = new TabTestHarness();
        var shared = Path.Combine(h.WorkFolder, ProjectFile.SharedName);
        var local = Path.Combine(h.WorkFolder, ProjectFile.LocalName);
        Write(shared, """
            {
              // The team's links and actions.
              "$schema": "https://example.com/claudette.schema.json",
              "actions": [ { "name": "Test", "command": "make test", "os": ["linux", "macos", "windows"] } ],
              "links": [ { "name": "Board", "url": "https://example.com/board", "note": "kept" } ],
            }
            """);
        Write(local, """{ "links": [ { "url": "https://mine.example" } ], "extra": 1 }""");
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => h.Shell.Links.Count == 2, "the sidebar's links");
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;

        Assert.Equal(["Board", "https://mine.example"], project.Links.Select(r => r.Name));
        Assert.Equal(["Shared (claudette.json)", "Just me (claudette.local.json)"], project.Links.Select(r => r.FileText));
        Assert.All(project.Links, r => Assert.False(r.HasWarning));

        // Add: the dialog asks which file, just the user's by default.
        project.AddLinkCommand.Execute(null);
        var editor = Assert.IsType<ProjectLinkEditorViewModel>(project.Editor);
        Assert.Equal("Add a link", editor.Title);
        Assert.True(editor.AsksForFile);
        Assert.True(editor.SaveToLocal);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Name = "Pull request";
        editor.Url = "https://github.com/org/repo/compare/{branch}?expand=1";
        Assert.Null(editor.UrlProblem);
        editor.SaveToShared = true;
        Assert.Contains("comments in it are dropped", editor.FileNote, StringComparison.Ordinal);
        editor.SaveCommand.Execute(null);

        Assert.Null(project.Editor);
        Assert.Equal(["Board", "Pull request", "https://mine.example"], project.Links.Select(r => r.Name));
        Assert.Equal("Pull request", project.SelectedLink!.Name);
        var root = Read(shared);
        Assert.Equal(["Board", "Pull request"], Names(root, "links"));
        Assert.Equal("https://github.com/org/repo/compare/{branch}?expand=1", root["links"]![1]!["url"]!.GetValue<string>());
        Assert.Equal("make test", root["actions"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(3, root["actions"]![0]!["os"]!.AsArray().Count);
        Assert.Equal("https://example.com/claudette.schema.json", root["$schema"]!.GetValue<string>());
        Assert.DoesNotContain("The team's links", File.ReadAllText(shared), StringComparison.Ordinal);
        // The sidebar picks it up, as it does after the actions editor saves.
        await TabTestHarness.Eventually(() => h.Shell.Links.Select(l => l.Name).SequenceEqual(["Board", "Pull request", "https://mine.example"]), "the new link in the sidebar");
        Assert.Equal("No git branch", InlineDispatcher.Read(() => tab.Links[1].Problem));

        // Edit: the entry's other fields are kept, and it stays in its file.
        project.SelectedLink = project.Links[0];
        project.EditLinkCommand.Execute(null);
        editor = Assert.IsType<ProjectLinkEditorViewModel>(project.Editor);
        Assert.Equal(("Edit link", false, "Board", "https://example.com/board"), (editor.Title, editor.AsksForFile, editor.Name, editor.Url));
        Assert.Equal("In claudette.json", editor.FileText);
        editor.Name = "Sprint board";
        editor.Url = "https://example.com/sprint";
        editor.SaveCommand.Execute(null);
        root = Read(shared);
        Assert.Equal(["Sprint board", "Pull request"], Names(root, "links"));
        Assert.Equal(("https://example.com/sprint", "kept"), (root["links"]![0]!["url"]!.GetValue<string>(), root["links"]![0]!["note"]!.GetValue<string>()));

        // Move: within its file. The shared file's links come first, so the local one can't move above them.
        project.SelectedLink = project.Links.Single(r => r.Name == "Pull request");
        Assert.False(project.MoveLinkDownCommand.CanExecute(null));
        project.MoveLinkUpCommand.Execute(null);
        Assert.Equal(["Pull request", "Sprint board"], Names(Read(shared), "links"));
        project.SelectedLink = project.Links.Single(r => r.Scope == ProjectFileScope.Local);
        Assert.False(project.MoveLinkUpCommand.CanExecute(null));
        Assert.False(project.MoveLinkDownCommand.CanExecute(null));

        // Remove: the local file keeps its other keys, and a link without a name stayed without one until then.
        Assert.Null(Read(local)["links"]![0]!["name"]);
        project.RemoveLinkCommand.Execute(null);
        root = Read(local);
        Assert.Empty(root["links"]!.AsArray());
        Assert.Equal(1, root["extra"]!.GetValue<int>());
        Assert.Null(project.LinksError);
        await TabTestHarness.Eventually(() => h.Shell.Links.Select(l => l.Name).SequenceEqual(["Pull request", "Sprint board"]), "the sidebar after the changes");
    }

    [Theory]
    [InlineData("file:///etc/hosts", "\"file:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("javascript:alert(1)", "\"javascript:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("vscode://file/x", "\"vscode:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("example.com/board", "example.com/board isn't a full web address, such as https://example.com.")]
    [InlineData("https://example.com/{ticket}", "Unknown placeholder {ticket}: use {branch}, {changelist} or {folderName}")]
    [InlineData("https://github.com/org/repo/compare/{branch}?expand=1", null)]
    [InlineData("https://swarm.example/changes/{changelist}", null)]
    [InlineData("http://docs.example/{folderName}/", null)]
    [InlineData("mailto:team@example.com", null)]
    public async Task A_links_address_is_checked_as_the_sidebar_opens_links(string url, string? problem)
    {
        await using var h = new TabTestHarness();
        await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;
        project.AddLinkCommand.Execute(null);
        var editor = Assert.IsType<ProjectLinkEditorViewModel>(project.Editor);
        Assert.Equal(ProjectLinks.PlaceholderHint, editor.PlaceholderHint);

        editor.Url = url;

        Assert.Equal(problem, editor.UrlProblem);
        Assert.Equal(problem is null, editor.SaveCommand.CanExecute(null));
        editor.CancelCommand.Execute(null);
        Assert.Null(project.Editor);
        Assert.False(File.Exists(Path.Combine(h.WorkFolder, ProjectFile.LocalName)));
    }

    [Fact]
    public async Task A_link_in_a_file_that_wont_open_is_listed_with_why()
    {
        await using var h = new TabTestHarness();
        Write(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """
            { "links": [ { "name": "Hosts", "url": "file:///etc/hosts" }, { "name": "No address" }, "just text" ] }
            """);
        await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        var rows = settings.Project!.Links;

        Assert.Equal(["Hosts", "(can't be read)", "(can't be read)"], rows.Select(r => r.Name));
        Assert.Equal("\"file:\" links don't open from Claudette; only https, http and mailto do.", rows[0].Warning);
        Assert.Equal(["links[1] has no url, so it was skipped.", "links[2] isn't an object, so it was skipped."], rows.Skip(1).Select(r => r.Warning));
        // One that can't be read can only be removed; removing it keeps the others as they were.
        settings.Project.SelectedLink = rows[2];
        Assert.False(settings.Project.EditLinkCommand.CanExecute(null));
        settings.Project.RemoveLinkCommand.Execute(null);
        var root = Read(Path.Combine(h.WorkFolder, ProjectFile.SharedName));
        Assert.Equal(2, root["links"]!.AsArray().Count);
        Assert.Equal("No address", root["links"]![1]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_file_that_isnt_valid_JSON_is_never_rewritten_and_the_page_says_why()
    {
        await using var h = new TabTestHarness();
        var shared = Path.Combine(h.WorkFolder, ProjectFile.SharedName);
        var local = Path.Combine(h.WorkFolder, ProjectFile.LocalName);
        const string broken = "{ \"links\": [ { \"name\": \"Board\" ";
        Write(shared, broken);
        await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;

        var error = Assert.Single(project.LinkFileErrors);
        Assert.StartsWith("claudette.json isn't valid JSON (", error, StringComparison.Ordinal);
        Assert.EndsWith("so Claudette won't rewrite it. Fix it by hand first.", error, StringComparison.Ordinal);

        // The dialog refuses that file, with the reason, and takes the other.
        project.AddLinkCommand.Execute(null);
        var editor = Assert.IsType<ProjectLinkEditorViewModel>(project.Editor);
        editor.Url = "https://example.com";
        editor.SaveToShared = true;
        Assert.Equal(error, editor.FileProblem);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.SaveToLocal = true;
        Assert.True(editor.SaveCommand.CanExecute(null));
        editor.CancelCommand.Execute(null);

        // The Actions page won't edit it either.
        project.SelectedProjectActionFile = project.ProjectActionFiles.Single(f => f.Value == ProjectFileScope.Shared);
        Assert.False(project.CanEditProjectActionFile);
        Assert.Equal(error, project.ProjectActionFileError);
        Assert.False(project.AddProjectActionCommand.CanExecute(null));

        // A file that stops being JSON while the page is open is refused when saving, and the page shows it as it is.
        Write(local, "not json");
        project.AddLinkCommand.Execute(null);
        editor = Assert.IsType<ProjectLinkEditorViewModel>(project.Editor);
        editor.Url = "https://example.com";
        editor.SaveCommand.Execute(null);
        Assert.StartsWith("claudette.local.json isn't valid JSON (", project.LinksError, StringComparison.Ordinal);
        Assert.Empty(project.Links);
        Assert.Equal(2, project.LinkFileErrors.Count);

        Assert.Equal(broken, File.ReadAllText(shared));
        Assert.Equal("not json", File.ReadAllText(local));
    }

    // ---- Actions ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_Actions_page_edits_either_files_actions_and_keeps_what_it_doesnt_edit()
    {
        await using var h = new TabTestHarness();
        var local = Path.Combine(h.WorkFolder, ProjectFile.LocalName);
        Write(local, """
            {
              "actions": [
                { "name": "First", "command": "one", "os": ["windows", "macos", "linux"] },
                { "name": "Elsewhere", "command": "only there", "os": ["plan9"] },
              ],
              "links": [ { "name": "Mine", "url": "https://mine.example" } ],
            }
            """);
        var tab = await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        settings.SelectedProjectPage = SettingsViewModel.ActionsPage;
        var project = settings.Project!;
        Assert.True(settings.IsActionsPage);
        Assert.Equal(ProjectFileScope.Local, project.SelectedProjectActionFile.Value);
        Assert.Equal(["First", "Elsewhere"], project.ProjectActions.Select(r => r.Name));
        Assert.Contains("only on plan9", project.ProjectActions[1].Detail, StringComparison.Ordinal);
        Assert.Contains("comments in it are dropped", project.ProjectActionsNote, StringComparison.Ordinal);

        // The same dialog as before, without asking for the file: the page has chosen it.
        project.AddProjectActionCommand.Execute(null);
        var editor = Assert.IsType<ProjectActionEditorViewModel>(project.Editor);
        Assert.False(editor.AsksForFile);
        editor.Name = "Second";
        editor.Command = "two";
        editor.WorkingFolder = "sub";
        editor.SaveCommand.Execute(null);
        Assert.Null(project.Editor);
        // Saved at once, as the rest of Settings is.
        Assert.Contains("Second", File.ReadAllText(local), StringComparison.Ordinal);
        project.MoveProjectActionUpCommand.Execute(null);
        project.SelectedProjectAction = project.ProjectActions.Single(r => r.Name == "First");
        project.EditProjectActionCommand.Execute(null);
        editor = Assert.IsType<ProjectActionEditorViewModel>(project.Editor);
        Assert.Equal("Edit action", editor.Title);
        editor.Name = "First, renamed";
        editor.SaveCommand.Execute(null);
        Assert.Equal(["First, renamed", "Second", "Elsewhere"], project.ProjectActions.Select(r => r.Name));

        var root = Read(local);
        Assert.Equal(["First, renamed", "Second", "Elsewhere"], Names(root, "actions"));
        Assert.Equal(3, root["actions"]![0]!["os"]!.AsArray().Count);
        Assert.Equal("plan9", root["actions"]![2]!["os"]![0]!.GetValue<string>());
        Assert.Equal("sub", root["actions"]![1]!["folder"]!.GetValue<string>());
        Assert.Equal("https://mine.example", root["links"]![0]!["url"]!.GetValue<string>());
        // Each save reloads the tab; it ends up with the last one.
        await TabTestHarness.Eventually(() => tab.ProjectActions.Select(a => a.Label).SequenceEqual(["First, renamed", "Second"]), "the tab's actions");
        Assert.False(File.Exists(Path.Combine(h.WorkFolder, ProjectFile.SharedName)));

        project.SelectedProjectAction = project.ProjectActions.Single(r => r.Name == "Elsewhere");
        project.RemoveProjectActionCommand.Execute(null);
        Assert.Equal(["First, renamed", "Second"], Names(Read(local), "actions"));
        await project.OpenProjectActionFileCommand.ExecuteAsync(null);
        Assert.Equal([local], h.Platform.OpenedFiles);
    }

    [Fact]
    public async Task Add_an_action_from_the_menu_opens_Settings_on_the_Actions_page_with_the_dialog_that_asks_for_the_file()
    {
        await using var h = new TabTestHarness();
        Write(Path.Combine(h.WorkFolder, ProjectFile.SharedName), """{ "links": [ { "name": "Board", "url": "https://example.com/board" } ] }""");
        var tab = await h.OpenTabAsync();
        var sibling = new TabViewModel(h.Services, h.Shell, new TabState { Folder = h.WorkFolder }, isRestored: false);
        await sibling.RefreshProjectAsync();
        h.Shell.Groups.Single().Tabs.Add(sibling);
        // From the menu of a tab that isn't selected: Settings follows the selected tab, so it's selected first.
        h.Shell.SelectedTab = AddUnstartedTab(h, "elsewhere");
        SettingsOpening? opening = null;
        h.Shell.ShowSettingsWindow = o =>
        {
            opening = o;
            return Task.CompletedTask;
        };

        await tab.AddProjectActionCommand.ExecuteAsync(null);

        Assert.Same(tab, h.Shell.SelectedTab);
        Assert.NotNull(opening);
        Assert.Equal((SettingsViewModel.ActionsPage, true), (opening.Category, opening.StartNewAction));
        Assert.Same(tab, opening.Project!.Tab);
        using var settings = new SettingsViewModel(h.Services, null, opening: opening);
        Assert.True(settings.IsActionsPage);
        var editor = Assert.IsType<ProjectActionEditorViewModel>(settings.Project!.Editor);
        Assert.Equal("Add an action", editor.Title);
        Assert.True(editor.AsksForFile);
        Assert.True(editor.SaveToLocal);
        Assert.False(editor.SaveCommand.CanExecute(null));
        editor.Name = "Serve";
        editor.Command = "npm run dev";
        editor.LaunchAndForget = true;
        editor.SaveToShared = true;
        Assert.Contains("comments in it are dropped", editor.FileNote, StringComparison.Ordinal);
        editor.SaveCommand.Execute(null);

        Assert.Null(settings.Project.Editor);
        Assert.Equal(ProjectFileScope.Shared, settings.Project.SelectedProjectActionFile.Value);
        Assert.Equal("Serve", settings.Project.SelectedProjectAction!.Name);
        var written = Read(Path.Combine(h.WorkFolder, ProjectFile.SharedName));
        Assert.Equal("https://example.com/board", written["links"]![0]!["url"]!.GetValue<string>());
        Assert.Equal(("Serve", "npm run dev", "launch"), (written["actions"]![0]!["name"]!.GetValue<string>(), written["actions"]![0]!["command"]!.GetValue<string>(), written["actions"]![0]!["mode"]!.GetValue<string>()));
        await TabTestHarness.Eventually(() => tab.ProjectButtonText == "Actions" && sibling.ProjectActions.Count == 1, "both tabs");
        Assert.Equal(ProjectActionKind.Launch, sibling.ProjectActions.Single().Kind);
    }

    [Fact]
    public async Task Tab_settings_says_where_the_actions_went_and_opens_that_page()
    {
        var (h, _, _) = ProjectToolsTests.UnrealHarness();
        await using var _h = h;
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        SettingsOpening? opening = null;
        h.Shell.ShowSettingsWindow = o =>
        {
            opening = o;
            return Task.CompletedTask;
        };
        h.Shell.OpenTabSettingsCommand.Execute(tab);
        var tabSettings = h.Shell.TabSettings!;

        Assert.Equal("Project actions are in Settings → NightOwl → Actions.", tabSettings.ProjectActionsText);
        await tabSettings.OpenProjectActionsCommand.ExecuteAsync(null);

        Assert.Equal((SettingsViewModel.ActionsPage, false), (opening!.Category, opening.StartNewAction));
        Assert.Same(tab, opening.Project!.Tab);
        // Tab settings stays open behind the window, with nothing applied yet.
        Assert.Same(tabSettings, h.Shell.TabSettings);
        opening.Project.Dispose();
    }

    // ---- Tools -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_Tools_page_changes_the_Unreal_configuration_and_engine_folder_the_menu_uses()
    {
        var (h, _, uproject) = ProjectToolsTests.UnrealHarness();
        await using var _h = h;
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;

        Assert.True(project.HasToolsProject);
        Assert.Equal("NightOwl · Unreal Engine", project.ToolsTitle);
        Assert.Equal($"Remembered on this machine for {uproject}.", project.ToolsKeyText);
        Assert.False(project.HasSeveralProjects);
        Assert.Equal("Editor configuration", project.ToolsChoiceLabel);
        Assert.Equal(["Development", "DebugGame"], project.ToolsChoiceOptions.Select(o => o.Label));
        Assert.Equal("Development", project.SelectedToolsChoice!.Value);

        project.SelectedToolsChoice = project.ToolsChoiceOptions.Single(o => o.Value == "DebugGame");

        Assert.Equal("DebugGame", h.Services.State.ProjectTools.Get(uproject, UnrealProvider.ConfigurationKey));
        await TabTestHarness.Eventually(() => project.SelectedToolsChoice?.Value == "DebugGame", "the configuration");
        Assert.True(InlineDispatcher.Read(() => tab.ProjectMenu.Single(e => e.Label == "DebugGame").IsChecked));
        Assert.Equal("Launch editor (DebugGame)", InlineDispatcher.Read(() => tab.ProjectActions[0].Label));

        // The engine folder: found in a parent folder until one is chosen.
        Assert.Equal(("Engine folder", h.Root, "Found automatically."), (project.ToolsFixName, project.ToolsFixCurrent, project.ToolsFixSource));
        Assert.False(project.ClearToolsFixCommand.CanExecute(null));
        h.Platform.FolderToPick = h.WorkFolder;
        await project.ChooseToolsFixCommand.ExecuteAsync(null);
        Assert.Contains("isn't an Unreal Engine folder", project.ToolsFixError, StringComparison.Ordinal);
        Assert.Null(h.Services.State.ProjectTools.Get(uproject, UnrealEngineLocator.EngineKey));

        var engine = Path.Combine(h.Root, "Engines", "UE_5.5");
        UnrealFixture.Write(engine, Path.Combine(h.Root, "unused"));
        h.Platform.FolderToPick = engine;
        await project.ChooseToolsFixCommand.ExecuteAsync(null);

        Assert.Null(project.ToolsFixError);
        Assert.Equal(engine, h.Services.State.ProjectTools.Get(uproject, UnrealEngineLocator.EngineKey));
        await TabTestHarness.Eventually(() => project.ToolsFixCurrent == engine, "the chosen engine");
        Assert.StartsWith("Chosen by you.", InlineDispatcher.Read(() => project.ToolsFixSource), StringComparison.Ordinal);
        Assert.Contains("chosen by you", InlineDispatcher.Read(() => tab.ProjectHeaderLines[0]), StringComparison.Ordinal);
        Assert.True(project.ClearToolsFixCommand.CanExecute(null));

        await project.ClearToolsFixCommand.ExecuteAsync(null);

        Assert.Null(h.Services.State.ProjectTools.Get(uproject, UnrealEngineLocator.EngineKey));
        await TabTestHarness.Eventually(() => project.ToolsFixCurrent == h.Root, "the engine found again");
        Assert.Equal("Found automatically.", project.ToolsFixSource);
        Assert.False(project.ClearToolsFixCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_folder_with_several_projects_picks_one_on_the_Tools_page()
    {
        await using var h = new TabTestHarness(launcher: new FakeLauncher());
        UnrealFixture.Write(h.Root, Path.Combine(h.WorkFolder, "Alpha"));
        var alpha = Path.Combine(h.WorkFolder, "Alpha", "Alpha.uproject");
        File.Move(Path.Combine(h.WorkFolder, "Alpha", "NightOwl.uproject"), alpha);
        File.WriteAllText(Path.Combine(h.WorkFolder, "Beta.uproject"), """{ "EngineAssociation": "" }""");
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;

        Assert.True(project.HasSeveralProjects);
        Assert.Equal(["Beta (Beta.uproject)", $"Alpha ({Path.Combine("Alpha", "Alpha.uproject")})"], project.ProjectChoices.Select(c => c.Label));
        Assert.Equal("Beta", project.Heading);

        project.SelectedProjectChoice = project.ProjectChoices[1];

        Assert.Equal(alpha, h.Services.State.ProjectTools.ChosenProjectFor(h.WorkFolder));
        await TabTestHarness.Eventually(() => project.Heading == "Alpha", "the other project");
        Assert.True(InlineDispatcher.Read(() => project.SelectedProjectChoice?.Label.StartsWith("Alpha", StringComparison.Ordinal) == true));
        Assert.True(InlineDispatcher.Read(() => tab.ProjectMenu.Single(e => e.Label == "Alpha").IsChecked));
    }

    [Fact]
    public async Task A_Unity_project_has_its_editor_and_code_optimization_on_the_Tools_page()
    {
        await using var h = new TabTestHarness(launcher: new FakeLauncher());
        h.Services.ProjectTools.OS = ToolOSExtensions.Current == ToolOS.Windows ? ToolOS.Windows : ToolOS.Linux;
        h.Services.ProjectTools.Paths = new ProjectToolPaths(Path.Combine(h.Root, "home"), ProgramFiles: Path.Combine(h.Root, "programs"));
        Write(Path.Combine(h.WorkFolder, "ProjectSettings", "ProjectVersion.txt"), "m_EditorVersion: 2022.3.20f1\n");
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "Assets"));
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;
        var root = tab.Project!.ProjectPath;

        Assert.Equal(("Code optimization", "Release"), (project.ToolsChoiceLabel, project.SelectedToolsChoice!.Value));
        Assert.Equal("Unity editor", project.ToolsFixName);
        Assert.Equal(tab.Project.Problem, project.ToolsFixCurrent);
        Assert.StartsWith("Not chosen", project.ToolsFixSource, StringComparison.Ordinal);

        project.SelectedToolsChoice = project.ToolsChoiceOptions.Single(o => o.Value == "Debug");
        Assert.Equal("Debug", h.Services.State.ProjectTools.Get(root, UnityProvider.OptimizationKey));

        var editor = Path.Combine(h.Root, "Editors", "2022.3.20f1", OperatingSystem.IsWindows() ? "Unity.exe" : "Unity");
        Write(editor, "");
        h.Platform.FileToPick = editor;
        await project.ChooseToolsFixCommand.ExecuteAsync(null);

        Assert.Equal(editor, h.Services.State.ProjectTools.Get(root, UnityEditors.EditorKey));
        await TabTestHarness.Eventually(() => project.ToolsFixCurrent == editor, "the chosen editor");
        await TabTestHarness.Eventually(() => project.SelectedToolsChoice?.Value == "Debug", "the optimization");
        Assert.Equal("Open in Unity (Debug)", InlineDispatcher.Read(() => tab.ProjectActions[0].Label));

        await project.ClearToolsFixCommand.ExecuteAsync(null);
        Assert.Null(h.Services.State.ProjectTools.Get(root, UnityEditors.EditorKey));
        await TabTestHarness.Eventually(() => project.ToolsFixCurrent == tab.Project?.Problem, "the editor forgotten");
    }

    [Fact]
    public async Task A_Godot_project_has_its_executable_on_the_Tools_page_and_no_other_choice()
    {
        await using var h = new TabTestHarness(launcher: new FakeLauncher());
        Write(Path.Combine(h.WorkFolder, "project.godot"), """
            config_version=5

            [application]
            config/name="Night Owl"
            config/features=PackedStringArray("4.3", "Forward Plus")
            """);
        var tab = await h.OpenTabAsync();
        await TabTestHarness.Eventually(() => tab.Project is not null, "the project");
        using var settings = await OpenSettingsAsync(h);
        var project = settings.Project!;
        var file = tab.Project!.ProjectPath;

        Assert.Equal("Night Owl", project.Heading);
        Assert.False(project.HasToolsChoice);
        Assert.Equal("Godot executable", project.ToolsFixName);

        h.Platform.FileToPick = Path.Combine(h.Root, "not-it.txt");
        Write(h.Platform.FileToPick, "");
        await project.ChooseToolsFixCommand.ExecuteAsync(null);
        Assert.Contains("doesn't look like Godot", project.ToolsFixError, StringComparison.Ordinal);

        var godot = Path.Combine(h.Root, "tools", OperatingSystem.IsWindows() ? "Godot_v4.3-stable_win64.exe" : "godot4");
        Write(godot, "");
        h.Platform.FileToPick = godot;
        await project.ChooseToolsFixCommand.ExecuteAsync(null);

        Assert.Null(project.ToolsFixError);
        Assert.Equal(godot, h.Services.State.ProjectTools.Get(file, Core.ProjectTools.Godot.GodotExecutables.ExecutableKey));
        await TabTestHarness.Eventually(() => project.ToolsFixCurrent == godot, "the chosen Godot");

        await project.ClearToolsFixCommand.ExecuteAsync(null);
        Assert.Null(h.Services.State.ProjectTools.Get(file, Core.ProjectTools.Godot.GodotExecutables.ExecutableKey));
    }

    [Fact]
    public async Task Without_a_project_the_Tools_page_says_so_and_points_to_the_defaults()
    {
        await using var h = new TabTestHarness();
        await h.OpenTabAsync();
        using var settings = await OpenSettingsAsync(h);
        settings.SelectedProjectPage = SettingsViewModel.ToolsPage;

        Assert.False(settings.Project!.HasToolsProject);
        Assert.False(settings.Project.HasToolsFix);
        Assert.Empty(settings.Project.ToolsSearchLabels);

        settings.ShowProjectToolsDefaultsCommand.Execute(null);

        Assert.True(settings.IsProjectTools);
        Assert.Equal("Project tools", settings.SelectedMainCategory);
    }
}
