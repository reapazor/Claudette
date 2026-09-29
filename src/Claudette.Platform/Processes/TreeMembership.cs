namespace Claudette.Platform.Processes;

/// <summary>A process found in a tree.</summary>
internal readonly record struct TreeMember(int Pid, int ParentPid, long StartKey, bool IsRoot, bool IsDetached);

/// <summary>
/// Finds a tree by walking parent links from the root, and remembers every process it has found, so one that is
/// re-parented out of the tree stays listed, marked detached, until it exits (DESIGN.md §4, Process monitor). Used on
/// macOS and Linux, and on Windows when the root couldn't be put in a Job Object.
/// <list type="bullet">
/// <item>Processes are identified by (PID, start key), so a reused PID is never confused with the process that had it
/// before.</item>
/// <item>A child can't start before its parent. A "child" that did belongs to an earlier process with the same PID.</item>
/// <item>On Windows a process keeps its parent's PID after the parent exits, so children of a remembered process are
/// found even once it has gone. On macOS and Linux they're re-parented and found through the remembered list.</item>
/// </list>
/// Not thread-safe: callers serialize updates.
/// </summary>
internal sealed class TreeMembership(int rootPid, long? rootStartKey = null)
{
    private long? _rootStartKey = rootStartKey;
    private Dictionary<int, long> _known = [];

    public int RootPid => rootPid;

    /// <summary>The root's start key, once the root has been seen.</summary>
    public long? RootStartKey => _rootStartKey;

    /// <summary>Walks the tree again. Returns the live members, root first, then in breadth-first order.</summary>
    /// <param name="parentOf">Every live process on the system, mapped to its parent PID.</param>
    /// <param name="startKeyOf">The start key of a live process, or null if it can't be read (it's then skipped).</param>
    public IReadOnlyList<TreeMember> Update(IReadOnlyDictionary<int, int> parentOf, Func<int, long?> startKeyOf)
    {
        var keys = new Dictionary<int, long?>();
        long? KeyOf(int pid)
        {
            if (!keys.TryGetValue(pid, out var key))
            {
                key = parentOf.ContainsKey(pid) ? startKeyOf(pid) : null;
                keys[pid] = key;
            }
            return key;
        }

        _rootStartKey ??= KeyOf(rootPid);
        if (_rootStartKey is not { } rootKey)
        {
            return [];
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var (pid, parent) in parentOf)
        {
            if (pid == parent)
            {
                continue;
            }
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }
            list.Add(pid);
        }
        foreach (var list in children.Values)
        {
            list.Sort();
        }

        var members = new List<TreeMember>();
        var visited = new HashSet<(int Pid, long Key)>();
        var queue = new Queue<(int Pid, long Key, bool Attached)>();

        void Walk(int startPid, long startKey, bool attached)
        {
            if (!visited.Add((startPid, startKey)))
            {
                return;
            }
            queue.Enqueue((startPid, startKey, attached));
            while (queue.TryDequeue(out var node))
            {
                var liveKey = KeyOf(node.Pid);
                var alive = liveKey == node.Key;
                if (alive)
                {
                    members.Add(new TreeMember(
                        node.Pid,
                        parentOf[node.Pid],
                        node.Key,
                        IsRoot: node.Pid == rootPid && node.Key == rootKey,
                        IsDetached: !node.Attached));
                }
                if (!children.TryGetValue(node.Pid, out var kids))
                {
                    continue;
                }
                foreach (var child in kids)
                {
                    if (KeyOf(child) is not { } childKey || childKey < node.Key)
                    {
                        continue;
                    }
                    // The PID now belongs to a newer process: only children older than that one are the gone process's.
                    if (!alive && liveKey is { } reusedKey && childKey >= reusedKey)
                    {
                        continue;
                    }
                    if (visited.Add((child, childKey)))
                    {
                        queue.Enqueue((child, childKey, node.Attached && alive));
                    }
                }
            }
        }

        Walk(rootPid, rootKey, attached: true);
        foreach (var (pid, key) in _known.OrderBy(p => p.Key))
        {
            Walk(pid, key, attached: false);
        }

        // Anything found once is remembered while it lives. A process that has gone can't start new children, and the
        // ones it had were found by this walk, so it can be forgotten.
        _known = members.Where(m => !m.IsRoot).ToDictionary(m => m.Pid, m => m.StartKey);
        return members;
    }

    /// <summary>
    /// For a complete list of a tree's live processes (a Job Object's): the PIDs that descend from the root through live
    /// parents. The others are detached.
    /// </summary>
    public static HashSet<int> AttachedToRoot(IReadOnlyCollection<(int Pid, int ParentPid, long StartKey)> processes, int rootPid)
    {
        var attached = new HashSet<int>();
        var byPid = new Dictionary<int, long>();
        foreach (var process in processes)
        {
            byPid[process.Pid] = process.StartKey;
        }
        if (!byPid.ContainsKey(rootPid))
        {
            return attached;
        }
        var children = processes.Where(p => p.Pid != p.ParentPid).ToLookup(p => p.ParentPid);
        attached.Add(rootPid);
        var queue = new Queue<int>([rootPid]);
        while (queue.TryDequeue(out var pid))
        {
            foreach (var child in children[pid])
            {
                if (child.StartKey >= byPid[pid] && attached.Add(child.Pid))
                {
                    queue.Enqueue(child.Pid);
                }
            }
        }
        return attached;
    }
}
