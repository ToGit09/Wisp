using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Wisp.Core.Settings;

namespace Wisp.App.Theme;

/// <summary>Applies the appearance settings live — no restart, no code, no files to edit.</summary>
public static class ThemeManager
{
    public static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Blau", "#4F6BED"), ("Violett", "#7C5CFA"), ("Türkis", "#0F9D9A"), ("Grün", "#2E9D57"),
        ("Orange", "#E07A1F"), ("Rot", "#D64545"), ("Rosa", "#D6458F"), ("Graphit", "#5B6170"),
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

        var accent = TryParse(settings.Accent) ?? Color.Parse(AccentPresets[0].Hex);
        var r = app.Resources;
        r["WispAccent"] = new SolidColorBrush(accent);
        r["WispAccentSoft"] = new SolidColorBrush(Color.FromArgb(0x29, accent.R, accent.G, accent.B));
        r["WispOnAccent"] = new SolidColorBrush(Luminance(accent) > 0.55 ? Color.Parse("#1D1F24") : Colors.White);

        // Fluent controls (checkboxes, sliders, focus rings) follow the accent as well.
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = Mix(accent, Colors.White, 0.15);
        r["SystemAccentColorLight2"] = Mix(accent, Colors.White, 0.30);
        r["SystemAccentColorLight3"] = Mix(accent, Colors.White, 0.45);
        r["SystemAccentColorDark1"] = Mix(accent, Colors.Black, 0.15);
        r["SystemAccentColorDark2"] = Mix(accent, Colors.Black, 0.30);
        r["SystemAccentColorDark3"] = Mix(accent, Colors.Black, 0.45);

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
