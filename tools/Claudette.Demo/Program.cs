// claudette-demo: demo content for trying Claudette's UI and for its screenshots, with no account and no tokens.
//
//   claudette-demo <folder> [--theme system|light|dark] [--style standard|claude] [--projects <folder>]
//
// Makes, in <folder> (replacing what an earlier run made there):
//   home/           for CLAUDETTE_HOME: settings with fake-claude as Claude Code and the theme and style asked for,
//                   seven tabs over three game projects (Unreal in C++, Unity in C#, Godot): the first with a long
//                   conversation and changed files, one with every file reviewed, and a thread with two sub-threads,
//                   a plan, a task list part-way done and three subagents; and a plan usage history that ends now
//   claude-config/  for CLAUDE_CONFIG_DIR: the tabs' transcripts, with the subagents' beside them
//   usage.json      for FAKE_CLAUDE_USAGE: the plan usage fake-claude reports, where the history ends
//   projects/       three git repositories, with Claude's changes in their working trees. --projects puts them
//                   elsewhere: the conversation shows their paths, so a short one such as C:\Demo reads better.
// then prints the environment to run Claudette with. A folder it would replace must be empty or one it made.
//
// tools/Claudette.Demo/screenshots.ps1 takes the README's screenshots with it, on Windows.
using Claudette.Core;
using Claudette.Core.Development;
using Claudette.Core.Settings;
using Claudette.Demo;

const string Usage = "usage: claudette-demo <folder> [--theme system|light|dark] [--style standard|claude] [--projects <folder>]";
string? folder = null;
string? projects = null;
var theme = ThemeChoice.Dark;
var style = AppStyle.Standard;
try
{
    var rest = new Queue<string>(args);
    while (rest.TryDequeue(out var arg))
    {
        switch (arg)
        {
            case "--theme":
                theme = Enum.Parse<ThemeChoice>(Next(rest), ignoreCase: true);
                break;
            case "--style":
                style = Enum.Parse<AppStyle>(Next(rest), ignoreCase: true);
                break;
            case "--projects":
                projects = Next(rest);
                break;
            default:
                folder = folder is null && !arg.StartsWith('-') ? arg : throw new ArgumentException($"Unexpected argument: {arg}");
                break;
        }
    }
    if (folder is null)
    {
        throw new ArgumentException("Which folder?");
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine(Usage);
    return 2;
}

folder = Path.GetFullPath(folder);
projects = Path.GetFullPath(projects ?? Path.Combine(folder, "projects"));
try
{
    // Projects inside the folder go with it.
    DemoFolder.Replace(folder);
    DemoFolder.Replace(projects);
}
catch (IOException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var home = Path.Combine(folder, "home");
var paths = AppPaths.Under(home);
var config = Path.Combine(folder, "claude-config");
var transcripts = Path.Combine(config, "projects", "demo");
var usage = Path.Combine(folder, "usage.json");
Directory.CreateDirectory(transcripts);
Directory.CreateDirectory(paths.DataDirectory);
Directory.CreateDirectory(paths.SettingsDirectory);

var tabs = DemoContent.Write(projects, transcripts);
DemoUsage.Write(paths.UsageDatabase, usage, DateTimeOffset.UtcNow, tabs);

var settings = new AppSettings();
settings.General.CheckForAppUpdates = false;
settings.General.ShowServiceStatus = false;
settings.ClaudeCode.ClaudePath = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "fake-claude.exe" : "fake-claude");
settings.ClaudeCode.CheckForUpdates = false;
settings.ClaudeCode.UseLoginShellEnvironment = false;
settings.Appearance.Theme = theme;
settings.Appearance.Style = style;
settings.Sessions.RestoreUnpinnedTabs = true;
await new JsonFileStore<AppSettings>(paths.SettingsFile).SaveAsync(settings);

await new JsonFileStore<AppState>(paths.StateFile).SaveAsync(new AppState
{
    Tabs = tabs,
    SelectedTabId = tabs[0].Id,
    SidebarWidth = 300,
    // Wide enough for the plan on the Tasks page, with Tasks beside Agents so both pages' tabs show.
    SidePanelWidth = 420,
    SidePanelPages = ["Files", "Agents", "Tasks", "Project", "Processes", "Mcp", "ScratchPad"],
    TrustedFolders = [.. tabs.Select(t => t.Folder).Distinct()],
    Window = new WindowPlacement(60, 40, 1480, 920, IsMaximized: false),
});

Console.WriteLine($"""
    Demo content in {folder}, with its projects in {projects}.
    Run Claudette with these set (the usage history ends now, so the header's projection reads best soon after):

      CLAUDETTE_HOME={home}
      CLAUDE_CONFIG_DIR={config}
      FAKE_CLAUDE_USAGE={usage}
      CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
    """);
return 0;

static string Next(Queue<string> rest) => rest.TryDequeue(out var value) ? value : throw new ArgumentException("A value is missing.");
