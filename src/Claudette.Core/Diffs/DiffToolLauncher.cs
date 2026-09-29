using System.Buffers;
using System.ComponentModel;
using System.Threading.Channels;
using Claudette.Core.Processes;

namespace Claudette.Core.Diffs;

/// <summary>The diff tool chosen in Settings → Diff tool (DESIGN.md §8, §14).</summary>
/// <param name="PresetId">One of the <see cref="DiffToolPresets"/> ids, for <see cref="DiffToolKind.Preset"/>.</param>
/// <param name="CustomCommand">The command line the user typed, for <see cref="DiffToolKind.Custom"/>.</param>
public sealed record DiffToolChoice(DiffToolKind Kind, string? PresetId = null, string? CustomCommand = null)
{
    public static DiffToolChoice BuiltIn { get; } = new(DiffToolKind.BuiltIn);
}

/// <summary>A diff tool couldn't be opened. The message is written for the user.</summary>
public sealed class DiffToolException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>
/// Opens a changed file in an external diff tool (DESIGN.md §8, "External diff tool"). The "before" side is a temporary
/// read-only file; the "now" side is the real file, so edits made in the tool are saved directly.
/// </summary>
public sealed class DiffToolLauncher
{
    private const string SampleBefore = "Claudette diff tool test\nThis line is only on the left.\nThe same on both sides.\n";
    private const string SampleAfter = "Claudette diff tool test\nThis line is only on the right.\nThe same on both sides.\n";

    // cmd.exe expands these even inside quotes.
    private static readonly SearchValues<char> BatchAlwaysUnsafe = SearchValues.Create("%!\"\r\n");

    // cmd.exe treats these as operators outside quotes; .NET only quotes arguments that contain whitespace.
    private static readonly SearchValues<char> BatchUnsafeUnquoted = SearchValues.Create("&|<>^");

    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly IFileProbe _probe;
    private readonly DiffToolPlatform _platform;

    /// <param name="probe">Finds preset tools. Defaults to the real file system.</param>
    /// <param name="platform">The OS whose preset variants are used. Defaults to this one.</param>
    public DiffToolLauncher(IProcessLauncher launcher, TimeProvider timeProvider, IFileProbe? probe = null, DiffToolPlatform? platform = null)
    {
        _launcher = launcher;
        _timeProvider = timeProvider;
        _probe = probe ?? FileProbe.Instance;
        _platform = platform ?? DiffToolPresets.CurrentPlatform;
    }

    /// <summary>
    /// Writes <paramref name="beforeContent"/> to a temporary read-only file in <paramref name="tempDirectory"/> and
    /// opens it against <paramref name="afterPath"/>. Returns once the tool has started; it isn't waited for.
    /// </summary>
    /// <exception cref="DiffToolException">The preset isn't installed, the custom command can't be parsed, the built-in
    /// view is selected, or the tool couldn't be started.</exception>
    public async Task LaunchAsync(DiffToolChoice choice, string? beforeContent, string afterPath, string tempDirectory, CancellationToken cancellationToken = default)
    {
        var (program, template) = Resolve(choice);
        var name = Path.GetFileName(afterPath);
        var left = await DiffTempFiles.WriteBeforeAsync(tempDirectory, name, beforeContent, _timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        var folder = Path.GetDirectoryName(afterPath);
        Start(program, template, new DiffToolSides(left, afterPath, $"{name} (before)", $"{name} (now)"),
            !string.IsNullOrEmpty(folder) && Directory.Exists(folder) ? folder : null);
    }

    /// <summary>Opens a small sample diff of two temporary files, for <b>Test</b> in Settings.</summary>
    /// <exception cref="DiffToolException">As for <see cref="LaunchAsync"/>.</exception>
    public async Task TestAsync(DiffToolChoice choice, string tempDirectory, CancellationToken cancellationToken = default)
    {
        var (program, template) = Resolve(choice);
        var folder = DiffTempFiles.CreateLaunchFolder(tempDirectory, _timeProvider.GetUtcNow());
        var left = await DiffTempFiles.WriteAsync(folder, DiffTempFiles.BeforeFileName("sample.txt"), SampleBefore, readOnly: true, cancellationToken).ConfigureAwait(false);
        var right = await DiffTempFiles.WriteAsync(folder, "sample.txt", SampleAfter, readOnly: false, cancellationToken).ConfigureAwait(false);
        Start(program, template, new DiffToolSides(left, right, "sample.txt (before)", "sample.txt (now)"), folder);
    }

    private (string Program, IReadOnlyList<string> Template) Resolve(DiffToolChoice choice)
    {
        switch (choice.Kind)
        {
            case DiffToolKind.Preset:
                var preset = DiffToolPresets.Find(choice.PresetId)
                    ?? throw new DiffToolException($"There's no diff tool preset called '{choice.PresetId}'. Choose another in Settings.");
                var variant = preset.For(_platform)
                    ?? throw new DiffToolException($"{preset.Name} isn't available on this OS. Choose another diff tool in Settings.");
                var found = DiffToolDetector.Find(_probe, _platform, preset)
                    ?? throw new DiffToolException($"{preset.Name} wasn't found. Install it, or choose another diff tool in Settings.");
                return (found.ExecutablePath, variant.Arguments);
            case DiffToolKind.Custom:
                if (!DiffToolCommand.TryParse(choice.CustomCommand, out var command, out var error))
                {
                    throw new DiffToolException($"The custom diff tool command can't be used: {error}");
                }
                return (command.Program, command.ArgumentTemplate);
            default:
                throw new DiffToolException("The built-in diff view is selected, so there's no external diff tool to open.");
        }
    }

    private void Start(string program, IReadOnlyList<string> template, DiffToolSides sides, string? workingDirectory)
    {
        var arguments = DiffToolTemplate.Substitute(template, sides);
        CheckBatchFileArguments(program, arguments);

        IRunningProcess process;
        try
        {
            process = _launcher.Start(new ProcessStartSpec(program, arguments) { WorkingDirectory = workingDirectory });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            throw new DiffToolException($"Couldn't start '{program}': {ex.Message}", ex);
        }
        process.CloseStandardInput();
        _ = ObserveAsync(process);
    }

    /// <summary>
    /// A <c>.cmd</c> or <c>.bat</c> program (such as VS Code's <c>code.cmd</c>) runs through cmd.exe, which would read
    /// some characters in a file name as commands. Such names are refused rather than passed on.
    /// </summary>
    private void CheckBatchFileArguments(string program, IReadOnlyList<string> arguments)
    {
        if (_platform != DiffToolPlatform.Windows
            || !(program.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || program.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        foreach (var argument in arguments)
        {
            var quoted = argument.Length == 0 || argument.Any(char.IsWhiteSpace);
            if (argument.AsSpan().ContainsAny(BatchAlwaysUnsafe) || (!quoted && argument.AsSpan().ContainsAny(BatchUnsafeUnquoted)))
            {
                throw new DiffToolException(
                    $"'{Path.GetFileName(program)}' runs through cmd.exe, which can't be given '{argument}' safely. Choose a diff tool that's an .exe, or rename the file.");
            }
        }
    }

    /// <summary>Waits for the tool to exit in the background, so its process gets disposed.</summary>
    private static async Task ObserveAsync(IRunningProcess process)
    {
        try
        {
            await Task.WhenAll(DrainAsync(process.StandardOutput), DrainAsync(process.StandardError), process.Exited).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing to report to: the tool runs on its own.
        }
        finally
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Already gone.
            }
        }
    }

    private static async Task DrainAsync(ChannelReader<string> reader)
    {
        await foreach (var _ in reader.ReadAllAsync().ConfigureAwait(false))
        {
        }
    }
}
