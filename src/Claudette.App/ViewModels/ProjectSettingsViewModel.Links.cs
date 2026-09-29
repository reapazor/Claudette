using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Claudette.Core.ProjectTools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// A link on Settings' Links page (DESIGN.md §18, "Links"). It keeps the entry's JSON as written, so saving keeps fields
/// Claudette doesn't edit.
/// </summary>
/// <param name="Link">What it reads as; null when it can't be read, when it can only be removed.</param>
/// <param name="Problem">Why it can't be read.</param>
public sealed record ProjectLinkRow(JsonNode? Raw, ProjectLink? Link, string? Problem, ProjectFileScope Scope)
{
    public string Name => Link?.Name ?? "(can't be read)";

    /// <summary>The name as written: empty when the entry has none, and the project's menu shows the address.</summary>
    public string GivenName => LenientJson.String(Raw, "name")?.Trim() ?? "";

    /// <summary>The address as written, placeholders and all.</summary>
    public string Url => Link?.Url ?? "";

    /// <summary>Which file it's in: "Shared (claudette.json)" or "Just me (claudette.local.json)".</summary>
    public string FileText => ProjectSettingsViewModel.FileLabel(Scope);

    public bool CanEdit => Link is not null;

    /// <summary>Why the project's menu won't open it (its scheme, a placeholder it doesn't know), or why it can't be read.</summary>
    public string? Warning => Link is { } link ? ProjectLinks.Validate(link.Url) : Problem;

    public bool HasWarning => Warning is not null;

    /// <summary>The entry to write back.</summary>
    public JsonNode? ToJson() => Link is { } link ? ProjectFile.LinkToJson(GivenName, link.Url, Raw) : Raw?.DeepClone();

    /// <summary>A link as the editor saved it: its JSON, with <paramref name="raw"/>'s other fields kept.</summary>
    public static ProjectLinkRow From(string name, string url, ProjectFileScope scope, JsonNode? raw = null)
    {
        var json = ProjectFile.LinkToJson(name, url, raw);
        return new ProjectLinkRow(json, new ProjectLink(name.Trim().Length > 0 ? name.Trim() : url.Trim(), url.Trim(), scope), null, scope);
    }
}

/// <summary>
/// Settings → the tab's project → <b>Links</b> (DESIGN.md §14, §18): the links of both files in the order the project's
/// menu shows them, the shared file's first. Each can be added, edited, removed or moved within its file, and the file is
/// saved at once. Only <c>links</c> is rewritten; a file that isn't valid JSON is left alone.
/// </summary>
public sealed partial class ProjectSettingsViewModel
{
    private readonly Dictionary<ProjectFileScope, string?> _linkFileErrors = [];

    /// <summary>Both files' links: <c>claudette.json</c>'s, then <c>claudette.local.json</c>'s.</summary>
    public ObservableCollection<ProjectLinkRow> Links { get; } = [];

    public bool HasLinks => Links.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditLinkCommand), nameof(RemoveLinkCommand), nameof(MoveLinkUpCommand), nameof(MoveLinkDownCommand))]
    public partial ProjectLinkRow? SelectedLink { get; set; }

    /// <summary>Files that can't be read, so their links can't be edited here: "claudette.json isn't valid JSON (…)".</summary>
    public IReadOnlyList<string> LinkFileErrors => [.. _linkFileErrors.Values.OfType<string>()];

    public bool HasLinkFileErrors => LinkFileErrors.Count > 0;

    /// <summary>Why the last change couldn't be saved; the list is back to what the files hold.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinksError))]
    public partial string? LinksError { get; private set; }

    public bool HasLinksError => LinksError is not null;

    /// <summary>Why a file can't take links now, for the link dialog; null when it can.</summary>
    private string? LinkFileProblem(ProjectFileScope scope) => _linkFileErrors.GetValueOrDefault(scope);

    private void LoadLinks()
    {
        Links.Clear();
        foreach (var scope in (ProjectFileScope[])[ProjectFileScope.Shared, ProjectFileScope.Local])
        {
            try
            {
                foreach (var entry in ProjectFile.ReadLinkEntries(Folder, scope))
                {
                    Links.Add(new ProjectLinkRow(entry.Raw, entry.Link, entry.Problem, scope));
                }
                _linkFileErrors[scope] = null;
            }
            catch (InvalidOperationException ex)
            {
                _linkFileErrors[scope] = ex.Message;
            }
        }
        SelectedLink = null;
        LinksChanged();
    }

    private void LinksChanged()
    {
        OnPropertyChanged(nameof(HasLinks));
        OnPropertyChanged(nameof(LinkFileErrors));
        OnPropertyChanged(nameof(HasLinkFileErrors));
        EditLinkCommand.NotifyCanExecuteChanged();
        RemoveLinkCommand.NotifyCanExecuteChanged();
        MoveLinkUpCommand.NotifyCanExecuteChanged();
        MoveLinkDownCommand.NotifyCanExecuteChanged();
    }

    /// <summary><b>Add a link…</b> from the project's menu: the same dialog as <b>Add…</b>.</summary>
    internal void StartNewLink() => AddLink();

    /// <summary><b>Add…</b>: the link dialog, which asks which file it goes in (just the user's by default).</summary>
    [RelayCommand]
    private void AddLink() => Editor = new ProjectLinkEditorViewModel(Folder, null, LinkFileProblem, (name, url, scope) =>
    {
        var row = ProjectLinkRow.From(name, url, scope);
        // The shared file's links come first, so a shared one goes at the end of those.
        Links.Insert(scope == ProjectFileScope.Shared ? Links.Count(r => r.Scope == ProjectFileScope.Shared) : Links.Count, row);
        if (SaveLinks(scope))
        {
            SelectedLink = row;
        }
    }, CloseEditor);

    [RelayCommand(CanExecute = nameof(CanEditSelectedLink))]
    private void EditLink()
    {
        if (SelectedLink is not { CanEdit: true } row)
        {
            return;
        }
        Editor = new ProjectLinkEditorViewModel(Folder, row, LinkFileProblem, (name, url, _) =>
        {
            var index = Links.IndexOf(row);
            if (index < 0)
            {
                return;
            }
            var edited = ProjectLinkRow.From(name, url, row.Scope, row.Raw);
            Links[index] = edited;
            if (SaveLinks(row.Scope))
            {
                SelectedLink = edited;
            }
        }, CloseEditor);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedLink))]
    private void RemoveLink()
    {
        if (SelectedLink is { } row && Links.Remove(row))
        {
            SelectedLink = null;
            SaveLinks(row.Scope);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveLinkUp))]
    private void MoveLinkUp() => MoveLink(-1);

    [RelayCommand(CanExecute = nameof(CanMoveLinkDown))]
    private void MoveLinkDown() => MoveLink(1);

    /// <summary>Moves a link past its neighbor in the same file; the shared file's links always come first.</summary>
    private void MoveLink(int by)
    {
        if (SelectedLink is not { } row || !CanMoveLink(by))
        {
            return;
        }
        var index = Links.IndexOf(row);
        Links.Move(index, index + by);
        SelectedLink = row;
        SaveLinks(row.Scope);
    }

    private bool HasSelectedLink() => SelectedLink is not null;

    private bool CanEditSelectedLink() => SelectedLink?.CanEdit == true;

    private bool CanMoveLinkUp() => CanMoveLink(-1);

    private bool CanMoveLinkDown() => CanMoveLink(1);

    private bool CanMoveLink(int by)
    {
        if (SelectedLink is not { } row)
        {
            return false;
        }
        var target = Links.IndexOf(row) + by;
        return target >= 0 && target < Links.Count && Links[target].Scope == row.Scope;
    }

    /// <summary>
    /// Writes one file's links as the list has them. When that fails (the file stopped being JSON, say), the list goes
    /// back to what the files hold and the page says why.
    /// </summary>
    private bool SaveLinks(ProjectFileScope scope)
    {
        try
        {
            ProjectFile.WriteLinks(Folder, scope, Links.Where(r => r.Scope == scope).Select(r => r.ToJson()));
        }
        catch (Exception ex) when (IsSaveFailure(ex))
        {
            LoadLinks();
            LinksError = SaveFailure(scope, ex);
            return false;
        }
        LinksError = null;
        LinksChanged();
        OnFileSaved();
        return true;
    }
}
