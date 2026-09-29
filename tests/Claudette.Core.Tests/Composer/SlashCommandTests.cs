using System.Text.Json.Nodes;
using Claudette.Core.Composer;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;

namespace Claudette.Core.Tests.Composer;

/// <summary>The composer's <c>/</c> autocomplete (DESIGN.md §5, "Composer").</summary>
public class SlashCommandTests
{
    private static SlashCommandCatalog Catalog()
    {
        var catalog = new SlashCommandCatalog();
        catalog.SetDescribed(SlashCommandInfo.ParseList(JsonNode.Parse("""
            [
              {"name":"ship-it","description":"Ship the change (project)","argumentHint":"<version>"},
              {"name":"clear","description":"Start a new session with empty context","argumentHint":"[name]","aliases":["reset","new"],"builtin":true},
              {"name":"compact","description":"Free up context by summarizing the conversation so far","argumentHint":"","builtin":true},
              {"name":"context","description":"Show context usage","builtin":true},
              {"name":"doctor","description":"Diagnose the installation","builtin":true},
              {"name":"__remote-workflow","description":"internal"},
              {"description":"no name"}
            ]
            """)!.AsArray()));
        return catalog;
    }

    private static SystemInitMessage Init(string json) =>
        MessageParser.TryParse(json, out var message, out _) ? (SystemInitMessage)message : throw new InvalidOperationException();

    [Fact]
    public void Initialize_commands_keep_their_details()
    {
        var clear = Catalog().Commands.Single(c => c.Name == "clear");

        Assert.Equal("[name]", clear.ArgumentHint);
        Assert.Equal(["reset", "new"], clear.Aliases);
        Assert.True(clear.IsBuiltIn);
        Assert.False(Catalog().Commands.Single(c => c.Name == "ship-it").IsBuiltIn);
    }

    [Fact]
    public void Internal_and_terminal_commands_are_not_offered()
    {
        var catalog = Catalog();
        catalog.SetAccepted(Init("""{"type":"system","subtype":"init","session_id":"s","slash_commands":["ship-it","clear","compact","context","doctor","review"],"terminal_slash_commands":["doctor"]}"""));

        Assert.Equal(["ship-it", "clear", "compact", "context", "review"], catalog.Commands.Select(c => c.Name));
        // Only system/init named it: offered, without a description.
        Assert.Null(catalog.Commands.Single(c => c.Name == "review").Description);
    }

    [Fact]
    public void An_init_without_the_field_changes_nothing()
    {
        var catalog = Catalog();

        catalog.SetAccepted(Init("""{"type":"system","subtype":"init","session_id":"s"}"""));

        Assert.Contains(catalog.Commands, c => c.Name == "doctor");
    }

    [Fact]
    public void Filtering_ranks_name_starts_before_aliases_contains_and_descriptions()
    {
        var catalog = Catalog();

        Assert.Equal(["compact", "context"], catalog.Filter("co").Select(c => c.Name));
        Assert.Equal(["clear"], catalog.Filter("reset").Select(c => c.Name));
        Assert.Equal(["context", "clear", "compact"], catalog.Filter("CONTEXT").Select(c => c.Name));
        Assert.Equal("ship-it", catalog.Filter("it").First().Name);
        Assert.Empty(catalog.Filter("zzz"));
        Assert.Equal(catalog.Commands, catalog.Filter(""));
    }

    [Fact]
    public void An_exact_name_comes_first()
    {
        var catalog = new SlashCommandCatalog();
        catalog.SetDescribed([new SlashCommandInfo("reviewer", null, null), new SlashCommandInfo("review", null, null)]);

        Assert.Equal(["review", "reviewer"], catalog.Filter("review").Select(c => c.Name));
    }

    [Fact]
    public void Commands_changed_replaces_the_list()
    {
        var catalog = Catalog();

        catalog.SetDescribed([new SlashCommandInfo("new-skill", "A skill found in a subfolder", null)]);

        Assert.Equal(["new-skill"], catalog.Commands.Select(c => c.Name));
    }

    [Theory]
    [InlineData("/", 1, "")]
    [InlineData("/com", 4, "com")]
    [InlineData("/com", 2, "c")]
    [InlineData("/compact now", 4, "com")]
    public void A_slash_at_the_start_is_a_command(string text, int caret, string query)
    {
        var token = ComposerTokens.FindSlashCommand(text, caret);

        Assert.Equal(new ComposerToken(0, caret, query), token);
    }

    [Theory]
    [InlineData("/compact now", 10)]
    [InlineData("hello /compact", 14)]
    [InlineData("", 0)]
    [InlineData("/x", 0)]
    public void Elsewhere_it_is_not(string text, int caret) => Assert.Null(ComposerTokens.FindSlashCommand(text, caret));

    [Fact]
    public void Picking_a_command_replaces_the_word_and_adds_a_space()
    {
        Assert.Equal(new ComposerEdit("/compact ", 9), ComposerTokens.ReplaceSlashCommand("/co", new ComposerToken(0, 3, "co"), "compact"));
        // The rest of the word after the caret goes too; what follows it stays.
        Assert.Equal(new ComposerEdit("/compact keep the tests", 9),
            ComposerTokens.ReplaceSlashCommand("/cozzz keep the tests", new ComposerToken(0, 3, "co"), "compact"));
    }
}
