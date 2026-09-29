using Claudette.Platform.Notifications;

namespace Claudette.App.Tests.Support;

/// <summary>Records the OS notifications and badge a test caused, and can "click" one.</summary>
internal sealed class FakeNotifier : INotifier, IAppBadge
{
    public List<OsNotification> Shown { get; } = [];

    public List<string> Removed { get; } = [];

    public int? Badge { get; private set; }

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
