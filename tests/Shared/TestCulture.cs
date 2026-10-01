using System.Globalization;
using System.Runtime.CompilerServices;

namespace Claudette.Tests;

/// <summary>
/// Every test project runs in the invariant culture, whatever the machine's, so numbers read the way the assertions
/// expect them ("40,000 of 200,000") on a de-DE or fr-FR machine too.
/// </summary>
internal static class TestCulture
{
#pragma warning disable CA2255 // A test assembly is the one place a module initializer should set the culture.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Use()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
    }
}
