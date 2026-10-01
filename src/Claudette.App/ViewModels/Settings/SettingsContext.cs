using Claudette.App.Services;
using Claudette.Core.Settings;

namespace Claudette.App.ViewModels.Settings;

/// <summary>
/// What the Settings window's pages share (DESIGN.md §14): the services, the settings they change, saving them, and
/// the shortcut being recorded, which Keyboard and Quick suffixes both set.
/// </summary>
public sealed class SettingsContext
{
    public SettingsContext(AppServices services)
    {
        Services = services;
        Shortcuts = new ShortcutRecorder(services.Settings, Save);
    }

    public AppServices Services { get; }

    public AppSettings Settings => Services.Settings;

    public ShortcutRecorder Shortcuts { get; }

    /// <summary>Saves the settings, which applies them: changes apply immediately, with no Save button.</summary>
    public void Save() => Services.SaveSettings();

    /// <summary>Every page shows its settings again, after settings sync took another machine's (DESIGN.md §14).</summary>
    public void RefreshAllPages() => AllPagesChanged?.Invoke();

    /// <summary>Raised by <see cref="RefreshAllPages"/>; each page refreshes everything it shows.</summary>
    public event Action? AllPagesChanged;
}
