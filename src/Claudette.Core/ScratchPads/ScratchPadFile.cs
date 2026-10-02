using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Json;
using Claudette.Core.Protocol;

namespace Claudette.Core.ScratchPads;

/// <summary>
/// A scratch pad as a file (DESIGN.md §18, "Scratch pad"): this machine's copy in the data folder, and the shared one in
/// the session library. Every save is a new <see cref="Revision"/>, and the shared copy remembers the revisions it built
/// on (<see cref="Lineage"/>), so a machine can tell whether the library's copy carries on from what it last saw there,
/// or replaced it without seeing it.
/// </summary>
public sealed record ScratchPadFile
{
    /// <summary>
    /// The file's format. A change that only adds fields keeps it, since readers ignore fields they don't know; one that
    /// removes a field or changes what one means takes the next number.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>How many earlier revisions the shared copy remembers: far more than machines write between two syncs.</summary>
    public const int LineageLength = 64;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public int Version { get; init; } = CurrentVersion;

    public string Text { get; init; } = "";

    /// <summary>This save's id; <c>""</c> for a pad never saved on this machine.</summary>
    public string Revision { get; init; } = "";

    /// <summary>The shared copy's earlier revisions, newest first, up to <see cref="LineageLength"/>.</summary>
    public IReadOnlyList<string> Lineage { get; init; } = [];

    /// <summary>
    /// This machine's copy only: the library's revision it was last the same as, or built on. Null before it has synced.
    /// </summary>
    public string? Synced { get; init; }

    /// <summary>The machine that saved it, as History names machines.</summary>
    public string Machine { get; init; } = "";

    public DateTimeOffset ChangedAt { get; init; }

    /// <summary>The project's remote, so a person can tell the library's pads apart; null for a pad that isn't shared.</summary>
    public string? Remote { get; init; }

    /// <summary>The folder inside the repository the pad belongs to.</summary>
    public string? PathInRepo { get; init; }

    /// <summary>A later Claudette wrote it in a format this one doesn't know, so it isn't written over.</summary>
    public bool IsNewerFormat => Version > CurrentVersion;

    /// <summary>On this machine, the text has changed since it was last the same as the library's.</summary>
    public bool HasUnsyncedChanges => Revision.Length > 0 && Revision != Synced;

    /// <summary>This copy is <paramref name="revision"/>, or was built on it.</summary>
    public bool Includes(string? revision) => revision is { Length: > 0 } && (Revision == revision || Lineage.Contains(revision));

    public string ToJson()
    {
        var file = new JsonObject
        {
            ["version"] = Version,
            ["text"] = Text,
            ["revision"] = Revision,
            ["machine"] = Machine,
            ["changedAt"] = ChangedAt.ToUnixTimeMilliseconds(),
        };
        if (Lineage.Count > 0)
        {
            file["lineage"] = new JsonArray([.. Lineage.Select(r => (JsonNode)r)]);
        }
        if (Synced is not null)
        {
            file["synced"] = Synced;
        }
        if (Remote is not null)
        {
            file["remote"] = Remote;
            file["pathInRepo"] = PathInRepo ?? "";
        }
        return file.ToJsonString(Indented);
    }

    /// <summary>
    /// Reads a pad's file tolerantly: unknown fields are ignored, and a missing one takes its default. Null for something
    /// that isn't a pad's file at all. A file in a newer format reads with its version, so it isn't written over.
    /// </summary>
    public static ScratchPadFile? Parse(string json)
    {
        JsonObject? file;
        try
        {
            file = JsonTree.ParseObject(json);
        }
        catch (JsonException)
        {
            return null;
        }
        if (file?.GetDouble("version") is not { } version || version < 1 || version > int.MaxValue)
        {
            return null;
        }
        return new ScratchPadFile
        {
            Version = (int)version,
            Text = file.GetString("text") ?? "",
            Revision = file.GetString("revision") ?? "",
            Lineage = [.. file.GetStringList("lineage").Take(LineageLength)],
            Synced = file.GetString("synced"),
            Machine = file.GetString("machine") ?? "",
            ChangedAt = file.GetDouble("changedAt") is { } at && at is >= 0 and <= MaxMilliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)at) : DateTimeOffset.MinValue,
            Remote = file.GetString("remote"),
            PathInRepo = file.GetString("pathInRepo"),
        };
    }

    /// <summary>9999-12-31, the latest time a <see cref="DateTimeOffset"/> holds.</summary>
    private const double MaxMilliseconds = 253_402_300_799_000;
}
