using Claudette.App.Conversation;
using Claudette.App.Services;
using Claudette.Core.Files;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary><b>Export conversation…</b> (DESIGN.md §5, "Export"): the conversation as a Markdown or HTML file.</summary>
public sealed partial class TabViewModel
{
    internal static readonly IReadOnlyList<SaveFileType> ExportTypes = [new("Markdown", "md"), new("Web page", "html")];

    [RelayCommand]
    private async Task ExportConversationAsync()
    {
        var name = string.Concat(DisplayName.Split(Path.GetInvalidFileNameChars())).Trim();
        if (await _services.Platform.PickSaveFileAsync("Export the conversation", $"{(name.Length > 0 ? name : "Conversation")}.md", ExportTypes) is not { } path)
        {
            return;
        }
        var html = Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";
        // Built on the UI thread, which owns the items; written off it.
        var text = html
            ? ConversationExport.ToHtml(DisplayName, Items.ToArray(), Folder)
            : ConversationExport.ToMarkdown(DisplayName, Items.ToArray(), Folder);
        try
        {
            await Task.Run(() => AtomicFile.WriteAllText(path, text));
            _conversation.AddNote($"Exported the conversation to {path}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _conversation.AddNote($"Couldn't export the conversation: {ex.Message}", NoteKind.Error);
        }
    }
}
