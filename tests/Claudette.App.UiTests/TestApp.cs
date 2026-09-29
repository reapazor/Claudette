using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Claudette.App.UiTests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace Claudette.App.UiTests;

/// <summary>
/// Claudette's own application (its styles, resources and templates) on Avalonia's headless platform. Headless drawing
/// measures text with fixed metrics, so layout is the same on every OS (DESIGN.md §15, "UI").
/// </summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [ModuleInitializer]
    public static void Initialize()
    {
        // Numbers and dates in snapshots read the same on every machine.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        VerifierSettings.DontScrubDateTimes();
    }
}
