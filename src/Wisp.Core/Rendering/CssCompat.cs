using System.Globalization;
using System.Text.RegularExpressions;

namespace Wisp.Core.Rendering;

/// <summary>
/// Rewrites CSS the HTML renderer does not know into what it does know. It ignores the <c>background</c>
/// shorthand altogether (buttons came out as white text on white), and rounds corners only with its own
/// <c>corner-radius</c>. Runs after <see cref="CssColors"/>, so colours are already in a readable form.
/// </summary>
public static partial class CssCompat
{
    public static string Rewrite(string css)
    {
        css = BackgroundShorthand().Replace(css, m =>
            FirstColor(m.Groups[1].Value) is { } color ? "background-color: " + color + Important(m.Groups[1].Value) : "");
        css = BorderRadius().Replace(css, m =>
            Radius(m.Groups[1].Value) is { } radius ? m.Value + "; corner-radius: " + radius : m.Value);
        return css;
    }

    /// <summary>The colour of a <c>background</c> value; for a gradient its first colour, so text on it stays readable.</summary>
    public static string? FirstColor(string value)
    {
        value = Url().Replace(value, " ");
        foreach (Match token in ColorToken().Matches(value))
        {
            var text = token.Value;
            if (text.StartsWith('#') || text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) return text;
            if (NamedColors.Contains(text)) return text.ToLowerInvariant();
        }
        return null;
    }

    static string Important(string value) => value.Contains("!important", StringComparison.OrdinalIgnoreCase) ? " !important" : "";

    /// <summary>One radius in px for all corners (the first of several); percentages cannot be translated.</summary>
    static string? Radius(string value)
    {
        var first = value.Split([' ', '\t', '/', '!'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (first.EndsWith("px", StringComparison.OrdinalIgnoreCase)) first = first[..^2];
        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Min(px, 100):0.#}px")
            : null;
    }

    static readonly HashSet<string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        "transparent", "black", "white", "red", "green", "blue", "yellow", "orange", "purple", "gray", "grey",
        "silver", "maroon", "navy", "teal", "olive", "lime", "aqua", "fuchsia", "pink", "brown", "gold", "beige",
        "ivory", "khaki", "coral", "crimson", "indigo", "violet", "magenta", "cyan", "tomato", "salmon", "orchid",
        "lightgray", "lightgrey", "darkgray", "darkgrey", "dimgray", "dimgrey", "gainsboro", "whitesmoke",
        "snow", "ghostwhite", "aliceblue", "lavender", "lightblue", "skyblue", "steelblue", "royalblue",
        "dodgerblue", "midnightblue", "darkblue", "lightgreen", "darkgreen", "seagreen", "forestgreen",
        "darkred", "firebrick", "lightyellow", "wheat", "tan", "slategray", "slategrey", "lightcyan", "honeydew",
    };

    // The value may contain url(data:…;base64,…): a semicolon inside url() does not end the declaration.
    [GeneratedRegex(@"(?<![-\w.#])background\s*:((?:url\([^)]*\)|[^;{}])*)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundShorthand();

    [GeneratedRegex(@"(?<![-\w])border-radius\s*:\s*([^;{}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BorderRadius();

    [GeneratedRegex(@"url\([^)]*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"#[0-9a-f]{3,8}\b|rgba?\([^)]*\)|(?<![-\w#.])[a-z]+(?![-\w(])", RegexOptions.IgnoreCase)]
    private static partial Regex ColorToken();
}
