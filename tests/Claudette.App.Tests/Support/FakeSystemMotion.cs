using Claudette.Core.Accessibility;

namespace Claudette.App.Tests.Support;

/// <summary>Stands in for the OS's reduce-motion setting (DESIGN.md §3, "Accessibility"), so the machine's own doesn't count.</summary>
internal sealed class FakeSystemMotion : ISystemMotion
{
    public bool PrefersReduced { get; set; }

    public int Reads { get; private set; }

    public Task<bool> PrefersReducedMotionAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(PrefersReduced);
    }
}
