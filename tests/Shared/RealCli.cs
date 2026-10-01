namespace Claudette.Tests;

/// <summary>Tests that run the real <c>claude</c> (<c>[Trait("Category", "RealCli")]</c>).</summary>
internal static class RealCli
{
    /// <summary>
    /// Skips the test when Claude Code isn't installed, as CLAUDE.md asks, unless <c>CLAUDETTE_REQUIRE_CLAUDE=1</c> says
    /// it must be: then the test fails instead, so a CI job meant to run them can't pass while running none.
    /// </summary>
    public static void SkipUnlessInstalled(bool installed, string reason = "Claude Code isn't installed.")
    {
        if (installed)
        {
            return;
        }
        if (Environment.GetEnvironmentVariable("CLAUDETTE_REQUIRE_CLAUDE") == "1")
        {
            Assert.Fail($"{reason} CLAUDETTE_REQUIRE_CLAUDE=1 asks for the RealCli tests to run.");
        }
        Assert.Skip(reason);
    }
}
