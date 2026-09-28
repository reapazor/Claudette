namespace Claudette.App.Services;

/// <summary>OS dialogs and the browser, behind an interface so view models stay testable.</summary>
public interface IPlatformServices
{
    Task<string?> PickFolderAsync(string title);

    Task<string?> PickFileAsync(string title);

    Task OpenUrlAsync(string url);
}

/// <summary>Runs work on the UI thread.</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}
