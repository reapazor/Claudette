using System.Runtime.InteropServices;
using System.Security;
using Claudette.Core.Updates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Core.LoginItems;

/// <summary>What the machine's state keeps about starting at login (DESIGN.md §9, "Starting at login").</summary>
public sealed class LoginItemRecord
{
    /// <summary>The copy the command entry was last written to start, or null when Claudette hasn't written one.</summary>
    public ClaudetteCopy? Target { get; set; }

    /// <summary>
    /// The installed Claudette, as it noted itself when it last started: a release, or failing that another build. A
    /// source build finds the release to start through it.
    /// </summary>
    public ClaudetteCopy? Installed { get; set; }

    /// <summary>The MSIX's own startup task was on when it last looked. Only the MSIX can see or change the task.</summary>
    public bool InstalledTaskEnabled { get; set; }
}

/// <summary>Whether Claudette starts at login, as the switch in Settings → General shows it.</summary>
/// <param name="IsOn">Something starts Claudette at login.</param>
/// <param name="CanChange">The switch can turn it on or off from this Claudette.</param>
/// <param name="Starts">The copy it starts, or would start once turned on. Null when it can't start here at all.</param>
/// <param name="Note">Why it can't be changed here, in words.</param>
/// <param name="Error">What went wrong reading or changing it, in words.</param>
public sealed record LoginItemStatus(bool IsOn, bool CanChange, ClaudetteCopy? Starts, string? Note = null, string? Error = null);

/// <summary>
/// Starting Claudette when the user logs in (DESIGN.md §9, "Starting at login"): turning it on and off, which copy of
/// Claudette the entry starts, and tidying the entry up as a Claudette starts. The entry prefers an installed release,
/// then another build, then a source build (<see cref="ClaudetteCopy.Rank"/>).
/// </summary>
/// <param name="current">The Claudette that's running.</param>
/// <param name="record">The machine's state about it, changed in place.</param>
/// <param name="save">Saves the state after <paramref name="record"/> changes.</param>
public sealed class StartAtLogin(ILoginItems items, ClaudetteCopy current, LoginItemRecord record, Action save, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public ClaudetteCopy Current => current;

    /// <summary>
    /// The copy the entry should start: the best by rank of the installed Claudette (when it's still here), the copy the
    /// entry starts now, and this one. On a tie the earlier wins, so a source build doesn't take the entry from another
    /// checkout's.
    /// </summary>
    public ClaudetteCopy Preferred()
    {
        ClaudetteCopy? best = null;
        foreach (var copy in new[] { record.Installed, record.Target, current })
        {
            if (copy is null || best is not null && copy.Rank <= best.Rank)
            {
                continue;
            }
            if (copy.IsSameCopy(current))
            {
                best = current;
            }
            else if (items.Exists(copy))
            {
                best = copy;
            }
        }
        return best ?? current;
    }

    /// <summary>
    /// For a start at login: a better copy to start instead of this one (the installed release, when this is a source
    /// build), or null to start here.
    /// </summary>
    public ClaudetteCopy? HandOverAtLogin()
    {
        if (items.UnavailableReason is not null)
        {
            return null;
        }
        var preferred = Preferred();
        return preferred.Rank > current.Rank ? preferred : null;
    }

    public async Task<LoginItemStatus> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (items.UnavailableReason is { } reason)
        {
            return new LoginItemStatus(false, false, null, reason);
        }
        try
        {
            return await ReadEntriesAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogWarning(ex, "Couldn't read whether Claudette starts at login.");
            return new LoginItemStatus(false, false, null, Error: $"Couldn't tell whether Claudette starts at login: {ex.Message}");
        }
    }

    private async Task<LoginItemStatus> ReadEntriesAsync(CancellationToken cancellationToken)
    {
        var entry = items.ReadEntry();
        if (items.PackageTask is { } task)
        {
            var state = await task.GetStateAsync(cancellationToken);
            Remember(state);
            var entryOn = entry == LoginEntryState.Enabled;
            return state switch
            {
                PackageTaskState.Enabled => new LoginItemStatus(true, true, current),
                PackageTaskState.EnabledByPolicy => new LoginItemStatus(true, false, current, "Turned on by your organization's policy."),
                PackageTaskState.DisabledByPolicy => new LoginItemStatus(false, false, current, "Turned off by your organization's policy."),
                // A source build's entry still starts it, handing over to this one.
                PackageTaskState.DisabledByUser => new LoginItemStatus(entryOn, entryOn, current, entryOn ? null : TurnedOffOutside()),
                _ => new LoginItemStatus(entryOn, true, current),
            };
        }
        var preferred = Preferred();
        if (preferred.Kind == AppInstallKind.Msix && record.InstalledTaskEnabled)
        {
            // Only the MSIX can turn its startup task off.
            return new LoginItemStatus(true, false, preferred,
                $"{Capitalize(preferred.Describe())} starts at login. Turn it off in that Claudette, or in {items.SystemSettingsName}.");
        }
        return entry switch
        {
            LoginEntryState.Enabled => new LoginItemStatus(true, true, preferred),
            LoginEntryState.DisabledByUser => new LoginItemStatus(false, false, preferred, TurnedOffOutside()),
            _ => new LoginItemStatus(false, true, preferred),
        };
    }

    /// <summary>Turns starting at login on or off, and returns how it stands after.</summary>
    public async Task<LoginItemStatus> SetAsync(bool on, CancellationToken cancellationToken = default)
    {
        if (items.UnavailableReason is not null)
        {
            return await ReadAsync(cancellationToken);
        }
        try
        {
            if (items.PackageTask is { } task)
            {
                if (on)
                {
                    var state = await task.RequestEnableAsync(cancellationToken);
                    Remember(state);
                    if (IsEnabled(state))
                    {
                        DeleteEntry();
                    }
                }
                else
                {
                    await task.DisableAsync(cancellationToken);
                    Remember(PackageTaskState.Disabled);
                    DeleteEntry();
                }
            }
            else if (on)
            {
                var preferred = Preferred();
                if (preferred.Kind != AppInstallKind.Msix)
                {
                    Write(preferred);
                }
                else if (!record.InstalledTaskEnabled)
                {
                    // Only the MSIX can turn on its startup task: the entry starts this Claudette, which hands over to
                    // the MSIX at login, and the MSIX then turns its own task on.
                    Write(current);
                }
            }
            else
            {
                DeleteEntry();
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogWarning(ex, "Couldn't turn starting at login {State}.", on ? "on" : "off");
            return await ReadAsync(cancellationToken) with { Error = $"Couldn't turn it {(on ? "on" : "off")}: {ex.Message}" };
        }
        return await ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Tidies the entry as this Claudette starts, without turning starting at login on or off. An installed Claudette
    /// notes itself, for source builds to find. The MSIX takes over from an entry a source build wrote for it. An entry
    /// whose copy is gone, or that a better copy is here for, starts the copy it should.
    /// </summary>
    public async Task RefreshAtLaunchAsync(CancellationToken cancellationToken = default)
    {
        if (items.UnavailableReason is not null)
        {
            return;
        }
        try
        {
            NoteInstalled();
            var entry = items.ReadEntry();
            if (items.PackageTask is { } task)
            {
                var state = await task.GetStateAsync(cancellationToken);
                if (entry == LoginEntryState.Enabled && state == PackageTaskState.Disabled)
                {
                    state = await task.RequestEnableAsync(cancellationToken);
                }
                Remember(state);
                if (entry != LoginEntryState.Missing && IsEnabled(state))
                {
                    DeleteEntry();
                }
            }
            else if (entry == LoginEntryState.Enabled)
            {
                var preferred = Preferred();
                if (preferred.Kind != AppInstallKind.Msix)
                {
                    if (!preferred.IsSameCopy(record.Target))
                    {
                        Write(preferred);
                    }
                }
                else if (record.InstalledTaskEnabled)
                {
                    // The MSIX's own task starts it.
                    DeleteEntry();
                }
                else if (record.Target is not { } target || !items.Exists(target))
                {
                    Write(current);
                }
            }
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogWarning(ex, "Couldn't tidy up the login entry.");
        }
    }

    /// <summary>
    /// Notes this Claudette as the installed one, unless it's a source build, or a better installed copy is still here.
    /// Between two of the same kind, the one that started last wins.
    /// </summary>
    private void NoteInstalled()
    {
        if (current.Rank == 0
            || record.Installed is { } installed && !installed.IsSameCopy(current) && installed.Rank > current.Rank && items.Exists(installed)
            || record.Installed == current)
        {
            return;
        }
        if (!current.IsSameCopy(record.Installed))
        {
            record.InstalledTaskEnabled = false;
        }
        record.Installed = current;
        save();
    }

    private void Remember(PackageTaskState state)
    {
        if (current.IsSameCopy(record.Installed) && record.InstalledTaskEnabled != IsEnabled(state))
        {
            record.InstalledTaskEnabled = IsEnabled(state);
            save();
        }
    }

    private void Write(ClaudetteCopy copy)
    {
        items.WriteEntry(copy);
        record.Target = copy;
        save();
    }

    private void DeleteEntry()
    {
        items.DeleteEntry();
        if (record.Target is not null)
        {
            record.Target = null;
            save();
        }
    }

    private string TurnedOffOutside() => $"Turned off in {items.SystemSettingsName}. Turn it on there.";

    private static bool IsEnabled(PackageTaskState state) => state is PackageTaskState.Enabled or PackageTaskState.EnabledByPolicy;

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException or ExternalException;

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
