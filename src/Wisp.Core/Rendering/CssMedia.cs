using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wisp.Core.Rendering;

/// <summary>
/// Resolves @media blocks of a mail's style sheet for a desktop reader. The HTML renderer does not evaluate
/// media queries: it applied the phone rules of newsletters too ("@media (max-width: 499px) { .content
/// { width: 100% !important } }"), which stretched fixed-width layouts and enlarged their images.
/// Matching blocks are unwrapped, all others dropped; other block at-rules (@font-face, @keyframes,
/// @supports, …) are dropped as the renderer cannot use them.
/// </summary>
public static partial class CssMedia
{
    /// <summary>Width the reader is assumed to have: wider than every phone breakpoint, like a desktop mail client.</summary>
    public const double DesktopWidth = 800;

    public static string Resolve(string css, double width = DesktopWidth)
    {
        var sb = new StringBuilder(css.Length);
        var i = 0;
        while (i < css.Length)
        {
            var at = css.IndexOf('@', i);
            if (at < 0)
            {
                sb.Append(css, i, css.Length - i);
                break;
            }
            sb.Append(css, i, at - i);

            var open = css.IndexOf('{', at);
            var semicolon = css.IndexOf(';', at);
            if (open < 0 || (semicolon >= 0 && semicolon < open))
            {
                // A statement like "@charset …;" (imports are already gone): keep as it is.
                var end = semicolon < 0 ? css.Length : semicolon + 1;
                sb.Append(css, at, end - at);
                i = end;
                continue;
            }

            var close = MatchingBrace(css, open);
            var prelude = css[(at + 1)..open].Trim();
            if (prelude.StartsWith("media", StringComparison.OrdinalIgnoreCase) && Matches(prelude[5..], width))
                sb.Append(Resolve(css[(open + 1)..close], width));
            i = close + 1;
        }
        return sb.ToString();
    }

    static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        return css.Length - 1; // unbalanced: the rest belongs to the block
    }

    /// <summary>A media query list matches when any of its queries does.</summary>
    internal static bool Matches(string queries, double width) =>
        queries.Split(',').Any(q => MatchesOne(q.Trim().ToLowerInvariant(), width));

    static bool MatchesOne(string query, double width)
    {
        var negate = false;
        if (query.StartsWith("not ", StringComparison.Ordinal)) { negate = true; query = query[4..]; }
        else if (query.StartsWith("only ", StringComparison.Ordinal)) query = query[5..];

        var result = true;
        foreach (var term in And().Split(query))
        {
            var t = term.Trim();
            if (t.Length == 0) continue;
            result &= t.StartsWith('(') ? Feature(t.Trim('(', ')', ' '), width) : t is "all" or "screen";
        }
        return negate ? !result : result;
    }

    /// <summary>A desktop screen: the given width, pixel ratio 1, light, landscape. Unknown features do not match.</summary>
    static bool Feature(string feature, double width)
    {
        var colon = feature.IndexOf(':');
        if (colon < 0) return feature is "color" or "hover";
        var name = feature[..colon].Trim();
        var value = feature[(colon + 1)..].Trim();
        return name switch
        {
            "min-width" or "min-device-width" => Length(value) is { } min && width >= min,
            "max-width" or "max-device-width" => Length(value) is { } max && width <= max,
            "-webkit-min-device-pixel-ratio" or "min--moz-device-pixel-ratio" or "min-device-pixel-ratio" => Number(value) is { } min && 1 >= min,
            "-webkit-max-device-pixel-ratio" or "max--moz-device-pixel-ratio" or "max-device-pixel-ratio" => Number(value) is { } max && 1 <= max,
            "min-resolution" => Resolution(value) is { } min && 1 >= min,
            "max-resolution" => Resolution(value) is { } max && 1 <= max,
            "prefers-color-scheme" => value == "light",
            "orientation" => value == "landscape",
            _ => false,
        };
    }

    static double? Length(string value) =>
        value.EndsWith("px", StringComparison.Ordinal) ? Number(value[..^2])
        : value.EndsWith("rem", StringComparison.Ordinal) ? Number(value[..^3]) * 16
        : value.EndsWith("em", StringComparison.Ordinal) ? Number(value[..^2]) * 16
        : Number(value);

    /// <summary>In device pixels per CSS pixel.</summary>
    static double? Resolution(string value) =>
        value.EndsWith("dppx", StringComparison.Ordinal) ? Number(value[..^4])
        : value.EndsWith("dpi", StringComparison.Ordinal) ? Number(value[..^3]) / 96
        : value.EndsWith('x') ? Number(value[..^1])
        : null;

    static double? Number(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    [GeneratedRegex(@"\band\b")]
    private static partial Regex And();
}
