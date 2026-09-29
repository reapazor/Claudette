using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Media.Imaging;
using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;

namespace Claudette.App.Tests;

/// <summary>The composer's <c>/</c> and <c>@</c> autocomplete and attachments (DESIGN.md §5, "Composer").</summary>
public partial class ComposerTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    /// <summary>A harness whose Claude Code lists a few slash commands in its <c>initialize</c> reply.</summary>
    private static TabTestHarness WithCommands()
    {
        var h = new TabTestHarness();
        var initialize = h.Transport.Answers["initialize"];
        h.Transport.Answers["initialize"] = request =>
        {
            var response = initialize(request)!;
            response["commands"] = JsonNode.Parse("""
                [
                  {"name":"review","description":"Review the changes (project)","argumentHint":"[path]"},
                  {"name":"compact","description":"Free up context by summarizing the conversation so far","argumentHint":"","builtin":true},
                  {"name":"context","description":"Show context usage","builtin":true},
                  {"name":"clear","description":"Start a new session","aliases":["reset"],"builtin":true}
                ]
                """);
            return response;
        };
        return h;
    }

    private static string[] Titles(TabViewModel tab) => InlineDispatcher.Read(() => tab.Completions.Items.Select(i => i.Title).ToArray());

    // ---- Slash commands ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_slash_lists_the_session_commands_filtered_as_you_type()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;

        completions.Update("/", 1);
        Assert.True(completions.IsOpen);
        Assert.Equal(CompletionKind.SlashCommand, completions.Kind);
        Assert.Equal(["/review", "/compact", "/context", "/clear"], Titles(tab));
        Assert.Equal("Review the changes (project)", completions.Items[0].Detail);
        Assert.Equal("[path]", completions.Items[0].Hint);
        Assert.Null(completions.Items[1].Hint);

        completions.Update("/co", 3);
        Assert.Equal(["/compact", "/context"], Titles(tab));
        Assert.True(completions.Items[0].IsSelected);

        completions.Update("/res", 4);
        Assert.Equal(["/clear"], Titles(tab));
    }

    [Fact]
    public async Task Picking_a_command_inserts_it_and_closes()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;
        completions.Update("/co", 3);

        completions.MoveSelection(1);
        var edit = completions.Accept();

        Assert.Equal("/context ", edit?.Text);
        Assert.Equal(9, edit?.Caret);
        Assert.False(completions.IsOpen);
        // The next update, with the caret after the space, keeps it closed.
        completions.Update("/context ", 9);
        Assert.False(completions.IsOpen);
    }

    [Fact]
    public async Task A_command_typed_in_full_is_ready_to_send()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();

        // The view lets Enter send then, instead of completing the command again.
        tab.Completions.Update("/compact", 8);
        Assert.True(tab.Completions.IsTypedInFull);
        tab.Completions.Update("/comp", 5);
        Assert.False(tab.Completions.IsTypedInFull);
    }

    [Fact]
    public async Task Selection_wraps_around()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;
        completions.Update("/co", 3);

        completions.MoveSelection(-1);
        Assert.Equal("/context", completions.SelectedItem?.Title);
        completions.MoveSelection(1);
        Assert.Equal("/compact", completions.SelectedItem?.Title);
        Assert.Single(completions.Items, i => i.IsSelected);
    }

    [Fact]
    public async Task Esc_keeps_it_closed_until_the_command_changes()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;
        completions.Update("/co", 3);

        completions.Dismiss();
        completions.Update("/com", 4);
        Assert.False(completions.IsOpen);

        completions.Update("", 0);
        completions.Update("/", 1);
        Assert.True(completions.IsOpen);
    }

    [Fact]
    public async Task No_popup_for_a_slash_after_the_first_word_or_mid_message()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();

        tab.Completions.Update("/compact now", 12);
        Assert.False(tab.Completions.IsOpen);
        tab.Completions.Update("please /co", 10);
        Assert.False(tab.Completions.IsOpen);
    }

    [Fact]
    public async Task System_init_and_commands_changed_update_the_list()
    {
        await using var h = WithCommands();
        var tab = await h.OpenTabAsync();

        h.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","slash_commands":["review","compact","context","clear","doctor","ship"],"terminal_slash_commands":["doctor"]}""");
        await TabTestHarness.Eventually(() => tab.SlashCommands.Commands.Any(c => c.Name == "ship"), "the init's commands");
        tab.Completions.Update("/", 1);
        Assert.Equal(["/review", "/compact", "/context", "/clear", "/ship"], Titles(tab));

        h.Transport.Emit("""{"type":"system","subtype":"commands_changed","session_id":"s1","commands":[{"name":"deploy","description":"Deploy it"},{"name":"compact","description":"Summarize"}]}""");
        await TabTestHarness.Eventually(() => tab.SlashCommands.Commands.Any(c => c.Name == "deploy"), "the changed commands");
        tab.Completions.Update("/d", 2);
        Assert.Equal(["/deploy"], Titles(tab));
    }

    [Fact]
    public async Task Before_claude_code_lists_its_commands_the_popup_says_so()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();

        tab.Completions.Update("/", 1);

        Assert.True(tab.Completions.IsOpen);
        Assert.Empty(tab.Completions.Items);
        Assert.Equal("Claude Code hasn't listed its commands yet.", tab.Completions.Message);
    }

    // ---- @ files ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_at_lists_the_folders_files()
    {
        await using var h = new TabTestHarness();
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "src"));
        await File.WriteAllTextAsync(Path.Combine(h.WorkFolder, "src", "app.cs"), "", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(h.WorkFolder, "readme.md"), "", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "node_modules", "lib"));
        await File.WriteAllTextAsync(Path.Combine(h.WorkFolder, "node_modules", "lib", "app.js"), "", TestContext.Current.CancellationToken);
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;

        completions.Update("look at @", 9);
        Assert.True(completions.IsOpen);
        Assert.Equal(CompletionKind.File, completions.Kind);
        await TabTestHarness.Eventually(() => completions.Items.Count == 2, "the top of the folder");
        Assert.Equal(["src/", "readme.md"], Titles(tab));

        completions.Update("look at @ap", 11);
        await TabTestHarness.Eventually(() => Titles(tab) is ["app.cs"], "the match");
        Assert.Equal("src/", completions.Items[0].Detail);

        var edit = completions.Accept();
        Assert.Equal("look at @src/app.cs ", edit?.Text);
    }

    [Fact]
    public async Task Picking_a_folder_lists_what_is_in_it()
    {
        await using var h = new TabTestHarness();
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "src", "deep"));
        await File.WriteAllTextAsync(Path.Combine(h.WorkFolder, "src", "deep", "notes.txt"), "", TestContext.Current.CancellationToken);
        var tab = await h.OpenTabAsync();
        var completions = tab.Completions;
        completions.Update("@sr", 3);
        await TabTestHarness.Eventually(() => Titles(tab) is ["src/", ..], "the folder");

        var edit = completions.Accept()!.Value;
        completions.Update(edit.Text, edit.Caret);

        Assert.Equal("@src/", edit.Text);
        await TabTestHarness.Eventually(() => Titles(tab) is ["deep/", "notes.txt"], "the folder's contents");
    }

    [Fact]
    public async Task No_matches_says_so()
    {
        await using var h = new TabTestHarness();
        await File.WriteAllTextAsync(Path.Combine(h.WorkFolder, "a.txt"), "", TestContext.Current.CancellationToken);
        var tab = await h.OpenTabAsync();

        tab.Completions.Update("@zzz", 4);

        await TabTestHarness.Eventually(() => tab.Completions.Message == "No matching files", "the message");
        Assert.Empty(tab.Completions.Items);
    }

    // ---- Attachments --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_attached_image_is_sent_as_an_image_block_and_shown_on_the_message()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.True(tab.AddImage(Png, "Pasted image"));
        Assert.True(tab.SendCommand.CanExecute(null));

        tab.ComposerText = "What's in this?";
        await tab.SendCommand.ExecuteAsync(null);

        var sent = h.Transport.Sent.Single(m => m["type"]?.GetValue<string>() == "user");
        var content = sent["message"]!["content"]!.AsArray();
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Png), content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("What's in this?", content[1]!["text"]!.GetValue<string>());
        var shown = Assert.IsType<UserMessageItem>(tab.Items[0]);
        Assert.True(shown.HasImages);
        Assert.Equal(Png, Assert.Single(shown.Images).Data);
        Assert.Empty(tab.Attachments);
    }

    [Fact]
    public async Task An_image_alone_can_be_sent()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        Assert.False(tab.SendCommand.CanExecute(null));

        tab.AddImage(Png, "Pasted image");
        await tab.SendCommand.ExecuteAsync(null);

        var content = h.Transport.Sent.Single(m => m["type"]?.GetValue<string>() == "user")["message"]!["content"]!.AsArray();
        Assert.Equal("image", Assert.Single(content)!["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task Removing_an_attachment_takes_it_off_the_message()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        tab.AddImage(Png, "one");

        tab.RemoveAttachmentCommand.Execute(tab.Attachments[0]);
        tab.ComposerText = "no image";
        await tab.SendCommand.ExecuteAsync(null);

        Assert.Equal(["no image"], h.Transport.SentUserTexts);
    }

    [Fact]
    public async Task Dropped_files_become_images_mentions_or_errors()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var image = Path.Combine(h.WorkFolder, "shot.png");
        await File.WriteAllBytesAsync(image, Png, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(h.WorkFolder, "src"));
        var code = Path.Combine(h.WorkFolder, "src", "app.cs");
        await File.WriteAllTextAsync(code, "class App {}", TestContext.Current.CancellationToken);
        var binary = Path.Combine(h.WorkFolder, "tool.exe");
        await File.WriteAllBytesAsync(binary, [0x4D, 0x5A, 0, 0], TestContext.Current.CancellationToken);

        var mentions = await tab.AddFilesAsync([image, code, binary, Path.Combine(h.WorkFolder, "src")]);

        Assert.Equal("@src/app.cs @src/ ", mentions);
        Assert.Equal("shot.png", Assert.Single(tab.Attachments).Name);
        Assert.Contains("can't read tool.exe", tab.AttachmentError, StringComparison.Ordinal);
        Assert.True(tab.HasAttachmentError);
    }

    [Fact]
    public async Task At_most_twenty_images_go_on_a_message()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        for (var i = 0; i < 20; i++)
        {
            Assert.True(tab.AddImage(Png, $"image {i}"));
        }

        Assert.False(tab.AddImage(Png, "one too many"));

        Assert.Equal(20, tab.Attachments.Count);
        Assert.Equal("A message can have up to 20 images.", tab.AttachmentError);
    }

    [Fact]
    public async Task Pasting_prefers_files_then_text_then_an_image()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var file = Path.Combine(h.WorkFolder, "notes.md");
        await File.WriteAllTextAsync(file, "# Notes", TestContext.Current.CancellationToken);

        h.Platform.ClipboardImage = Png;
        await h.Platform.SetClipboardTextAsync("some text");
        Assert.Null(await tab.PasteAttachmentsAsync());
        Assert.Empty(tab.Attachments);

        h.Platform.ClipboardFiles.Add(file);
        Assert.Equal("@notes.md ", await tab.PasteAttachmentsAsync());

        h.Platform.ClipboardFiles.Clear();
        await h.Platform.SetClipboardTextAsync("");
        Assert.Equal("", await tab.PasteAttachmentsAsync());
        Assert.Equal("Pasted image", Assert.Single(tab.Attachments).Name);

        h.Platform.ClipboardImage = null;
        Assert.Null(await tab.PasteAttachmentsAsync());
    }

    [Fact]
    public async Task A_pasted_screenshot_is_sent_before_the_text_and_leaves_the_composer()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        h.Platform.ClipboardImage = Png;

        Assert.Equal("", await tab.PasteAttachmentsAsync());
        Assert.Equal("Pasted image", Assert.Single(tab.Attachments).Name);
        Assert.True(tab.SendCommand.CanExecute(null));
        tab.ComposerText = "The walk cycle takes half the screen here";
        await tab.SendCommand.ExecuteAsync(null);

        var content = h.Transport.Sent.Single(m => m["type"]?.GetValue<string>() == "user")["message"]!["content"]!.AsArray();
        Assert.Equal(["image", "text"], content.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("base64", content[0]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Png), content[0]!["source"]!["data"]!.GetValue<string>());
        Assert.Equal("The walk cycle takes half the screen here", content[1]!["text"]!.GetValue<string>());
        Assert.Empty(tab.Attachments);
        Assert.Equal("", tab.ComposerText);
        Assert.False(tab.SendCommand.CanExecute(null));
    }

    [Fact]
    public async Task Text_that_is_only_white_space_does_not_keep_an_image_from_pasting()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        // Some apps put a line break or a space on the clipboard with a copied picture.
        await h.Platform.SetClipboardTextAsync("\r\n");
        h.Platform.ClipboardImage = Png;

        Assert.Equal("", await tab.PasteAttachmentsAsync());
        Assert.Single(tab.Attachments);

        // Without an image, the white space pastes as text as usual.
        h.Platform.ClipboardImage = null;
        Assert.Null(await tab.PasteAttachmentsAsync());
        Assert.Single(tab.Attachments);
    }

    [Fact]
    public async Task A_pasted_image_that_is_too_big_or_unreadable_says_why()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var huge = new byte[Core.Composer.Attachments.MaxImageBytes + 1];
        Png.CopyTo(huge, 0);
        h.Platform.ClipboardImage = huge;

        Assert.Equal("", await tab.PasteAttachmentsAsync());

        Assert.Empty(tab.Attachments);
        // Rounded up, so it doesn't read "20 MB; images can be up to 20 MB".
        Assert.Matches(@"^Pasted image is 20[.,]1 MB; images can be up to 20 MB\.$", tab.AttachmentError);
        Assert.False(tab.SendCommand.CanExecute(null));

        h.Platform.ClipboardImage = [0x42, 0x4D, 0, 0];
        await tab.PasteAttachmentsAsync();
        Assert.Equal("Pasted image isn't an image Claude can read. Use PNG, JPEG, GIF or WebP.", tab.AttachmentError);
    }

    [Fact]
    public void A_pasted_bitmap_is_PNG_unless_that_is_too_big_then_JPEG()
    {
        // A screenshot: PNG, and nothing else is tried.
        Assert.Same(Png, ImageFiles.Encode(options => options is PngBitmapEncoderOptions ? Png : throw new InvalidOperationException("Only PNG was needed.")));

        // A photo-like 5K frame can be 20 MB or more as PNG (22.6 MB measured with Skia); as JPEG it's a tenth of that.
        var tried = new List<BitmapEncoderOptions>();
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0];
        var encoded = ImageFiles.Encode(options =>
        {
            tried.Add(options);
            return options is PngBitmapEncoderOptions ? new byte[Core.Composer.Attachments.MaxImageBytes + 1] : jpeg;
        });

        Assert.Same(jpeg, encoded);
        Assert.Equal(90, Assert.IsType<JpegBitmapEncoderOptions>(tried[1]).Quality);
        Assert.Equal("image/jpeg", Core.Protocol.MessageImage.DetectMediaType(encoded));
    }

    [Fact]
    public async Task A_restored_prompt_shows_its_images()
    {
        await using var h = new TabTestHarness();
        h.WriteTranscript("img-1", new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "Match this design" },
                    new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = Convert.ToBase64String(Png) } }),
            },
            ["cwd"] = h.WorkFolder,
            ["sessionId"] = "img-1",
            ["timestamp"] = "2026-09-28T10:00:00Z",
        }.ToJsonString());
        h.Shell.OpenHistoryCommand.Execute(null);
        var history = h.Shell.History!;
        await TabTestHarness.Eventually(() => !history.IsLoading, "History to load");

        await history.OpenCommand.ExecuteAsync(Assert.Single(Assert.Single(history.Groups).Entries));

        var tab = h.Shell.SelectedTab!;
        await TabTestHarness.Eventually(() => tab.Items.OfType<UserMessageItem>().Any(), "the restored prompt");
        var prompt = InlineDispatcher.Read(() => tab.Items.OfType<UserMessageItem>().Single());
        Assert.Equal("Match this design", prompt.Text);
        Assert.Equal(Png, Assert.Single(prompt.Images).Data);
    }

    // ---- Tool cards -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Read", "IconToolRead")]
    [InlineData("Edit", "IconToolEdit")]
    [InlineData("MultiEdit", "IconToolEdit")]
    [InlineData("Write", "IconToolWrite")]
    [InlineData("Bash", "IconToolBash")]
    [InlineData("Grep", "IconToolSearch")]
    [InlineData("Glob", "IconToolFolder")]
    [InlineData("WebFetch", "IconToolWeb")]
    [InlineData("WebSearch", "IconToolWebSearch")]
    [InlineData("Agent", "IconToolAgent")]
    [InlineData("Task", "IconToolAgent")]
    [InlineData("TodoWrite", "IconToolTodo")]
    [InlineData("mcp__github__create_issue", "IconToolMcp")]
    [InlineData("Skill", "IconToolSkill")]
    [InlineData("SomethingNew", "IconToolDefault")]
    public void Each_tool_has_an_icon(string tool, string key) =>
        Assert.Equal(key, new ToolUseItem("t1", tool, []).IconKey);

    [Fact]
    public void Every_icon_is_defined_in_the_app_resources()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Claudette.App", "App.axaml"));
        var defined = GeometryKey().Matches(app).Select(m => m.Groups[1].Value).ToHashSet();
        var tools = new[] { "Read", "Edit", "Write", "Bash", "Grep", "Glob", "WebFetch", "WebSearch", "Agent", "TodoWrite", "mcp__x__y", "Skill", "AskUserQuestion", "ExitPlanMode", "Other" };

        Assert.All(tools.Select(ToolIcons.KeyFor).Append("IconToolFolder").Append("IconAttach"), key => Assert.Contains(key, defined));
    }

    [Fact]
    public async Task Open_diff_on_an_edit_card_opens_the_file_in_the_diff_view()
    {
        await using var h = new TabTestHarness();
        var tab = await h.OpenTabAsync();
        var path = Path.Combine(h.WorkFolder, "auth.cs");
        await File.WriteAllTextAsync(path, "new line\n", TestContext.Current.CancellationToken);
        Diffs.DiffSource? requested = null;
        tab.DiffRequested += source => requested = source;

        h.Transport.Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "e1", ["name"] = "Edit", ["input"] = new JsonObject { ["file_path"] = path, ["old_string"] = "old line", ["new_string"] = "new line" } }) },
        });
        h.Transport.Emit(new JsonObject
        {
            ["type"] = "user",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = "e1", ["content"] = "Edited" }) },
            ["tool_use_result"] = new JsonObject { ["filePath"] = path, ["oldString"] = "old line", ["newString"] = "new line", ["originalFile"] = "old line\n", ["structuredPatch"] = new JsonArray() },
        });
        await TabTestHarness.Eventually(() => tab.Items.OfType<ToolUseItem>().Any(t => t.CanOpenDiff), "the finished edit");
        var card = InlineDispatcher.Read(() => tab.Items.OfType<ToolUseItem>().Single());

        await tab.OpenToolDiffCommand.ExecuteAsync(card);

        Assert.NotNull(requested);
        Assert.Equal(path, requested.Path);
        Assert.Equal("auth.cs", requested.DisplayPath);
        Assert.Equal("old line\n", requested.Before);
    }

    [Fact]
    public void Only_a_finished_file_change_can_be_opened()
    {
        var read = new ToolUseItem("r1", "Read", []) { IsComplete = true };
        var write = new ToolUseItem("w1", "Write", []);
        Assert.False(read.CanOpenDiff);
        Assert.False(write.CanOpenDiff);

        write.IsComplete = true;
        Assert.True(write.CanOpenDiff);
        write.IsError = true;
        Assert.False(write.CanOpenDiff);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Claudette.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Couldn't find the repository root.");
    }

    [GeneratedRegex("""<StreamGeometry x:Key="([A-Za-z]+)">""")]
    private static partial Regex GeometryKey();
}
