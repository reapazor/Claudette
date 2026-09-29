using Claudette.Platform.Notifications;

namespace Claudette.App.Tests.Support;

/// <summary>Records the OS notifications, badge, icon frames and flashes a test caused, and can "click" a notification.</summary>
internal sealed class FakeNotifier : INotifier, IAppBadge
{
    public List<OsNotification> Shown { get; } = [];

    public List<string> Removed { get; } = [];

    public int? Badge { get; private set; }

    /// <summary>None unless a test says otherwise, so the icon's animation stays out of tests that aren't about it.</summary>
    public AppIconSurface Surface { get; set; } = AppIconSurface.None;

    /// <summary>The animation frame the icon shows, or null.</summary>
    public byte[]? Frame { get; private set; }

    public string? FrameDescription { get; private set; }

    public bool Flashing { get; private set; }

    public int Flashes { get; private set; }

    public bool IsAvailable { get; set; } = true;

    public event Action<string>? Activated;

    public void Show(OsNotification notification)
    {
        lock (Shown)
        {
            Shown.Add(notification);
        }
    }

    public void Remove(string id) => Removed.Add(id);

    public void SetCount(int count) => Badge = count;

    public void ShowFrame(byte[]? png, string? description)
    {
        Frame = png;
        FrameDescription = description;
    }

    public void Flash(bool on)
    {
        Flashing = on;
        Flashes += on ? 1 : 0;
    }

    /// <summary>The user clicks the last notification shown with this id.</summary>
    public void Click(string id) => Activated?.Invoke(id);

    public OsNotification? Last(string idPrefix)
    {
        lock (Shown)
        {
            return Shown.LastOrDefault(n => n.Id.StartsWith(idPrefix, StringComparison.Ordinal));
        }
    }

    public void Dispose()
    {
    }
}
