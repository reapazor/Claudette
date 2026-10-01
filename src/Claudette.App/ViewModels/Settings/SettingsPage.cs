using System.Runtime.CompilerServices;
using Claudette.App.Services;
using Claudette.Core.Settings;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// One page of the Settings window (DESIGN.md §14): a category, or one of the selected tab's project pages. Each owns
/// its settings, the search box's entries for them, and <b>Reset to defaults</b>. Pages share a
/// <see cref="SettingsContext"/>.
/// </summary>
public abstract partial class SettingsPage : ViewModelBase
{
    protected SettingsPage(SettingsContext context, string title)
    {
        Context = context;
        Title = title;
        context.AllPagesChanged += () => OnPropertyChanged(string.Empty);
    }

    /// <summary>The page's name in the sidebar: one of <see cref="SettingsCategory"/>, or a project page's.</summary>
    public string Title { get; }

    protected SettingsContext Context { get; }

    protected AppServices Services => Context.Services;

    protected AppSettings Settings => Context.Settings;

    /// <summary>
    /// The page's settings for the search box, by the names they're found under, beside the properties they find.
    /// SettingsWindowTests checks that the page shows each one (<see cref="SettingsSearchResult.PageText"/>).
    /// </summary>
    public abstract IEnumerable<SettingsSearchResult> SearchEntries { get; }

    /// <summary>A search entry on this page; <paramref name="pageText"/> when the page words it differently.</summary>
    protected virtual SettingsSearchResult Entry(string label, string? pageText = null) => new(Title, label) { PageText = pageText };

    /// <summary>
    /// <b>Reset to defaults</b>: puts the page's settings back, then refreshes everything the page shows, so no value
    /// can be left showing what it was.
    /// </summary>
    [RelayCommand]
    private void Reset()
    {
        ResetSettings();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Puts the page's settings back to their defaults, and saves them.</summary>
    protected abstract void ResetSettings();

    protected void Save() => Context.Save();

    /// <summary>Changes a setting from its property: applies it, says the property changed, and saves.</summary>
    protected void Set<T>(T value, Action<T> apply, [CallerMemberName] string? property = null)
    {
        apply(value);
        OnPropertyChanged(property);
        Save();
    }
}

/// <summary>A retention option in a dropdown.</summary>
public sealed record RetentionChoice(RetentionPeriod Period)
{
    public override string ToString() => Period.Label();
}
