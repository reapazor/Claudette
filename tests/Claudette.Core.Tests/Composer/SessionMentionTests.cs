using Claudette.Core.Composer;

namespace Claudette.Core.Tests.Composer;

/// <summary>Mentions of other sessions and agents in the composer (DESIGN.md §5, "Autocomplete").</summary>
public sealed class SessionMentionTests
{
    private static readonly MentionTarget ArtPage = new("Art page", "Art page", MentionKind.Tab);
    private static readonly MentionTarget Worker = new("api-worker", "api-worker", MentionKind.Session, @"C:\work\api");
    private static readonly MentionTarget Explorer = new("Find the config loader", "a1b2c3d4", MentionKind.Subagent, "Explore");
    private static readonly MentionTarget[] Targets = [ArtPage, Worker, Explorer];

    // ---- Resolving a message -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("tell @\"Art page\" the build is green", "tell \"Art page\" the build is green")]
    [InlineData("@api-worker, is the migration done?", "api-worker, is the migration done?")]
    [InlineData("(ask @API-WORKER)", "(ask API-WORKER)")]
    [InlineData("ask @api-worker.", "ask api-worker.")]
    [InlineData("line one\n@\"Find the config loader\" again", "line one\n\"Find the config loader\" again")]
    public void A_mention_loses_its_at_sign(string typed, string sent)
    {
        var resolved = SessionMentions.Resolve(typed, Targets);

        Assert.Equal(sent, resolved.Text);
        Assert.Single(resolved.Mentioned);
    }

    [Theory]
    [InlineData("see @src/main.cs")]
    [InlineData("mail me@api-worker")]
    [InlineData("@api-workers are busy")]
    [InlineData("@\"Art page")]
    [InlineData("@\"Art\npage\"")]
    [InlineData("no mention at all")]
    public void Anything_else_stays_as_typed(string typed)
    {
        var resolved = SessionMentions.Resolve(typed, Targets);

        Assert.Equal(typed, resolved.Text);
        Assert.Empty(resolved.Mentioned);
    }

    [Fact]
    public void Each_one_named_is_listed_once_in_the_order_they_come()
    {
        var resolved = SessionMentions.Resolve("@api-worker then @\"Art page\", then @api-worker again", Targets);

        Assert.Equal("api-worker then \"Art page\", then api-worker again", resolved.Text);
        Assert.Equal([Worker, ArtPage], resolved.Mentioned);
    }

    // ---- What Claude is told ------------------------------------------------------------------------------------

    [Fact]
    public void One_mention_is_a_sentence_with_where_to_send()
    {
        Assert.Equal(
            "[Claudette] \"Art page\" in this message is another Claude Code session on this machine, open in another of Claudette's tabs. "
            + "To message it, call SendMessage with to: \"Art page\".",
            SessionMentions.Note([ArtPage]));
        Assert.Equal(
            "[Claudette] \"Find the config loader\" in this message is one of your subagents (Explore). To message it, call SendMessage with to: \"a1b2c3d4\".",
            SessionMentions.Note([Explorer]));
    }

    [Fact]
    public void Several_are_a_list()
    {
        var note = SessionMentions.Note([Worker, new MentionTarget("Docs", "Docs", MentionKind.SubThread)]);

        Assert.Equal(
            "[Claudette] Who this message names, each of whom you can message with SendMessage:\n"
            + "- \"api-worker\" is another Claude Code session on this machine, working in C:\\work\\api. Its to: \"api-worker\".\n"
            + "- \"Docs\" is one of this thread's sub-threads: Claudette delivers what you send it, and sends you its result when it finishes. Its to: \"Docs\".",
            note);
    }

    // ---- The list -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_list_puts_names_that_start_with_what_is_typed_first()
    {
        MentionTarget[] targets = [new("Fix the loader", "a", MentionKind.Tab), new("loader-tests", "b", MentionKind.Session), new("Reload", "c", MentionKind.Tab)];

        Assert.Equal(["loader-tests", "Fix the loader", "Reload"], SessionMentions.Match(targets, "load", 10).Select(t => t.Name));
        Assert.Equal(["Fix the loader"], SessionMentions.Match(targets, "the", 10).Select(t => t.Name));
        Assert.Empty(SessionMentions.Match(targets, "zz", 10));
    }

    [Fact]
    public void A_subagent_is_found_by_its_id_too() =>
        Assert.Equal([Explorer], SessionMentions.Match(Targets, "a1b2", 10));

    [Fact]
    public void With_nothing_typed_the_first_are_listed() =>
        Assert.Equal([ArtPage, Worker], SessionMentions.Match(Targets, "", 2));

    [Fact]
    public void Names_that_clash_are_numbered()
    {
        var unique = SessionMentions.Unique([ArtPage, ArtPage with { Kind = MentionKind.Session }]);

        Assert.Equal(["Art page", "Art page (2)"], unique.Select(t => t.Name));
        Assert.Equal(["Art page", "Art page"], unique.Select(t => t.Address));
    }

    // ---- Picking one in the list ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("tell @art", "Art page", "tell @\"Art page\" ")]
    [InlineData("tell @api", "api-worker", "tell @api-worker ")]
    [InlineData("tell @api", "sub/thread", "tell @sub/thread ")]
    public void A_picked_name_is_inserted_as_a_mention(string typed, string name, string expected)
    {
        var token = ComposerTokens.FindMention(typed, typed.Length)!.Value;

        var edit = ComposerTokens.ReplaceMention(typed, token, name, isName: true);

        Assert.Equal(expected, edit.Text);
        Assert.Equal(expected.Length, edit.Caret);
    }
}
