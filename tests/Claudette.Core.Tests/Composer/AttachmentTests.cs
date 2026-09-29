using System.Text.Json.Nodes;
using Claudette.Core.Composer;
using Claudette.Core.Protocol;
using Claudette.Core.Tests.Support;
using Claudette.Core.Transcripts;

namespace Claudette.Core.Tests.Composer;

/// <summary>Dropped and pasted files, and the messages that carry images (DESIGN.md §5, "Attachments").</summary>
public sealed class AttachmentTests : IDisposable
{
    internal static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly TempFolder _root = new("claudette-attach");

    public void Dispose() => _root.Dispose();

    private string Work => _root.CreateFolder("work");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' }, "image/gif")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0, 0 }, null)]
    [InlineData(new byte[] { }, null)]
    public void Image_types_are_told_by_their_first_bytes(byte[] data, string? mediaType) =>
        Assert.Equal(mediaType, MessageImage.DetectMediaType(data));

    [Fact]
    public async Task An_image_file_is_attached_as_an_image()
    {
        var path = Path.Combine(Work, "shot.png");
        await File.WriteAllBytesAsync(path, Png, Token);

        var attachment = await Attachments.FromPathAsync(path, Work, Token);

        Assert.Equal("image/png", attachment.Image?.MediaType);
        Assert.Equal(Png, attachment.Image?.Data);
        Assert.Null(attachment.Mention);
    }

    [Fact]
    public async Task A_text_file_in_the_folder_becomes_a_relative_mention()
    {
        _root.Write("work/src/app.cs", "class App {}");
        _root.Write("work/docs/my notes.md", "# Notes");

        Assert.Equal("@src/app.cs", (await Attachments.FromPathAsync(_root.Combine("work", "src", "app.cs"), Work, Token)).Mention);
        Assert.Equal("@\"docs/my notes.md\"", (await Attachments.FromPathAsync(_root.Combine("work", "docs", "my notes.md"), Work, Token)).Mention);
    }

    [Fact]
    public async Task Files_elsewhere_and_folders_are_mentioned_by_full_path()
    {
        var outside = _root.Write("elsewhere/readme.txt", "hello");
        _root.CreateFolder("work/src");

        Assert.Equal(ComposerTokens.Mention(Path.GetFullPath(outside)), (await Attachments.FromPathAsync(outside, Work, Token)).Mention);
        Assert.Equal("@src/", (await Attachments.FromPathAsync(_root.Combine("work", "src"), Work, Token)).Mention);
    }

    [Fact]
    public async Task A_pdf_is_mentioned_but_other_binary_files_are_refused()
    {
        var pdf = Path.Combine(Work, "spec.pdf");
        await File.WriteAllBytesAsync(pdf, [.. "%PDF-1.7\n"u8, 0, 1, 2], Token);
        var binary = Path.Combine(Work, "app.dll");
        await File.WriteAllBytesAsync(binary, [0x4D, 0x5A, 0, 0, 3], Token);
        var bmp = Path.Combine(Work, "old.bmp");
        await File.WriteAllBytesAsync(bmp, [(byte)'B', (byte)'M', 0, 0], Token);

        Assert.Equal("@spec.pdf", (await Attachments.FromPathAsync(pdf, Work, Token)).Mention);
        Assert.Contains("can't read app.dll", (await Attachments.FromPathAsync(binary, Work, Token)).Error, StringComparison.Ordinal);
        Assert.NotNull((await Attachments.FromPathAsync(bmp, Work, Token)).Error);
        Assert.Contains("no longer exists", (await Attachments.FromPathAsync(Path.Combine(Work, "gone.txt"), Work, Token)).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Images_over_the_size_limit_are_refused()
    {
        var path = Path.Combine(Work, "huge.png");
        await using (var file = File.Create(path))
        {
            await file.WriteAsync(Png, Token);
            file.SetLength(Attachments.MaxImageBytes + 1);
        }

        var attachment = await Attachments.FromPathAsync(path, Work, Token);

        Assert.Null(attachment.Image);
        Assert.Contains("up to 20 MB", attachment.Error, StringComparison.Ordinal);
        Assert.NotNull(Attachments.FromImageBytes(new byte[Attachments.MaxImageBytes + 1], "big").Error);
        Assert.Contains("isn't an image", Attachments.FromImageBytes([1, 2, 3], "odd").Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_message_with_images_sends_them_before_the_text()
    {
        var message = OutgoingMessages.UserMessage("what is this? see @a.txt", [new MessageImage("image/png", Png)]);

        var content = message["message"]!["content"]!.AsArray();
        Assert.Equal("user", message["type"]!.GetValue<string>());
        Assert.Equal(2, content.Count);
        Assert.Equal("image", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("base64", content[0]!["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", content[0]!["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Png, Convert.FromBase64String(content[0]!["source"]!["data"]!.GetValue<string>()));
        // Last, because Claude Code only expands @mentions in the last block.
        Assert.Equal("what is this? see @a.txt", content[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Without_images_or_text_the_message_stays_simple()
    {
        Assert.Equal("hi", OutgoingMessages.UserMessage("hi", [])["message"]!["content"]!.GetValue<string>());
        Assert.Single(OutgoingMessages.UserMessage("", [new MessageImage("image/png", Png)])["message"]!["content"]!.AsArray());
    }

    [Fact]
    public void A_transcript_prompt_keeps_its_images()
    {
        var data = Convert.ToBase64String(Png);
        var transcript = TranscriptReader.Read(
        [
            new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "What is in this image?" },
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = data } }),
                },
            }.ToJsonString(),
            // Claude Code notes where it cached the image in a meta entry, which isn't shown.
            """{"type":"user","isMeta":true,"message":{"role":"user","content":[{"type":"text","text":"[Image: source: /tmp/x/images/1.png]"}]}}""",
            new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = "not base64!" } },
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/png", ["data"] = data } }),
                },
            }.ToJsonString(),
        ]);

        var prompts = transcript.Items.OfType<TranscriptPrompt>().ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.Equal("What is in this image?", prompts[0].Text);
        Assert.Equal(Png, Assert.Single(prompts[0].Images).Data);
        // An image-only prompt, with the unreadable image skipped.
        Assert.Equal("", prompts[1].Text);
        Assert.Equal("image/png", Assert.Single(prompts[1].Images).MediaType);
    }
}
