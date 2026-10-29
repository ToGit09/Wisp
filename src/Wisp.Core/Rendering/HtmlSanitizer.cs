using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Wisp.Core.Rendering;

/// <summary>
/// Strips everything active from HTML mail before it reaches the renderer: scripts, frames, forms,
/// event handlers, external style sheets and dangerous URL schemes. The renderer used by the app
/// executes no JavaScript anyway; this is the second line of defence.
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

    public sealed record Result(string Html, bool HasRemoteImages);

    public static Result Sanitize(string html)
    {
        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);
        var remoteImages = false;

        foreach (var node in doc.DocumentNode.Descendants().ToList())
        {
            if (node.NodeType == HtmlNodeType.Comment)
            {
                node.Remove(); // conditional comments can hide markup
                continue;
            }
            if (node.NodeType != HtmlNodeType.Element) continue;
            if (DroppedElements.Contains(node.Name))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToList())
            {
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

        foreach (var style in doc.DocumentNode.Descendants("style").ToList())
        {
            if (RemoteUrl().IsMatch(style.InnerText)) remoteImages = true;
            style.InnerHtml = CleanCss(style.InnerText);
        }

        var body = doc.DocumentNode.SelectSingleNode("//body");
        var styles = string.Concat(doc.DocumentNode.Descendants("style").Select(s => s.OuterHtml));
        var content = body is null ? doc.DocumentNode.OuterHtml : styles + body.InnerHtml;
        return new Result(content, remoteImages);
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
        return CssColors.Normalize(css);
    }

    [GeneratedRegex(@"url\s*\(\s*['""]?\s*(https?:)?//", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteUrl();

    [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
    private static partial Regex CssImport();

    [GeneratedRegex(@"(expression\s*\(|behavior\s*:|-moz-binding\s*:|javascript\s*:)", RegexOptions.IgnoreCase)]
    private static partial Regex CssDangerous();
}
