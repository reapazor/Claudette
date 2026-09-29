using Claudette.Core.LoginItems;
using Claudette.Core.Updates;

namespace Claudette.Core.Tests.LoginItems;

/// <summary>What a login entry runs to start each kind of copy of Claudette (DESIGN.md §9, "Starting at login").</summary>
public sealed class LoginCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("claudette-login-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void The_app_is_started_with_open_so_macOS_starts_it_as_an_app()
    {
        var command = LoginCommand.For(new ClaudetteCopy(AppInstallKind.MacApp, "/Applications/Claudette.app", "0.3.0"), windows: false, "dotnet");

        Assert.Equal(new LoginCommand("/usr/bin/open", ["-a", "/Applications/Claudette.app", "--args", "--login"]), command, CommandComparer.Instance);
    }

    [Theory]
    [InlineData(true, "Claudette.exe")]
    [InlineData(false, "Claudette")]
    public void A_build_is_started_with_its_own_program(bool windows, string program)
    {
        File.WriteAllText(Path.Combine(_root, program), "");

        var command = LoginCommand.For(new ClaudetteCopy(AppInstallKind.SourceBuild, _root, "0.3.0"), windows, "dotnet");

        Assert.Equal(new LoginCommand(Path.Combine(_root, program), ["--login"]), command, CommandComparer.Instance);
    }

    [Fact]
    public void A_build_without_a_program_of_its_own_is_started_with_dotnet()
    {
        var command = LoginCommand.For(new ClaudetteCopy(AppInstallKind.Other, _root, "0.3.0"), windows: true, "/usr/share/dotnet/dotnet");

        Assert.Equal(new LoginCommand("/usr/share/dotnet/dotnet", [Path.Combine(_root, "Claudette.dll"), "--login"]), command, CommandComparer.Instance);
    }

    [Fact]
    public void The_MSIX_has_no_command_since_only_its_startup_task_starts_it()
    {
        Assert.Null(LoginCommand.For(new ClaudetteCopy(AppInstallKind.Msix, "reapazor.Claudette_1a2b3c4d5e6f7", "0.3.0"), windows: true, "dotnet"));
    }

    [Theory]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", @"C:\Program Files\dotnet\dotnet.exe")]
    [InlineData(@"D:\Claudette\Claudette.exe", "dotnet")]
    [InlineData(null, "dotnet")]
    public void Dotnet_is_the_one_running_Claudette_when_it_was_started_that_way(string? processPath, string host)
    {
        Assert.Equal(host, LoginCommand.DotnetHost(processPath));
    }

    [Fact]
    public void A_copy_is_there_while_its_files_are()
    {
        var build = new ClaudetteCopy(AppInstallKind.SourceBuild, _root, "0.3.0");
        Assert.False(build.ExistsOnDisk());

        File.WriteAllText(Path.Combine(_root, "Claudette.dll"), "");

        Assert.True(build.ExistsOnDisk());
        Assert.True(new ClaudetteCopy(AppInstallKind.MacApp, _root, "0.3.0").ExistsOnDisk());
        Assert.False(new ClaudetteCopy(AppInstallKind.MacApp, Path.Combine(_root, "Claudette.app"), "0.3.0").ExistsOnDisk());
    }

    private sealed class CommandComparer : IEqualityComparer<LoginCommand?>
    {
        public static readonly CommandComparer Instance = new();

        public bool Equals(LoginCommand? x, LoginCommand? y) =>
            x is not null && y is not null && x.Program == y.Program && x.Arguments.SequenceEqual(y.Arguments);

        public int GetHashCode(LoginCommand? obj) => obj?.Program.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
