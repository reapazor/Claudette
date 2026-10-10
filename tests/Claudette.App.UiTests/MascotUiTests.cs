using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Claudette.App.Mascot;
using Claudette.App.Services;
using Claudette.App.Tests.Support;
using Claudette.App.ViewModels;
using Claudette.App.Views;

namespace Claudette.App.UiTests;

/// <summary>Claudette on the composer (DESIGN.md §5): where she stands, and what she keeps clear of.</summary>
public class MascotUiTests
{
    [AvaloniaFact]
    public async Task She_stands_on_the_composer_box_over_the_conversation_and_nothing_clips_her()
    {
        await using var scene = await Scene.OpenAsync();

        await scene.UntilStandingAsync();

        var figure = scene.Layer.Figure;
        var her = scene.InWindow(figure);
        var box = scene.InWindow(scene.Box);
        Assert.Equal(box.Top, her.Bottom, precision: 1);
        Assert.InRange(her.Left, box.Left, box.Right - her.Width);
        // She stands over the conversation's foot, above the composer: nothing between her and the tab may cut her off.
        var composer = scene.InWindow(figure.FindAncestorOfType<ComposerView>()!);
        Assert.True(her.Top < composer.Top, "she stands above the composer");
        foreach (var clipping in figure.GetVisualAncestors().OfType<Visual>().TakeWhile(v => v is not TabView).Where(v => v.ClipToBounds))
        {
            Assert.True(scene.InWindow(clipping).Contains(her), $"{clipping.GetType().Name} clips her");
        }
    }

    [AvaloniaFact]
    public async Task She_comes_up_over_Send_and_keeps_to_the_last_third_of_the_box()
    {
        await using var scene = await Scene.OpenAsync();
        await scene.UntilStandingAsync();

        var send = scene.Window.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Send" && b.IsEffectivelyVisible);
        var box = scene.InWindow(scene.Box);
        var cell = scene.Layer.Figure.Bounds.Width / MascotArt.Width;
        Assert.Equal(scene.InWindow(send).Center.X, scene.InWindow(scene.Layer.Figure).Center.X, cell);

        // Her room, which she never leaves (MascotTests), is the box's last third, out to its right corner.
        var room = scene.Director.Room!.Value;
        Assert.InRange(room.Left * cell, box.Width * 2 / 3, box.Width * 2 / 3 + cell);
        Assert.InRange(box.Width - room.Right * cell, scene.Box.CornerRadius.TopRight, scene.Box.CornerRadius.TopRight + 2 + cell);
    }

    [AvaloniaFact]
    public async Task She_rides_the_composer_box_up_as_it_grows()
    {
        await using var scene = await Scene.OpenAsync();
        await scene.UntilStandingAsync();
        var before = scene.InWindow(scene.Box).Top;

        scene.Tab.ComposerText = "First line\nSecond line\nThird line\nFourth line";
        UiText.Settle(scene.Window);

        var box = scene.InWindow(scene.Box);
        Assert.True(box.Top < before - 20, "the box grew upwards");
        Assert.Equal(box.Top, scene.InWindow(scene.Layer.Figure).Bottom, precision: 1);
    }

    [AvaloniaFact]
    public async Task She_keeps_right_of_the_working_line_and_ducks_while_a_note_sits_on_the_box()
    {
        await using var scene = await Scene.OpenAsync();
        await scene.UntilStandingAsync();

        scene.Tab.ComposerText = "Refactor the parser";
        await scene.Tab.SendCommand.ExecuteAsync(null);
        scene.H.Transport.Emit("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-opus-5-5","permissionMode":"default"}""");
        await UiText.SettleUntilAsync(scene.Window, () => scene.Tab.Status == TabStatus.Working, "the turn");
        await scene.UntilAsync(() => scene.Director.Frame.Pose is "typeLeft" or "typeRight", "her typing");

        var words = scene.Window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("working-verb"));
        Assert.True(scene.InWindow(scene.Layer.Figure).Left > scene.InWindow(words).Right, "she stands clear of the working line");

        scene.Tab.AttachmentError = "notes.pdf is too big to attach.";
        await scene.UntilAsync(() => !scene.Director.Frame.IsVisible, "her to duck");
        Assert.False(scene.Layer.Figure.IsVisible);

        scene.Tab.AttachmentError = null;
        await scene.UntilAsync(() => scene.Director.Frame is { IsVisible: true, Pose: "climb" }, "her hands back on the edge");
        Assert.True(scene.Layer.Figure.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_click_on_her_startles_her_and_the_composer_takes_clicks_beside_her()
    {
        await using var scene = await Scene.OpenAsync();
        await scene.UntilStandingAsync();

        var her = scene.InWindow(scene.Layer.Figure);
        scene.Window.MouseDown(her.Center, MouseButton.Left);
        scene.Window.MouseUp(her.Center, MouseButton.Left);
        UiText.Settle(scene.Window);

        Assert.Equal("armsUp", scene.Director.Frame.Pose);
        Assert.Contains(scene.Director.Frame.Props, p => p.Name == "bang");
        // The layer itself lets clicks through: beside her, the conversation is what's there.
        var beside = new Point(her.Right + 40, her.Center.Y);
        Assert.Null((scene.Window.InputHitTest(beside) as Visual)?.FindAncestorOfType<MascotLayer>(includeSelf: true));
    }

    [AvaloniaFact]
    public async Task With_the_setting_off_she_isnt_there()
    {
        await using var h = new TabTestHarness(s => s.Appearance.ShowClaudette = false, dispatcher: new AvaloniaUiDispatcher());
        h.Services.Mascot.OnSettingsChanged();
        await h.OpenTabAsync();
        var window = UiText.Show(new ShellView { DataContext = h.Shell });

        var layer = window.GetVisualDescendants().OfType<MascotLayer>().Single(l => l.IsEffectivelyVisible);
        Assert.Null(layer.Director);
        Assert.False(layer.Figure.IsVisible);
    }

    /// <summary>A shell with one tab and Claudette on, on the fake clock.</summary>
    private sealed class Scene : IAsyncDisposable
    {
        private Scene(TabTestHarness h, TabViewModel tab, Window window)
        {
            H = h;
            Tab = tab;
            Window = window;
            Layer = window.GetVisualDescendants().OfType<MascotLayer>().Single(l => l.IsEffectivelyVisible);
            Box = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "ComposerBox" && b.IsEffectivelyVisible);
        }

        public TabTestHarness H { get; }

        public TabViewModel Tab { get; }

        public Window Window { get; }

        public MascotLayer Layer { get; }

        public Border Box { get; }

        public MascotDirector Director => H.Services.Mascot.Director!;

        public static async Task<Scene> OpenAsync()
        {
            var h = new TabTestHarness(dispatcher: new AvaloniaUiDispatcher());
            var tab = await h.OpenTabAsync();
            return new Scene(h, tab, UiText.Show(new ShellView { DataContext = h.Shell }));
        }

        /// <summary>Runs her clock on, a tenth of a second at a time, until <paramref name="condition"/> holds.</summary>
        public Task UntilAsync(Func<bool> condition, string what) =>
            Waiting.UntilAsync(condition, what, poll: () =>
            {
                H.Time.Advance(TimeSpan.FromMilliseconds(100));
                UiText.Settle(Window);
            });

        /// <summary>Up from behind the box, where she starts, and standing on it.</summary>
        public Task UntilStandingAsync() =>
            UntilAsync(() => Director.Frame is { Pose: "stand", Drop: 0 } && Layer.Figure.IsVisible, "her to stand on the box");

        public Rect InWindow(Visual visual) =>
            new Rect(visual.Bounds.Size).TransformToAABB(visual.TransformToVisual(Window) ?? Matrix.Identity);

        public async ValueTask DisposeAsync()
        {
            Window.Close();
            await H.DisposeAsync();
        }
    }
}
