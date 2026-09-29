using System.Security;
using Claudette.Core.LoginItems;
using Claudette.Core.Processes;

namespace Claudette.Platform.LoginItems.Mac;

/// <summary>
/// Starting Claudette at login on macOS (DESIGN.md §9, "Starting at login"): a LaunchAgent in
/// <c>~/Library/LaunchAgents</c>, which launchd runs when the user logs in. It runs <c>open -a Claudette.app --args
/// --login</c> for the app, and a build's own program otherwise. A LaunchAgent works on macOS 12, which the app still
/// supports, and for a source build, which has no bundle; <c>SMAppService</c> needs macOS 13 and a bundle. Only file
/// work, so it's tested on every OS.
/// </summary>
public sealed class LaunchAgentLoginItems(IProcessLauncher launcher, string launchAgentsDirectory) : ILoginItems
{
    public const string Label = "com.reapazor.claudette.login";

    /// <summary>The app's bundle identifier, so Login Items names the agent after Claudette.</summary>
    private const string BundleIdentifier = "com.reapazor.claudette";

    public string AgentPath => Path.Combine(launchAgentsDirectory, Label + ".plist");

    public string? UnavailableReason => null;

    public string SystemSettingsName => "System Settings → General → Login Items";

    public IPackageStartupTask? PackageTask => null;

    /// <summary>Whether the agent is there. Login Items can also turn it off under Allow in the Background, which isn't read.</summary>
    public LoginEntryState ReadEntry() => File.Exists(AgentPath) ? LoginEntryState.Enabled : LoginEntryState.Missing;

    public void WriteEntry(ClaudetteCopy copy)
    {
        Directory.CreateDirectory(launchAgentsDirectory);
        var temp = AgentPath + ".tmp";
        File.WriteAllText(temp, Plist(Command(copy)));
        File.Move(temp, AgentPath, overwrite: true);
    }

    public void DeleteEntry()
    {
        if (File.Exists(AgentPath))
        {
            File.Delete(AgentPath);
        }
    }

    public bool Exists(ClaudetteCopy copy) => copy.ExistsOnDisk();

    public void Start(ClaudetteCopy copy)
    {
        var command = Command(copy);
        launcher.Start(new ProcessStartSpec(command.Program, command.Arguments) { Detached = true });
    }

    /// <summary>
    /// The agent: run once at login, in the user's graphical session. launchd would otherwise stop what the command
    /// starts when it exits, such as the copy a source build runs from, so the process group is left alone.
    /// </summary>
    public static string Plist(LoginCommand command)
    {
        var arguments = string.Concat(command.Arguments.Prepend(command.Program).Select(a => $"\n\t\t<string>{SecurityElement.Escape(a)}</string>"));
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>Label</key>
            	<string>{Label}</string>
            	<key>ProgramArguments</key>
            	<array>{arguments}
            	</array>
            	<key>RunAtLoad</key>
            	<true/>
            	<key>LimitLoadToSessionType</key>
            	<string>Aqua</string>
            	<key>ProcessType</key>
            	<string>Interactive</string>
            	<key>AbandonProcessGroup</key>
            	<true/>
            	<key>AssociatedBundleIdentifiers</key>
            	<array>
            		<string>{BundleIdentifier}</string>
            	</array>
            </dict>
            </plist>

            """;
    }

    private static LoginCommand Command(ClaudetteCopy copy) =>
        LoginCommand.For(copy, windows: false, LoginCommand.DotnetHost(Environment.ProcessPath))
        ?? throw new InvalidOperationException("An MSIX can't start on macOS.");
}
