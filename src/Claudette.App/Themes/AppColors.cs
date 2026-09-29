using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Claudette.Core.Settings;

namespace Claudette.App.Themes;

/// <summary>
/// Settings → Appearance → Colors (DESIGN.md §3, "Visual style"). System accent is App.axaml's tokens and Fluent's own
/// palette, with the OS's accent color. Claude puts <see cref="ClaudeColors"/> over those tokens, and gives Fluent a
/// palette of Claude's warm greys, ivory and orange for what it draws itself: window backgrounds, text, controls and
/// the accent, whose lighter and darker shades Fluent works out from it. Both work in light and dark.
/// </summary>
public sealed class AppColors(Application app)
{
    /// <summary>Claude's orange.</summary>
    public static readonly Color ClaudeAccent = Color.Parse("#D97757");

    private readonly Dictionary<(ThemeVariant Variant, object Key), object?> _system = [];
    private ClaudeColors? _claude;

    public ColorPalette Current { get; private set; } = ColorPalette.System;

    public void Apply(ColorPalette palette)
    {
        if (palette == Current)
        {
            return;
        }
        Current = palette;
        var claude = palette == ColorPalette.Claude;
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
                if (!_system.ContainsKey((variant, key)))
                {
                    _system[(variant, key)] = tokens.TryGetValue(key, out var original) ? original : null;
                }
                if (claude)
                {
                    tokens[key] = claudeTokens[key];
                }
                else if (_system[(variant, key)] is { } original)
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
        BaseHigh = Color.Parse("#1F1E1D"),
        BaseMediumHigh = Color.Parse("#CC1F1E1D"),
        BaseMedium = Color.Parse("#991F1E1D"),
        BaseMediumLow = Color.Parse("#661F1E1D"),
        BaseLow = Color.Parse("#331F1E1D"),
        ChromeAltLow = Color.Parse("#1F1E1D"),
        ChromeBlackHigh = Color.Parse("#1F1E1D"),
        ChromeBlackMedium = Color.Parse("#991F1E1D"),
        ChromeBlackMediumLow = Color.Parse("#661F1E1D"),
        ChromeBlackLow = Color.Parse("#331F1E1D"),
        ChromeDisabledHigh = Color.Parse("#D1CFC5"),
        ChromeDisabledLow = Color.Parse("#85827A"),
        ChromeGray = Color.Parse("#7A776E"),
        ChromeHigh = Color.Parse("#D1CFC5"),
        ChromeLow = Color.Parse("#F0EEE6"),
        ChromeMedium = Color.Parse("#E8E6DC"),
        ChromeMediumLow = Color.Parse("#F5F4EF"),
        ListLow = Color.Parse("#141F1E1D"),
        ListMedium = Color.Parse("#291F1E1D"),
    };

    /// <summary>Warm charcoal page, darker sidebar, off-white text.</summary>
    private static ColorPaletteResources DarkPalette() => new()
    {
        Accent = ClaudeAccent,
        RegionColor = Color.Parse("#262624"),
        AltHigh = Color.Parse("#262624"),
        AltMediumHigh = Color.Parse("#CC262624"),
        AltMedium = Color.Parse("#99262624"),
        AltMediumLow = Color.Parse("#66262624"),
        AltLow = Color.Parse("#33262624"),
        BaseHigh = Color.Parse("#F5F4EF"),
        BaseMediumHigh = Color.Parse("#CCF5F4EF"),
        BaseMedium = Color.Parse("#99F5F4EF"),
        BaseMediumLow = Color.Parse("#66F5F4EF"),
        BaseLow = Color.Parse("#33F5F4EF"),
        ChromeAltLow = Color.Parse("#F5F4EF"),
        ChromeDisabledHigh = Color.Parse("#3A3935"),
        ChromeDisabledLow = Color.Parse("#8A8880"),
        ChromeGray = Color.Parse("#85837C"),
        ChromeHigh = Color.Parse("#6B6A65"),
        ChromeLow = Color.Parse("#1F1E1D"),
        ChromeMedium = Color.Parse("#2B2B28"),
        ChromeMediumLow = Color.Parse("#30302E"),
        ListLow = Color.Parse("#19F5F4EF"),
        ListMedium = Color.Parse("#33F5F4EF"),
    };
}
