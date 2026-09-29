using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Claudette.Core.Settings;

namespace Claudette.App.Themes;

/// <summary>
/// The colors of Settings → Appearance → Style (DESIGN.md §3, "Visual style"). Standard is App.axaml's tokens and
/// Fluent's own palette, with the OS's accent color. Claude puts <see cref="ClaudeColors"/> over those tokens, and gives
/// Fluent a palette of the Claude apps' warm greys, ivory and orange for what it draws itself: window backgrounds, text,
/// controls and the accent, whose lighter and darker shades Fluent works out from it. Both work in light and dark. The
/// Claude style's shapes are styles under the "claude" class, which ShellView takes.
/// </summary>
public sealed class AppColors(Application app)
{
    /// <summary>Claude's orange.</summary>
    public static readonly Color ClaudeAccent = Color.Parse("#D97757");

    /// <summary>
    /// Serifs close to the Claude apps' reply typeface, most alike first, with one on each OS: Charter on macOS (and
    /// as Bitstream Charter on Linux), Georgia and Cambria on Windows, Noto Serif and DejaVu Serif on Linux.
    /// </summary>
    public const string SerifFonts = "Charter, Bitstream Charter, Georgia, Cambria, Noto Serif, DejaVu Serif";

    private readonly Dictionary<(ThemeVariant Variant, object Key), object?> _standard = [];
    private ClaudeColors? _claude;

    public AppStyle Current { get; private set; } = AppStyle.Standard;

    /// <summary>
    /// The font of Claude's replies: the conversation font when one is set, otherwise a serif in the Claude style, as
    /// the Claude apps set replies, and the app's own font in Standard.
    /// </summary>
    public static FontFamily ReplyFont(AppStyle style, string? conversationFont) =>
        conversationFont is { } font ? new FontFamily($"{font}, {FontFamily.DefaultFontFamilyName}")
        : style == AppStyle.Claude ? new FontFamily($"{SerifFonts}, {FontFamily.DefaultFontFamilyName}")
        : FontFamily.Default;

    public void Apply(AppStyle style)
    {
        if (style == Current)
        {
            return;
        }
        Current = style;
        var claude = style == AppStyle.Claude;
        _claude ??= new ClaudeColors();
        foreach (var (variant, provider) in _claude.ThemeDictionaries)
        {
            if (provider is not IResourceDictionary claudeTokens
                || !app.Resources.ThemeDictionaries.TryGetValue(variant, out var target)
                || target is not IResourceDictionary tokens)
            {
                continue;
            }
            foreach (var key in claudeTokens.Keys.ToList())
            {
                if (!_standard.ContainsKey((variant, key)))
                {
                    _standard[(variant, key)] = tokens.TryGetValue(key, out var original) ? original : null;
                }
                if (claude)
                {
                    tokens[key] = claudeTokens[key];
                }
                else if (_standard[(variant, key)] is { } original)
                {
                    tokens[key] = original;
                }
                else
                {
                    tokens.Remove(key);
                }
            }
        }
        // Fluent reads most palette colors once, when its resources are first used, so a palette set on the running
        // theme would only change the accent. A fresh theme with the palette already set reads them all.
        if (app.Styles.OfType<FluentTheme>().FirstOrDefault() is not { } fluent)
        {
            return;
        }
        var fresh = new FluentTheme { DensityStyle = fluent.DensityStyle };
        if (claude)
        {
            fresh.Palettes[ThemeVariant.Light] = LightPalette();
            fresh.Palettes[ThemeVariant.Dark] = DarkPalette();
        }
        app.Styles[app.Styles.IndexOf(fluent)] = fresh;
    }

    /// <summary>Ivory page, warm greys, near-black text.</summary>
    private static ColorPaletteResources LightPalette() => new()
    {
        Accent = ClaudeAccent,
        RegionColor = Color.Parse("#FAF9F5"),
        BaseHigh = Color.Parse("#141413"),
        BaseMediumHigh = Color.Parse("#CC141413"),
        BaseMedium = Color.Parse("#99141413"),
        BaseMediumLow = Color.Parse("#66141413"),
        BaseLow = Color.Parse("#33141413"),
        ChromeAltLow = Color.Parse("#141413"),
        ChromeBlackHigh = Color.Parse("#141413"),
        ChromeBlackMedium = Color.Parse("#99141413"),
        ChromeBlackMediumLow = Color.Parse("#66141413"),
        ChromeBlackLow = Color.Parse("#33141413"),
        ChromeDisabledHigh = Color.Parse("#E8E6DC"),
        ChromeDisabledLow = Color.Parse("#8A8880"),
        ChromeGray = Color.Parse("#73726C"),
        ChromeHigh = Color.Parse("#D6D3C8"),
        ChromeLow = Color.Parse("#F5F4ED"),
        ChromeMedium = Color.Parse("#EDEBE3"),
        ChromeMediumLow = Color.Parse("#FAF9F5"),
        ListLow = Color.Parse("#141F1E1D"),
        ListMedium = Color.Parse("#291F1E1D"),
    };

    /// <summary>Charcoal page, darker sidebar, off-white text.</summary>
    private static ColorPaletteResources DarkPalette() => new()
    {
        Accent = ClaudeAccent,
        RegionColor = Color.Parse("#262624"),
        AltHigh = Color.Parse("#262624"),
        AltMediumHigh = Color.Parse("#CC262624"),
        AltMedium = Color.Parse("#99262624"),
        AltMediumLow = Color.Parse("#66262624"),
        AltLow = Color.Parse("#33262624"),
        BaseHigh = Color.Parse("#FAF9F5"),
        BaseMediumHigh = Color.Parse("#CCFAF9F5"),
        BaseMedium = Color.Parse("#99FAF9F5"),
        BaseMediumLow = Color.Parse("#66FAF9F5"),
        BaseLow = Color.Parse("#33FAF9F5"),
        ChromeAltLow = Color.Parse("#FAF9F5"),
        ChromeDisabledHigh = Color.Parse("#3A3935"),
        ChromeDisabledLow = Color.Parse("#8A8880"),
        ChromeGray = Color.Parse("#9C9A92"),
        ChromeHigh = Color.Parse("#6B6A65"),
        ChromeLow = Color.Parse("#1F1E1D"),
        ChromeMedium = Color.Parse("#2B2A28"),
        ChromeMediumLow = Color.Parse("#30302E"),
        ListLow = Color.Parse("#19FAF9F5"),
        ListMedium = Color.Parse("#33FAF9F5"),
    };
}
