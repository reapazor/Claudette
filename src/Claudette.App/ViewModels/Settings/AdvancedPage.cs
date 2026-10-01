using System.Runtime.InteropServices;
using Claudette.Core.Installation;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// Settings → Advanced (DESIGN.md §14): extra arguments for <c>claude</c>, protocol logging (DESIGN.md §13, "Logging"),
/// the data folder, and Diagnostics (DESIGN.md §16, "Staying tolerant at runtime").
/// </summary>
/// <param name="claudeCode">Claude Code, whose Claude app settings the copied diagnostics report.</param>
public sealed partial class AdvancedPage(SettingsContext context, ClaudeCodePage claudeCode) : SettingsPage(context, SettingsCategory.Advanced)
{
    public override IEnumerable<SettingsSearchResult> SearchEntries =>
    [
        Entry("Extra arguments for every claude process"),
        Entry("Log protocol traffic"),
        Entry("Open log folder"),
        Entry("Diagnostics"),
        Entry("Copy diagnostics"),
        Entry("Minimum supported Claude Code version"),
        Entry("Open data folder"),
    ];

    public string ExtraArguments
    {
        get => Settings.Advanced.ExtraArguments;
        set => Set(value, v => Settings.Advanced.ExtraArguments = v ?? "");
    }

    [RelayCommand]
    private Task OpenDataFolderAsync() => Services.Platform.RevealFolderAsync(Services.Paths.DataDirectory);

    /// <summary>Each session's raw protocol traffic, to the log folder (DESIGN.md §13, "Logging").</summary>
    public bool LogProtocol
    {
        get => Settings.Advanced.LogProtocol;
        set => Set(value, v => Settings.Advanced.LogProtocol = v);
    }

    [RelayCommand]
    private Task OpenLogFolderAsync()
    {
        Directory.CreateDirectory(Services.Paths.ProtocolLogDirectory);
        return Services.Platform.RevealFolderAsync(Services.Paths.ProtocolLogDirectory);
    }

    protected override void ResetSettings()
    {
        Settings.Advanced = new AdvancedSettings();
        Save();
    }

    // ---- Diagnostics (DESIGN.md §16, "Staying tolerant at runtime") -------------------------------------------------

    public string MinimumVersionText => ClaudeLocator.MinimumVersion.ToString();

    public string InstalledVersionText => Services.InstalledClaudeVersion?.ToString() ?? "Not found";

    /// <summary>Whether the login shell's environment is used, which shell and how long it took, or why not. Never values.</summary>
    public string LoginShellText => Services.UserEnvironment.Describe();

    /// <summary>Whether the computer is kept awake for tabs connected to the Claude app, or why not (DESIGN.md §18).</summary>
    public string KeepAwakeText => Services.RemoteControl.DescribeKeepAwake();

    /// <summary>What Claude Code has sent this run that Claudette doesn't know, in words.</summary>
    public string DiagnosticsText => DiagnosticsReport(includeHeader: false);

    /// <summary><b>Refresh</b>: what the page shows, as it is now.</summary>
    [RelayCommand]
    private void RefreshDiagnostics()
    {
        OnPropertyChanged(nameof(DiagnosticsText));
        OnPropertyChanged(nameof(InstalledVersionText));
        OnPropertyChanged(nameof(LoginShellText));
        OnPropertyChanged(nameof(KeepAwakeText));
    }

    [RelayCommand]
    private Task CopyDiagnosticsAsync() => Services.Platform.SetClipboardTextAsync(DiagnosticsReport(includeHeader: true));

    /// <summary>The Diagnostics page's contents, and with the header, the report copied for a bug report.</summary>
    internal string DiagnosticsReport(bool includeHeader)
    {
        var snapshot = Services.Diagnostics.Snapshot();
        var lines = new List<string>();
        if (includeHeader)
        {
            lines.Add($"Claudette {Services.Build.Description}");
            lines.Add($"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})");
            lines.Add($"Claude Code: {InstalledVersionText}{(Services.Install is { } install ? $" at {install.Path}" : "")}");
            lines.Add($"Minimum supported Claude Code: {MinimumVersionText}");
            lines.Add($"Login shell environment: {LoginShellText}");
            lines.Add($"Claude app (Remote Control): new tabs {(claudeCode.ConnectNewTabsToClaudeApp ? "connect" : "don't connect")}; "
                + (claudeCode.RemoteControlUnavailableText ?? "available for this account") + ".");
            lines.Add($"Keeping the computer awake: {KeepAwakeText}");
            lines.Add($"Protocol logging: {(LogProtocol ? "on" : "off")}");
            lines.Add("");
        }
        lines.Add(snapshot.UnknownMessageCount == 0
            ? "Unknown message types: none"
            : $"Unknown message types ({snapshot.UnknownMessageCount} skipped): " + string.Join(", ", snapshot.UnknownMessageTypes.Select(t => $"{t.Key} ×{t.Value}")));
        lines.Add(snapshot.UnknownFields.Count == 0
            ? "New fields: none"
            : "New fields: " + string.Join(", ", snapshot.UnknownFields.Select(f => $"{f.Key} ×{f.Value}")));
        lines.Add($"Lines that couldn't be read: {snapshot.ParseErrors}");
        return string.Join(Environment.NewLine, lines);
    }
}
