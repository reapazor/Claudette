namespace Claudette.Core.ScratchPads;

/// <summary>What syncing a scratch pad with the session library came to (DESIGN.md §18, "Scratch pad").</summary>
public enum ScratchPadSyncKind
{
    /// <summary>This machine's copy and the library's are the same: nothing changed.</summary>
    InSync,

    /// <summary>The library's copy has this machine's text under another revision, which this machine's copy takes.</summary>
    Adopted,

    /// <summary>This machine's changes went to the library.</summary>
    Pushed,

    /// <summary>Another machine's changes came here.</summary>
    Pulled,

    /// <summary>Both machines changed it without seeing the other's change: the user chooses what it becomes.</summary>
    Conflict,

    /// <summary>A later Claudette wrote the library's copy, so it isn't written over.</summary>
    NewerFormat,

    /// <summary>The library couldn't be read or written; this machine's copy is kept for next time.</summary>
    Unavailable,
}

/// <summary>Another copy of the pad that this machine's doesn't include.</summary>
/// <param name="Theirs">The library's copy, or a sync client's copy of it.</param>
/// <param name="CopyPath">The sync client's copy (<c>&lt;id&gt; (1).json</c>), which goes once it's resolved; null for the library's own.</param>
public sealed record ScratchPadConflict(ScratchPadFile Theirs, string? CopyPath);

/// <summary>The outcome of a sync.</summary>
/// <param name="Local">This machine's copy afterwards: what the pad shows and saves.</param>
public sealed record ScratchPadSyncResult(ScratchPadSyncKind Kind, ScratchPadFile Local)
{
    /// <summary>For <see cref="ScratchPadSyncKind.Pushed"/>, what was written to the library.</summary>
    public ScratchPadFile? Library { get; init; }

    /// <summary>
    /// What the user has to choose about: the library's copy for <see cref="ScratchPadSyncKind.Conflict"/>, or after any
    /// other outcome a sync client's copy that this machine's doesn't include.
    /// </summary>
    public ScratchPadConflict? Conflict { get; init; }

    /// <summary>For <see cref="ScratchPadSyncKind.Unavailable"/>, why.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// How a scratch pad on this machine and the shared one in the session library come together (DESIGN.md §18, "Scratch
/// pad"). The library's copy wins when it carries on from what this machine last synced and nothing has changed here
/// since; this machine's wins when it carries on from the library's copy. Otherwise both changed, and the user chooses.
/// Pure, so every case is tested without files.
/// </summary>
public static class ScratchPadSync
{
    /// <summary>A new revision's id.</summary>
    public static string NewRevision() => Guid.NewGuid().ToString("N")[..16];

    /// <param name="local">This machine's copy.</param>
    /// <param name="library">The library's copy; null when it has none.</param>
    public static ScratchPadSyncResult Decide(ScratchPadFile local, ScratchPadFile? library)
    {
        if (library is null)
        {
            // Nothing shared yet, or the library moved: share this machine's, unless it was never written.
            return local.Revision.Length == 0
                ? new ScratchPadSyncResult(ScratchPadSyncKind.InSync, local)
                : Push(local, local.Synced is { } synced && synced != local.Revision ? [synced] : []);
        }
        if (library.IsNewerFormat)
        {
            return new ScratchPadSyncResult(ScratchPadSyncKind.NewerFormat, local);
        }
        if (local.Synced is not null && library.Revision == local.Synced)
        {
            return local.HasUnsyncedChanges
                ? Push(local, Lineage([library.Revision], library.Lineage))
                : new ScratchPadSyncResult(ScratchPadSyncKind.InSync, local);
        }
        if (library.Text == local.Text)
        {
            return local.Revision == library.Revision && local.Synced == library.Revision
                ? new ScratchPadSyncResult(ScratchPadSyncKind.InSync, local)
                : new ScratchPadSyncResult(ScratchPadSyncKind.Adopted, local with { Revision = library.Revision, Synced = library.Revision });
        }
        // Never written here, or unchanged here since a revision the library's copy carries on from: take it.
        if (local.Revision.Length == 0 || (!local.HasUnsyncedChanges && library.Includes(local.Synced)))
        {
            return new ScratchPadSyncResult(ScratchPadSyncKind.Pulled, Pulled(library));
        }
        return new ScratchPadSyncResult(ScratchPadSyncKind.Conflict, local) { Conflict = new ScratchPadConflict(library, null) };
    }

    /// <summary>
    /// The user's choice about a conflict: the pad becomes <paramref name="text"/>, carrying on from the library's copy,
    /// from theirs and from this machine's, so every machine that has any of them takes it without asking again.
    /// </summary>
    /// <param name="library">The library's copy now; null when it has none.</param>
    /// <param name="conflict">What the user chose about.</param>
    /// <param name="revision">The new revision, when one is written.</param>
    public static ScratchPadSyncResult Resolve(ScratchPadFile local, ScratchPadFile? library, ScratchPadConflict conflict, string text, string revision, string machine, DateTimeOffset now)
    {
        var theirs = conflict.Theirs;
        // The library's copy moved on while the user chose: ask again about that, rather than write over what it added.
        if (conflict.CopyPath is null && library is not null && library.Revision != theirs.Revision)
        {
            return library.Text == text
                ? new ScratchPadSyncResult(ScratchPadSyncKind.Pulled, Pulled(library))
                : new ScratchPadSyncResult(ScratchPadSyncKind.Conflict, local) { Conflict = new ScratchPadConflict(library, null) };
        }
        if (conflict.CopyPath is null && library is not null && library.Text == text)
        {
            return new ScratchPadSyncResult(ScratchPadSyncKind.Pulled, Pulled(library));
        }
        var written = new ScratchPadFile
        {
            Text = text,
            Revision = revision,
            Lineage = Lineage([library?.Revision, theirs.Revision, local.Revision, local.Synced], library?.Lineage ?? [], theirs.Lineage),
            Machine = machine,
            ChangedAt = now,
            Remote = local.Remote ?? theirs.Remote,
            PathInRepo = local.PathInRepo ?? theirs.PathInRepo,
        };
        return new ScratchPadSyncResult(ScratchPadSyncKind.Pushed, written with { Lineage = [], Synced = revision }) { Library = written };
    }

    private static ScratchPadSyncResult Push(ScratchPadFile local, IReadOnlyList<string> lineage) =>
        new(ScratchPadSyncKind.Pushed, local with { Synced = local.Revision })
        {
            Library = local with { Synced = null, Lineage = lineage },
        };

    private static ScratchPadFile Pulled(ScratchPadFile library) => library with { Synced = library.Revision, Lineage = [] };

    /// <summary>Revisions newest first, each once, up to <see cref="ScratchPadFile.LineageLength"/>.</summary>
    private static IReadOnlyList<string> Lineage(IEnumerable<string?> newest, params IEnumerable<string>[] earlier) =>
        [.. newest.Concat(earlier.SelectMany(l => l)).OfType<string>().Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).Take(ScratchPadFile.LineageLength)];
}
