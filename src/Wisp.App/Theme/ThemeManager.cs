using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Wisp.Core.Settings;

namespace Wisp.App.Theme;

/// <summary>Applies the appearance settings live — no restart, no code, no files to edit.</summary>
public static class ThemeManager
{
    /// <summary>Wisp 2: violet, like the app icon (Wisp 1 was blue, still among the presets).</summary>
    public const string DefaultAccent = AppearanceSettings.DefaultAccent;

    public static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Violett", DefaultAccent), ("Blau", "#4F6BED"), ("Türkis", "#0E8F8A"), ("Grün", "#2F9A5B"),
        ("Orange", "#D9771F"), ("Rot", "#D64550"), ("Rosa", "#D2468C"), ("Graphit", "#5E6272"),
    ];

    public static readonly (string Name, string Value)[] Fonts =
    [
        ("Inter (Standard)", "Inter"),
        ("Systemschrift", "System"),
        ("Serifenschrift", "Serif"),
        ("Gut lesbar (breit)", "Wide"),
    ];

    public static void Apply(AppearanceSettings settings)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = settings.Theme switch
        {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        // The accent lives in the light and the dark dictionary: on dark surfaces the same colour is lifted a
        // little, so accent-coloured text (links, counters, icons) stays readable there.
        var accent = TryParse(settings.Accent) ?? Color.Parse(AccentPresets[0].Hex);
        var r = app.Resources;
        foreach (var (variant, dark) in new[] { (ThemeVariant.Light, false), (ThemeVariant.Dark, true) })
        {
            if (!r.ThemeDictionaries.TryGetValue(variant, out var provider) || provider is not ResourceDictionary d) continue;
            var shown = dark && Luminance(accent) < 0.6 ? Mix(accent, Colors.White, 0.22) : accent;
            var brush = new SolidColorBrush(shown);
            var onAccent = new SolidColorBrush(Luminance(shown) > 0.6 ? Color.Parse("#1C1A24") : Colors.White);
            d["WispAccent"] = brush;
            d["WispAccentSoft"] = new SolidColorBrush(Color.FromArgb(dark ? (byte)0x38 : (byte)0x26, shown.R, shown.G, shown.B));
            d["WispOnAccent"] = onAccent;

            // Fluent controls (checkboxes, sliders, focus rings) follow the accent as well.
            d["SystemAccentColor"] = shown;
            d["SystemAccentColorLight1"] = Mix(shown, Colors.White, 0.15);
            d["SystemAccentColorLight2"] = Mix(shown, Colors.White, 0.30);
            d["SystemAccentColorLight3"] = Mix(shown, Colors.White, 0.45);
            d["SystemAccentColorDark1"] = Mix(shown, Colors.Black, 0.15);
            d["SystemAccentColorDark2"] = Mix(shown, Colors.Black, 0.30);
            d["SystemAccentColorDark3"] = Mix(shown, Colors.Black, 0.45);

            // Menus: the row under the mouse lights up in the accent colour (like on the Mac), text and icon turn white.
            d["MenuFlyoutItemBackgroundPointerOver"] = brush;
            d["MenuFlyoutItemBackgroundPressed"] = new SolidColorBrush(Mix(shown, Colors.Black, 0.12));
            d["MenuFlyoutItemForegroundPointerOver"] = onAccent;
            d["MenuFlyoutItemForegroundPressed"] = onAccent;
            d["MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver"] = onAccent;
            d["MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed"] = onAccent;
            d["MenuFlyoutSubItemChevronPointerOver"] = onAccent;
            d["MenuFlyoutSubItemChevronSubMenuOpened"] = onAccent;
            d["MenuFlyoutSubItemBackgroundPointerOver"] = brush;
            d["MenuFlyoutSubItemBackgroundSubMenuOpened"] = brush;
            d["MenuFlyoutSubItemForegroundPointerOver"] = onAccent;
            d["MenuFlyoutSubItemForegroundSubMenuOpened"] = onAccent;
        }

        r["WispFont"] = settings.Font switch
        {
            "System" => FontFamily.Default,
            "Serif" => new FontFamily("Georgia, Cambria, DejaVu Serif, Noto Serif, serif"),
            "Wide" => new FontFamily("Verdana, DejaVu Sans, Noto Sans, sans-serif"),
            _ => new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"),
        };

        var size = Math.Clamp(settings.FontSize, 11, 22);
        r["WispFontSize"] = size;
        r["WispSmallFontSize"] = size - 2;
        r["WispTitleFontSize"] = size + 6;
        r["WispRowPadding"] = settings.Density switch
        {
            Density.Compact => new Thickness(10, 4),
            Density.Relaxed => new Thickness(14, 13),
            _ => new Thickness(12, 8),
        };
    }

    public static Color? TryParse(string? hex) => Color.TryParse(hex, out var color) ? color : null;

    static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));
}
