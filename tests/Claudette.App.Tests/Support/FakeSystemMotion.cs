using Claudette.Core.Accessibility;

namespace Claudette.App.Tests.Support;

/// <summary>Stands in for the OS's reduce-motion setting (DESIGN.md §3, "Accessibility"), so the machine's own doesn't count.</summary>
internal sealed class FakeSystemMotion : ISystemMotion
{
    public bool PrefersReduced { get; set; }

    public int Reads { get; private set; }

    /// <summary>Set to hold reads until it's completed, as a slow <c>gsettings</c> would.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        if (Gate is { } gate)
        {
            await gate.Task;
        }
        return PrefersReduced;
    }
}
