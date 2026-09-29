using Claudette.Core.Claude;
using Claudette.Core.ProjectTools;
using ProcessStartSpec = Claudette.Core.Processes.ProcessStartSpec;

namespace Claudette.Core.Tests.ProjectTools;

/// <summary>
/// The environment project actions start with (DESIGN.md §18, "Project tools"): the login shell's when there is one
/// (DESIGN.md §13, "Login shell environment"), less Claude Code's session variables.
/// </summary>
public sealed class ProjectToolEnvironmentTests : IDisposable
{
    private readonly string _bin = Directory.CreateTempSubdirectory("claudette-projecttools-bin-").FullName;

    [Fact]
    public void With_the_login_shells_environment_a_bare_program_is_found_on_its_PATH()
    {
        var godot = Path.Combine(_bin, "godot");
        File.WriteAllText(godot, "");
        var user = new Dictionary<string, string> { ["PATH"] = _bin, ["HOME"] = "/home/me", ["CLAUDECODE"] = "1" };

        var spec = ProjectToolEnvironment.Apply(new ProcessStartSpec("godot", ["--editor"]), user);

        Assert.Equal(godot, spec.FileName);
        Assert.Equal(_bin, spec.Environment!["PATH"]);
        Assert.Equal("/home/me", spec.Environment["HOME"]);
        // A custom action that runs claude runs it as a terminal would, not as a child session.
        Assert.All(ClaudeEnvironment.SessionVariables, v => Assert.False(spec.Environment.ContainsKey(v)));
    }

    [Fact]
    public void Without_it_the_program_is_left_for_the_OS_to_find_in_Claudettes_environment()
    {
        var spec = ProjectToolEnvironment.Apply(new ProcessStartSpec("godot", []));

        Assert.Equal("godot", spec.FileName);
        Assert.NotNull(spec.Environment);
        Assert.All(ClaudeEnvironment.SessionVariables, v => Assert.False(spec.Environment!.ContainsKey(v)));
    }

    [Fact]
    public void A_spec_with_its_own_environment_keeps_it()
    {
        var own = new Dictionary<string, string> { ["PATH"] = "/opt/own" };

        var spec = ProjectToolEnvironment.Apply(new ProcessStartSpec("godot", []) { Environment = own }, new Dictionary<string, string> { ["PATH"] = _bin });

        Assert.Same(own, spec.Environment);
        Assert.Equal("godot", spec.FileName);
    }

    public void Dispose() => Directory.Delete(_bin, recursive: true);
}
