using System.Text.Json.Nodes;
using Claudette.Core.ProjectTools;
using Claudette.Core.Tests.Support;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary><c>claudette.json</c> and <c>claudette.local.json</c>: reading, merging, editing and links (DESIGN.md §18).</summary>
public sealed class ProjectFileTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-projectfile");

    public void Dispose() => _temp.Dispose();

    private const string Shared = """
        {
          // Committed with the project.
          "actions": [
            { "name": "Run tests", "command": "dotnet test", "folder": "src", "mode": "output", "note": "unknown fields are fine", },
            { "name": "Open Grafana", "command": "start https://grafana", "mode": "launch", "os": ["windows"] },
            { "name": "Tail logs", "command": "tail -f log.txt", "os": "linux" },
            { "command": "no name" },
            { "name": "No command" },
            { "name": "Odd mode", "command": "x", "mode": "background" },
            "not an object",
          ],
          "links": [
            { "name": "Board", "url": "https://example.atlassian.net/jira/software/projects/ABC/boards/1" },
            { "url": "https://github.com/org/repo/compare/{branch}?expand=1" },
            { "name": "No url" },
          ],
          "somethingElse": { "kept": true },
        }
        """;

    [Fact]
    public void The_shared_file_is_read_tolerantly_and_bad_entries_are_skipped_with_a_reason()
    {
        var contents = ProjectFile.Parse(Shared, ProjectFileScope.Shared, ToolOS.Linux);

        Assert.Equal(["Run tests", "Tail logs"], contents.Actions.Select(a => a.Name));
        var tests = contents.Actions[0];
        Assert.Equal(("shared:0", ProjectFileScope.Shared, "dotnet test", "src", CustomActionMode.RunWithOutput), (tests.Id, tests.Scope, tests.Command, tests.WorkingFolder, tests.Mode));
        Assert.Equal("shared:2", contents.Actions[1].Id);
        Assert.Equal(
        [
            "claudette.json: actions[3] has no name, so it was skipped.",
            "claudette.json: actions[4] (\"No command\") has no command, so it was skipped.",
            "claudette.json: actions[5] (\"Odd mode\") has mode \"background\"; it can be \"output\" or \"launch\", so it was skipped.",
            "claudette.json: actions[6] isn't an object, so it was skipped.",
            "claudette.json: links[2] has no url, so it was skipped.",
        ], contents.Problems);
        Assert.Equal(["Board", "https://github.com/org/repo/compare/{branch}?expand=1"], contents.Links.Select(l => l.Name));
    }

    [Theory]
    [InlineData(ToolOS.Windows, new[] { "Run tests", "Open Grafana" })]
    [InlineData(ToolOS.MacOS, new[] { "Run tests" })]
    [InlineData(ToolOS.Linux, new[] { "Run tests", "Tail logs" })]
    public void An_action_whose_os_leaves_out_this_machine_isnt_shown(ToolOS os, string[] names)
    {
        Assert.Equal(names, ProjectFile.Parse(Shared, ProjectFileScope.Shared, os).Actions.Select(a => a.Name));
        Assert.Equal(LaunchMode(os), ProjectFile.Parse(Shared, ProjectFileScope.Shared, os).Actions.Any(a => a.Mode == CustomActionMode.LaunchAndForget));

        static bool LaunchMode(ToolOS os) => os == ToolOS.Windows;
    }

    [Fact]
    public void The_local_file_comes_after_the_shared_one_and_missing_files_mean_nothing()
    {
        var folder = _temp.CreateFolder("game");
        Assert.True(ProjectFile.Read(folder, ToolOS.Linux).IsEmpty);

        _temp.Write("game/claudette.json", Shared);
        _temp.Write("game/claudette.local.json", """
            { "actions": [ { "name": "My build", "command": "make mine" } ], "links": [ { "name": "My board", "url": "https://mine.example" } ] }
            """);

        var both = ProjectFile.Read(folder, ToolOS.Linux);

        Assert.Equal(["Run tests", "Tail logs", "My build"], both.Actions.Select(a => a.Name));
        Assert.Equal(("local:0", ProjectFileScope.Local), (both.Actions[2].Id, both.Actions[2].Scope));
        Assert.Equal(["Board", "https://github.com/org/repo/compare/{branch}?expand=1", "My board"], both.Links.Select(l => l.Name));
        Assert.Equal(["Run tests", "Tail logs"], both.Actions.Where(a => a.Scope == ProjectFileScope.Shared).Select(a => a.Name));
    }

    [Fact]
    public void A_file_that_isnt_JSON_is_skipped_with_a_reason_and_the_other_still_counts()
    {
        var folder = _temp.CreateFolder("game");
        _temp.Write("game/claudette.json", "{ \"actions\": [ oops");
        _temp.Write("game/claudette.local.json", """{ "actions": [ { "name": "Mine", "command": "make" } ] }""");

        var contents = ProjectFile.Read(folder, ToolOS.Linux);

        Assert.Equal(["Mine"], contents.Actions.Select(a => a.Name));
        Assert.StartsWith("claudette.json isn't valid JSON, so it was skipped:", Assert.Single(contents.Problems), StringComparison.Ordinal);
        Assert.Single(ProjectFile.Parse("[1, 2]", ProjectFileScope.Local, ToolOS.Linux).Problems);
        Assert.Single(ProjectFile.Parse("""{ "actions": { "name": "x" } }""", ProjectFileScope.Local, ToolOS.Linux).Problems);
        Assert.True(ProjectFile.Parse("", ProjectFileScope.Local, ToolOS.Linux).IsEmpty);
    }

    // ---- Editing --------------------------------------------------------------------------------------------------

    [Fact]
    public void Saving_actions_keeps_the_other_keys_and_each_entrys_own_fields()
    {
        var folder = _temp.CreateFolder("game");
        var path = _temp.Write("game/claudette.json", Shared);
        var entries = ProjectFile.ReadEntries(folder, ProjectFileScope.Shared, ToolOS.MacOS);
        Assert.Equal(7, entries.Count);
        Assert.False(entries[1].ForThisOS);
        Assert.NotNull(entries[3].Problem);

        var edited = entries[0].Action!.Clone();
        edited.Name = "Run all tests";
        edited.Command = "dotnet test && echo <done>";
        edited.WorkingFolder = null;
        ProjectFile.WriteActions(folder, ProjectFileScope.Shared,
            [ProjectFile.ToJson(edited, entries[0].Raw), entries[1].Raw, ProjectFile.ToJson(new CustomProjectAction { Name = "Serve", Command = "./serve", Mode = CustomActionMode.LaunchAndForget })]);

        var text = File.ReadAllText(path);
        var root = JsonNode.Parse(text)!.AsObject();
        Assert.Equal(["actions", "links", "somethingElse"], root.Select(p => p.Key));
        Assert.Contains("\n  \"actions\": [", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("dotnet test && echo <done>", text, StringComparison.Ordinal);
        var first = root["actions"]![0]!.AsObject();
        Assert.Equal("unknown fields are fine", first["note"]!.GetValue<string>());
        Assert.Null(first["folder"]);
        Assert.Equal("windows", root["actions"]![1]!["os"]![0]!.GetValue<string>());
        Assert.Equal("launch", root["actions"]![2]!["mode"]!.GetValue<string>());
        Assert.True(root["somethingElse"]!["kept"]!.GetValue<bool>());
        Assert.Equal(["Run all tests", "Serve"], ProjectFile.Read(folder, ToolOS.MacOS).Actions.Select(a => a.Name));
    }

    [Fact]
    public void A_missing_file_is_created_and_one_that_isnt_JSON_is_never_overwritten()
    {
        var folder = _temp.CreateFolder("game");

        ProjectFile.WriteActions(folder, ProjectFileScope.Local, [ProjectFile.ToJson(new CustomProjectAction { Name = "Mine", Command = "make", WorkingFolder = "src" })]);
        Assert.Equal(("Mine", "src"), (ProjectFile.Read(folder, ToolOS.Linux).Actions.Single().Name, ProjectFile.Read(folder, ToolOS.Linux).Actions.Single().WorkingFolder));

        var broken = _temp.Write("game/claudette.json", "{ broken");
        Assert.Throws<InvalidOperationException>(() => ProjectFile.WriteActions(folder, ProjectFileScope.Shared, []));
        Assert.Throws<InvalidOperationException>(() => ProjectFile.ReadEntries(folder, ProjectFileScope.Shared, ToolOS.Linux));
        Assert.Equal("{ broken", File.ReadAllText(broken));
    }

    // ---- Links ----------------------------------------------------------------------------------------------------------

    private static readonly LinkValues Values = new("feature/owl eyes", "12345", "Night Owl");

    [Fact]
    public void Placeholders_are_filled_and_escaped()
    {
        var link = ProjectLinks.Resolve(new ProjectLink("PR", "https://github.com/org/repo/compare/{branch}?expand=1&cl={changelist}&f={folderName}", ProjectFileScope.Shared), Values);

        Assert.Equal("https://github.com/org/repo/compare/feature%2Fowl%20eyes?expand=1&cl=12345&f=Night%20Owl", link.Url);
        Assert.True(link.IsEnabled);
        Assert.Equal(link.Url, link.Tip);
        Assert.Equal("mailto:team@example.com?subject=Night%20Owl", ProjectLinks.Resolve(new ProjectLink("Mail", "mailto:team@example.com?subject={FolderName}", ProjectFileScope.Local), Values).Url);
    }

    [Theory]
    [InlineData("file:///etc/passwd", "\"file:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("javascript:alert(1)", "\"javascript:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("vscode://file/{folderName}", "\"vscode:\" links don't open from Claudette; only https, http and mailto do.")]
    [InlineData("example.com/board", "example.com/board isn't a full web address, such as https://example.com.")]
    [InlineData("https://example.com/{user}", "Unknown placeholder {user}: use {branch}, {changelist} or {folderName}")]
    public void Only_https_http_and_mailto_links_open(string url, string reason)
    {
        var link = ProjectLinks.Resolve(new ProjectLink("Link", url, ProjectFileScope.Shared), Values);

        Assert.False(link.IsEnabled);
        Assert.Null(link.Url);
        Assert.Equal(reason, link.Problem);
    }

    [Fact]
    public void A_placeholder_that_cant_be_filled_disables_the_link_and_says_why()
    {
        var none = new LinkValues(null, null, "game");

        Assert.Equal("No git branch", ProjectLinks.Resolve(new ProjectLink("PR", "https://github.com/o/r/compare/{branch}", ProjectFileScope.Shared), none).Problem);
        Assert.Equal("No Perforce changelist", ProjectLinks.Resolve(new ProjectLink("Swarm", "https://swarm/changes/{changelist}", ProjectFileScope.Shared), none).Problem);
        Assert.True(ProjectLinks.Resolve(new ProjectLink("Board", "https://example.com/{folderName}", ProjectFileScope.Shared), none).IsEnabled);
    }
}
