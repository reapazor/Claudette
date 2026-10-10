using Claudette.Core.Settings;

namespace Claudette.Demo;

/// <summary>
/// Past sessions for History (DESIGN.md §9): more in the demo's three projects, one of them in a worktree of starfall,
/// and four older projects with no tabs, so History's chips run past five and offer the rest under "+2 more".
/// </summary>
internal static class DemoHistory
{
    /// <summary>Writes the sessions' transcripts to <paramref name="transcripts"/>, and the older projects' folders under <paramref name="projects"/>.</summary>
    /// <returns>The marks of the sessions that have one, for the state's <see cref="AppState.SessionMarks"/>.</returns>
    public static Dictionary<string, SessionMark> Write(string projects, string transcripts)
    {
        var starfall = Path.Combine(projects, "starfall");
        var tidepool = Path.Combine(projects, "tidepool");
        var orbit = Path.Combine(projects, "orbit-game");
        var marks = new Dictionary<string, SessionMark>();

        // A worktree Claude Code made, listed under starfall. Not made on disk: opening it says its folder is missing.
        Session("3e7a91c4-5b20-4d8f-a6e1-0c9b8d7f6a01", Path.Combine(starfall, ".claude", "worktrees", "quiet-heron"), "worktree-quiet-heron", DaysAgo(1, 16, 20),
            "The Tundra map hitches every few seconds. Profile it and find out why.",
            "Unreal Insights shows a 40 ms spike every 3.2 s in `UTundraWeatherSubsystem::Tick`: each gust rebuilds every snow decal's render state. Updating only the decals the gust moved takes it to 0.4 ms.",
            "Tundra map hitches");
        Session("b14c6e2a-9f38-4a71-8d05-7e6f5a4b3c02", starfall, "main", DaysAgo(2, 14, 5),
            "The dash cooldown in the HUD counts down faster than the real cooldown.",
            "The widget counted down with real time, and the cooldown with game time, so slow motion pulled them apart. Both use the world's time now.",
            "Dash cooldown in the HUD", TabMark.Check);
        Session("c25d7f3b-0a49-4b82-9e16-8f7a6b5c4d03", starfall, "feature/ledge-grab", DaysAgo(4, 10, 30),
            "Add a ledge grab: when a jump ends against a ledge at chest height, hang from it, then climb up on jump.",
            "Added `ULedgeGrabComponent`, which traces forward and down at the top of a jump. The climb is a root motion montage, so it lines up with any ledge height in range.",
            "Ledge grab");
        Session("d36e8a4c-1b5a-4c93-8f27-9a8b7c6d5e04", tidepool, "main", DaysAgo(1, 11, 5),
            "Bake the harbor's lightmaps without the seams between the pier's planks.",
            "The seams came from overlapping lightmap UVs on `Pier_Planks`. Generated new ones with a 4-pixel margin and baked again: no seams at 40 texels per unit.",
            "Harbor lightmap seams", TabMark.Check);
        Session("e47f9b5d-2c6b-4da4-9038-0b9c8d7e6f05", tidepool, "main", DaysAgo(3, 15, 40),
            "The fishing line clips through the boat's hull when you cast from the stern.",
            "The line's collider ignored the boat's layer. It collides with the hull now, and bends around the stern.",
            "Fishing line clips the hull");
        Session("f5809c6e-3d7c-4eb5-8149-1c0d9e8f7a06", orbit, "main", DaysAgo(5, 9, 15),
            "Port the save system from JSON files to ResourceSaver.",
            "Saves are `SaveGame` resources now, written with `ResourceSaver.save`. Old JSON saves are read once and converted.",
            "Saves with ResourceSaver");

        // Older projects, with no tabs open.
        Session("0a1b2c3d-4e5f-4a6b-8c7d-2e1f0a9b8c07", Older("lighthouse-launcher"), "main", DaysAgo(6, 13, 0),
            "Sign the launcher's Windows build in CI.",
            "The release job signs `Lighthouse.exe` with `signtool` and the certificate from the pipeline's secrets, then checks the signature before uploading.",
            "Sign the Windows launcher");
        Session("1b2c3d4e-5f6a-4b7c-9d8e-3f2a1b0c9d08", Older("shader-lab"), "main", DaysAgo(8, 17, 45),
            "Write a toon shader with two bands of light and a rim light.",
            "`Toon.shader` quantizes the light into two bands with a soft step, and adds a Fresnel rim tinted by the main light's color.",
            "Toon shader with a rim light", TabMark.Star);
        Session("2c3d4e5f-6a7b-4c8d-8e9f-4a3b2c1d0e09", Older("build-farm"), "main", DaysAgo(9, 8, 50),
            "Why do the nightly Unreal builds run out of disk?",
            "Each build keeps its own Derived Data Cache under the agent's work folder. A shared DDC on the build share, and cleaning `Intermediate` after packaging, frees about 180 GB a night.",
            "Nightly builds out of disk");
        Session("3d4e5f6a-7b8c-4d9e-9f0a-5b4c3d2e1f10", Older("pixel-tools"), "main", DaysAgo(12, 19, 25),
            "Add an export to Aseprite's JSON format.",
            "**Export → Aseprite JSON** writes the sheet and a JSON file with each frame's rectangle and duration, and the tags as Aseprite names them.",
            "Aseprite JSON export");
        return marks;

        void Session(string id, string folder, string branch, DateTimeOffset start, string prompt, string reply, string title, TabMark? mark = null)
        {
            new DemoTranscript(id, folder, branch, start).Prompt(prompt).Text(reply, last: true).Title(title).Save(transcripts);
            if (mark is { } chosen)
            {
                marks[id] = new SessionMark { Mark = TabMarks.Key(chosen), At = start };
            }
        }

        string Older(string name)
        {
            var folder = Path.Combine(projects, name);
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    private static DateTimeOffset DaysAgo(int days, int hour, int minute) =>
        new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).AddDays(-days).AddHours(hour).AddMinutes(minute);
}
