using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>A simple in-window confirmation, such as closing a working or pinned tab.</summary>
public sealed partial class ConfirmationViewModel(string title, string message, string confirmText, Func<Task> onConfirm, Action close) : ViewModelBase
{
    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmText { get; } = confirmText;

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        close();
        await onConfirm();
    }

    [RelayCommand]
    private void Cancel() => close();
}
