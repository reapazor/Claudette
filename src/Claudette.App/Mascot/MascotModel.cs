namespace Claudette.App.Mascot;

/// <summary>What the tab she stands on is doing, which she reacts to (DESIGN.md §5, "Claudette on the composer").</summary>
public enum MascotMood
{
    Idle,

    /// <summary>Claude is working: she works too, with whatever suits the tool it's running.</summary>
    Working,

    /// <summary>A prompt waits on the user: she waves now and then.</summary>
    Waiting,

    /// <summary>A usage limit holds the task: she dozes by an hourglass.</summary>
    Resting,
}

/// <summary>What the main agent's newest running tool does, which decides what she works with while Claude works.</summary>
public enum MascotTool
{
    /// <summary>None running, or one she has nothing for: her laptop, or a clipboard in plan mode.</summary>
    None,

    /// <summary>Reading or searching files: a magnifying glass.</summary>
    Reading,

    /// <summary>Editing or writing files: a hammer.</summary>
    Editing,

    /// <summary>Running a command: a little terminal.</summary>
    Running,

    /// <summary>Fetching a page or searching the web: a globe.</summary>
    Web,

    /// <summary>Starting subagents: she juggles, a ball for each one running.</summary>
    Agents,
}

/// <summary>The tools she knows from Claude Code's names for them (the working line knows the same ones).</summary>
public static class MascotTools
{
    public static MascotTool For(string? toolName) => toolName switch
    {
        "Read" or "Grep" or "Glob" or "LS" or "NotebookRead" => MascotTool.Reading,
        "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => MascotTool.Editing,
        "Bash" or "PowerShell" or "BashOutput" or "Monitor" => MascotTool.Running,
        "WebFetch" or "WebSearch" => MascotTool.Web,
        "Agent" or "Task" => MascotTool.Agents,
        _ => MascotTool.None,
    };
}

/// <summary>Everything about the tab she's on that she reacts to, as it is now.</summary>
public sealed record MascotSituation
{
    public static readonly MascotSituation Quiet = new();

    public MascotMood Mood { get; init; }

    /// <summary>While Claude works, what its newest running tool does.</summary>
    public MascotTool Tool { get; init; }

    /// <summary>Subagents running.</summary>
    public int Agents { get; init; }

    /// <summary>The tab is in plan mode: she has a clipboard.</summary>
    public bool Planning { get; init; }

    /// <summary>The context window is nearly full: she sweats.</summary>
    public bool ContextFull { get; init; }

    /// <summary>Claude Code is compacting the conversation: she sweeps up.</summary>
    public bool Compacting { get; init; }

    /// <summary>Other tabs waiting on the user: she points them out, toward the sidebar.</summary>
    public int OthersWaiting { get; init; }
}

/// <summary>
/// A prop drawn with her: <see cref="X"/> cells from her left, its bottom row <see cref="Bottom"/> cells above the edge.
/// <see cref="Front"/> draws it in front of the composer box rather than behind, as her legs dangling over it.
/// </summary>
public sealed record MascotProp(string Name, int X, int Bottom, bool Front = false);

/// <summary>
/// How she looks now. <see cref="X"/> is her left edge, in cells from the composer box's left; <see cref="Drop"/> is how
/// many cells lower she is than standing on its top edge, behind the box, or higher when it's negative (a hop). It's
/// whole cells but while she's falling, when gravity has her part-way between.
/// </summary>
public sealed record MascotFrame(string Pose, int X, double Drop, IReadOnlyList<MascotProp> Props)
{
    /// <summary>What she wears on her head (<see cref="MascotArt.Hats"/>), or null.</summary>
    public string? Hat { get; init; }

    /// <summary>What she says, in a bubble over her, or null.</summary>
    public string? Say { get; init; }

    /// <summary>All of her is behind the box.</summary>
    public bool IsBehind => Drop >= MascotArt.HeightOf(Pose);

    /// <summary>Something of her shows: some of her, or her hands on the edge as she starts to climb up.</summary>
    public bool IsVisible => !IsBehind || Props.Count > 0;
}

/// <summary>
/// Where on the box's top edge she goes, in cells from its left. Her patch is <see cref="Left"/> to <see cref="Right"/>,
/// where she wanders: her left edge goes from <see cref="Left"/> to <see cref="Right"/> less her width. <see cref="Home"/>
/// is where she tends to go back to, over the Send button. The free edge, <see cref="EdgeLeft"/> to
/// <see cref="EdgeRight"/>, can be wider: set down there, she lands where she was put and walks back.
/// </summary>
public readonly record struct MascotRoom(int Left, int Right, int Home)
{
    public int EdgeLeft { get; init; } = Left;

    public int EdgeRight { get; init; } = Right;

    public int Width => Right - Left;

    public int MaxX => Math.Max(Left, Right - MascotArt.Width);

    /// <summary>Her home, as near it as the patch lets her stand.</summary>
    public int HomeX => Clamp(Home);

    /// <summary>Inside her patch.</summary>
    public int Clamp(int x) => Math.Clamp(x, Left, MaxX);

    /// <summary>Anywhere on the free edge.</summary>
    public int ClampToEdge(int x) => Math.Clamp(x, Math.Min(EdgeLeft, Left), Math.Max(Math.Max(EdgeLeft, Left), Math.Max(EdgeRight, Right) - MascotArt.Width));

    public bool InPatch(int x) => x >= Left && x <= MaxX;
}

/// <summary>One step of what she's doing: a pose held for a while, a step sideways, how far down she is, what she says.</summary>
internal sealed record MascotStep(
    string Pose,
    TimeSpan Duration,
    int Move = 0,
    double Drop = 0,
    IReadOnlyList<MascotProp>? Props = null,
    string? Say = null);

/// <summary>How long she waits between the things she does.</summary>
public sealed record MascotSpell(TimeSpan Shortest, TimeSpan Longest)
{
    /// <summary>Every 10 to 30 seconds, the default.</summary>
    public static readonly MascotSpell Lively = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

    /// <summary>Every 30 to 90 seconds.</summary>
    public static readonly MascotSpell Calm = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
}

/// <summary>The words she says, which come from the app: the keyboard shortcuts as they're bound now.</summary>
public interface IMascotLines
{
    /// <summary>A tip to show now and then, or null for none.</summary>
    string? Tip(Random random);

    /// <summary>Pointing toward the sidebar while <paramref name="count"/> other tabs wait on the user.</summary>
    string OthersWaiting(int count);
}
