using System.Globalization;
using System.Text.RegularExpressions;

namespace Wisp.Core.Rendering;

/// <summary>
/// Rewrites CSS colours into forms the HTML renderer reads correctly. It takes the alpha of rgba() as 0–255,
/// so the very common <c>rgba(0,0,0,0.87)</c> (Google, many newsletters) came out fully transparent and the
/// text vanished. hsl(), percentages, the space syntax and #RGBA / #RRGGBBAA it does not understand at all.
/// </summary>
public static partial class CssColors
{
    public static string Normalize(string css)
    {
        css = ColorFunction().Replace(css, m => TryParseFunction(m.Groups[1].Value, m.Groups[2].Value, out var color) ? Format(color) : m.Value);
        css = HexWithAlpha().Replace(css, m => Format(ParseHex(m.Groups[1].Value)));
        return css;
    }

    readonly record struct Rgba(int R, int G, int B, double A);

    static string Format(Rgba c)
    {
        var alpha = (int)Math.Round(Math.Clamp(c.A, 0, 1) * 255);
        // With the spaces: the renderer ignores rgba() written in fewer than 16 characters.
        return alpha == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{c.R:x2}{c.G:x2}{c.B:x2}")
            : string.Create(CultureInfo.InvariantCulture, $"rgba({c.R}, {c.G}, {c.B}, {alpha})");
    }

    static bool TryParseFunction(string name, string args, out Rgba color)
    {
        color = default;
        // "0, 0, 0, 0.87" as well as the newer "0 0 0 / 87%"
        var parts = args.Split([',', '/', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is not (3 or 4)) return false;
        var alpha = 1.0;
        if (parts.Length == 4 && !TryNumber(parts[3], 1, out alpha)) return false;

        if (name.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryNumber(parts[0], 255, out var r) || !TryNumber(parts[1], 255, out var g) || !TryNumber(parts[2], 255, out var b)) return false;
            color = new Rgba(Channel(r), Channel(g), Channel(b), alpha);
            return true;
        }

        if (!TryHue(parts[0], out var h) || !TryNumber(parts[1], 100, out var s) || !TryNumber(parts[2], 100, out var l)) return false;
        var (red, green, blue) = FromHsl(h, s / 100, l / 100);
        color = new Rgba(red, green, blue, alpha);
        return true;
    }

    /// <summary>A number, or a percentage of <paramref name="full"/>.</summary>
    static bool TryNumber(string text, double full, out double value)
    {
        var percent = text.EndsWith('%');
        if (!double.TryParse(percent ? text[..^1] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
        if (percent) value = value / 100 * full;
        return true;
    }

    static bool TryHue(string text, out double degrees)
    {
        if (text.EndsWith("deg", StringComparison.OrdinalIgnoreCase)) text = text[..^3];
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out degrees);
    }

    static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 255));

    static (int R, int G, int B) FromHsl(double h, double s, double l)
    {
        h = (h % 360 + 360) % 360 / 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return (Hue(p, q, h + 1 / 3.0), Hue(p, q, h), Hue(p, q, h - 1 / 3.0));
    }

    static int Hue(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        var v = t < 1 / 6.0 ? p + (q - p) * 6 * t
            : t < 0.5 ? q
            : t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6
            : p;
        return Channel(v * 255);
    }

    static Rgba ParseHex(string hex)
    {
        if (hex.Length == 4) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        int Byte(int i) => int.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgba(Byte(0), Byte(1), Byte(2), Byte(3) / 255.0);
    }

    [GeneratedRegex(@"\b(rgba?|hsla?)\(\s*([^()]*?)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex ColorFunction();

    // Only inside declarations: the look-ahead skips id selectors such as "#abcd {" in style sheets.
    [GeneratedRegex(@"(?<![\w-])#([0-9a-f]{8}|[0-9a-f]{4})\b(?![^{}]*\{)", RegexOptions.IgnoreCase)]
    private static partial Regex HexWithAlpha();
}
