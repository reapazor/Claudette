using Claudette.Core.Logging;
using Claudette.Core.Tests.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.Core.Tests.Logging;

/// <summary>Claudette's own log, <c>claudette.log</c> (DESIGN.md §13, "Logging").</summary>
public sealed class FileLoggerFactoryTests : IDisposable
{
    private readonly TempFolder _temp = new("claudette-log");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Warnings_and_errors_are_written_with_their_exceptions()
    {
        using var loggers = new FileLoggerFactory(_temp.Combine("logs"), _time);
        var logger = loggers.CreateLogger("Tab");

        logger.LogInformation("Not kept.");
        logger.LogWarning("Couldn't read {Path}.", "state.json");
        logger.LogError(new InvalidOperationException("boom\nsecond line"), "Failed.");

        var lines = File.ReadAllLines(loggers.Path);
        Assert.Equal("2026-10-01 09:30:00.000Z WARN  Tab: Couldn't read state.json.", lines[0]);
        Assert.Equal("2026-10-01 09:30:00.000Z ERROR Tab: Failed.", lines[1]);
        Assert.StartsWith("    System.InvalidOperationException: boom", lines[2], StringComparison.Ordinal);
        Assert.Equal("    second line", lines[3]);
        Assert.DoesNotContain(lines, l => l.Contains("Not kept", StringComparison.Ordinal));
    }

    [Fact]
    public void A_full_log_is_kept_as_the_previous_one()
    {
        var folder = _temp.Combine("logs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileLoggerFactory.FileName), new string('x', (int)FileLoggerFactory.MaxBytes + 1));
        using var loggers = new FileLoggerFactory(folder, _time);

        loggers.CreateLogger("App").LogWarning("Fresh.");

        Assert.Equal(FileLoggerFactory.MaxBytes + 1, new FileInfo(Path.Combine(folder, FileLoggerFactory.PreviousFileName)).Length);
        Assert.EndsWith("App: Fresh.", File.ReadAllText(loggers.Path).TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_log_that_cant_be_written_is_skipped()
    {
        // The "folder" is a file, so nothing can be written under it.
        var blocked = _temp.Combine("not-a-folder");
        File.WriteAllText(blocked, "");
        using var loggers = new FileLoggerFactory(blocked, _time);

        loggers.CreateLogger("App").LogError("Nowhere to go.");
    }
}
