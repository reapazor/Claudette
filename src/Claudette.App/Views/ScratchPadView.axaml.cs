using Avalonia.Controls;
using Avalonia.Threading;
using Claudette.App.ViewModels;

namespace Claudette.App.Views;

/// <summary>
/// The Scratch Pad page (DESIGN.md §18, "Scratch pad"). What it does beyond bindings: text added from the conversation
/// is brought into view and selected, without taking the keyboard from where it was.
/// </summary>
public partial class ScratchPadView : UserControl
{
    private ScratchPadViewModel? _pad;

    public ScratchPadView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_pad is not null)
        {
            _pad.Added -= OnAdded;
        }
        _pad = DataContext as ScratchPadViewModel;
        if (_pad is not null)
        {
            _pad.Added += OnAdded;
        }
    }

    private void OnAdded(int start, int length) =>
        // The page may only now be opening: once it's laid out.
        Dispatcher.UIThread.Post(() =>
        {
            var end = Math.Min(start + length, Editor.Text?.Length ?? 0);
            Editor.CaretIndex = end;
            Editor.SelectionStart = Math.Min(start, end);
            Editor.SelectionEnd = end;
        }, DispatcherPriority.Background);
}
