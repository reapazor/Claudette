using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Claudette.Core.Library;
using Claudette.Core.Protocol;
using Claudette.Core.Json;

namespace Claudette.Core.Settings;

/// <summary>One synced setting in <c>settings-sync.json</c>: its value, when it last changed and on which machine.</summary>
public sealed class SyncedSetting
{
    public JsonNode? Value { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public string Machine { get; set; } = "";
}

/// <summary>
/// <c>&lt;library&gt;/settings-sync.json</c> (DESIGN.md §14, "Settings sync"):
/// <c>{ "version": 1, "values": { "&lt;path&gt;": { "value": …, "changedAt": "…", "machine": "…" } } }</c>.
/// </summary>
public sealed class SyncedSettingsFile
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public Dictionary<string, SyncedSetting> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>What this machine last saw or published for one setting.</summary>
public sealed class SettingsSyncStateEntry
{
    public JsonNode? Value { get; set; }

    public DateTimeOffset ChangedAt { get; set; }
}

/// <summary>This machine's side of settings sync, kept by the app with its local state (it doesn't sync).</summary>
public sealed class SettingsSyncState
{
    public Dictionary<string, SettingsSyncStateEntry> Values { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>The outcome of <see cref="SettingsSync.Merge"/>.</summary>
/// <param name="ToApply">Settings to change on this machine, by path; apply each with <see cref="SettingsSync.Apply"/>.</param>
/// <param name="Published">Paths whose local value goes into the synced file.</param>
/// <param name="Remote">The synced file to write when <see cref="RemoteChanged"/>.</param>
/// <param name="State">This machine's new sync state, to save.</param>
public sealed record SettingsMergeResult(
    IReadOnlyDictionary<string, JsonNode?> ToApply,
    IReadOnlyList<string> Published,
    SyncedSettingsFile Remote,
    SettingsSyncState State)
{
    public bool RemoteChanged => Published.Count > 0;
}

/// <summary>
/// Settings sync through the session library (DESIGN.md §14, "Settings sync (optional)"). Pure logic over JSON: the
/// app decides which settings sync and when to merge. Every synced setting is a leaf path such as
/// <c>appearance.theme</c>, and keeps the time it last changed; the newest change wins per path, so edits on two
/// machines don't overwrite each other wholesale.
/// </summary>
public static class SettingsSync
{
    /// <summary>
    /// The leaf values of <paramref name="settings"/> under the given top-level sections or path prefixes, by dotted
    /// path. Arrays are single values (for example <c>quickSuffixes</c>), and so are the objects named in
    /// <paramref name="leaves"/>, which suits dictionary-like settings whose keys come and go. Property names must not
    /// contain dots. The values are copies.
    /// </summary>
    public static Dictionary<string, JsonNode?> Flatten(JsonObject settings, IEnumerable<string> include, IEnumerable<string>? leaves = null)
    {
        var prefixes = include.ToArray();
        var leafSet = new HashSet<string>(leaves ?? [], StringComparer.Ordinal);
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        Walk(settings, "");
        return result;

        void Walk(JsonObject obj, string prefix)
        {
            foreach (var (name, node) in obj)
            {
                var path = prefix.Length == 0 ? name : $"{prefix}.{name}";
                if (!prefixes.Any(p => Covers(p, path) || Covers(path, p)))
                {
                    continue;
                }
                if (node is JsonObject child && !leafSet.Contains(path))
                {
                    Walk(child, path);
                }
                else if (prefixes.Any(p => Covers(p, path)))
                {
                    result[path] = node?.DeepClone();
                }
            }
        }
    }

    /// <summary>Sets one leaf in <paramref name="settings"/>, creating the objects on its path as needed.</summary>
    public static void Apply(JsonObject settings, string path, JsonNode? value)
    {
        var names = path.Split('.');
        var current = settings;
        foreach (var name in names[..^1])
        {
            if (current[name] is not JsonObject next)
            {
                next = new JsonObject();
                current[name] = next;
            }
            current = next;
        }
        current[names[^1]] = value?.DeepClone();
    }

    /// <summary>Reads <c>settings-sync.json</c> leniently: a missing or unreadable file, or a bad entry, reads as nothing.</summary>
    public static SyncedSettingsFile ReadFile(string path)
    {
        var file = new SyncedSettingsFile();
        try
        {
            if (!File.Exists(path) || JsonTree.Parse(File.ReadAllText(path)) is not JsonObject root)
            {
                return file;
            }
            if (root.GetDouble("version") is { } version)
            {
                file.Version = (int)version;
            }
            foreach (var (key, node) in root.GetObject("values") ?? new JsonObject())
            {
                if (node is JsonObject entry
                    && entry.GetString("changedAt") is { } changedAt
                    && DateTimeOffset.TryParse(changedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                {
                    file.Values[key] = new SyncedSetting
                    {
                        Value = entry["value"]?.DeepClone(),
                        ChangedAt = time,
                        Machine = entry.GetString("machine") ?? "",
                    };
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new SyncedSettingsFile();
        }
        return file;
    }

    /// <summary>Writes <c>settings-sync.json</c> to a temporary file and renames it into place.</summary>
    public static Task WriteFileAsync(string path, SyncedSettingsFile file, CancellationToken cancellationToken = default) =>
        LibraryFiles.WriteTextAsync(path, JsonSerializer.Serialize(file, JsonFileStore<SyncedSettingsFile>.Options), cancellationToken);

    /// <summary>
    /// Whether the synced file already has settings, for the first-time question: <b>Use synced settings</b> (merge
    /// with an empty state, which takes every synced value) or <b>Replace them with this machine's</b>
    /// (<see cref="PublishAll"/>).
    /// </summary>
    public static bool HasRemoteValues(SyncedSettingsFile file) => file.Values.Count > 0;

    /// <summary>
    /// Merges this machine's settings with the synced file, per path:
    /// <list type="bullet">
    /// <item>A local value that differs from what <paramref name="state"/> last saw is a local edit: it's published,
    /// stamped <paramref name="now"/>.</item>
    /// <item>A synced value that changed after what <paramref name="state"/> last saw, and differs from the local value,
    /// is applied locally.</item>
    /// <item>When both changed, the later change wins. Equal values are never a conflict.</item>
    /// <item>With no state for a path (sync just turned on, or a new setting), the synced value is taken if there is one;
    /// otherwise the local value is published.</item>
    /// <item>Synced paths this machine doesn't have (from another version) are kept in the file, not applied.</item>
    /// </list>
    /// </summary>
    /// <param name="local">This machine's current values, from <see cref="Flatten"/>.</param>
    public static SettingsMergeResult Merge(
        IReadOnlyDictionary<string, JsonNode?> local,
        SettingsSyncState state,
        SyncedSettingsFile remote,
        DateTimeOffset now,
        string machine)
    {
        var toApply = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var published = new List<string>();
        var newRemote = new SyncedSettingsFile { Values = new(StringComparer.Ordinal) };
        var newState = new SettingsSyncState();

        foreach (var (path, synced) in remote.Values)
        {
            if (!local.ContainsKey(path))
            {
                newRemote.Values[path] = Copy(synced);
            }
        }

        foreach (var (path, value) in local)
        {
            remote.Values.TryGetValue(path, out var synced);
            state.Values.TryGetValue(path, out var seen);

            if (seen is null)
            {
                if (synced is not null)
                {
                    Take(path, value, synced);
                }
                else
                {
                    Publish(path, value, now);
                }
            }
            else if (!Same(value, seen.Value))
            {
                // Edited here since the last sync.
                if (synced is not null && Same(synced.Value, value))
                {
                    Keep(path, synced);
                }
                else if (synced is not null && synced.ChangedAt > seen.ChangedAt && synced.ChangedAt > now)
                {
                    Take(path, value, synced);
                }
                else
                {
                    Publish(path, value, now);
                }
            }
            else if (synced is not null && synced.ChangedAt >= seen.ChangedAt)
            {
                // Unchanged here. An equal time with a different value also takes the synced one, so two machines
                // can't keep overwriting each other.
                if (synced.ChangedAt > seen.ChangedAt || !Same(synced.Value, value))
                {
                    Take(path, value, synced);
                }
                else
                {
                    Keep(path, synced);
                }
            }
            else if (synced is not null && Same(synced.Value, value))
            {
                Keep(path, synced);
            }
            else
            {
                // The synced file lost this setting or holds an older value (for example a sync client restored an
                // older copy): put back what this machine knows is newer.
                Publish(path, value, seen.ChangedAt);
            }
        }

        return new SettingsMergeResult(toApply, published, newRemote, newState);

        void Take(string path, JsonNode? localValue, SyncedSetting synced)
        {
            if (!Same(synced.Value, localValue))
            {
                toApply[path] = synced.Value?.DeepClone();
            }
            Keep(path, synced);
        }

        void Keep(string path, SyncedSetting synced)
        {
            newRemote.Values[path] = Copy(synced);
            newState.Values[path] = new SettingsSyncStateEntry { Value = synced.Value?.DeepClone(), ChangedAt = synced.ChangedAt };
        }

        void Publish(string path, JsonNode? value, DateTimeOffset changedAt)
        {
            published.Add(path);
            newRemote.Values[path] = new SyncedSetting { Value = value?.DeepClone(), ChangedAt = changedAt, Machine = machine };
            newState.Values[path] = new SettingsSyncStateEntry { Value = value?.DeepClone(), ChangedAt = changedAt };
        }
    }

    /// <summary><b>Replace them with this machine's</b>: a synced file holding exactly this machine's values, stamped <paramref name="now"/>.</summary>
    public static SettingsMergeResult PublishAll(IReadOnlyDictionary<string, JsonNode?> local, DateTimeOffset now, string machine)
    {
        var remote = new SyncedSettingsFile { Values = new(StringComparer.Ordinal) };
        var state = new SettingsSyncState();
        foreach (var (path, value) in local)
        {
            remote.Values[path] = new SyncedSetting { Value = value?.DeepClone(), ChangedAt = now, Machine = machine };
            state.Values[path] = new SettingsSyncStateEntry { Value = value?.DeepClone(), ChangedAt = now };
        }
        return new SettingsMergeResult(new Dictionary<string, JsonNode?>(StringComparer.Ordinal), [.. local.Keys], remote, state);
    }

    /// <summary>Whether two setting values are the same JSON.</summary>
    internal static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static SyncedSetting Copy(SyncedSetting synced) =>
        new() { Value = synced.Value?.DeepClone(), ChangedAt = synced.ChangedAt, Machine = synced.Machine };

    /// <summary>Whether <paramref name="path"/> is <paramref name="prefix"/> or inside it.</summary>
    private static bool Covers(string prefix, string path) =>
        path.Length == prefix.Length
            ? path == prefix
            : path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal) && path[prefix.Length] == '.';
}
