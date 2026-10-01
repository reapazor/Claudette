using System.Text;
using Claudette.Core.Files;
using Claudette.Core.LoginItems;
using Claudette.Core.Processes;

namespace Claudette.Platform.LoginItems.Linux;

/// <summary>
/// Starting Claudette at login on Linux (DESIGN.md §9, "Starting at login"): <c>claudette.desktop</c> in the XDG
/// autostart folder, which desktops that follow the Desktop Application Autostart spec run at login. Only file work, so
/// it's tested on every OS.
/// </summary>
public sealed class XdgAutostartLoginItems(IProcessLauncher launcher, string autostartDirectory) : ILoginItems
{
    public const string FileName = "claudette.desktop";

    /// <summary>What the Desktop Entry spec says needs quoting in an <c>Exec</c> argument.</summary>
    private const string Reserved = " \t\n\"'\\><~|&;$*?#()`";

    public string FilePath => Path.Combine(autostartDirectory, FileName);

    public string? UnavailableReason => null;

    public string SystemSettingsName => "your desktop's startup applications";

    public IPackageStartupTask? PackageTask => null;

    /// <summary><c>$XDG_CONFIG_HOME/autostart</c>, or <c>~/.config/autostart</c> when it isn't set.</summary>
    public static string DefaultDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg)
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "autostart");
    }

    /// <summary>
    /// A file with <c>Hidden=true</c>, or GNOME's <c>X-GNOME-Autostart-enabled=false</c>, is there but turned off: that's
    /// how the desktops' startup settings turn one off.
    /// </summary>
    public LoginEntryState ReadEntry()
    {
        if (!File.Exists(FilePath))
        {
            return LoginEntryState.Missing;
        }
        foreach (var line in File.ReadLines(FilePath).Select(l => l.Replace(" ", "")))
        {
            if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase) || line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase))
            {
                return LoginEntryState.DisabledByUser;
            }
        }
        return LoginEntryState.Enabled;
    }

    public void WriteEntry(ClaudetteCopy copy)
    {
        Directory.CreateDirectory(autostartDirectory);
        AtomicFile.WriteAllText(FilePath, DesktopEntry(Command(copy)));
    }

    public void DeleteEntry()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    public bool Exists(ClaudetteCopy copy) => copy.ExistsOnDisk();

    public void Start(ClaudetteCopy copy)
    {
        var command = Command(copy);
        launcher.Start(new ProcessStartSpec(command.Program, command.Arguments) { Detached = true });
    }

    public static string DesktopEntry(LoginCommand command)
    {
        var exec = string.Join(' ', command.Arguments.Prepend(command.Program).Select(ExecArgument));
        return $"""
            [Desktop Entry]
            Type=Application
            Name=Claudette
            Comment=A native desktop app for Claude Code
            Exec={exec}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
    }

    /// <summary>
    /// An <c>Exec</c> argument as the Desktop Entry spec reads it: in double quotes when it has a reserved character, with
    /// <c>"</c>, <c>`</c>, <c>$</c> and <c>\</c> escaped inside; then every backslash doubled for the value's own
    /// escaping, and <c>%</c> written <c>%%</c> so it isn't a field code.
    /// </summary>
    public static string ExecArgument(string argument)
    {
        var quoted = argument;
        if (argument.Length == 0 || argument.IndexOfAny(Reserved.ToCharArray()) >= 0)
        {
            var builder = new StringBuilder("\"");
            foreach (var c in argument)
            {
                if (c is '"' or '`' or '$' or '\\')
                {
                    builder.Append('\\');
                }
                builder.Append(c);
            }
            quoted = builder.Append('"').ToString();
        }
        return quoted.Replace("\\", "\\\\").Replace("%", "%%");
    }

    private static LoginCommand Command(ClaudetteCopy copy) =>
        LoginCommand.For(copy, windows: false, LoginCommand.DotnetHost(Environment.ProcessPath))
        ?? throw new InvalidOperationException("An MSIX can't start on Linux.");
}
