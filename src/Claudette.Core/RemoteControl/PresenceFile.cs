using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.RemoteControl;

/// <summary>
/// The file every <c>claude</c> Claudette starts is told about with <c>CLAUDE_CLIENT_PRESENCE_FILE</c> (DESIGN.md §10,
/// §18). Claude Code skips Remote Control's pushes to the phone while it exists, so it exists only while Claudette's
/// window is in front: the phone buzzes when you're away from Claudette, not while you're looking at it.
/// </summary>
public sealed class PresenceFile : IDisposable
{
    /// <summary>Documented: "notifications are skipped while the file exists".</summary>
    public const string Variable = "CLAUDE_CLIENT_PRESENCE_FILE";

    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    /// <summary>Removes a file left by a Claudette that didn't close cleanly: nobody is looking at it now.</summary>
    public PresenceFile(string path, ILogger? logger = null)
    {
        Path = path;
        _logger = logger ?? NullLogger.Instance;
        Write(false);
    }

    public string Path { get; }

    /// <summary>Whether the file is there now.</summary>
    public bool IsPresent { get; private set; }

    /// <summary>Creates the file while the user is at Claudette, and deletes it when they aren't.</summary>
    public void SetPresent(bool present)
    {
        lock (_lock)
        {
            if (present != IsPresent)
            {
                Write(present);
            }
        }
    }

    /// <summary>At exit: nobody is at Claudette any more.</summary>
    public void Dispose() => SetPresent(false);

    private void Write(bool present)
    {
        try
        {
            if (present)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(Path, "");
            }
            else
            {
                File.Delete(Path);
            }
            IsPresent = present;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // At worst the phone buzzes while the user is at Claudette, or doesn't while they're away.
            _logger.LogWarning(ex, "Couldn't {Action} the presence file.", present ? "create" : "delete");
        }
    }
}
