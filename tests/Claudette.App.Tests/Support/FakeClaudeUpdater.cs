using Claudette.Core.Installation;
using Claudette.Core.Processes;

namespace Claudette.App.Tests.Support;

/// <summary>Plays Claude Code's installation for update tests (DESIGN.md §12): what checks find and what updating does.</summary>
internal sealed class FakeClaudeUpdater(Version installed) : IClaudeUpdater
{
    public Version Installed { get; set; } = installed;

    /// <summary>What the package manager has, if newer.</summary>
    public Version? Available { get; set; }

    public ClaudeUpdateKind Kind { get; set; } = ClaudeUpdateKind.SelfUpdate;

    public int Checks { get; private set; }

    public List<ClaudeUpdatePlan> Updates { get; } = [];

    /// <summary>What an update prints and whether it works.</summary>
    public string[] UpdateOutput { get; set; } = ["Current version: 2.1.284", "Successfully updated from 2.1.284 to version 2.1.290"];

    public bool UpdateFails { get; set; }

    public ClaudeUpdatePlan Plan => Kind switch
    {
        ClaudeUpdateKind.Homebrew => new ClaudeUpdatePlan(Kind, "Homebrew (claude-code)", new ProcessStartSpec("brew", ["upgrade", "claude-code"])),
        ClaudeUpdateKind.WinGet => new ClaudeUpdatePlan(Kind, "WinGet", new ProcessStartSpec("winget.exe", ["upgrade", "--id", "Anthropic.ClaudeCode"])),
        ClaudeUpdateKind.Manual => new ClaudeUpdatePlan(Kind, "apt", ManualCommand: "sudo apt update && sudo apt upgrade claude-code"),
        ClaudeUpdateKind.None => new ClaudeUpdatePlan(Kind, "Native installer", Note: "Updates are turned off on this machine (DISABLE_UPDATES)."),
        _ => new ClaudeUpdatePlan(Kind, "Native installer", new ProcessStartSpec("claude", ["update"])),
    };

    public Task<ClaudeUpdateCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        Checks++;
        var doctor = new ClaudeDoctorReport { InstallType = ClaudeInstallType.Native, Version = Installed, AutoUpdates = "enabled", Channel = "latest", Warnings = [new DoctorWarning("Something is off", "Run claude install")] };
        return Task.FromResult(new ClaudeUpdateCheck(Installed, doctor, Plan, Available is { } a && a > Installed ? a : null, DateTimeOffset.Parse("2026-09-28T12:00:00Z")));
    }

    public Task<ClaudeUpdateResult> UpdateAsync(ClaudeUpdatePlan plan, Action<string>? onOutput = null, CancellationToken cancellationToken = default)
    {
        Updates.Add(plan);
        foreach (var line in UpdateOutput)
        {
            onOutput?.Invoke(line);
        }
        if (UpdateFails)
        {
            return Task.FromResult(new ClaudeUpdateResult(false, Installed, "'claude update' failed (exit 1). Error: Failed to install native update"));
        }
        Installed = Available ?? new Version(2, 1, 290);
        Available = null;
        return Task.FromResult(new ClaudeUpdateResult(true, Installed, $"Claude Code {Installed} is installed."));
    }
}
