using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Claudette.App.Services;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        // Tunnel, so the key press is recorded before a focused button or text box acts on it.
        AddHandler(KeyDownEvent, OnRecordingKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    /// <summary>While a shortcut is being recorded (Settings → Keyboard), the next key press is the new shortcut.</summary>
    private void OnRecordingKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SettingsViewModel { IsRecordingShortcut: true } settings)
        {
            return;
        }
        e.Handled = true;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            settings.CancelRecording();
        }
        else if (Shortcuts.FromKeyPress(e.Key, e.KeyModifiers) is { } chord)
        {
            settings.RecordShortcut(chord);
        }
    }
}
