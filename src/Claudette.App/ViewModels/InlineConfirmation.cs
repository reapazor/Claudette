using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Claudette.App.ViewModels;

/// <summary>
/// A second step that shows in place before something that is hard to take back, such as Bypass permissions or
/// reverting a file: <see cref="Ask"/> opens it, and its <b>Confirm</b> or <b>Cancel</b> closes it.
/// </summary>
public sealed partial class InlineConfirmation(Func<Task> confirm) : ObservableObject
{
    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [RelayCommand]
    public void Ask() => IsOpen = true;

    [RelayCommand]
    private Task ConfirmAsync()
    {
        if (!IsOpen)
        {
            return Task.CompletedTask;
        }
        IsOpen = false;
        return confirm();
    }

    [RelayCommand]
    public void Cancel() => IsOpen = false;
}

/// <summary>
/// An <see cref="InlineConfirmation"/> about one thing, such as the model to switch to: <see cref="Value"/> is what
/// <b>Confirm</b> goes ahead with.
/// </summary>
public sealed partial class InlineConfirmation<T>(Func<T, Task> confirm) : ObservableObject where T : class
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial T? Value { get; private set; }

    public bool IsOpen => Value is not null;

    public void Ask(T value) => Value = value;

    [RelayCommand]
    private Task ConfirmAsync()
    {
        if (Value is not { } value)
        {
            return Task.CompletedTask;
        }
        Value = null;
        return confirm(value);
    }

    [RelayCommand]
    public void Cancel() => Value = null;
}
