using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// A simple in-window confirmation, such as closing a working or pinned tab. It can offer a second choice besides
/// the main one, such as "Open a copy" next to "Take over".
/// </summary>
public sealed partial class ConfirmationViewModel(
    string title,
    string message,
    string confirmText,
    Func<Task> onConfirm,
    Action close,
    string? secondaryText = null,
    Func<Task>? onSecondary = null) : ViewModelBase
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    public string? SecondaryText { get; } = secondaryText;

    public bool HasSecondary => SecondaryText is not null && onSecondary is not null;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        close();
        await onConfirm();
    }

    [RelayCommand]
    private async Task SecondaryAsync()
    {
        close();
        if (onSecondary is not null)
        {
            await onSecondary();
        }
    }

    [RelayCommand]
    private void Cancel() => close();
}
