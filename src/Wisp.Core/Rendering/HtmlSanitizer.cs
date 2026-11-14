using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Wisp.Core.Rendering;

/// <summary>
/// Strips everything active from HTML mail before it reaches the renderer: scripts, frames, forms,
/// event handlers, external style sheets and dangerous URL schemes. The renderer used by the app
/// executes no JavaScript anyway; this is the second line of defence.
/// It also removes what a mail hides on purpose (preview text, tracking pixels), which the renderer would show.
/// </summary>
public static partial class HtmlSanitizer
{
    static readonly HashSet<string> DroppedElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet", "form", "input",
        "button", "select", "textarea", "meta", "link", "base", "template", "audio", "video", "source",
        "track", "canvas", "svg", "math", "portal", "dialog",
    };

    static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "background", "poster", "action", "formaction", "xlink:href", "srcset", "lowsrc", "dynsrc", "longdesc",
    };

    /// <param name="Background">Page colour the mail asks for (body bgcolor / background), as a CSS colour; null if none.</param>
    public sealed record Result(string Html, bool HasRemoteImages, string? Background = null);

    public static Result Sanitize(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        // Mail bodies arrive with CRLF; the renderer breaks lines in <pre> only at a bare LF.
        doc.LoadHtml(html.ReplaceLineEndings("\n"));
        var remoteImages = false;

        foreach (var node in doc.DocumentNode.Descendants().ToList())
        {
            if (node.NodeType == HtmlNodeType.Comment)
            {
                node.Remove(); // conditional comments can hide markup
                continue;
            }
            if (node.NodeType != HtmlNodeType.Element) continue;
            if (DroppedElements.Contains(node.Name) || IsTrackingPixel(node))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToList())
            {
                // The renderer reads `<img alt width="128">` as alt="width" and loses the width; write alt="" instead.
                if (attribute.QuoteType == AttributeValueQuote.WithoutValue) attribute.QuoteType = AttributeValueQuote.DoubleQuote;
                var name = attribute.Name;
                var value = HtmlEntity.DeEntitize(attribute.Value ?? "").Trim();
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) || name is "srcdoc" or "http-equiv" or "ping")
                {
                    attribute.Remove();
                    continue;
                }
                if (name.Equals("style", StringComparison.OrdinalIgnoreCase))
                {
                    if (RemoteUrl().IsMatch(value)) remoteImages = true;
                    attribute.Value = CleanCss(value);
                    continue;
                }
                if (!UrlAttributes.Contains(name)) continue;

                var isImageSource = node.Name is "img" && name is "src" || name is "background";
                if (name is "srcset" or "lowsrc" or "dynsrc" or "action" or "formaction" or "xlink:href" or "poster" or "longdesc")
                {
                    attribute.Remove();
                    continue;
                }
                if (!IsAllowedUrl(value, isImageSource))
                {
                    attribute.Remove();
                    continue;
                }
                if (isImageSource && IsRemote(value)) remoteImages = true;
            }

            if (node.Name == "a")
            {
                node.SetAttributeValue("rel", "noopener noreferrer");
                node.Attributes.Remove("target");
            }
        }

        RemoveHidden(doc);

        foreach (var style in doc.DocumentNode.Descendants("style").ToList())
        {
            if (RemoteUrl().IsMatch(style.InnerText)) remoteImages = true;
            style.InnerHtml = CssMedia.Resolve(CleanCss(style.InnerText));
        }

        LayoutFixes.Apply(doc);

        var body = doc.DocumentNode.SelectSingleNode("//body");
        var styles = string.Concat(doc.DocumentNode.Descendants("style").Select(s => s.OuterHtml));
        if (body is null) return new Result(doc.DocumentNode.OuterHtml, remoteImages);

        // Only the content of <body> is handed on, so what the body itself says (font, text colour) moves to a
        // wrapper; its background becomes the colour of the whole sheet the mail is shown on.
        var (wrapperStyle, background) = BodyStyle(body);
        var content = wrapperStyle.Length > 0
            ? $"<div style=\"{wrapperStyle.Replace('"', '\'')}\">{body.InnerHtml}</div>"
            : body.InnerHtml;
        return new Result(styles + content, remoteImages, background);
    }

    public static bool IsRemote(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("//", StringComparison.Ordinal);

    static bool IsAllowedUrl(string url, bool image)
    {
        if (url.Length == 0 || url.StartsWith('#')) return true;
        if (IsRemote(url)) return true;
        if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) return !image;
        if (image && url.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return true;
        if (image && url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                  && !url.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) return true;
        return false; // javascript:, vbscript:, file:, data:text/html, relative paths, …
    }

    static string CleanCss(string css)
    {
        css = CssImport().Replace(css, "");
        css = CssDangerous().Replace(css, "");
        return CssCompat.Rewrite(CssColors.Normalize(css));
    }

    // ───────────────────────── Hidden content ─────────────────────────

    /// <summary>
    /// A 1×1 (or 0×0) image exists only to report that the mail was opened. It is dropped for good: it is never
    /// loaded, not even when the user allows the images of this mail, and does not count as an image to load.
    /// </summary>
    static bool IsTrackingPixel(HtmlNode node)
    {
        if (node.Name != "img") return false;
        var style = node.GetAttributeValue("style", "");
        return Tiny(node.GetAttributeValue("width", ""), StylePx(style, "width"))
            && Tiny(node.GetAttributeValue("height", ""), StylePx(style, "height"));

        static bool Tiny(string attribute, double? style)
        {
            attribute = attribute.Trim();
            if (attribute.EndsWith("px", StringComparison.OrdinalIgnoreCase)) attribute = attribute[..^2];
            var size = style ?? (double.TryParse(attribute, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null);
            return size is <= 1;
        }
    }

    static double? StylePx(string style, string property) =>
        Regex.Match(style, $@"(?<![-\w]){property}\s*:\s*([\d.]+)(px)?\s*(!important)?\s*(;|$)", RegexOptions.IgnoreCase) is { Success: true } m
        && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <summary>
    /// Removes what the sender hides with CSS the renderer does not apply: the preview text at the top of
    /// newsletters (display: none, max-height: 0, opacity: 0, mso-hide: all, font-size: 0). It showed up as a
    /// stray first line and landed in copied text.
    /// </summary>
    static void RemoveHidden(HtmlDocument doc)
    {
        foreach (var node in doc.DocumentNode.Descendants().Where(n => n.NodeType == HtmlNodeType.Element).ToList())
        {
            if (node.Name is "html" or "body" or "head" or "style") continue;
            var style = node.GetAttributeValue("style", "");
            if (style.Length == 0) continue;
            if (IsHidden(style))
            {
                node.Remove();
                continue;
            }
            // font-size: 0 hides the element's own text, but is also a layout trick around children that set
            // their own size again; so only text that really inherits the zero goes.
            if (!ZeroFont().IsMatch(style)) continue;
            foreach (var text in node.Descendants().Where(n => n.NodeType == HtmlNodeType.Text).ToList())
            {
                var sized = text.Ancestors().TakeWhile(a => a != node)
                    .Any(a => a.Name == "font" && a.Attributes.Contains("size") || FontSize().IsMatch(a.GetAttributeValue("style", "")));
                if (!sized) text.Remove();
            }
        }
    }

    /// <summary>The element is hidden by its own style in a way the renderer (and the text view) would not notice.</summary>
    internal static bool IsHidden(string style) =>
        style.Length > 0 && (HiddenBox().IsMatch(style) || (ZeroHeight().IsMatch(style) && OverflowHidden().IsMatch(style)));

    // ───────────────────────── Body ─────────────────────────

    /// <summary>Style for the wrapper that stands in for &lt;body&gt;, and the page background the body asks for.</summary>
    static (string Style, string? Background) BodyStyle(HtmlNode body)
    {
        var style = body.GetAttributeValue("style", "");
        string? background = null;
        if (BackgroundColor().Match(style) is { Success: true } m) background = m.Groups[1].Value.Trim();
        else if (body.GetAttributeValue("bgcolor", "").Trim() is { Length: > 0 } bg) background = HexWithoutHash().IsMatch(bg) ? "#" + bg : bg;

        // Sizes and the background do not belong on the wrapper: it is as wide as the sheet, and the sheet has the colour.
        style = BodyOnly().Replace(style, "").Trim().Trim(';').Trim();
        if (body.GetAttributeValue("text", "").Trim() is { Length: > 0 } text && !ColorDeclaration().IsMatch(style))
            style = (style.Length > 0 ? style + "; " : "") + "color: " + (HexWithoutHash().IsMatch(text) ? "#" + text : text);
        return (CssInjection().Replace(style, ""), background is null ? null : CssInjection().Replace(background, ""));
    }

    [GeneratedRegex(@"url\s*\(\s*['""]?\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"(expression\s*\(|behavior\s*:|-moz-binding\s*:|javascript\s*:)", RegexOptions.IgnoreCase)]
    private static partial Regex CssDangerous();

    [GeneratedRegex(@"(?<![-\w])(display\s*:\s*none|mso-hide\s*:\s*all|opacity\s*:\s*0(\.0+)?\s*(!important)?\s*(;|$))", RegexOptions.IgnoreCase)]
    private static partial Regex HiddenBox();

    [GeneratedRegex(@"(?<![-\w])(max-height|height)\s*:\s*0(px)?\s*(!important)?\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroHeight();

    [GeneratedRegex(@"(?<![-\w])overflow\s*:\s*hidden", RegexOptions.IgnoreCase)]
    private static partial Regex OverflowHidden();

    [GeneratedRegex(@"(?<![-\w])font-size\s*:\s*0(px|pt|em)?\s*(!important)?\s*(;|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ZeroFont();

    [GeneratedRegex(@"(?<![-\w])font(-size)?\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex FontSize();

    [GeneratedRegex(@"(?<![-\w])background-color\s*:\s*([^;!]+)", RegexOptions.IgnoreCase)]
    private static partial Regex BackgroundColor();

    [GeneratedRegex(@"(?<![-\w])(background(-color|-image)?|(min-|max-)?(width|height)|margin(-\w+)?|padding(-\w+)?|corner-radius|border(-\w+)*)\s*:[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex BodyOnly();

    [GeneratedRegex(@"(?<![-\w])color\s*:", RegexOptions.IgnoreCase)]
    private static partial Regex ColorDeclaration();

    [GeneratedRegex(@"^([0-9a-f]{3}|[0-9a-f]{6})$", RegexOptions.IgnoreCase)]
    private static partial Regex HexWithoutHash();

    [GeneratedRegex(@"[<>{}]")]
    private static partial Regex CssInjection();
}
